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

	static async Task<Result> Send( string url, string method, HttpContent content, string bearer )
	{
		if ( _inFlight )
			return new Result { Error = "Another lichess request is running — try again in a moment." };
		if ( !_backoff )
			return new Result { Error = "lichess asked us to slow down — try again in a minute." };

		_inFlight = true;
		try
		{
			// Accept: application/json is REQUIRED, not cosmetic. lichess content-
			// negotiates on shared paths: POST /api/import without it returns 200 +
			// the imported game's HTML page (the web-form response), not {id,url} —
			// that was M2's dead import (confirmed in-editor: HTTP 200, HTML body, no
			// url). /api/account happens to be JSON-only, which is why sign-in worked
			// regardless. Send it on every request so no endpoint can surprise us.
			var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
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
	public static Task<Result> ImportPgn( string pgn )
	{
		var content = new StringContent(
			"pgn=" + Uri.EscapeDataString( pgn ),
			Encoding.UTF8, "application/x-www-form-urlencoded" );
		return Send( Base + "/api/import", "POST", content, null );
	}

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
