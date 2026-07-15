using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>Where a gamchess sign-in attempt has got to.</summary>
public enum GamchessSignInPhase
{
	Idle,
	Starting,           // registering our state with gamchess
	WaitingForBrowser,  // authorize URL is up; polling /code
	Exchanging,         // got the code; swapping it for a token AT LICHESS
	Done,
	Failed,
}

/// <summary>
/// Paste-free lichess sign-in via gamchess (issue #7, the point of M7).
///
/// <para>The dance, and why each step is where it is:</para>
/// <list type="number">
/// <item>We generate a high-entropy <c>state</c> and a PKCE verifier. Only the
/// state goes to gamchess; the verifier never leaves this machine.</item>
/// <item><c>POST /begin</c> registers state → our FP-verified SteamID, and returns
/// the exact redirect_uri to use.</item>
/// <item>The player opens the authorize URL and approves. lichess redirects their
/// browser to gamchess's /callback, which parks the code against our SteamID and
/// shows a neutral page — it never displays the code.</item>
/// <item>We poll <c>GET /code</c>, authenticated as that SteamID, and get the code
/// exactly once.</item>
/// <item>We exchange the code for a bearer <b>at lichess</b>, with the verifier only
/// we hold, then converge on <see cref="LichessAuth.SignInWithToken"/> — the same
/// storage path the paste flow uses. gamchess never sees the token.</item>
/// </list>
///
/// <para><b>The accepted cost:</b> the game must be running to sign in, because the
/// client holds the verifier. Sign-in starts from the game anyway, so this isn't a
/// real constraint — it just means the website can't sign you into lichess alone.</para>
///
/// <para><b>Still one paste, and there always will be:</b> s&amp;box has no API to
/// open a browser (CLAUDE.md), so the player copies the authorize URL. What M7
/// removes is the <i>return</i> paste — you no longer hand a redirect URL back to
/// the game. Fully-automatic OAuth is impossible here and this doesn't change that.</para>
///
/// <para>Polling is driven from a UI <c>OnUpdate</c> via <see cref="Tick"/> rather
/// than an async delay loop, matching how the rest of the codebase waits.</para>
/// </summary>
public static class GamchessSignIn
{
	/// <summary>Seconds between /code polls. The endpoint 404s until the browser
	/// comes back, which is cheap and expected.</summary>
	const float PollSeconds = 2f;

	/// <summary>Give up after this long. Kept under gamchess's 5-minute state TTL so
	/// we fail with a clear message rather than silently polling a dead state.</summary>
	const float TimeoutSeconds = 4.5f * 60f;

	public static GamchessSignInPhase Phase { get; private set; } = GamchessSignInPhase.Idle;

	/// <summary>The lichess authorize URL for the player to open. No browser-open
	/// API exists, so the UI offers this as click-to-copy.</summary>
	public static string AuthorizeUrl { get; private set; }

	/// <summary>User-facing failure text, or null.</summary>
	public static string Error { get; private set; }

	public static bool Active =>
		Phase is GamchessSignInPhase.Starting
			or GamchessSignInPhase.WaitingForBrowser
			or GamchessSignInPhase.Exchanging;

	/// <summary>Is the paste-free path even possible? False on a non-Steam build (no
	/// FP token to authenticate the poll with) — the UI then offers only the paste
	/// flows, which always work.</summary>
	public static bool Supported => GamchessAuth.Available;

	static RealTimeUntil _nextPoll;
	static RealTimeUntil _expires;
	static bool _pollInFlight;

	/// <summary>Kick off a sign-in. Fire-and-forget; watch <see cref="Phase"/>.</summary>
	public static void Begin() => _ = DoBegin();

	static async Task DoBegin()
	{
		if ( Active ) return;

		Error = null;
		AuthorizeUrl = null;
		Phase = GamchessSignInPhase.Starting;

		if ( !Supported )
		{
			Fail( "No Steam identity on this build — paste a token instead." );
			return;
		}

		// The state must be registered with gamchess BEFORE lichess can redirect,
		// or the callback resolves to nobody and the code is dropped.
		var state = LichessOAuth.RandomState();

		var res = await GamchessApi.LichessBegin( state );
		if ( !res.Ok )
		{
			// gamchess unreachable is the expected failure, not an exceptional one:
			// the paste flows still work, so say so rather than sounding broken.
			Fail( GamchessApi.Unreachable
				? "Can't reach the Gambit server — paste a token instead."
				: res.Error ?? "Couldn't start sign-in." );
			return;
		}

		var begin = GamchessApi.Deserialize<GamchessBeginResponse>( res.Body );
		if ( begin == null || string.IsNullOrEmpty( begin.redirect_uri ) )
		{
			Fail( "The Gambit server sent an unexpected reply." );
			return;
		}

		// Use the server's redirect_uri verbatim — lichess compares it byte-for-byte
		// at authorize and at exchange.
		LichessOAuth.RedirectUri = begin.redirect_uri;
		AuthorizeUrl = LichessOAuth.Start( state );

		_expires = TimeoutSeconds;
		_nextPoll = PollSeconds;
		Phase = GamchessSignInPhase.WaitingForBrowser;
	}

	/// <summary>Drive the poll. Call every frame from a UI <c>OnUpdate</c>; it
	/// rate-limits itself and is a no-op unless we're waiting.</summary>
	public static void Tick()
	{
		if ( Phase != GamchessSignInPhase.WaitingForBrowser ) return;

		if ( (float)_expires <= 0f )
		{
			Fail( "Sign-in timed out — start again." );
			return;
		}
		if ( _pollInFlight || (float)_nextPoll > 0f ) return;

		_nextPoll = PollSeconds;
		_ = Poll();
	}

	static async Task Poll()
	{
		_pollInFlight = true;
		try
		{
			var res = await GamchessApi.LichessCode();

			// 404 is the normal "browser hasn't come back yet" — keep waiting.
			if ( res.NotFound ) return;
			if ( !res.Ok )
			{
				// Don't kill the attempt on a transient blip; the timeout is the
				// backstop. A dead server is already handled by the circuit breaker.
				return;
			}

			var payload = GamchessApi.Deserialize<GamchessCodeResponse>( res.Body );
			if ( payload == null || string.IsNullOrEmpty( payload.code ) ) return;

			Phase = GamchessSignInPhase.Exchanging;

			// The exchange runs HERE, with the verifier that never left this machine.
			var (ok, error) = await LichessOAuth.CompleteWithCode( payload.code );
			if ( !ok )
			{
				Fail( error ?? "Couldn't finish sign-in." );
				return;
			}

			// Record the link so the archive can show a lichess name beside the
			// SteamID. Best-effort: we are signed in either way, and failing here
			// must not undo that.
			await LinkBestEffort();

			Phase = GamchessSignInPhase.Done;
			Log.Info( $"[Gambit] signed in to lichess as {LichessAuth.Username} (via gamchess, no paste)" );
		}
		finally
		{
			_pollInFlight = false;
		}
	}

	static async Task LinkBestEffort()
	{
		var username = LichessAuth.Username;
		if ( string.IsNullOrEmpty( username ) ) return;

		var res = await GamchessApi.PutLichessLink( username );
		if ( res.Ok ) return;

		// 409: someone else already linked this lichess account. Worth saying out
		// loud — it's the one failure here a human might need to act on — but it
		// still doesn't affect being signed in.
		Log.Info( res.Status == 409
			? "[Gambit] that lichess account is already linked to another Steam account — archive link skipped"
			: $"[Gambit] couldn't record the lichess link (archive only): {res.Error}" );
	}

	/// <summary>Abandon the attempt.</summary>
	public static void Cancel()
	{
		LichessOAuth.Cancel();
		Reset();
	}

	/// <summary>Back to Idle, and put the redirect back to the paste flow's
	/// localhost value so the fallback isn't left pointing at gamchess.</summary>
	public static void Reset()
	{
		Phase = GamchessSignInPhase.Idle;
		AuthorizeUrl = null;
		Error = null;
		_pollInFlight = false;
		LichessOAuth.RedirectUri = LichessOAuth.LocalhostRedirectUri;
	}

	static void Fail( string error )
	{
		LichessOAuth.Cancel();
		Phase = GamchessSignInPhase.Failed;
		AuthorizeUrl = null;
		Error = error;
		// Leave the redirect pointing back at the paste flow — a failed gamchess
		// attempt must not poison the fallback.
		LichessOAuth.RedirectUri = LichessOAuth.LocalhostRedirectUri;
		Log.Info( "[Gambit] gamchess sign-in failed: " + error );
	}
}
