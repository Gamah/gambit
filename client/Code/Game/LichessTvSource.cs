using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// One lichess TV channel, streamed from gamchess over a WebSocket for this client
/// (M9; the client-facing transport became a WebSocket push in M18).
///
/// <para><b>Per-client, and that is the existing pattern rather than a new one.</b> The
/// north wall was already per-client — <c>SpectatorController._featuredIndex</c> is
/// local, so two players at that wall already see different tables. This just extends
/// the cycle with a source that happens to come over a socket.</para>
///
/// <para>gamchess still holds the single lichess ndjson stream and stays the sole
/// token/stream holder — this is NOT the client reading lichess directly. What changed
/// in M18 is only the hop from gamchess to us: it was a version-gated long poll with a
/// clock latency-compensation apparatus bolted on to make a 5s-held poll feel live; it
/// is now a socket that pushes one full, self-contained <see cref="TvState"/> snapshot
/// whenever the channel's state changes — latest-wins, no cursor.</para>
///
/// <para>Not a <see cref="IBoardGame"/>: that seam is for boards you can PLAY at, and
/// TV is spectate-only. It has no seat, no turn, and nothing to submit.</para>
///
/// <para><b>Nothing here is authoritative and nothing here is required.</b> It runs no
/// clock authority and adjudicates nothing — lichess is the only authority, and the
/// clocks arrive with each frame. If gamchess is down, every property stays empty and
/// the wall falls back to mirroring real tables, which is its original job.</para>
/// </summary>
public sealed class LichessTvSource
{
	/// <summary>Between reconnect attempts after a socket drops or a handshake fails.
	/// Guards against a reconnect storm — a refused or flapping host must not have us
	/// dialling every frame.</summary>
	const float ReconnectBackoffSeconds = 3f;

	/// <summary>How many consecutive failed connects before we decide a channel is not
	/// being served, rather than merely blipping.
	///
	/// <para>A WebSocket handshake failure does not cleanly tell us 401 from 404 from a
	/// transient 1006 (see the class notes and PLAN), so — unlike the old long poll,
	/// which had lichess's literal 404 to trust — we infer "not served" from repeated
	/// immediate failures instead of acting on the first one. A few retries first keeps
	/// a single network blip from falling a good channel back to the default.</para></summary>
	const int RejectAfterFailures = 3;

	public string Channel { get; private set; } = LichessTv.DefaultChannel;

	/// <summary>Channels gamchess has (by repeated refusal) shown it will not serve.
	///
	/// <para>Load-bearing, not a cache. The wall re-asserts the desired channel EVERY
	/// frame from the player's saved pick, so without a memory of the refusal the
	/// fallback below is undone before the next connect — and the result is a permanent
	/// backoff loop against a dead channel with the board flickering to "connecting".
	/// Remembering the refusal is what makes "the server wins" actually stick.</para></summary>
	readonly HashSet<string> _rejected = new();

	/// <summary>How long to stop trying after gamchess refuses even the default channel.
	/// Self-healing rather than permanent: the likeliest cause is a gamchess older than
	/// the M18 WebSocket route (its GET handler answers the upgrade with plain JSON, so
	/// the handshake fails for every channel), and a deploy shouldn't need the player to
	/// restart. Mirrors <c>GamchessApi.SessionMintRetrySeconds</c>, for the same reason.</summary>
	const float UnavailableRetrySeconds = 120f;

	RealTimeUntil _unavailableUntil;

	/// <summary>gamchess serves no channel we know how to ask for — including the
	/// default. Nothing left to try, so stop dialling rather than loop.</summary>
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
	// the game we're showing ends, we HOLD on its final position for FanfareSeconds and,
	// once we know how it went, put a two-line WHO-WON / WHY banner over it.
	//
	// Two things are deliberately DECOUPLED. The HOLD starts the instant we see the game
	// ended (the featured id changed) — that's what freezes the position and the clocks.
	// The BANNER only appears once we actually know who won and why, which arrives a beat
	// later (gamchess fetches the result from the game export off the reader's path). We
	// do NOT show a bare "Game over" placeholder in the gap: the presence of the held
	// position is already "a game ended", and a placeholder that then rewrites itself
	// reads worse than a clean position that gains a result line. If the result never
	// arrives, the hold simply expires with no banner and the wall moves on.
	//
	// We KEEP THE SOCKET OPEN throughout — the connection is what tells gamchess someone
	// is watching. Pushes during the hold aren't applied to the board; the LATEST is kept
	// in _latest and revealed when the hold expires. Every message is a full snapshot, so
	// this is one field to hold, not a queue to drain.

	/// <summary>The hold timer. Set to FanfareSeconds the instant we detect the game
	/// ended; while it runs we freeze the finished position and refuse to apply new state.
	/// The hold, NOT the banner, is what "we are showing a finished game" means — the
	/// banner may still be empty (waiting on the result) for the first part of it.</summary>
	RealTimeUntil _holdUntil;

	/// <summary>Are we holding a finished position right now? Freezes the clocks
	/// (<see cref="ClockFor"/>), stops the board advancing (<see cref="Apply"/>), and is
	/// how a caller knows a dead game is on the board even before its result line lands.</summary>
	bool Holding => (float)_holdUntil > 0f;

	/// <summary>The game we've already begun a hold for.
	///
	/// <para>Needed because gamchess reports the last ending until the NEXT one, so
	/// <c>last_game_id</c> keeps matching our frozen <see cref="_gameId"/> after the hold
	/// expires — and the hold would re-arm on the very next push, forever, and the wall
	/// would never advance past the finished game.</para></summary>
	string _fanfareShownFor;

	/// <summary>Whether the banner has already taken a real result, so the per-frame
	/// <see cref="Pump"/> during the hold doesn't re-derive it every frame. Stays false
	/// while the result is still unknown, so a late result is picked up. Reset on clear.</summary>
	bool _fanfareUpgraded;

	/// <summary>The joined result line ("White wins — out of time"), or null while there
	/// is no finished-game result to show — which is BOTH during live play and during the
	/// early part of a hold before the result has arrived. Callers wanting "is a finished
	/// game on the board" must ask <see cref="ShowingFinished"/>, not this: a hold with no
	/// banner yet is still a finished game.</summary>
	public string FanfareText { get; private set; }

	/// <summary>"White wins" — the banner's first line, or null. Never a bare "Game over":
	/// the banner shows only a real result (a winner, a draw, an abort), and stays empty
	/// otherwise.</summary>
	public string FanfareHeadline { get; private set; }

	/// <summary>"out of time" — the banner's second line, or null when there's no reason
	/// to give (an abort, or a result we couldn't fetch).</summary>
	public string FanfareReason { get; private set; }

	/// <summary>Is a finished game on the board right now? True for the whole hold, banner
	/// or not. Anything that must not treat a dead game as live — a running clock, a
	/// ticking highlight — asks this.</summary>
	public bool ShowingFinished => Holding;

	// The last clocks lichess told us, in SECONDS (the TV feed's unit; the Board API
	// sends the same idea in ms), and when they were snapped onto us.
	int _whiteBank, _blackBank;
	RealTimeSince _sinceBank;

	/// <summary>How stale the banked clocks already were when gamchess sent them, in
	/// seconds — <c>age_ms/1000</c> from the snapshot. Subtracted from the ticking seat;
	/// see <see cref="ClockFor"/>.</summary>
	float _ageAtSnap;

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
	/// rounds, and reading LOW is explicitly permitted where reading high is not. M18
	/// leans into that on purpose and keeps the mechanism tiny.</para>
	///
	/// <para><b>Why it stays low for free.</b> A push arrives the instant a move lands, so
	/// a live game's steady-state frames are FRESH — and flooring the displayed second
	/// (which <see cref="TimeControl.Format"/> already does) absorbs the sub-second
	/// transport latency, so the wall reads low without any correction at all. The one
	/// case the floor can't cover is CONNECTING mid-think: gamchess hands the new socket
	/// its stored frame, already <c>age_ms</c> milliseconds stale, and without subtracting
	/// that the clock would read HIGH by the age. So <see cref="_ageAtSnap"/> takes it
	/// back off — the whole of the old <c>clock_age_ms</c> + <c>hold_ms</c> + round-trip
	/// apparatus collapses to this one subtraction, because a push has no hold to measure
	/// and no cursor to reconcile.</para>
	///
	/// <para><b>What survives, and is documented rather than denied.</b> The lichess →
	/// gamchess leg is spent before any of this: nothing downstream of lichess knows the
	/// move-instant T0, so a small residual HIGH bias remains on that leg. <see
	/// cref="ClockLeadSeconds"/> is a fixed deliberate undershoot on top — belt-and-braces
	/// in the one permitted direction, since erring low is free and erring high is the
	/// bug.</para></summary>
	public float WhiteClock => ClockFor( ChessSeat.White );
	public float BlackClock => ClockFor( ChessSeat.Black );

	/// <summary>A fixed undershoot, seconds. Small and one-directional: the house rule
	/// forbids reading high, so shaving a quarter-second off is free insurance against
	/// the irrecoverable lichess→gamchess leg, where a fair estimate would be a coin-flip
	/// on the forbidden outcome. Tunable.</summary>
	const float ClockLeadSeconds = 0.25f;

	float ClockFor( ChessSeat seat )
	{
		float bank = seat == ChessSeat.White ? _whiteBank : _blackBank;
		// A finished game's clock does not run — frozen for the whole hold, banner or not.
		if ( ShowingFinished ) return bank;
		// Only the side to move is spending time. Nothing to run down before the first
		// frame either — TickingSeat is null until then.
		//
		// The staleness applies ONLY here, and that is not an optimisation: the idle
		// side's clock is not running, so however stale the frame is, their bank is still
		// exactly right. Subtracting from both would invent a loss of time that never
		// happened — and on a wall showing a 60s bullet game, visibly.
		if ( TickingSeat != seat ) return bank;
		return MathF.Max( 0f, bank - _ageAtSnap - ClockLeadSeconds - (float)_sinceBank );
	}

	public ChessSeat? TickingSeat { get; private set; }

	/// <summary>Why there's nothing to show, or null. Never fatal.</summary>
	public string StatusText { get; private set; } = "Connecting to lichess TV…";

	public bool HasPosition => !string.IsNullOrEmpty( Fen );

	// ── The socket ──

	WebSocket _ws;
	bool _connecting;
	RealTimeUntil _reconnectBackoff;
	int _connectFailures;

	/// <summary>The latest snapshot received, and whether it has been reflected onto the
	/// board yet. A push arrives on any thread the engine marshals the event to (the game
	/// main thread), stores it here, and lets <see cref="Pump"/> apply it — which lets a
	/// fanfare hold BUFFER the latest without losing it, and lets a hold that expires
	/// while the game is quiet still reveal the next position on the next Tick.</summary>
	TvState _latest;
	bool _latestApplied = true;

	/// <summary>Point at a different channel — a REQUEST, not a command: a channel
	/// gamchess has already refused is silently swapped for the default.
	///
	/// <para>Safe to call every frame with the same value; only a real change does
	/// anything. A real change drops the socket and everything else, because the old
	/// channel's position belongs to a different game and showing it under the new
	/// channel's name would be a lie. A fresh socket always gets the current snapshot
	/// first, so there is no cursor to reset.</para></summary>
	public void SetChannel( string channel )
	{
		channel = LichessTv.Coerce( channel );

		// gamchess has already refused this one. Honour that rather than let the caller
		// re-assert it forever — the server is the authority on what it serves.
		if ( _rejected.Contains( channel ) )
			channel = LichessTv.DefaultChannel;

		if ( channel == Channel ) return;

		Channel = channel;
		DisposeSocket();
		_connectFailures = 0;
		_reconnectBackoff = 0f; // reconnect to the new channel promptly
		Clear();
		StatusText = $"Connecting to lichess TV ({LichessTv.Label( channel )})…";
	}

	/// <summary>Nobody is watching any more. Idempotent; safe to call every frame.
	///
	/// <para><b>Dropping the socket is the point.</b> The connection IS the watch signal:
	/// closing it is what tells gamchess we left, so it can drop the upstream after its
	/// linger. Clearing the position matters too — it's frozen the moment we stop, and a
	/// frozen game is indistinguishable from a live one on a board this size.</para></summary>
	public void StopWatching()
	{
		DisposeSocket();
		_reconnectBackoff = 0f;
		if ( HasPosition ) Clear();
		StatusText = null;
	}

	/// <summary>Call every frame while this source IS being watched.
	///
	/// <para>Ticking is the watch signal — stop calling this and the socket is disposed
	/// (below) and gamchess drops its upstream after its own linger. Two jobs each frame:
	/// keep a connection up (dialling when there isn't one and the backoff has elapsed),
	/// and pump any buffered snapshot onto the board — the latter is what lets a fanfare
	/// hold that expires during a quiet moment still reveal the next game.</para></summary>
	public void Tick()
	{
		// Advance a held fanfare / apply a buffered snapshot even when no push is arriving.
		Pump();

		if ( Unavailable ) return;
		if ( _connecting ) return;
		if ( _ws is { IsConnected: true } ) return;
		if ( (float)_reconnectBackoff > 0f ) return;

		_ = Connect();
	}

	async Task Connect()
	{
		_connecting = true;
		var asked = Channel;
		WebSocket ws = null;
		try
		{
			var (bearer, steamId) = await GamchessApi.WsCredentials();
			if ( string.IsNullOrEmpty( bearer ) )
			{
				// No Steam credentials — can't authenticate the socket. Back off and let
				// Tick try again; this is a degrade to "unavailable for now", never a crash.
				_reconnectBackoff = ReconnectBackoffSeconds;
				StatusText = "lichess TV needs Steam.";
				return;
			}
			if ( asked != Channel ) return; // the player cycled while we fetched credentials

			var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer };
			// A session bearer carries its SteamID in its MAC and needs no header; the FP
			// fallback does, so send it when present. (Whether the WS handshake may set this
			// extra header is weaker ground than the session path — see the class notes.)
			if ( !string.IsNullOrEmpty( steamId ) )
				headers[GamchessApi.SteamIdHeader] = steamId;

			ws = new WebSocket();
			ws.OnMessageReceived += OnMessage;
			ws.OnDisconnected += OnDisconnected;
			_ws = ws;

			// Throws on a failed handshake (a refused channel, an expired session, a host
			// that isn't there). ChannelSocketUrl escapes the key; gamchess re-checks its
			// allowlist and 404s anything else before the upgrade.
			await ws.Connect( LichessTvApi.ChannelSocketUrl( asked ), headers );

			// Connected: the socket clearly works for this channel, so forget prior failures.
			// The first push (the current snapshot) will land via OnMessage.
			_connectFailures = 0;
		}
		catch ( Exception e )
		{
			// A one-shot WebSocket that failed its handshake is spent — dispose it rather
			// than leak it, and drop our reference (unhooking first so its Dispose-fired
			// OnDisconnected doesn't re-arm anything).
			if ( ws != null )
			{
				ws.OnMessageReceived -= OnMessage;
				ws.OnDisconnected -= OnDisconnected;
				ws.Dispose();
				if ( _ws == ws ) _ws = null;
			}
			OnConnectFailed( asked, e );
		}
		finally
		{
			_connecting = false;
		}
	}

	/// <summary>A handshake failed. Back off, and after enough consecutive failures on one
	/// channel decide it isn't being served rather than merely blipping — falling a
	/// non-default channel back to the default, or, if even the default won't connect,
	/// giving up for a while (most likely a gamchess older than the M18 WebSocket route).</summary>
	void OnConnectFailed( string asked, Exception e )
	{
		if ( asked != Channel ) return; // stale failure for a channel we've since left

		_reconnectBackoff = ReconnectBackoffSeconds;
		_connectFailures++;
		if ( _connectFailures < RejectAfterFailures )
		{
			// Might just be a blip. Keep the last position up, keep trying.
			if ( !HasPosition )
				StatusText = GamchessApi.Unreachable ? "lichess TV is offline." : "Waiting for lichess TV…";
			return;
		}

		Log.Warning( $"[Gambit] lichess TV: couldn't connect to '{asked}' ({e.Message})" );

		if ( asked == LichessTv.DefaultChannel )
		{
			// Even the default won't connect — this gamchess serves no TV we can reach
			// (most likely one older than the M18 route). Nothing to fall back to, so stop
			// for a while rather than loop, and forget the refusals so a deploy is picked up
			// without restarting the game.
			_unavailableUntil = UnavailableRetrySeconds;
			_rejected.Clear();
			_connectFailures = 0;
			Clear();
			StatusText = "lichess TV isn't available here.";
			return;
		}

		// A specific channel is refused: remember it (so the wall can't re-assert it next
		// frame) and fall back to the default.
		_rejected.Add( asked );
		_connectFailures = 0;
		SetChannel( LichessTv.DefaultChannel );
	}

	/// <summary>A message arrived (on the game main thread). Buffer it and try to apply.
	/// Every message is a full snapshot, so the latest is all that matters.</summary>
	void OnMessage( string json )
	{
		var st = GamchessApi.Deserialize<TvState>( json );
		if ( st == null ) return;
		_latest = st;
		_latestApplied = false;
		Pump();
	}

	/// <summary>An established socket dropped, for any reason. Null it and back off so Tick
	/// reconnects; keep the <c>_rejected</c>/<c>Unavailable</c> memory. Deliberately does
	/// NOT clear the board — a brief blip reconnects within gamchess's linger and re-attaches
	/// to the same live upstream, so the last position is still valid, and blanking it would
	/// flicker the wall on every hiccup.</summary>
	void OnDisconnected( int status, string reason )
	{
		// The socket disposed itself on disconnect; just drop our reference. (Unhooking a
		// disposed object's events is harmless, and keeps a stray late callback from firing
		// against a socket we've moved on from.)
		if ( _ws != null )
		{
			_ws.OnMessageReceived -= OnMessage;
			_ws.OnDisconnected -= OnDisconnected;
			_ws = null;
		}
		_reconnectBackoff = ReconnectBackoffSeconds;
		// Nothing to apply from a dropped socket; whatever was buffered is now history.
		_latestApplied = true;
	}

	void DisposeSocket()
	{
		var ws = _ws;
		_ws = null;
		if ( ws == null ) return;
		// Unhook BEFORE Dispose: Dispose fires OnDisconnected, and we don't want our
		// handler to re-arm a reconnect for a socket we are deliberately tearing down.
		ws.OnMessageReceived -= OnMessage;
		ws.OnDisconnected -= OnDisconnected;
		ws.Dispose();
	}

	/// <summary>Apply the buffered snapshot to the board, unless a fanfare hold says to
	/// wait. Called from both <see cref="OnMessage"/> and <see cref="Tick"/>, so a hold
	/// that expires with no new push still advances on the next frame.</summary>
	void Pump()
	{
		if ( _latest == null || _latestApplied ) return;
		if ( Apply( _latest ) )
			_latestApplied = true;
	}

	/// <summary>Reflect one snapshot onto the board. Returns true when the board actually
	/// advanced (the message is consumed); false when we ARMED or are HOLDING a fanfare and
	/// the reveal is deferred, so the same buffered message (or a newer one) is applied when
	/// the hold expires.</summary>
	bool Apply( TvState st )
	{
		if ( !string.IsNullOrEmpty( st.error ) )
		{
			Clear();
			StatusText = st.error;
			return true;
		}
		if ( string.IsNullOrEmpty( st.fen ) )
		{
			StatusText = "Waiting for lichess TV…";
			return true;
		}

		// Did the game WE are showing just end?
		//
		// WE work that out, from the featured game changing away from the one on our board.
		// Nothing else can mean that, and it needs nothing from the server beyond the id —
		// the server's contribution is the REASON only, and it degrades: no result, or a
		// result for some other game, and we still say the game ended, just not how.
		//
		// Once per game (_fanfareShownFor keyed on the game that ENDED): the ids go on
		// differing for the whole hold, so without it the fanfare re-arms every push and the
		// wall never moves on. And _gameId non-empty is what stops the very first featured —
		// a game we never showed — announcing an ending.
		bool endedOnOurBoard = !string.IsNullOrEmpty( _gameId )
			&& !string.IsNullOrEmpty( st.game_id )
			&& st.game_id != _gameId
			&& _gameId != _fanfareShownFor;

		if ( endedOnOurBoard )
		{
			// Start the HOLD immediately — this freezes the final position and the clocks.
			// The banner is set separately, below, once we actually know the result: no bare
			// "Game over" in the gap.
			_fanfareShownFor = _gameId;
			_holdUntil = LichessTv.FanfareSeconds;
			_fanfareUpgraded = false;
			StatusText = null;
			Log.Info( $"[Gambit] lichess TV: {_gameId} ended, holding" );
		}

		// Set the WHO-WON / WHY banner once — the instant we have a real result for the game
		// we're holding on. It arrives a beat after the hold starts (gamchess publishes the
		// featured swap immediately and fetches the result off the reader's path), and may
		// already be present on the arming push (a reconnect onto a settled state). We show
		// nothing until then, and nothing at all if the result is unknown — the held position
		// already says "a game ended", so a "Game over" placeholder that rewrites itself is
		// worse than a clean position that gains a result line. Guarded by _fanfareUpgraded so
		// Pump (which re-runs Apply every frame of the hold) sets it once, not per frame.
		if ( Holding && !_fanfareUpgraded && _gameId == _fanfareShownFor
			&& st.last_game_id == _gameId && !string.IsNullOrEmpty( st.last_status ) )
		{
			LichessTv.Result( st.last_status, st.last_winner, out var head, out var reason );
			// A null headline is the "we can't say who or why" case (an unrecognised or
			// still-settling status). Keep waiting rather than show a placeholder.
			if ( !string.IsNullOrEmpty( head ) )
			{
				FanfareHeadline = head;
				FanfareReason = reason;
				FanfareText = reason == null ? head : $"{head} — {reason}";
				_fanfareUpgraded = true;
			}
		}

		// Hold the finished position: keep the buffered snapshot un-applied until the hold
		// expires, then reveal whatever is current by construction (the latest push wins).
		if ( Holding ) return false;

		// The hold is over (or never started): the fanfare is history.
		ClearFanfare();

		_gameId = st.game_id;
		Fen = st.fen;
		LastMoveUci = st.last_move_uci;
		WhiteName = string.IsNullOrEmpty( st.white_name ) ? "White" : st.white_name;
		BlackName = string.IsNullOrEmpty( st.black_name ) ? "Black" : st.black_name;
		WhiteTitle = st.white_title;
		BlackTitle = st.black_title;
		WhiteRating = st.white_rating;
		BlackRating = st.black_rating;

		// Snap the clocks on every applied snapshot. A push only ever arrives on a real
		// change, so — unlike the old long poll, which could return the SAME state on a
		// timed-out hold and needed a version/newGame guard against re-snapping a stale value
		// into a sawtooth — there is no duplicate to guard against here. Pump applies each
		// message exactly once (_latestApplied), so each snap is a genuinely new frame.
		_whiteBank = st.white_clock;
		_blackBank = st.black_clock;
		_ageAtSnap = st.age_ms / 1000f;
		_sinceBank = 0f;

		TickingSeat = st.ticking_seat switch
		{
			"white" => ChessSeat.White,
			"black" => ChessSeat.Black,
			_ => null,
		};
		StatusText = null;

		// LOCAL end-of-game detection — the fast path a wall wants (M18).
		//
		// A checkmate or stalemate is IN the position we just applied, so we can freeze and
		// announce it the INSTANT the mating move lands, instead of running the mated side's
		// clock down for ~2s until lichess features the next game (the feed has no game-over
		// event; the featured swap is its only other end signal, and it lingers). Standard
		// channels only — a variant "mate" isn't one — and only checkmate/stalemate reach here;
		// a resign or a flag isn't visible in the position and still falls through to the swap
		// path below on the next messages. Locally derived, so _fanfareUpgraded = true keeps the
		// later swap/fetch from overwriting a result we already know for certain.
		if ( LichessTv.IsStandardRules( Channel )
			&& LichessTv.TryPositionResult( st.fen, out var posHead, out var posReason ) )
		{
			_fanfareShownFor = _gameId;
			_holdUntil = LichessTv.FanfareSeconds;
			FanfareHeadline = posHead;
			FanfareReason = posReason;
			FanfareText = posReason == null ? posHead : $"{posHead} — {posReason}";
			_fanfareUpgraded = true;
			Log.Info( $"[Gambit] lichess TV: {_gameId} ended (from position) — {FanfareText}" );
		}

		return true;
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
		_ageAtSnap = 0f;
		// TickingSeat null stops ClockFor counting down against a bank of 0 anyway, but
		// clearing it is what makes "no position" mean no clock rather than 0:00.
		TickingSeat = null;

		// There's no position, so there's nothing to hold on. Dropping _gameId matters
		// most: it's what the next snapshot's ending is matched against, and a stale one
		// would announce the ending of a game we are no longer showing.
		_gameId = null;
		_fanfareShownFor = null;
		// A buffered snapshot belongs to the position we're clearing — drop it too.
		_latest = null;
		_latestApplied = true;
		ClearFanfare();
	}

	/// <summary>End the hold and drop the banner together. One method because the banner
	/// fields must agree, and a headline left behind would print under the next game's
	/// position; clearing <see cref="_holdUntil"/> here is what lets the next game apply.</summary>
	void ClearFanfare()
	{
		FanfareText = null;
		FanfareHeadline = null;
		FanfareReason = null;
		_holdUntil = 0f;
		_fanfareUpgraded = false;
	}
}
