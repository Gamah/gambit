using System.Threading.Tasks;
using Gambit.Api.Lichess;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// This player's lichess link, as the UI sees it.
///
/// <para><b>"Am I linked?" is now answered LOCALLY</b> (HTTPFIX). The token lives
/// on this machine, so the answer is instant, offline, and cannot be wrong —
/// where it used to be a 3-second poll of gamchess whose first answer was
/// "unknown". <see cref="LichessTokenStore"/> is the authority.</para>
///
/// <para>What is still worth asking gamchess is whether IT agrees, and that is a
/// genuinely different question with a real consequence: the two-seat flow needs
/// gamchess to know your lichess username so it can tell the opposite seat. A
/// disagreement means the directory won't work, so it is surfaced
/// (<see cref="ServerDisagrees"/>) rather than hidden.</para>
///
/// <para>Static rather than a component because there is exactly one local player
/// and their link is a property of them, not of any board: the wall panel, the
/// engaged screen and every table's play button read the same answer, and none of
/// them should be issuing their own request for it.</para>
///
/// <para>gamchess unreachable degrades to <see cref="Offline"/> and changes
/// nothing else — a lichess game runs between this client and lichess, and
/// nothing in that path goes through our backend.</para>
/// </summary>
public static class LichessLinkState
{
	/// <summary>How often to re-check gamchess's opinion, while the board is being
	/// looked at. Nothing waits on this any more, so it is a background
	/// reconciliation rather than the thing that makes "linked!" appear.</summary>
	const float PollSeconds = 10f;

	/// <summary>Is this player linked? Answered from disk — instant, offline, and
	/// true the moment the link finishes.</summary>
	public static bool Linked => LichessTokenStore.Linked;

	/// <summary>Their lichess display name, when linked.</summary>
	public static string Username => LichessTokenStore.Username;

	/// <summary>We asked and couldn't reach gamchess. Not an error worth a popup,
	/// and not a reason to stop playing on lichess.</summary>
	public static bool Offline { get; private set; }

	/// <summary>We hold a token but gamchess has no link for us — so the two-seat
	/// flow can't look up an opponent's username.
	///
	/// <para>Reachable for real: a claim that failed after a successful exchange, a
	/// database restored from before the link, or an unlink done on the web while
	/// the game still holds the key. The board says to link again, which is a
	/// cheap fix for a state that would otherwise fail confusingly at the moment
	/// two people sit down to play.</para></summary>
	public static bool ServerDisagrees { get; private set; }

	/// <summary>The grant on this machine predates the scope set this build asks
	/// for. Re-linking is the only way to widen a lichess token — there are no
	/// refresh tokens.</summary>
	public static bool ScopesAreStale => LichessTokenStore.ScopesAreStale;

	/// <summary>Have we ever had an answer from gamchess? Distinguishes "gamchess
	/// says no" from "haven't asked yet".</summary>
	public static bool Known { get; private set; }

	static bool _inFlight;
	static RealTimeUntil _nextPoll;

	/// <summary>Reconcile with gamchess, at most every <see cref="PollSeconds"/>.
	/// Safe to call every frame — that is how the engaged screen uses it.</summary>
	public static void Poll()
	{
		if ( !GamchessAuth.Available ) return;   // no Steam ⇒ no gamchess
		if ( _inFlight || (float)_nextPoll > 0f ) return;

		// Claim before awaiting, or this fires once per frame until the first
		// request lands — the TryArchive lesson.
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

		var link = GamchessApi.Deserialize<LichessLinkStatus>( res.Body );
		if ( link == null )
		{
			Offline = true;
			return;
		}

		Offline = false;
		Known = true;
		ServerDisagrees = LichessTokenStore.Linked && !link.linked;
	}

	/// <summary>A link just completed. Called by <see cref="LichessLink"/> once the
	/// token is on disk and gamchess has recorded it, so nothing has to wait for a
	/// poll to catch up. Takes no name: the name comes off the token store, which is
	/// the authority, and passing one in would be a second source to disagree.</summary>
	public static void AdoptLink()
	{
		Known = true;
		Offline = false;
		ServerDisagrees = false;
		_nextPoll = PollSeconds;
	}

	/// <summary>Force the next <see cref="Poll"/> to really ask.</summary>
	public static void Invalidate()
	{
		_nextPoll = 0f;
		Known = false;
	}

	/// <summary>Drop everything (unlink, sign-out). Does NOT delete the token —
	/// <see cref="LichessLink.Unlink"/> owns that, and owns revoking it first.</summary>
	public static void Forget()
	{
		Offline = false;
		Known = false;
		ServerDisagrees = false;
		_nextPoll = 0f;
	}
}
