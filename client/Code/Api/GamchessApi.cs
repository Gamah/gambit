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
/// backend at chess.gamah.net: server-side Steam identity and the durable game
/// archive.
///
/// <para><b>gamchess is never required.</b> If it is down, unreachable, or simply
/// not configured, the game plays exactly as it does otherwise: walking the lobby
/// and playing at a board never touch it. Nothing here may block scene load,
/// <c>OnStart</c>, or a game ending — every call is awaited off the critical path,
/// bounded by <see cref="Timeout"/>, and failure degrades to "archive off" plus a
/// log line. That is why <see cref="Send"/> never throws and returns a plain
/// <see cref="Result"/>.</para>
///
/// <para>There is deliberately no single-flight rule: serialising calls would let
/// one request swallow a game-end archive POST, and losing an archived game is a
/// worse failure than two concurrent requests to our own server. The gate is a
/// circuit breaker instead — after a failure we stop trying for
/// <see cref="BreakerSeconds"/>, so a dead gamah.net costs one timeout, not one
/// per call.</para>
///
/// <para>The only credential that crosses this seam is the Facepunch auth token,
/// which proves Steam identity and is minted per call by
/// <see cref="GamchessAuth"/>.</para>
/// </summary>
public static class GamchessApi
{
	/// <summary>Public root. Must also be in <c>gambit.sbproj</c>'s HttpAllowList
	/// (D8) or every request fails — the allowlist is the only entry there now.</summary>
	public const string Base = "https://chess.gamah.net";

	/// <summary>The same host over WebSocket (M18), for the TV push. <c>wss://</c> goes
	/// through the SAME <c>Http.IsAllowedAsync</c> as an <c>https</c> call — the URL
	/// policy is only scheme/IP checks, so our own host is allowed either way.</summary>
	public const string WsBase = "wss://chess.gamah.net";

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

	// Circuit breaker: the whole gate.
	static RealTimeUntil _breaker; // default 0 → elapsed → ready

	/// <summary>True while the circuit breaker is open (a recent call failed, so
	/// we're not retrying yet). UI can read this to say "archive offline".</summary>
	public static bool Unreachable => (float)_breaker > 0f;

	/// <summary>Seconds until we'll try gamchess again; 0 when ready.</summary>
	public static float BreakerRemaining => Math.Max( 0f, (float)_breaker );

	/// <summary>Clear the breaker — for an explicit user retry / console ping. Also
	/// re-enables session minting: this is the "try everything again" lever, and a user
	/// who asked for a retry means all of it.</summary>
	public static void ResetBreaker()
	{
		_breaker = 0f;
		_sessionMintBlocked = 0f;
	}

	/// <summary>
	/// One request. Never throws. <paramref name="bearer"/> is null for the public
	/// endpoints.
	///
	/// <para><paramref name="steamId"/> accompanies a FACEPUNCH token only — it is the
	/// unverified claim gamchess checks the token against. A session bearer carries its
	/// SteamID inside its MAC, so it needs no header and is sent without one.</para>
	/// </summary>
	static async Task<Result> Send( string path, string method, HttpContent content,
		string steamId, string bearer, bool bypassBreaker = false )
	{
		if ( !bypassBreaker && Unreachable )
			return new Result { Error = $"gamchess is offline — retrying in {BreakerRemaining:0}s." };

		try
		{
			var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
			if ( !string.IsNullOrEmpty( bearer ) )
			{
				headers["Authorization"] = "Bearer " + bearer;
				if ( !string.IsNullOrEmpty( steamId ) )
					headers[SteamIdHeader] = steamId;
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

	// ── The game session (M9) ──

	/// <summary>The gamchess session bearer, or null. MEMORY ONLY — never
	/// <c>FileSystem.Data</c>.
	///
	/// <para>This is not tidiness. A session authorises everything this SteamID can
	/// do, including playing lichess games as them, and gamchess's sessions are
	/// stateless: there is no way to revoke one. "Can a rogue lobby host read another
	/// client's FileSystem.Data?" is still an open spike — do not hand it a
	/// credential to find. <see cref="GamchessAuth"/> holds the FP token the same
	/// way, for the same reason.</para></summary>
	static string _session;
	static RealTimeUntil _sessionExpires;

	/// <summary>Set when a mint FAILS, and this is load-bearing rather than tidy.
	///
	/// <para>The breaker only opens on 5xx/transport errors — a 4xx is a real answer and
	/// leaves it closed. So against a server that simply has no <c>/api/v1/session</c> (a
	/// pre-M9 deploy), an unremembered failure means EVERY request pays a doomed POST
	/// before falling back to the FP path — doubling the request count of a polling
	/// client, permanently. Remember the failure and just use the FP path meanwhile.</para></summary>
	static RealTimeUntil _sessionMintBlocked;

	/// <summary>How long to stop trying to mint after a failure. Long enough that a
	/// server without the route costs ~nothing; short enough that a deploy is picked up
	/// without a restart.</summary>
	const float SessionMintRetrySeconds = 120f;

	/// <summary>Re-mint this long before the server would expire us. The 401 retry is
	/// the real safety net; this just keeps the common case off it.</summary>
	const float SessionMarginSeconds = 60f;

	/// <summary>Drop the cached session (sign-out, unlink, an explicit retry).</summary>
	public static void ForgetSession()
	{
		_session = null;
		_sessionExpires = 0f;
		// Deliberately does NOT clear _sessionMintBlocked: forgetting a session is not
		// evidence that minting works again. ResetBreaker is the explicit "try
		// everything again" lever.
	}

	/// <summary>
	/// Current session bearer, minting one from a Facepunch token if needed. Null when
	/// Steam isn't available or the mint failed — callers fall back to the FP path,
	/// which still works and just costs a Facepunch round-trip per request.
	///
	/// <para>One Facepunch call per hour instead of one per request. gamchess verifies
	/// an FP token against Facepunch on EVERY authed request, so a polling client (a
	/// relayed game, the TV wall) would otherwise spend a round-trip per player per
	/// ~5s, forever.</para>
	/// </summary>
	static async Task<string> Session( bool forceRefresh = false )
	{
		if ( !forceRefresh && !string.IsNullOrEmpty( _session ) && (float)_sessionExpires > 0f )
			return _session;

		// A recent mint failed. Don't pay for another one on every request — see
		// _sessionMintBlocked.
		if ( (float)_sessionMintBlocked > 0f ) return null;

		// A session is minted from an FP token and nothing else — gamchess refuses to
		// mint one from a session, or a client could renew itself forever and the
		// short TTL would mean nothing.
		var (steamId, fpToken) = await GamchessAuth.Credentials( forceRefresh );
		if ( string.IsNullOrEmpty( fpToken ) ) return null;

		var res = await Send( "/api/v1/session", "POST", null, steamId, fpToken );
		if ( !res.Ok )
		{
			// Never fatal: the caller falls back to the FP token, which authenticates
			// exactly the same identity — just more expensively.
			ForgetSession();
			_sessionMintBlocked = SessionMintRetrySeconds;
			return null;
		}

		var body = Deserialize<SessionResponse>( res.Body );
		if ( body == null || string.IsNullOrEmpty( body.token ) )
		{
			ForgetSession();
			_sessionMintBlocked = SessionMintRetrySeconds;
			return null;
		}

		_session = body.token;
		// Trust our own clock rather than the server's expires_at: the two may disagree,
		// and a client clock skewed forward would make us re-mint constantly while one
		// skewed back would make us hold a dead token. A fixed local window can do
		// neither.
		_sessionExpires = SessionTtlSeconds - SessionMarginSeconds;
		return _session;
	}

	/// <summary>Mirrors gamchess's <c>sessionGameTTL</c>. If the two ever disagree the
	/// SERVER wins — a 401 re-mints, so being wrong here costs one extra request, not
	/// a broken client.</summary>
	const float SessionTtlSeconds = 3600f;

	/// <summary>An authed request. Uses a gamchess session bearer when it can, and on
	/// a 401 re-mints once and retries.
	///
	/// <para>The session is what keeps the hot path off Facepunch: every request
	/// authed this way costs gamchess one local HMAC and no network at all. Without a
	/// session we fall back to the FP token, which works identically and just costs
	/// gamchess a Facepunch round-trip — so a mint failure degrades performance,
	/// never function.</para>
	///
	/// <para>Public so <see cref="LichessApi"/> can build on it rather than
	/// reimplement the token dance, the timeout and the breaker. Every gamchess
	/// call in the codebase goes through this method — that is the point of the
	/// seam.</para></summary>
	public static async Task<Result> SendAuthed( string path, string method, HttpContent content )
	{
		var session = await Session();
		if ( !string.IsNullOrEmpty( session ) )
		{
			// No SteamID header: the MAC carries it.
			var sres = await Send( path, method, content, null, session );
			if ( !sres.Unauthorized ) return sres;

			// 401: the session expired mid-flight (or gamchess restarted with a random
			// SESSION_SECRET). Drop it before doing anything else.
			//
			// Belt and braces rather than a fix for a live bug: every path that blocks
			// minting also forgets the session first, so a blocked re-mint below cannot
			// currently leave a dead session cached for later requests to keep failing on.
			// But that invariant lives in another method, and "we just got a 401, so this
			// session is dead" is true right here without knowing it.
			ForgetSession();

			// Then mint a fresh one and try once more — exactly once, because a retry loop
			// against an auth failure is how you get banned.
			session = await Session( forceRefresh: true );
			if ( !string.IsNullOrEmpty( session ) )
				return await Send( path, method, content, null, session );

			// Couldn't re-mint; fall through to the FP path rather than fail.
		}

		var (steamId, token) = await GamchessAuth.Credentials();
		if ( string.IsNullOrEmpty( token ) )
			return new Result { Error = "No Steam auth token — gamchess features need Steam." };

		var res = await Send( path, method, content, steamId, token );
		if ( !res.Unauthorized ) return res;

		(steamId, token) = await GamchessAuth.Credentials( forceRefresh: true );
		if ( string.IsNullOrEmpty( token ) ) return res;

		return await Send( path, method, content, steamId, token );
	}

	/// <summary>Credentials for a WebSocket connection (M18, the TV push): the bearer
	/// to put in an <c>Authorization: Bearer …</c> header, and a SteamID that
	/// accompanies a FACEPUNCH token only.
	///
	/// <para>The session path is the normal one and needs no SteamID — the MAC carries
	/// it, exactly as <see cref="Send"/> omits the header for a session. The FP fallback
	/// returns the claimed SteamID too, though whether a WS handshake may set the extra
	/// <c>X-Steam-Id</c> header is weaker ground (see the caller); a minted session
	/// sidesteps it entirely. Returns a null bearer when Steam is unavailable, so the
	/// caller can degrade to "TV unavailable" rather than dial without auth.</para>
	///
	/// <para>Public so <see cref="LichessTvApi"/> / the TV source can authenticate a
	/// socket through the same session machinery every HTTP call uses, rather than
	/// reimplement the token dance.</para></summary>
	public static async Task<(string bearer, string steamId)> WsCredentials()
	{
		var session = await Session();
		if ( !string.IsNullOrEmpty( session ) )
			return (session, null);

		// No session (mint blocked, or no route on an older gamchess): fall back to the
		// FP token, which authenticates the same identity and just costs gamchess a
		// Facepunch round-trip on the handshake.
		var (steamId, token) = await GamchessAuth.Credentials();
		return (token, steamId);
	}

	/// <summary>A JSON body. Public for <see cref="LichessApi"/>, same reasoning as
	/// <see cref="SendAuthed"/>.</summary>
	public static HttpContent Json( object o ) =>
		new StringContent( JsonSerializer.Serialize( o ), Encoding.UTF8, "application/json" );

	// ── Endpoints ──

	/// <summary>Liveness. Bypasses the breaker so a console ping always really
	/// tries.</summary>
	public static Task<Result> Health() =>
		Send( "/health", "GET", null, null, null, bypassBreaker: true );

	/// <summary>Archive a finished game. Idempotent on <paramref name="clientGameId"/>
	/// (the host generates it at game start and [Sync]s it), so both seats may post
	/// the same game and the second is a no-op. SteamIDs go as STRINGS — a SteamID64
	/// exceeds JavaScript's 2^53, so a bare number would be corrupted by the web
	/// viewer reading the archive.</summary>
	public static Task<Result> PostGame( string clientGameId, string pgn, ulong whiteSteamId,
		ulong blackSteamId, string result ) =>
		SendAuthed( "/api/v1/games", "POST", Json( new
		{
			client_game_id = clientGameId,
			pgn,
			white_steam_id = whiteSteamId.ToString(),
			black_steam_id = blackSteamId.ToString(),
			result,
		} ) );

	/// <summary>YOUR archived games, newest first.
	/// <para>The archive is private: there is no way to ask for someone else's, and
	/// no SteamID parameter to pass. The server takes the identity from our FP token
	/// (or a Steam OpenID session on the web) and returns only games we sat in.</para></summary>
	public static Task<Result> ListGames( int limit = 50 ) =>
		SendAuthed( $"/api/v1/games?limit={limit}", "GET", null );

	/// <summary>One of your archived games. 404 if you didn't play in it — the same
	/// answer as "doesn't exist", so ids aren't probeable.</summary>
	public static Task<Result> GetGame( string id ) =>
		SendAuthed( $"/api/v1/games/{Uri.EscapeDataString( id )}", "GET", null );

	// ── Helpers ──

	/// <summary>A fresh <c>client_game_id</c> — a RFC 4122 v4 UUID string.
	///
	/// <para>Hand-rolled from <c>Random.Shared</c> rather than <c>Guid.NewGuid()</c>:
	/// Guid appears nowhere else in this codebase, and NewGuid reaches into platform
	/// crypto interop — precisely the shape the s&amp;box whitelist rejects with
	/// SB1000. Random.Shared is already proven here (FloorCheckerboard).</para>
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

	/// <summary>Mask a credential for logs — never print one whole. An FP auth
	/// token is short-lived but it is still a credential.</summary>
	public static string Redact( string token )
	{
		if ( string.IsNullOrEmpty( token ) ) return "(none)";
		return token.Length <= 8 ? "****" : token[..4] + "…" + token[^2..];
	}

	/// <summary>Trim a body for a log line.</summary>
	public static string Truncate( string s, int max ) =>
		s == null ? "" : s.Length <= max ? s : s[..max] + "…";
}
