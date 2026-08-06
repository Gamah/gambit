using System;

namespace Gambit.Api.Lichess;

/// <summary>
/// Being a good lichess API citizen — the C# port of the server's
/// <c>internal/lichess/etiquette.go</c>.
///
/// <para><b>What changed with custody, and what did not.</b> Gambit's lichess
/// traffic used to leave one IP — gamchess's — so every player shared one budget
/// and one player mashing a button could earn a 429 for everybody. That is over:
/// each client now spends its own IP on its own games. <b>The rules are
/// unchanged anyway</b>, because they were never only about the shared budget:
/// a 429 is still a request to slow down, an unidentified client is still one
/// lichess cannot attribute or reach, and a retry into a throttle is still how a
/// throttle becomes a ban.</para>
///
/// <para><b>Delete no rule on the grounds that "it's my own budget now."</b> A
/// household, a LAN party or any NAT still shares one IP, and the whole
/// playerbase still shares one <c>clientOrigin</c> that lichess can kill
/// wholesale.</para>
///
/// <para>Sandbox-free: the harness drives the whole state machine, which is what
/// the deleted <c>etiquette_test.go</c> did for the Go copy.</para>
/// </summary>
public static class LichessEtiquette
{
	/// <summary>Identifies Gambit to lichess on every request, streams included.
	///
	/// <para>Deliberately specific: a real name, a URL and a contact. lichess
	/// records a <c>userAgent</c> per access token, so this is how they attribute
	/// our traffic, throttle us, or reach us — exactly what a future conversation
	/// about limits needs. Do not make it generic, and do not reduce it to a
	/// version string.</para>
	///
	/// <para><b>Kept byte-identical to the server's</b> <c>lichess.UserAgent</c>.
	/// Every Gambit request should look like Gambit whether it came from a wall
	/// or from a board.</para>
	///
	/// <para><b>But the CLIENT cannot send it as <c>User-Agent</c>, and no amount
	/// of trying will change that</b> — see <see cref="IdentityHeader"/>. Read
	/// that before "fixing" the header name back.</para></summary>
	public const string UserAgent =
		"TerrysGambit/1.0 (+https://chess.gamah.net; chess in s&box; contact: anthropic@gamah.net)";

	/// <summary>The header <see cref="UserAgent"/> actually travels in, from the
	/// client.
	///
	/// <para><b><c>User-Agent</c> is not settable from a s&amp;box game, twice
	/// over</b>, verified in the shipped engine 2026-08-06: it is in
	/// <c>Http.ForbiddenHeaders</c>, so <c>Http.CreateRequest</c> <b>throws</b>
	/// <c>InvalidOperationException("Not allowed to set header 'User-Agent'")</c>
	/// before the request leaves — which is what broke linking — and even past
	/// that, <c>SboxHttpHandler.HandleRequestAsync</c> unconditionally
	/// <c>Remove</c>s the header and re-adds <c>"facepunch-sbox"</c> on every
	/// send, redirects included. So there is no bypass to find: every request a
	/// client makes reaches lichess as <c>facepunch-sbox</c>. (<c>Referer</c> is
	/// forced the same way, and <c>WebSocket.Connect</c> applies the same list.)
	/// The rule is the engine's, not a policy of ours to argue with, and
	/// <c>TryAddWithoutValidation</c> does not dodge it.</para>
	///
	/// <para>So the client says who it is in a header it IS allowed to set. This
	/// is weaker than a real User-Agent — lichess's own etiquette asks for the
	/// standard header and nothing reads this one — but it is honest and it is
	/// attributable, and the alternative is Gambit traffic that is
	/// indistinguishable from every other s&amp;box game. <b>The SERVER still
	/// sends the real thing</b> (its RoundTripper is unaffected by any of this),
	/// so Gambit's TV traffic is still properly identified; the same string here
	/// is what lets someone reading lichess's logs join the two up.</para>
	///
	/// <para>If this is ever worth fixing properly it is an upstream change —
	/// the same shape as our <c>RequestStreamAsync</c> fix — letting a game
	/// APPEND to the engine's User-Agent rather than replace it. Ask before
	/// building it: the forced UA is deliberate on Facepunch's part.</para></summary>
	public const string IdentityHeader = "X-Gambit-Client";

	/// <summary>lichess: <i>"wait a full minute before resuming API usage"</i>. We
	/// take that literally and apply it to EVERY outbound call, not just the one
	/// that earned it — the limit is per-IP, so a 429 anywhere means this machine
	/// is going too fast.</summary>
	public const double BackoffSeconds = 60;

	/// <summary>lila's <c>Limiters.setupPost</c> — <c>RateLimit[IpAddress](5, 1.minute)</c>.
	/// <b>[SOURCE]</b>, read from lila master 2026-07-15; re-check before relying on it.
	///
	/// <para><b>It keeps its number and loses its reason.</b> It was 5/min because
	/// that is lila's per-IP limit and our whole playerbase shared one IP. Now a
	/// player spends their own — but mashing the button earns a 429 that arms
	/// <i>their own</i> 60-second stop-everything, so refusing locally with a legible
	/// reason is still strictly better than finding out from lichess. And on a shared
	/// IP, 5/min per client is not conservative.</para></summary>
	public const int SeeksPerMinute = 5;

	// A monotonic clock is injected so the harness can drive the window without
	// sleeping a minute. In the game this is RealTime.Now; nothing else may set it.
	static Func<double> _now = () => 0;
	static double _backoffUntil;
	static readonly double[] _seeks = new double[SeeksPerMinute];
	static int _seekCount;

	/// <summary>Point the governor at a clock. Called once at startup by
	/// <see cref="LichessClient"/>; the harness supplies its own.</summary>
	public static void UseClock( Func<double> now )
	{
		_now = now ?? ( () => 0 );
	}

	/// <summary>Forget everything. Tests, and the "try everything again" lever.</summary>
	public static void Reset()
	{
		_backoffUntil = 0;
		_seekCount = 0;
	}

	/// <summary>Seconds until we will talk to lichess again; 0 when ready.</summary>
	public static double BackoffRemaining => Math.Max( 0, _backoffUntil - _now() );

	/// <summary>We are inside the post-429 minute.</summary>
	public static bool BackingOff => BackoffRemaining > 0;

	/// <summary>A 429 landed. Stops EVERY outbound call for the full minute.</summary>
	public static void Note429() => _backoffUntil = _now() + BackoffSeconds;

	/// <summary>Reserve one of the per-minute seek slots.
	///
	/// <para>Returns false and fills <paramref name="reason"/> with something a
	/// player can read. Callers must show that and stop — never retry, which is the
	/// etiquette rule that matters most.</para></summary>
	public static bool TakeSeekSlot( out string reason )
	{
		double now = _now();
		double cutoff = now - 60;

		// Compact the window in place: at most five entries, so a linear pass is
		// cheaper than anything cleverer and allocates nothing.
		int kept = 0;
		for ( int i = 0; i < _seekCount; i++ )
			if ( _seeks[i] > cutoff )
				_seeks[kept++] = _seeks[i];
		_seekCount = kept;

		if ( _seekCount >= SeeksPerMinute )
		{
			int wait = (int)Math.Ceiling( _seeks[0] + 60 - now );
			reason = $"Lichess allows about {SeeksPerMinute} lobby seeks a minute. " +
				$"About {Math.Max( 1, wait )}s to wait.";
			return false;
		}

		_seeks[_seekCount++] = now;
		reason = null;
		return true;
	}
}
