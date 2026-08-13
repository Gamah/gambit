using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api.Lichess;

/// <summary>
/// The ONE seam every lichess request goes through (HTTPFIX).
///
/// <para>Before this, the client held no lichess token and spoke no lichess
/// protocol — gamchess did both, because <c>Http.RequestStreamAsync</c> returned
/// before the body ended and playing a lichess game means holding an ndjson
/// stream open. That engine bug is fixed (our own fix, <c>42cee680</c>), so the
/// token lives on this machine now and this file is what talks to lichess.</para>
///
/// <para><b>Why a single seam and not a helper.</b> The server guaranteed its
/// identification with a <c>RoundTripper</c> — a call site physically could not
/// forget it. s&amp;box has no such mechanism; headers are a per-call dictionary.
/// So the guarantee is replaced by a rule: <b>every lichess request in the
/// codebase is built here</b>, exactly as <see cref="GamchessApi.SendAuthed"/> is
/// the only <c>Http.RequestAsync</c> call site for our own backend. Everything
/// else describes a request and hands it over. A <c>Http.Request*</c> call to
/// lichess.org anywhere else in <c>client/</c> is a bug, not a shortcut.</para>
///
/// <para><b>It never throws.</b> The engine throws <c>HttpRequestException</c> on
/// a non-2xx and <c>InvalidOperationException</c> on a disallowed URI or header;
/// the house style is <see cref="GamchessApi.Result"/>, which never does. This
/// CONVERTS to that shape rather than inheriting the engine's.</para>
///
/// <para><b>It does NOT share gamchess's circuit breaker.</b> Deliberate: lichess
/// being slow must not stop us archiving a game to our own backend, and a dead
/// gamah.net must not stop a lichess game in progress. Two hosts, two failure
/// domains.</para>
/// </summary>
public static class LichessClient
{
	/// <summary>lichess's API root. Also in <c>gambit.sbproj</c>'s HttpAllowList —
	/// which gates nothing (the engine has no per-package host allowlist; see
	/// GAMCHESS.md, and do not diagnose against it) but declares the hosts we mean
	/// to talk to, and this is the second one.</summary>
	public const string Base = "https://lichess.org";

	/// <summary>Per-request ceiling for a BUFFERED call. Streams are bounded by the
	/// game's life instead — see <see cref="OpenStream"/>.</summary>
	public const float Timeout = 10f;

	/// <summary>One buffered request's answer. Deliberately the same shape as
	/// <see cref="GamchessApi.Result"/> so call sites read the same, but a separate
	/// type: these are two different servers with two different failure stories.</summary>
	public struct Result
	{
		public bool Ok;
		public int Status;      // 0 when the request never reached lichess
		public string Body;
		public string Error;    // null when Ok

		/// <summary>The token is dead — the player revoked our grant on
		/// <c>/account/security</c>, or it expired. Re-linking is the only fix;
		/// there are no refresh tokens.</summary>
		public readonly bool Unauthorized => Status == 401;

		public readonly bool RateLimited => Status == 429;

		/// <summary>lichess's own error text, which is usually the useful part
		/// ("No such user: bob", "Not your turn", "This game cannot be aborted").
		/// Falls back to whatever we know.</summary>
		public readonly string Reason
		{
			get
			{
				var body = Parse<LichessErrorBody>( Body );
				if ( !string.IsNullOrEmpty( body?.error ) ) return body.error;
				return Error ?? "Lichess didn't say why.";
			}
		}
	}

	static bool _clockSet;

	/// <summary>The governor needs a monotonic clock and this is the only place
	/// that knows the engine has one. Idempotent; called before every request so
	/// there is no startup ordering to get wrong.</summary>
	static void EnsureClock()
	{
		if ( _clockSet ) return;
		_clockSet = true;
		LichessEtiquette.UseClock( () => RealTime.Now );
	}

	/// <summary>Headers for one request.
	///
	/// <para><paramref name="token"/> null or empty means <b>ANONYMOUS — no
	/// Authorization header at all</b>, and that is a feature rather than a
	/// convenience. <c>POST /api/challenge/open</c> is <c>security: []</c> and
	/// <b>403s a board:play token</b> ("Missing scope: challenge:write"), so the
	/// shareable-link flow must present nothing. An empty "Bearer " would 401.</para></summary>
	static Dictionary<string, string> Headers( string token, string accept, bool form )
	{
		var h = new Dictionary<string, string>
		{
			["Accept"] = accept,
			// The obligation this whole seam exists to keep — under the one header
			// name the engine will let a game set. "User-Agent" THROWS here (it is
			// in Http.ForbiddenHeaders) and is overwritten with "facepunch-sbox"
			// even if it didn't; see LichessEtiquette.IdentityHeader for why there
			// is no way round that and why we send it anyway.
			[LichessEtiquette.IdentityHeader] = LichessEtiquette.UserAgent,
		};
		if ( !string.IsNullOrEmpty( token ) ) h["Authorization"] = "Bearer " + token;
		if ( form ) h["Content-Type"] = "application/x-www-form-urlencoded";
		return h;
	}

	/// <summary>
	/// One buffered request. Never throws.
	///
	/// <para>Pass <paramref name="token"/> = null for the anonymous endpoints. The
	/// pre-flight is the etiquette: inside the post-429 minute we refuse locally
	/// rather than spend a request being told the same thing.</para>
	/// </summary>
	public static async Task<Result> Send( string path, string method, string token,
		Dictionary<string, string> form = null )
	{
		EnsureClock();

		if ( LichessEtiquette.BackingOff )
		{
			return new Result
			{
				Error = $"Lichess asked us to slow down — {LichessEtiquette.BackoffRemaining:0}s to wait.",
			};
		}

		try
		{
			HttpContent content = null;
			if ( form != null )
				content = new StringContent( FormEncode( form ), Encoding.UTF8,
					"application/x-www-form-urlencoded" );

			using var cts = new CancellationTokenSource();
			cts.CancelAfter( TimeSpan.FromSeconds( Timeout ) );

			var resp = await Http.RequestAsync( Base + path, method, content,
				Headers( token, "application/json", form != null ), cts.Token );

			int status = (int)resp.StatusCode;
			string body = await resp.Content.ReadAsStringAsync();

			// A 429 anywhere stops EVERY outbound call for a minute. lichess's own
			// words, and the rule that keeps a throttle from becoming a ban.
			if ( status == 429 ) LichessEtiquette.Note429();

			return new Result
			{
				Ok = resp.IsSuccessStatusCode,
				Status = status,
				Body = body,
				Error = resp.IsSuccessStatusCode ? null : $"Lichess returned {status}.",
			};
		}
		catch ( Exception e )
		{
			// Timeout, DNS, a disallowed URI, lichess down — all the same to us.
			// NOTE what is deliberately absent: a retry. Reporting the reason and
			// letting the player decide is the etiquette rule.
			return new Result { Error = "Couldn't reach lichess: " + e.Message };
		}
	}

	/// <summary>
	/// Open an ndjson stream. The caller OWNS the returned stream and must dispose
	/// it.
	///
	/// <para><b>Disposal is the whole lifecycle.</b> The engine's fix wraps the
	/// response and the body together, and its own docs are blunt about it —
	/// "dispose it or the connection stays open". A leaked stream is not a leaked
	/// object here: on the Board API an open connection means <i>this player is
	/// present</i>, and an open event stream means <i>this token's one slot is
	/// taken</i>. Both fail silently.</para>
	///
	/// <para><b>The timeout is the caller's cancellation token, not a clock.</b>
	/// <c>HttpClient.Timeout</c> does not bound the body read, which is the
	/// OPPOSITE of <see cref="GamchessApi"/>'s 8-second <c>CancelAfter</c>: a game
	/// stream is bounded by the game's life and cancelled on stand-up, disengage or
	/// teardown — never by elapsed time. A stream that times out mid-think is a
	/// game that stops updating.</para>
	///
	/// <para>Throws nothing useful to a caller, so it returns null and fills
	/// <paramref name="error"/> instead. A non-2xx surfaces as
	/// <c>HttpRequestException</c> with the status attached — the body is not
	/// readable on this path, so an error stream reports its status and no more.</para>
	/// </summary>
	public static async Task<Stream> OpenStream( string path, string method, string token,
		CancellationToken ct, Dictionary<string, string> form = null )
	{
		EnsureClock();

		if ( LichessEtiquette.BackingOff )
			throw new LichessStreamException( 0,
				$"Lichess asked us to slow down — {LichessEtiquette.BackoffRemaining:0}s to wait." );

		HttpContent content = null;
		if ( form != null )
			content = new StringContent( FormEncode( form ), Encoding.UTF8,
				"application/x-www-form-urlencoded" );

		try
		{
			return await Http.RequestStreamAsync( Base + path, method, content,
				Headers( token, "application/x-ndjson", form != null ), ct );
		}
		catch ( HttpRequestException e )
		{
			int status = e.StatusCode is { } s ? (int)s : 0;
			if ( status == 429 ) LichessEtiquette.Note429();
			throw new LichessStreamException( status, $"Lichess returned {status}." );
		}
		catch ( OperationCanceledException )
		{
			throw;   // ours; the caller asked
		}
		catch ( Exception e )
		{
			throw new LichessStreamException( 0, "Couldn't reach lichess: " + e.Message );
		}
	}

	/// <summary>Form-encode a body. Hand-rolled because
	/// <c>FormUrlEncodedContent</c> pulls in collection shapes that are not worth
	/// the whitelist risk for four key-value pairs, and because being able to see
	/// the exact bytes matters on an API that compares some of them literally.</summary>
	static string FormEncode( Dictionary<string, string> form )
	{
		var sb = new StringBuilder();
		foreach ( var (k, v) in form )
		{
			if ( sb.Length > 0 ) sb.Append( '&' );
			sb.Append( Uri.EscapeDataString( k ) ).Append( '=' ).Append( Uri.EscapeDataString( v ?? "" ) );
		}
		return sb.ToString();
	}

	/// <summary>Parse a JSON body, or null on any error — never throws into UI
	/// code. Same contract as <see cref="GamchessApi.Deserialize{T}"/>.</summary>
	public static T Parse<T>( string json ) where T : class
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return null;
		try { return JsonSerializer.Deserialize<T>( json ); }
		catch { return null; }
	}
}

/// <summary>A stream that could not be opened. Carries lichess's status so a
/// caller can tell a dead token (401) from a rate limit (429) from a host that
/// isn't there (0).</summary>
public sealed class LichessStreamException : Exception
{
	public int Status { get; }

	public LichessStreamException( int status, string message ) : base( message )
	{
		Status = status;
	}

	/// <summary>The player revoked our grant, or it expired. Nothing retries past
	/// this — they have to link again.</summary>
	public bool Unauthorized => Status == 401;
}

/// <summary>lichess's own error body. Their 400s say useful things and a bare
/// status would throw them away.</summary>
public sealed class LichessErrorBody
{
	public string error { get; set; }
}
