using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// One lichess TV channel, polled from gamchess for this client (M9).
///
/// <para><b>Per-client, and that is the existing pattern rather than a new one.</b> The
/// north wall was already per-client — <c>SpectatorController._featuredIndex</c> is
/// local, so two players at that wall already see different tables. This just extends
/// the cycle with a source that happens to come over HTTP.</para>
///
/// <para>Not a <see cref="IBoardGame"/>: that seam is for boards you can PLAY at, and
/// TV is spectate-only. It has no seat, no turn, and nothing to submit.</para>
///
/// <para><b>Nothing here is authoritative and nothing here is required.</b> It runs no
/// clock and adjudicates nothing — lichess is the only authority, and the clocks
/// arrive with each frame. If gamchess is down, every property stays empty and the
/// wall falls back to mirroring real tables, which is its original job.</para>
/// </summary>
public sealed class LichessTvSource
{
	/// <summary>Between polls after a failure. The gamchess breaker only opens on
	/// 5xx/transport errors, so a 4xx returns INSTANTLY — without this the poll loop
	/// re-fires next frame and we'd hammer gamchess hundreds of times a second. The
	/// same guard <see cref="LichessGameController"/> needs, for the same reason.</summary>
	const float PollBackoffSeconds = 3f;

	public string Channel { get; private set; } = LichessTv.DefaultChannel;

	/// <summary>Channels gamchess has told us (with a 404) it will not serve.
	///
	/// <para>Load-bearing, not a cache. The wall re-asserts the desired channel EVERY
	/// frame from the player's saved pick, so without a memory of the refusal the
	/// fallback below is undone before the next poll — and the result is a permanent
	/// 3s-backoff loop against a dead channel with the board flickering to "connecting".
	/// Remembering the refusal is what makes "the server wins" actually stick.</para></summary>
	readonly HashSet<string> _rejected = new();

	/// <summary>How long to stop trying after gamchess refuses even the default channel.
	/// Self-healing rather than permanent: the likeliest cause is a gamchess older than
	/// the TV routes, and a deploy shouldn't need the player to restart. Mirrors
	/// <c>GamchessApi.SessionMintRetrySeconds</c>, for the same reason.</summary>
	const float UnavailableRetrySeconds = 120f;

	RealTimeUntil _unavailableUntil;

	/// <summary>gamchess serves no channel we know how to ask for — including the
	/// default. Nothing left to try, so stop polling rather than loop.</summary>
	public bool Unavailable => (float)_unavailableUntil > 0f;

	public string Fen { get; private set; }
	public string LastMoveUci { get; private set; }
	public string WhiteName { get; private set; }
	public string BlackName { get; private set; }
	public string WhiteTitle { get; private set; }
	public string BlackTitle { get; private set; }
	public int WhiteRating { get; private set; }
	public int BlackRating { get; private set; }

	/// <summary>The game currently on the board. Tracked so we can tell when gamchess
	/// says the game we're SHOWING has ended, rather than some other one.</summary>
	string _gameId;

	// ── The fanfare ──
	//
	// lichess TV cuts to the next game the instant one ends. On a wall that reads as a
	// glitch: the result never appears, the pieces just jump to a new position. So when
	// the game we're showing ends, we stop on it for FanfareSeconds with a line saying
	// how it went, and only then move on.
	//
	// We KEEP POLLING throughout — polling is what tells gamchess someone is watching,
	// so pausing it would let the upstream drop just because a game ended. The updates
	// simply aren't applied.
	//
	// And there is no buffer to grow. gamchess keeps only the LATEST state per channel
	// (one slot, overwritten), so "hold for 3s then take whatever's current" costs
	// nothing and skips whatever happened in between by construction. Nothing to bound,
	// nothing to speed up, nothing to drain — the relay already abandons all but the
	// latest, which is the behaviour we'd otherwise have had to build.

	RealTimeUntil _fanfareUntil;

	/// <summary>The game we've already shown the fanfare for.
	///
	/// <para>Needed because gamchess reports the last ending until the NEXT one, so
	/// <c>last_game_id</c> keeps matching our frozen <see cref="_gameId"/> after the hold
	/// expires — and the fanfare would re-arm on the very next poll, forever, and the
	/// wall would never advance past the finished game.</para></summary>
	string _fanfareShownFor;

	/// <summary>The result line for the game on the board, or null — the one-line form,
	/// for callers that have a line rather than a banner.
	///
	/// <para><b>This — not <see cref="InFanfare"/> — is what "the game being shown has
	/// finished" means</b>, and the two are not the same window. It is cleared in Poll at
	/// the moment the new state is applied, so it is true for exactly as long as a
	/// finished position is on the board.</para></summary>
	public string FanfareText { get; private set; }

	/// <summary>"White wins" — the banner's first line.</summary>
	public string FanfareHeadline { get; private set; }

	/// <summary>"out of time" — the banner's second line, or null when there's no reason
	/// to give (an abort, or a result we couldn't fetch).</summary>
	public string FanfareReason { get; private set; }

	/// <summary>Is the finished game on the board right now? Anything that must not treat
	/// a dead game as live — a running clock, a ticking highlight — asks this.</summary>
	public bool ShowingFinished => FanfareText != null;

	/// <summary>Are we still holding, i.e. must we refuse to apply new state?
	///
	/// <para><b>Narrower than <see cref="ShowingFinished"/>, deliberately.</b> The hold
	/// expiring does not put the new game on the board — only a poll landing does, and
	/// that is up to <c>pollHold</c> (5s) away. Using this to decide "is the game over"
	/// leaves a gap where the board still shows the finished position while the clock
	/// resumes draining and the result line vanishes: the exact thing the fanfare exists
	/// to prevent, moved a few seconds later.</para></summary>
	public bool InFanfare => FanfareText != null && (float)_fanfareUntil > 0f;

	// The last clocks lichess told us, in SECONDS (the TV feed's unit; the Board API
	// sends the same idea in ms), and when they landed.
	int _whiteBank, _blackBank;
	RealTimeSince _sinceBank;

	/// <summary>How stale the banked clocks already were when we got them, in seconds.
	/// Subtracted from the ticking seat — see <see cref="ClockFor"/>.</summary>
	float _bankLag;

	/// <summary>Seconds left for each seat, counted down locally between frames.
	///
	/// <para><b>lichess only sends a clock when a move happens</b>, so a frame's value is
	/// the truth at that instant and nothing arrives until the next move. Reporting it
	/// raw leaves the clock frozen for the whole of someone's think — which on a wall
	/// reads as a broken board, not a thinking player. So we run the side-to-move's clock
	/// down from the last frame and snap both back to whatever the next one says.</para>
	///
	/// <para><b>The house rule: a live clock must never read HIGHER than the time actually
	/// left.</b> It is why <see cref="TimeControl.Format"/> truncates where the PGN writer
	/// rounds, and reading LOW is explicitly permitted where reading high is not.</para>
	///
	/// <para><b>This code broke that rule for two milestones, and the reason it gave was
	/// written in three places.</b> The claim was that "counting down from a known-good
	/// value can only ever read low", so it drifts low by about the network latency. That
	/// fails at its first step: <b>the value is already stale on arrival.</b> lichess
	/// stamps the clock at the move instant T0; the frame reaches us at T0+L; the code
	/// banked it and zeroed its age, so at wall-clock T0+L we displayed T0's value while
	/// the player had already burned L. Displayed = true + L. It read HIGH, by the whole
	/// chain, until the next move corrected it. Anything that re-derives a clock here must
	/// reason from <b>when the value was stamped</b>, never from when we received it.</para>
	///
	/// <para><b>What <see cref="_bankLag"/> now removes, and what it cannot.</b> gamchess
	/// reports <c>clock_age_ms</c> (how long the value sat with IT) and <c>hold_ms</c> (how
	/// long it sat on our request), so we can subtract its staleness plus our own measured
	/// network time. <b>The lichess → gamchess leg survives by construction</b> — nothing
	/// downstream of lichess knows T0, and no client-side fix can invent it. So a small
	/// residual high bias remains, and it is documented rather than denied. Its magnitude
	/// has never been measured; only its direction is certain.</para>
	///
	/// <para>The network estimate deliberately uses the FULL round trip rather than half
	/// of it. An unbiased estimate would be wrong in both directions; the house rule is
	/// one-directional, so a deliberate undershoot satisfies it where a fair guess would
	/// not. Erring low is free, and erring high is the bug.</para></summary>
	public float WhiteClock => ClockFor( ChessSeat.White );
	public float BlackClock => ClockFor( ChessSeat.Black );

	float ClockFor( ChessSeat seat )
	{
		float bank = seat == ChessSeat.White ? _whiteBank : _blackBank;
		// A finished game's clock does not run. ShowingFinished, not InFanfare: the hold
		// can expire seconds before a poll actually replaces the position, and the clock
		// must not spring back to life on a dead game in the meantime.
		if ( ShowingFinished ) return bank;
		// Only the side to move is spending time. Nothing to run down before the first
		// frame either — TickingSeat is null until then.
		//
		// The lag applies ONLY here, and that is not an optimisation: the idle side's
		// clock is not running, so however stale the frame is, their bank is still
		// exactly right. Subtracting the lag from both would invent a loss of time that
		// never happened — and on a wall showing a 60s bullet game, visibly.
		if ( TickingSeat != seat ) return bank;
		return MathF.Max( 0f, bank - _bankLag - (float)_sinceBank );
	}

	public ChessSeat? TickingSeat { get; private set; }

	/// <summary>Why there's nothing to show, or null. Never fatal.</summary>
	public string StatusText { get; private set; } = "Connecting to lichess TV…";

	public bool HasPosition => !string.IsNullOrEmpty( Fen );

	ulong _version;
	bool _pollInFlight;
	RealTimeUntil _pollBackoff;

	/// <summary>Point at a different channel — a REQUEST, not a command: a channel
	/// gamchess has already refused is silently swapped for the default.
	///
	/// <para>Safe to call every frame with the same value; only a real change does
	/// anything. A real change drops everything, because the old channel's position
	/// belongs to a different game and showing it under the new channel's name would be
	/// a lie.</para></summary>
	public void SetChannel( string channel )
	{
		channel = LichessTv.Coerce( channel );

		// gamchess has already 404'd this one. Honour that rather than let the caller
		// re-assert it forever — the server is the authority on what it serves.
		if ( _rejected.Contains( channel ) )
			channel = LichessTv.DefaultChannel;

		if ( channel == Channel ) return;

		Channel = channel;
		_version = 0;
		Clear();
		StatusText = $"Connecting to lichess TV ({LichessTv.Label( channel )})…";
	}

	/// <summary>Nobody is watching any more. Idempotent; safe to call every frame.
	///
	/// <para><b>Resetting the version is the point.</b> Our <c>since</c> is only meaningful
	/// against the channel gamchess currently holds — and when the last watcher leaves,
	/// gamchess drops the upstream and the NEXT one gets a fresh channel whose version
	/// starts at 1. Come back holding <c>since=500</c> and we'd ask it to tell us about
	/// version 501 of a counter that just went back to 1: we'd sit through hold after
	/// hold waiting for a number that takes hundreds of moves to arrive. Starting from 0
	/// asks for "whatever is on now", which is the only sensible question after a gap.</para>
	///
	/// <para>Clearing the position matters too: it's frozen the moment we stop polling,
	/// and a frozen game is indistinguishable from a live one on a board this size.</para></summary>
	public void StopWatching()
	{
		_version = 0;
		if ( HasPosition ) Clear();
		StatusText = null;
	}

	/// <summary>Call every frame while this source IS being watched.
	///
	/// <para>Polling IS the watch signal — there is no register/unregister to get wrong.
	/// Stop calling this and the polls stop, and gamchess drops its upstream after its
	/// own idle TTL. That is what keeps a wall nobody is looking at from costing lichess
	/// a held stream.</para></summary>
	public void Tick()
	{
		if ( Unavailable ) return;
		if ( _pollInFlight ) return;
		if ( (float)_pollBackoff > 0f ) return;

		_pollInFlight = true;
		_ = Poll();
	}

	async Task Poll()
	{
		// Capture the channel we're asking about: SetChannel can land while this is in
		// flight, and applying a blitz frame to a rapid channel would be a real bug.
		var asked = Channel;

		// Time our own round trip. Most of a long poll's round trip is gamchess WAITING,
		// not the network — so this is only half the measurement; hold_ms in the answer
		// is the other half. See _bankLag.
		RealTimeSince sent = 0f;
		var res = await LichessTvApi.PollChannel( asked, _version );
		_lastRoundTrip = (float)sent;
		_pollInFlight = false;

		if ( asked != Channel ) return; // the player cycled mid-poll; the answer is stale

		if ( !res.Ok )
		{
			_pollBackoff = PollBackoffSeconds;

			// A 404 means gamchess doesn't serve this channel — our list disagrees with
			// its allowlist. The server wins: remember the refusal (so the wall can't
			// re-assert it next frame) and fall back.
			if ( res.NotFound )
			{
				_rejected.Add( asked );
				Log.Warning( $"[Gambit] gamchess doesn't serve TV channel '{asked}'" );

				if ( _rejected.Contains( LichessTv.DefaultChannel ) )
				{
					// Even the default is refused — this gamchess serves no TV we can ask
					// for (most likely one older than the TV routes). Nothing to fall back
					// to, so stop rather than loop, but only for a while: forget the
					// refusals so a deploy is picked up without restarting the game.
					_unavailableUntil = UnavailableRetrySeconds;
					_rejected.Clear();
					Clear();
					StatusText = "lichess TV isn't available here.";
					return;
				}

				SetChannel( LichessTv.DefaultChannel );
				return;
			}

			Clear();
			StatusText = GamchessApi.Unreachable
				? "lichess TV is offline."
				: "Waiting for lichess TV…";
			return;
		}

		var st = GamchessApi.Deserialize<TvState>( res.Body );
		if ( st == null ) return;

		// Did anything actually happen, or did the poll just time out?
		//
		// A long poll that reaches its hold answers with the CURRENT state at the SAME
		// version — a real answer, not an error, and one that arrives every ~5s through
		// any think. gamchess only bumps the version when the state really changes, so
		// this is exactly the "is this news" test. `!=` rather than `>`: a channel that
		// was dropped and reopened restarts its version at 1, and that is news too.
		bool advanced = st.version != _version;
		_version = st.version;

		if ( !string.IsNullOrEmpty( st.error ) )
		{
			Clear();
			StatusText = st.error;
			return;
		}
		if ( string.IsNullOrEmpty( st.fen ) )
		{
			StatusText = "Waiting for lichess TV…";
			return;
		}

		// Did the game WE are showing just end?
		//
		// WE work that out, from the featured game changing away from the one on our
		// board. Nothing else can mean that, and it needs nothing from the server.
		//
		// The first version asked gamchess instead — it fired only when `last_game_id`
		// came back matching — which quietly made the whole feature depend on the server
		// half being deployed. Against a gamchess that predates it, `last_game_id` is
		// never sent, the condition is never true, and the wall silently never announces
		// anything. That is exactly what happened, and it was invisible because a
		// fanfare that never fires looks identical to a fanfare that isn't wired up.
		//
		// The server's contribution is now the REASON only, and it degrades: no result,
		// or a result for some other game, and we still say the game ended — just not how.
		//
		// Once per game (_fanfareShownFor keyed on the game that ENDED): the ids go on
		// differing for the whole hold, so without it the fanfare re-arms every poll and
		// the wall never moves on. And `_gameId` non-empty is what stops the very first
		// featured — a game we never showed — announcing an ending.
		bool endedOnOurBoard = !string.IsNullOrEmpty( _gameId )
			&& !string.IsNullOrEmpty( st.game_id )
			&& st.game_id != _gameId
			&& _gameId != _fanfareShownFor;

		if ( endedOnOurBoard )
		{
			// Only trust the result if it is THIS game's. gamchess reports the last
			// ending it saw, which after a missed poll may be a different game entirely.
			bool haveResult = st.last_game_id == _gameId;
			_fanfareShownFor = _gameId;

			var status = haveResult ? st.last_status : null;
			var winner = haveResult ? st.last_winner : null;
			LichessTv.Result( status, winner, out var headline, out var reason );
			FanfareHeadline = headline;
			FanfareReason = reason;
			FanfareText = reason == null ? headline : $"{headline} — {reason}";

			_fanfareUntil = LichessTv.FanfareSeconds;
			StatusText = null;

			Log.Info( $"[Gambit] lichess TV: {_gameId} ended — {FanfareText}"
				+ ( haveResult ? "" : " (gamchess sent no result for it)" ) );
		}

		// The reason arrives AFTER the swap now. gamchess publishes the featured swap the
		// instant it sees it and fetches how the old game ended in the background, so the
		// first frame of an ending carries an empty status ("Game over") and a later one
		// fills it in. While we're still holding on THIS ended game, adopt the better line
		// when it lands — otherwise the fanfare would keep the bare "Game over" for the
		// whole hold even though lichess told us how it went a beat later.
		if ( InFanfare && _gameId == _fanfareShownFor && st.last_game_id == _gameId
			&& !string.IsNullOrEmpty( st.last_status ) )
		{
			LichessTv.Result( st.last_status, st.last_winner, out var lateHead, out var lateReason );
			FanfareHeadline = lateHead;
			FanfareReason = lateReason;
			FanfareText = lateReason == null ? lateHead : $"{lateHead} — {lateReason}";
		}

		// Hold the finished position. We keep POLLING (that's what tells gamchess someone
		// is here) but apply nothing — and because gamchess keeps only the latest state,
		// there is no queue piling up behind this. When the hold ends we take whatever is
		// current, having skipped the moves in between by construction.
		if ( InFanfare ) return;

		// The hold is over (or never started): the fanfare is history.
		ClearFanfare();

		// Read BEFORE _gameId is overwritten below — comparing it afterwards would compare
		// the id to itself and be false forever. See the clock snap.
		bool newGame = st.game_id != _gameId;

		_gameId = st.game_id;
		Fen = st.fen;
		LastMoveUci = st.last_move_uci;
		WhiteName = string.IsNullOrEmpty( st.white_name ) ? "White" : st.white_name;
		BlackName = string.IsNullOrEmpty( st.black_name ) ? "Black" : st.black_name;
		WhiteTitle = st.white_title;
		BlackTitle = st.black_title;
		WhiteRating = st.white_rating;
		BlackRating = st.black_rating;

		// Snap the clocks ONLY on real news, and this guard is the whole feature.
		//
		// Re-snapping on a timed-out poll would reset the countdown to a value that is
		// already 5 seconds stale — so the clock would tick down for 5s, jump back UP to
		// where it started, and do it again forever. That sawtooth is worse than the
		// frozen clock this replaced: it makes the clock read HIGHER than the time
		// actually left, which is the one direction a live clock must never go.
		//
		// `newGame` (read above, before _gameId moved) is not belt-and-braces. Through a
		// fanfare we keep polling and keep consuming versions while applying nothing, so
		// by the time the hold ends `advanced` may well be false — and the new game would
		// inherit the finished game's clocks and hold them until its first move. A
		// different game is always news, whatever the version says.
		if ( advanced || newGame )
		{
			_whiteBank = st.white_clock;
			_blackBank = st.black_clock;
			_bankLag = LagOf( st );
			_sinceBank = 0f;
		}

		TickingSeat = st.ticking_seat switch
		{
			"white" => ChessSeat.White,
			"black" => ChessSeat.Black,
			_ => null,
		};
		StatusText = null;
	}

	/// <summary>How stale these clocks already were when they reached us, in seconds:
	/// gamchess's own staleness plus our network time.
	///
	/// <para>Network time is the round trip MINUS the hold, because a long poll's round
	/// trip is mostly gamchess waiting for something to happen. Reading the hold as
	/// latency would subtract up to 5 seconds from the clock.</para>
	///
	/// <para>Uses the full remaining round trip rather than halving it for the downstream
	/// leg. That over-subtracts, deliberately: the house rule is one-directional, so an
	/// undershoot is free and a fair estimate is a coin-flip on the one outcome that is
	/// forbidden. See <see cref="ClockFor"/>.</para>
	///
	/// <para>Clamped at both ends. Zero when gamchess sends nothing (one that predates
	/// this simply gets the old behaviour instead of a broken clock), and capped at
	/// <see cref="MaxLagSeconds"/> so a pathological measurement can't blank a clock that
	/// has real time on it — a wall reading 0:00 on a live game is its own kind of
	/// lie.</para></summary>
	float LagOf( TvState st )
	{
		float age = st.clock_age_ms / 1000f;
		float hold = st.hold_ms / 1000f;
		float network = MathF.Max( 0f, _lastRoundTrip - hold );
		return Math.Clamp( age + network, 0f, MaxLagSeconds );
	}

	/// <summary>Our last poll's round trip, seconds. Includes gamchess's hold; LagOf
	/// takes that back off.</summary>
	float _lastRoundTrip;

	/// <summary>Ceiling on the staleness correction. Generous — this is a backstop
	/// against a nonsense measurement, not a tuning knob. A real correction is
	/// milliseconds; anything near this means something else is wrong, and eating a
	/// player's whole clock because a poll took a minute would be worse than the bias.</summary>
	const float MaxLagSeconds = 10f;

	void Clear()
	{
		Fen = null;
		LastMoveUci = null;
		WhiteName = null;
		BlackName = null;
		WhiteTitle = null;
		BlackTitle = null;
		WhiteRating = 0;
		BlackRating = 0;
		_whiteBank = 0;
		_blackBank = 0;
		// The lag belongs to the banks it was measured for — cleared with them. Nothing
		// depends on this today (the next state is always a newGame, which re-derives
		// it), but a bank and its staleness are one value in two fields, and clearing
		// only one of them is how that stops being true.
		_bankLag = 0f;
		// TickingSeat null stops ClockFor counting down against a bank of 0 anyway, but
		// clearing it is what makes "no position" mean no clock rather than 0:00.
		TickingSeat = null;

		// There's no position, so there's nothing to hold on. Dropping _gameId matters
		// most: it's what the next poll's ending is matched against, and a stale one
		// would announce the ending of a game we are no longer showing.
		_gameId = null;
		_fanfareShownFor = null;
		ClearFanfare();
	}

	/// <summary>Drop the held result. One method because it is three fields that must
	/// agree — <see cref="ShowingFinished"/> keys on FanfareText, and a headline left
	/// behind would print under the next game's position.</summary>
	void ClearFanfare()
	{
		FanfareText = null;
		FanfareHeadline = null;
		FanfareReason = null;
		_fanfareUntil = 0f;
	}
}
