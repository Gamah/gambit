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
/// west wall was already per-client — <c>SpectatorController._featuredIndex</c> is
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

	/// <summary>The result line for the game on the board, or null.
	///
	/// <para><b>This — not <see cref="InFanfare"/> — is what "the game being shown has
	/// finished" means</b>, and the two are not the same window. It is cleared in Poll at
	/// the moment the new state is applied, so it is true for exactly as long as a
	/// finished position is on the board.</para></summary>
	public string FanfareText { get; private set; }

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

	/// <summary>Seconds left for each seat, counted down locally between frames.
	///
	/// <para><b>lichess only sends a clock when a move happens</b>, so a frame's value is
	/// the truth at that instant and nothing arrives until the next move. Reporting it
	/// raw leaves the clock frozen for the whole of someone's think — which on a wall
	/// reads as a broken board, not a thinking player. So we run the side-to-move's clock
	/// down from the last frame and snap both back to whatever the next one says.</para>
	///
	/// <para>lichess stays the only authority: this never invents time, only spends it,
	/// and every frame overwrites it. That direction matters — the house rule is that a
	/// live clock must never read HIGHER than the time actually left (it's why
	/// <see cref="TimeControl.Format"/> truncates where the PGN writer rounds), and
	/// counting down from a known-good value can only ever read low. It drifts low by
	/// about the network latency, and corrects on every move.</para></summary>
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
		if ( TickingSeat != seat ) return bank;
		return MathF.Max( 0f, bank - (float)_sinceBank );
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
		var res = await LichessTvApi.PollChannel( asked, _version );
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
		// Match on the id: gamchess reports the last ending until the next one, so "a
		// game ended" is not news — "the game on MY board ended" is. And once per game:
		// last_game_id goes on matching after the hold expires, so without
		// _fanfareShownFor the fanfare would re-arm on the next poll and the wall would
		// never move on.
		if ( !string.IsNullOrEmpty( st.last_game_id )
			&& st.last_game_id == _gameId
			&& st.last_game_id != _fanfareShownFor )
		{
			_fanfareShownFor = st.last_game_id;
			FanfareText = LichessTv.ResultLine( st.last_status, st.last_winner );
			_fanfareUntil = LichessTv.FanfareSeconds;
			StatusText = null;
		}

		// Hold the finished position. We keep POLLING (that's what tells gamchess someone
		// is here) but apply nothing — and because gamchess keeps only the latest state,
		// there is no queue piling up behind this. When the hold ends we take whatever is
		// current, having skipped the moves in between by construction.
		if ( InFanfare ) return;

		// The hold is over (or never started): the fanfare is history.
		FanfareText = null;

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
		// TickingSeat null stops ClockFor counting down against a bank of 0 anyway, but
		// clearing it is what makes "no position" mean no clock rather than 0:00.
		TickingSeat = null;

		// There's no position, so there's nothing to hold on. Dropping _gameId matters
		// most: it's what the next poll's last_game_id is matched against, and a stale
		// one would announce the ending of a game we are no longer showing.
		_gameId = null;
		FanfareText = null;
		_fanfareUntil = 0f;
		_fanfareShownFor = null;
	}
}
