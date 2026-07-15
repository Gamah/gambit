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

		Fen = st.fen;
		LastMoveUci = st.last_move_uci;
		WhiteName = string.IsNullOrEmpty( st.white_name ) ? "White" : st.white_name;
		BlackName = string.IsNullOrEmpty( st.black_name ) ? "Black" : st.black_name;
		WhiteTitle = st.white_title;
		BlackTitle = st.black_title;
		WhiteRating = st.white_rating;
		BlackRating = st.black_rating;

		// Better data has arrived: snap both clocks to it and restart the local
		// countdown from now. Every frame does this, so local drift can never accumulate
		// past one move.
		_whiteBank = st.white_clock;
		_blackBank = st.black_clock;
		_sinceBank = 0f;

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
	}
}
