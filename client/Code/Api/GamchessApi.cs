using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// Single seam for every gamchess call (issue #7 / M7). gamchess is Gambit's own
/// backend at chess.gamah.net: server-side identity, the lichess OAuth <b>code</b>
/// relay, and the durable game archive.
///
/// <para><b>gamchess is never required.</b> If it is down, unreachable, or simply
/// not configured, every feature that predates M7 must behave exactly as it did
/// before: local play, puzzles, spectating and token-paste sign-in all work with
/// gamchess dead. Nothing here may block scene load, <c>OnStart</c>, or a game
/// ending — every call is awaited off the critical path, bounded by
/// <see cref="Timeout"/>, and failure degrades to "archive off" plus a log line.
/// That is why <see cref="Send"/> never throws and returns a plain
/// <see cref="Result"/>.</para>
///
/// <para><b>This has its own gate, deliberately separate from
/// <see cref="LichessApi"/>'s.</b> Different host, different limits: gamchess
/// calls must never contend with lichess's single-flight gate or be stalled by
/// its 60-second 429 back-off, and must never trip that back-off either. The two
/// share no state.</para>
///
/// <para>Unlike lichess there is no single-flight rule here — serialising calls
/// would let an in-progress sign-in poll swallow a game-end archive POST, and
/// losing an archived game to a UI poll is a worse failure than two concurrent
/// requests to our own server. The gate is a circuit breaker instead: after a
/// failure we stop trying for <see cref="BreakerSeconds"/>, so a dead gamah.net
/// costs one timeout, not one per call.</para>
///
/// <para><b>No lichess token ever goes to gamchess.</b> Not in a header, not in a
/// body, not ever — gamchess has no column for one and no exchange path. What
/// crosses this seam is the Facepunch auth token (proves Steam identity, minted
/// per call by <see cref="GamchessAuth"/>) and OAuth <i>codes</i>, which are
/// single-use, ~1 minute, and useless without the PKCE verifier that never leaves
/// this machine.</para>
/// </summary>
public static class GamchessApi
{
	/// <summary>Public root. Must also be in <c>gambit.sbproj</c>'s HttpAllowList
	/// (D8) or every request fails — gamchess is the first non-lichess host Gambit
	/// has ever talked to.</summary>
	public const string Base = "https://chess.gamah.net";

	/// <summary>Per-request ceiling. Short on purpose: a hung backend must not be
	/// something the player can feel.</summary>
	public const float Timeout = 8f;

	/// <summary>How long to stop calling after a failure. A dead gamah.net should
	/// cost one timeout, not one per call.</summary>
	public const float BreakerSeconds = 60f;

	/// <summary>The SteamID header. gamchess treats it as an unverified CLAIM and
	/// trusts only what Facepunch echoes back — see the server's internal/api/auth.go.</summary>
	public const string SteamIdHeader = "X-Steam-Id";

	public struct Result
	{
		public bool Ok;
		public int Status;      // 0 when the request never reached gamchess
		public string Body;
		public string Error;    // null when Ok

		public bool Unauthorized => Status == 401;
		public bool NotFound => Status == 404;
	}

	// gamchess's OWN gate — no shared state with LichessApi.
	static RealTimeUntil _breaker; // default 0 → elapsed → ready

	/// <summary>True while the circuit breaker is open (a recent call failed, so
	/// we're not retrying yet). UI can read this to say "archive offline".</summary>
	public static bool Unreachable => (float)_breaker > 0f;

	/// <summary>Seconds until we'll try gamchess again; 0 when ready.</summary>
	public static float BreakerRemaining => Math.Max( 0f, (float)_breaker );

	/// <summary>Clear the breaker — for an explicit user retry / console ping.</summary>
	public static void ResetBreaker() => _breaker = 0f;

	/// <summary>
	/// One request. Never throws. <paramref name="steamId"/>/<paramref name="fpToken"/>
	/// are null for the public endpoints.
	/// </summary>
	static async Task<Result> Send( string path, string method, HttpContent content,
		string steamId, string fpToken, bool bypassBreaker = false )
	{
		if ( !bypassBreaker && Unreachable )
			return new Result { Error = $"gamchess is offline — retrying in {BreakerRemaining:0}s." };

		try
		{
			var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
			if ( !string.IsNullOrEmpty( fpToken ) )
			{
				headers["Authorization"] = "Bearer " + fpToken;
				headers[SteamIdHeader] = steamId ?? "";
			}

			using var cts = new CancellationTokenSource();
			cts.CancelAfter( TimeSpan.FromSeconds( Timeout ) );

			var resp = await Http.RequestAsync( Base + path, method, content, headers, cts.Token );
			int status = (int)resp.StatusCode;
			var body = await resp.Content.ReadAsStringAsync();

			// A 5xx means our backend is sick; open the breaker. 4xx is a real
			// answer (401/404/403/400) — the server is healthy, so leave it closed.
			if ( status >= 500 )
			{
				_breaker = BreakerSeconds;
				return new Result { Status = status, Body = body, Error = $"gamchess returned {status}." };
			}

			return new Result
			{
				Ok = resp.IsSuccessStatusCode,
				Status = status,
				Body = body,
				Error = resp.IsSuccessStatusCode ? null : $"gamchess returned {status}.",
			};
		}
		catch ( Exception e )
		{
			// Timeout, DNS failure, allowlist rejection, gamah.net down — all the
			// same to us: back off and let everything else carry on working.
			_breaker = BreakerSeconds;
			return new Result { Error = "Couldn't reach gamchess: " + e.Message };
		}
	}

	/// <summary>An authed request: mints a Facepunch token, and on 401 re-mints
	/// once and retries (rotaliate's rule — FP tokens really do expire).</summary>
	static async Task<Result> SendAuthed( string path, string method, HttpContent content )
	{
		var (steamId, token) = await GamchessAuth.Credentials();
		if ( string.IsNullOrEmpty( token ) )
			return new Result { Error = "No Steam auth token — gamchess features need Steam." };

		var res = await Send( path, method, content, steamId, token );
		if ( !res.Unauthorized ) return res;

		// 401: the token expired mid-flight. Mint a fresh one and try once more.
		// Exactly once — a retry loop against an auth failure is how you get banned.
		(steamId, token) = await GamchessAuth.Credentials( forceRefresh: true );
		if ( string.IsNullOrEmpty( token ) ) return res;

		return await Send( path, method, content, steamId, token );
	}

	static HttpContent Json( object o ) =>
		new StringContent( JsonSerializer.Serialize( o ), Encoding.UTF8, "application/json" );

	// ── Endpoints ──

	/// <summary>Liveness. Bypasses the breaker so a console ping always really
	/// tries.</summary>
	public static Task<Result> Health() =>
		Send( "/health", "GET", null, null, null, bypassBreaker: true );

	/// <summary>Register an OAuth <c>state</c> against our verified SteamID.
	/// Returns <c>{redirect_uri}</c> — the exact value to hand lichess, which must
	/// match byte-for-byte at authorize and at exchange.</summary>
	public static Task<Result> LichessBegin( string state ) =>
		SendAuthed( "/api/v1/auth/lichess/begin", "POST", Json( new { state } ) );

	/// <summary>Claim this SteamID's pending OAuth code, once. 404 is the normal
	/// "not yet" — this endpoint is polled.</summary>
	public static Task<Result> LichessCode() =>
		SendAuthed( "/api/v1/auth/lichess/code", "GET", null );

	/// <summary>Record the SteamID → lichess-username link. The username comes from
	/// lichess's own /api/account: gamchess can't look it up itself because it never
	/// holds a token.</summary>
	public static Task<Result> PutLichessLink( string lichessUsername ) =>
		SendAuthed( "/api/v1/links/lichess", "PUT", Json( new { lichess_username = lichessUsername } ) );

	/// <summary>Unlink. The delete path the security posture requires — a persisted
	/// SteamID↔lichess link is durable identity data, so it must stay removable.</summary>
	public static Task<Result> DeleteLichessLink() =>
		SendAuthed( "/api/v1/links/lichess", "DELETE", null );

	/// <summary>Archive a finished game. Idempotent on <paramref name="clientGameId"/>
	/// (the host generates it at game start and [Sync]s it), so both seats may post
	/// the same game and the second is a no-op. SteamIDs go as STRINGS — a SteamID64
	/// exceeds JavaScript's 2^53, so a bare number would be corrupted by any web
	/// client reading the archive.</summary>
	public static Task<Result> PostGame( string clientGameId, string pgn, ulong whiteSteamId,
		ulong blackSteamId, string result, string lichessGameId = null ) =>
		SendAuthed( "/api/v1/games", "POST", Json( new
		{
			client_game_id = clientGameId,
			pgn,
			white_steam_id = whiteSteamId.ToString(),
			black_steam_id = blackSteamId.ToString(),
			result,
			lichess_game_id = lichessGameId,
		} ) );

	/// <summary>A player's archived games, newest first. Public — no auth.</summary>
	public static Task<Result> ListGames( ulong steamId, int limit = 50 ) =>
		Send( $"/api/v1/games?steam_id={steamId}&limit={limit}", "GET", null, null, null );

	/// <summary>One archived game. Public — no auth.</summary>
	public static Task<Result> GetGame( string id ) =>
		Send( $"/api/v1/games/{Uri.EscapeDataString( id )}", "GET", null, null, null );

	// ── Helpers ──

	/// <summary>A fresh <c>client_game_id</c> — a RFC 4122 v4 UUID string.
	///
	/// <para>Hand-rolled from <c>Random.Shared</c> rather than <c>Guid.NewGuid()</c>:
	/// Guid appears nowhere else in this codebase, and NewGuid reaches into platform
	/// crypto interop — precisely the shape the s&amp;box whitelist rejects with
	/// SB1000. Same reasoning that left SHA-256 and base64url hand-rolled in
	/// <see cref="LichessOAuth"/>. Random.Shared is already proven here.</para>
	///
	/// <para>This is an idempotency key, not a secret — it is [Sync]ed to both seats
	/// on purpose so either can submit the same game — so a non-cryptographic source
	/// is fine. The server parses it strictly (Go's uuid.Parse), so the layout has to
	/// be exact: 8-4-4-4-12, version nibble '4', variant nibble in 8..b.</para></summary>
	public static string NewClientGameId()
	{
		const string hex = "0123456789abcdef";
		var sb = new StringBuilder( 36 );
		for ( int i = 0; i < 36; i++ )
		{
			if ( i is 8 or 13 or 18 or 23 ) sb.Append( '-' );
			else if ( i == 14 ) sb.Append( '4' );                                  // version 4
			else if ( i == 19 ) sb.Append( hex[8 + System.Random.Shared.Next( 4 )] ); // variant 10xx
			else sb.Append( hex[System.Random.Shared.Next( 16 )] );
		}
		return sb.ToString();
	}

	/// <summary>Parse a JSON body, or null on any error (never throws into UI code).</summary>
	public static T Deserialize<T>( string json ) where T : class
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return null;
		try { return JsonSerializer.Deserialize<T>( json ); }
		catch { return null; }
	}
}
