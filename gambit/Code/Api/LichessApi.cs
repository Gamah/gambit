using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// Single seam for every lichess REST call (PLAN.md new file, D8 allowlist).
/// Enforces the two rate rules from CLAUDE.md across the whole client:
///   • one REST request in flight at a time (single-flight gate), and
///   • a full 60-second back-off after any HTTP 429.
///
/// Every request rides through <see cref="Send"/>, which returns a plain
/// <see cref="Result"/> (status + body + a human-friendly error) so callers
/// never touch <c>HttpResponseMessage</c> directly. The 4-argument
/// <c>Http.RequestAsync(url, method, content, headers)</c> form is used
/// throughout — it's the shape proven to send bodies/headers correctly in the
/// parent project (rotaliate ApiClient), and the fix for M2's dead import
/// (the old 3-arg call never landed the form body — see PLAN.md M2 note).
///
/// Every request also sends <c>Accept: application/json</c> — mandatory, since
/// lichess content-negotiates on shared paths (that missing header, not the
/// 3-vs-4-arg request shape, was M2's dead import: POST /api/import replied 200 +
/// the game's HTML page instead of {id,url}).
///
/// The bearer token is a secret (PLAN.md D3): it is only ever placed in the
/// Authorization header here and is never logged (see <see cref="Redact"/>).
/// </summary>
public static class LichessApi
{
	public const string Base = "https://lichess.org";

	/// <summary>Outcome of a REST call — success flag, HTTP status, raw body,
	/// and a ready-to-show error string (null on success).</summary>
	public struct Result
	{
		public bool Ok;
		public int Status;      // 0 when the request never reached lichess
		public string Body;
		public string Error;    // null when Ok

		public bool Unauthorized => Status == 401;
	}

	// Client-wide rate discipline (CLAUDE.md). Static so every controller and
	// screen shares one gate — not per-instance.
	static bool _inFlight;
	static RealTimeUntil _backoff; // default 0 → already elapsed → ready to send

	/// <summary>True while a request is running — the HUD/splash can grey out
	/// their buttons instead of racing the single-flight gate.</summary>
	public static bool Busy => _inFlight;

	/// <summary>Seconds left on the 429 back-off, 0 when clear. (RealTimeUntil casts
	/// to seconds-remaining: positive while pending, negative once elapsed.)</summary>
	public static float BackoffRemaining => Math.Max( 0f, (float)_backoff );

	static async Task<Result> Send( string url, string method, HttpContent content, string bearer, string accept = "application/json" )
	{
		if ( _inFlight )
			return new Result { Error = "Another lichess request is running — try again in a moment." };
		if ( !_backoff )
			return new Result { Error = "lichess asked us to slow down — try again in a minute." };

		_inFlight = true;
		try
		{
			// Accept: application/json is REQUIRED on the JSON APIs, not cosmetic.
			// lichess content-negotiates on shared paths: POST /api/import without it
			// returns 200 + the imported game's HTML page (the web-form response), not
			// {id,url} — that was M2's dead import (confirmed in-editor: HTTP 200, HTML
			// body, no url). /api/account happens to be JSON-only, which is why sign-in
			// worked regardless. Send it on every request so no endpoint can surprise
			// us — except the self-seat attempt, which overrides to text/html to mimic
			// a browser loading the challenge page.
			var headers = new Dictionary<string, string> { ["Accept"] = accept };
			if ( !string.IsNullOrEmpty( bearer ) )
				headers["Authorization"] = "Bearer " + bearer;

			var resp = await Http.RequestAsync( url, method, content, headers );
			int status = (int)resp.StatusCode;

			if ( status == 429 )
			{
				_backoff = 60f; // full minute, per lichess guidance
				return new Result { Status = 429, Error = "lichess rate limit hit — wait a minute and retry." };
			}

			var body = await resp.Content.ReadAsStringAsync();
			return new Result
			{
				Ok = resp.IsSuccessStatusCode,
				Status = status,
				Body = body,
				Error = resp.IsSuccessStatusCode ? null : $"lichess returned {status}.",
			};
		}
		catch ( Exception e )
		{
			return new Result { Error = "Couldn't reach lichess: " + e.Message };
		}
		finally
		{
			_inFlight = false;
		}
	}

	// ── Endpoints used in M3 ──

	/// <summary>Fetch the token owner's account (validates the token as a side
	/// effect — a bad/expired token comes back 401).</summary>
	public static Task<Result> GetAccount( string token ) =>
		Send( Base + "/api/account", "GET", null, token );

	/// <summary>Revoke the current token (logout). Best-effort — a failure still
	/// lets the client forget the token locally.</summary>
	public static Task<Result> DeleteToken( string token ) =>
		Send( Base + "/api/token", "DELETE", null, token );

	/// <summary>Import a finished game's PGN (unauthenticated, 100/hour/IP). This
	/// is the call M2 shipped broken; it lives here now so it shares the rate gate
	/// and the proven 4-arg request shape.</summary>
	public static Task<Result> ImportPgn( string pgn ) =>
		PostForm( "/api/import", "pgn=" + Uri.EscapeDataString( pgn ) );

	/// <summary>Create an open-ended challenge anyone can join by opening a URL
	/// (M4 — no streaming needed; the game plays on lichess.org). Unauthenticated,
	/// so it is always casual/unrated — exactly what we want. Clocks are in seconds
	/// (10+0 rapid = 600/0). The reply carries <c>urlWhite</c>/<c>urlBlack</c>,
	/// which pin the colours.</summary>
	public static Task<Result> CreateOpenChallenge( int clockLimitSeconds, int clockIncrementSeconds, string name = null, string token = null )
	{
		var body = $"rated=false&clock.limit={clockLimitSeconds}&clock.increment={clockIncrementSeconds}&variant=standard";
		if ( !string.IsNullOrEmpty( name ) )
			body += "&name=" + Uri.EscapeDataString( name );
		// Created with the player's token when they intend to sit in on it in sbox,
		// so it's cancellable and tied to their session; unauthenticated (token null)
		// for the pure browser-link flow (M4a).
		return Send( Base + "/api/challenge/open", "POST", Form( body ), token );
	}

	/// <summary>POST an <c>x-www-form-urlencoded</c> body to a lichess path,
	/// unauthenticated (used by import and the OAuth token exchange).</summary>
	public static Task<Result> PostForm( string path, string formBody )
	{
		return Send( Base + path, "POST", Form( formBody ), null );
	}

	static StringContent Form( string body ) =>
		new( body, Encoding.UTF8, "application/x-www-form-urlencoded" );

	// ── Board API play (M4 in-sbox polling play) ──
	// Live move streams are unavailable under s&box Http (PLAN.md risk 1), so play
	// is driven by POLLING GetAccountPlaying instead of the ndjson game stream.
	// All of these are token-authenticated (board:play scope); the token only ever
	// rides in the Authorization header, never logged/synced (D3).

	/// <summary>Challenge a specific lichess user to a casual game (Rapid 10+0 =
	/// 600/0). They accept on lichess.org; the game then appears in
	/// <see cref="GetAccountPlaying"/>. <paramref name="color"/> is "white"/"black"/
	/// "random" (we pass the seat the challenger sat at).</summary>
	public static Task<Result> ChallengeUser( string username, string color, int limit, int inc, string token )
	{
		var body = $"rated=false&clock.limit={limit}&clock.increment={inc}&color={color}&variant=standard";
		return Send( $"{Base}/api/challenge/{Uri.EscapeDataString( username )}", "POST", Form( body ), token );
	}

	/// <summary>Challenge the Stockfish AI (level 1–8) — starts a game immediately
	/// (no accept needed), so the reply is the game itself. Handy for testing the
	/// play loop with no second party.</summary>
	public static Task<Result> ChallengeAi( int level, string color, int limit, int inc, string token )
	{
		var body = $"level={level}&clock.limit={limit}&clock.increment={inc}&color={color}&variant=standard";
		return Send( $"{Base}/api/challenge/ai", "POST", Form( body ), token );
	}

	/// <summary>The signed-in user's ongoing games (fen, lastMove, isMyTurn,
	/// secondsLeft, opponent). A normal single-response endpoint — this is the
	/// poll target that stands in for the unavailable ndjson game stream.</summary>
	public static Task<Result> GetAccountPlaying( string token ) =>
		Send( Base + "/api/account/playing", "GET", null, token );

	/// <summary>Play a UCI move in a Board-API game (short request, works without
	/// streaming). lichess rejects illegal/out-of-turn moves with 4xx.</summary>
	public static Task<Result> BoardMove( string gameId, string uci, string token ) =>
		Send( $"{Base}/api/board/game/{gameId}/move/{uci}", "POST", null, token );

	/// <summary>Resign a Board-API game.</summary>
	public static Task<Result> BoardResign( string gameId, string token ) =>
		Send( $"{Base}/api/board/game/{gameId}/resign", "POST", null, token );

	/// <summary>Cancel a challenge we created that hasn't been accepted yet.</summary>
	public static Task<Result> CancelChallenge( string challengeId, string token ) =>
		Send( $"{Base}/api/challenge/{challengeId}/cancel", "POST", null, token );

	/// <summary>Best-effort, UNDOCUMENTED: GET a colour-pinned open-challenge URL
	/// with our bearer token to try to claim that seat without a browser. lichess
	/// has no API to seat the creator of an open challenge, so this only works if
	/// loading the URL server-side claims the seat (unverified). Sends
	/// <c>Accept: text/html</c> to mimic a browser rather than the JSON API view.</summary>
	public static Task<Result> SeatOpenChallenge( string seatUrl, string token ) =>
		Send( seatUrl, "GET", null, token, accept: "text/html" );

	/// <summary>Export a finished game as JSON to read its final status/winner —
	/// account/playing drops a game the moment it ends, so this fills in the result.</summary>
	public static Task<Result> GameExport( string gameId ) =>
		Send( $"{Base}/game/export/{gameId}", "GET", null, null );

	// ── Helpers ──

	/// <summary>Parse a JSON body into <typeparamref name="T"/>, or null on any
	/// error (never throws into UI code). System.Text.Json is whitelisted — the
	/// same serializer PlayerData persists with.</summary>
	public static T Deserialize<T>( string json ) where T : class
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return null;
		try { return JsonSerializer.Deserialize<T>( json ); }
		catch { return null; }
	}

	/// <summary>Mask a token for logs — never print it whole (PLAN.md D3, the
	/// old GUID Redact discipline).</summary>
	public static string Redact( string token )
	{
		if ( string.IsNullOrEmpty( token ) ) return "(none)";
		return token.Length <= 8 ? "****" : token[..4] + "…" + token[^2..];
	}

	/// <summary>Trim a body for a log line.</summary>
	public static string Truncate( string s, int max ) =>
		s == null ? "" : s.Length <= max ? s : s[..max] + "…";
}
