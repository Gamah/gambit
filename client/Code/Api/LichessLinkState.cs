using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// This player's lichess link, cached, and the one thing that polls for it (M8).
///
/// <para>Static rather than a component because there is exactly one local player
/// and their link is a property of them, not of any board: the wall panel, the
/// engaged screen and every table's play button all read the same answer, and
/// none of them should be issuing their own request for it.</para>
///
/// <para><b>Polls only while the lichess station is engaged</b> — never in the
/// background, and never per-frame. Two guards do that: <see cref="Poll"/> is
/// only called from the engaged screen's update, and <see cref="_inFlight"/> is
/// claimed BEFORE the await, which is the <c>LocalGameController.TryArchive</c>
/// lesson — without it, <c>OnUpdate</c> fires a request every frame until the
/// first one returns.</para>
///
/// <para>Once linked, it stops polling: a link doesn't change on its own. Unlink
/// updates the cache directly, and <see cref="Invalidate"/> forces a re-check.</para>
///
/// <para>gamchess unreachable degrades to <see cref="Offline"/> and changes
/// nothing else. Lichess is never required for anything.</para>
/// </summary>
public static class LichessLinkState
{
	/// <summary>How often to ask, while the board is being looked at. The player is
	/// alt-tabbed in a browser completing an OAuth flow, so this decides how long
	/// "linked!" takes to appear — a few seconds is the whole budget.</summary>
	const float PollSeconds = 3f;

	/// <summary>Is this player linked? False until we've been told otherwise.</summary>
	public static bool Linked { get; private set; }

	/// <summary>Their lichess display name, when linked.</summary>
	public static string Username { get; private set; }

	/// <summary>We asked and couldn't reach gamchess. Not an error worth a popup —
	/// the board says so and the game carries on.</summary>
	public static bool Offline { get; private set; }

	/// <summary>Have we ever had an answer? Distinguishes "not linked" from
	/// "haven't asked yet", which the UI shows differently.</summary>
	public static bool Known { get; private set; }

	static bool _inFlight;
	static RealTimeUntil _nextPoll;

	/// <summary>
	/// Ask gamchess whether we're linked, at most every <see cref="PollSeconds"/>.
	/// Safe to call every frame — that is how the engaged screen uses it.
	/// </summary>
	public static void Poll()
	{
		if ( !GamchessAuth.Available ) return;   // no Steam ⇒ no gamchess ⇒ no lichess
		if ( Linked ) return;                    // a link doesn't lapse on its own
		if ( _inFlight || (float)_nextPoll > 0f ) return;

		// Claim before awaiting, or this fires once per frame until the first
		// request lands.
		_inFlight = true;
		_nextPoll = PollSeconds;
		_ = Fetch();
	}

	static async Task Fetch()
	{
		var res = await LichessApi.Status();
		_inFlight = false;

		if ( !res.Ok )
		{
			Offline = true;
			return;
		}

		var link = GamchessApi.Deserialize<LichessLink>( res.Body );
		if ( link == null )
		{
			Offline = true;
			return;
		}

		Offline = false;
		Known = true;
		Linked = link.linked;
		Username = link.username;
	}

	/// <summary>Unlink, and update the cache. The server revokes at lichess and
	/// then forgets the token; we only have to stop claiming to be linked.</summary>
	public static async Task Unlink()
	{
		var res = await LichessApi.Unlink();
		if ( !res.Ok )
		{
			Offline = true;
			return;
		}
		Linked = false;
		Username = null;
		Offline = false;
		// Known stays TRUE: gamchess just told us the link is gone, which is a real
		// answer, not an absence of one. (Polling does resume from here — the gate
		// is Linked, which is now false — so an unlink made in a browser is picked
		// up within a poll. Calling Invalidate() as well would just re-ask
		// immediately for something we were only this moment told.)
		Known = true;
	}

	/// <summary>Force the next <see cref="Poll"/> to really ask.</summary>
	public static void Invalidate()
	{
		_nextPoll = 0f;
		Known = false;
	}

	/// <summary>Drop everything (sign-out).</summary>
	public static void Forget()
	{
		Linked = false;
		Username = null;
		Offline = false;
		Known = false;
		_nextPoll = 0f;
	}
}
