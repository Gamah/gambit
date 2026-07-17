using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Chess;
using Gambit.World;
using Sandbox;
using Sandbox.UI; // Clipboard

namespace Gambit.Game;

/// <summary>
/// Anonymous two-seat chess at one table (CLAUDE.md D1/D7). One instance per
/// station, added by ChessRing next to ChessStation, replicating with the
/// network-spawned station GO.
///
/// Flow: both seats fill, each player picks a time control and readies up, and the
/// host starts the game once both are ready. The mover's client
/// validates its move against the embedded rules (ChessGame), applies it, and
/// relays it with <see cref="NetChessMove"/>; every other client applies the
/// UCI to its own ChessGame (falling back to the FEN snapshot on any mismatch),
/// and the host folds the FEN into the synced <see cref="BoardFen"/> so late
/// joiners can reconstruct the position. Mate/stalemate/auto-draws are detected
/// independently on every client by the deterministic rules; resignation, seat
/// abandonment and flag-fall need their own RPCs.
///
/// Clocks are host-only: only the host decrements, calls the flag, and applies the
/// increment, because only the host's tick is authoritative. Clients never run a
/// clock of their own — they render the synced copy, so nobody can flag on lag.
///
/// Move history (SAN, for the HUD list and the archived PGN) lives in each
/// client's own ChessGame — players seated from move 1 have all of it; a FEN
/// resync or late join keeps the position but loses the history, which only
/// costs a shorter move list (and means that client won't archive the game).
///
/// Draws and takebacks are agreed here too, not just on lichess: the offers are
/// [Sync] state, the host decides when an agreement has happened, and it derives the
/// caller's seat from Rpc.Caller so nobody can agree on the other player's behalf. A
/// takeback rewinds every client's board IN PLACE (ChessGame.TruncateToPly), which is
/// what keeps the surviving moves — and keeps MoveCount an absolute ply, which is the
/// index space _clockLog is keyed in. Moves carry a takeback epoch so one crossing a
/// rewind on the wire is dropped rather than putting the undone move back.
/// </summary>
public sealed class LocalGameController : Component, IBoardGame
{
	/// <summary>Occupancy/seat source for this table. Set by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }

	/// <summary>The controller living beside the given station, or null.</summary>
	public static LocalGameController For( ChessStation station ) =>
		station?.Components.Get<LocalGameController>();

	// ── Host-authoritative synced state (late-joiner snapshot) ──

	// Table lifecycle, driven by the host in HostUpdate:
	//   Idle ──both seats fill──▶ Playing ──mate/resign/abandon──▶ Over
	//   Playing ──seat empties before any move──▶ Idle (no result, no blame)
	//   Over ──table fully vacated──▶ Idle       Over ──New Game / new pair──▶ Playing
	const int PhaseIdle = 0, PhasePlaying = 1, PhaseOver = 2;

	[Sync( SyncFlags.FromHost )] public int Phase { get; set; }

	/// <summary>Increments for every fresh game at this table; 0 = table has
	/// never hosted a game. Clients reset their local ChessGame on change.</summary>
	[Sync( SyncFlags.FromHost )] public int GameId { get; set; }

	/// <summary>Idempotency key for the gamchess archive (M7), minted by the host at
	/// game start. Synced precisely so BOTH seats can submit the same game and the
	/// second POST is a no-op: move history lives in each seated client's own
	/// ChessGame, not the host's, so the host often has no PGN to submit and can't
	/// be the one to upload. Null when no game is in progress.
	/// <para>Not a secret — an idempotency key, safe to sync.</para></summary>
	[Sync( SyncFlags.FromHost )] public string ClientGameId { get; set; }

	/// <summary>Latest position, folded by the host from NetChessMove relays.
	/// Null/empty = start position.</summary>
	[Sync( SyncFlags.FromHost )] public string BoardFen { get; set; }

	/// <summary>UCI of the last applied move (for the late-joiner last-move highlight).</summary>
	[Sync( SyncFlags.FromHost )] public string LastMoveUci { get; set; }

	/// <summary>Why the last game ended — "Checkmate", "Resignation", "White left the board"…</summary>
	[Sync( SyncFlags.FromHost )] public string OverReason { get; set; }

	/// <summary>"1-0" / "0-1" / "1/2-1/2" once over (sign + HUD display).</summary>
	[Sync( SyncFlags.FromHost )] public string OverResult { get; set; }

	// ── Time control + ready (issue: seated board panel) ──

	/// <summary>Index into <see cref="TimeControl.All"/> for this table. Settable by
	/// either seated player while the table is idle; frozen once a game starts.</summary>
	[Sync( SyncFlags.FromHost )] public int TimeControlIndex { get; set; } = TimeControl.DefaultIndex;

	/// <summary>Per-seat ready flag. Both must be set for the host to start a game —
	/// occupying both seats is no longer enough, or there'd be no window in which to
	/// pick a time control. Cleared on game start, game end, a seat emptying, and any
	/// change of time control.</summary>
	[Sync( SyncFlags.FromHost )] public bool WhiteReady { get; set; }
	[Sync( SyncFlags.FromHost )] public bool BlackReady { get; set; }

	/// <summary>Per-seat "play this on lichess" opt-in (M8). Settable while the
	/// table is idle, exactly like <see cref="WhiteReady"/>, and cleared by all the
	/// same things — an empty seat is never opted in, and changing the time control
	/// or the pairing retracts it.
	/// <para>Per-seat rather than per-table because it is a decision about YOUR
	/// lichess account. Both seats must opt in, and gamchess independently requires
	/// both to ask before it will start anything — this flag is the UI's half of
	/// that agreement, not the authorisation.</para></summary>
	[Sync( SyncFlags.FromHost )] public bool WhiteLichess { get; set; }
	[Sync( SyncFlags.FromHost )] public bool BlackLichess { get; set; }

	/// <summary>Standing draw / takeback offers, by seat. Host-authoritative like every
	/// other table fact, and <c>[Sync]</c> rather than a broadcast because an offer is
	/// STATE, not an event: a late joiner (and a spectator) must see the offer that is
	/// already standing, and a client that missed the RPC would show a draw button that
	/// silently means something else.</summary>
	[Sync( SyncFlags.FromHost )] public bool WhiteDrawOffer { get; set; }
	[Sync( SyncFlags.FromHost )] public bool BlackDrawOffer { get; set; }
	[Sync( SyncFlags.FromHost )] public bool WhiteTakebackOffer { get; set; }
	[Sync( SyncFlags.FromHost )] public bool BlackTakebackOffer { get; set; }

	/// <summary>Captured by the host at game start: this game is being played on
	/// lichess. Frozen for the game's duration, so a toggle can't change the rules
	/// under a game in progress.</summary>
	[Sync( SyncFlags.FromHost )] public bool LichessGame { get; set; }

	/// <summary>Both seats want this table's next game on lichess.</summary>
	public bool BothWantLichess => WhiteLichess && BlackLichess;

	/// <summary>Lichess opt-in for a seat.</summary>
	public bool LichessFor( ChessSeat seat ) =>
		seat == ChessSeat.White ? WhiteLichess : BlackLichess;

	/// <summary>Seconds left on each clock. Host-authoritative and throttled to
	/// <see cref="ClockSyncInterval"/> — the host holds the precise value locally
	/// (<c>_whiteRemaining</c>) and publishes a rounded-off copy for display, rather
	/// than dirtying a synced float every frame on every table in the ring.</summary>
	[Sync( SyncFlags.FromHost )] public float WhiteClock { get; set; }
	[Sync( SyncFlags.FromHost )] public float BlackClock { get; set; }

	/// <summary>The table's chosen control.</summary>
	public TimeControl Tc => TimeControl.At( TimeControlIndex );

	/// <summary>Ready flag for a seat.</summary>
	public bool ReadyFor( ChessSeat seat ) =>
		seat == ChessSeat.White ? WhiteReady : BlackReady;

	/// <summary>Displayed seconds left for a seat.</summary>
	public float ClockFor( ChessSeat seat ) =>
		seat == ChessSeat.White ? WhiteClock : BlackClock;

	/// <summary>Whose clock is running, or null when no clock is ticking (idle,
	/// over, or an untimed game).</summary>
	public ChessSeat? TickingSeat =>
		Playing && Game != null && !Tc.IsUnlimited
			? ( Game.WhiteToMove ? ChessSeat.White : ChessSeat.Black )
			: null;

	/// <summary>A game is live at this table right now.</summary>
	public bool Playing => Phase == PhasePlaying;

	/// <summary>The last game ended and its result is still on display.</summary>
	public bool GameOver => Phase == PhaseOver;

	// ── Local state (every client) ──

	/// <summary>This client's rules instance for the current GameId. Null until
	/// the first game starts at this table.</summary>
	public ChessGame Game { get; private set; }

	int _localGameId;      // GameId our Game instance was built for
	string _pgnWhiteName;  // seat names captured at game start (seats may empty before the game ends)
	string _pgnBlackName;
	ulong _pgnWhiteSteamId; // and their SteamIds, for the gamchess archive (M7)
	ulong _pgnBlackSteamId;
	string _pgnTimeControl; // and the control it was played at — TimeControlIndex is free to change once the game is Over
	bool _historyIntact;    // false once our ChessGame came from a FEN snapshot — no moves to archive
	string _archivedId;     // ClientGameId this client has already POSTed

	// Host-only clock bookkeeping. The synced WhiteClock/BlackClock are a throttled
	// copy of these; these are the ones that actually decrement.
	float _whiteRemaining, _blackRemaining;
	float _sinceClockSync;
	int _hostPly; // moves the host has folded this game — the ply NetClockStamp keys on

	/// <summary>How many takebacks have landed on THIS board, counted locally.
	///
	/// <para>The stale-move guard, and it can't be a ply number. A move carries the
	/// epoch it was played under; a takeback bumps it. After a rewind the plies repeat,
	/// so "this move is stale" and "I am behind" look identical by ply — and guessing
	/// wrong either drops a real move (board freezes) or applies a dead one (the rewind
	/// silently undoes itself and the history goes with it).</para>
	///
	/// <para>Set from NetTakeback's own payload rather than from a [Sync], so it changes
	/// at the exact instant the board rewinds. A [Sync] arrives on its own schedule, and
	/// the whole race is measured in the gap.</para></summary>
	int _takebackEpoch;

	/// <summary>Authoritative post-move clocks per 0-based ply, for the PGN's {[%clk]}
	/// comments. Filled by <see cref="NetClockStamp"/> on every client, because the
	/// seated clients are the ones that archive and only the host knows the real time.
	/// Empty for untimed games and for anyone who joined mid-game.</summary>
	readonly Dictionary<int, (float White, float Black)> _clockLog = new();

	/// <summary>How often the host publishes the clocks while there's time to spare. The
	/// HUD reads mm:ss up here, so 10Hz is already far finer than anything it can show.</summary>
	const float ClockSyncInterval = 0.1f;

	/// <summary>Publish rate once a clock drops under <see cref="TimeControl.DecimalBelowSeconds"/>,
	/// where the HUD switches to tenths. 10Hz there would be exactly one update per displayed
	/// digit — no headroom, so a frame-timing wobble visibly skips or repeats a tenth. ~33Hz
	/// gives the display something to land on. The cost is bounded: it only applies to a live
	/// table inside the last minute of someone's clock. A whole bullet game sits down here,
	/// which is the point.</summary>
	const float ClockSyncIntervalLow = 0.03f;

	/// <summary>A game has started at this table and hasn't been cleared yet
	/// (over or not) — the view renders Game instead of the start position.</summary>
	public bool HasGame => Game != null;

	/// <summary>Local player's seat at this table, or null when not seated here.</summary>
	public ChessSeat? LocalSeat =>
		ChessStation.Active == Station && Station != null ? ChessStation.ActiveSeat : null;

	/// <summary>Seconds left on a seat's clock — the seam's copy. Null when nothing is
	/// live or the game is untimed.</summary>
	public float? SeatClock( ChessSeat seat ) =>
		Playing && !Tc.IsUnlimited ? ClockFor( seat ) : null;

	/// <summary>Seconds left on the local player's own clock. Null when not seated here.</summary>
	public float? LocalSeatClock => LocalSeat is { } seat ? SeatClock( seat ) : null;

	/// <summary>It's the local seated player's turn in a live game.</summary>
	public bool IsMyTurn =>
		Playing && Game != null && LocalSeat != null
		&& Game.WhiteToMove == ( LocalSeat == ChessSeat.White );

	protected override void OnUpdate()
	{
		SyncLocalGame();
		TryArchive();

		// After SyncLocalGame, so a premove is judged against the position we
		// actually hold rather than one we're about to throw away.
		FirePremove();

		if ( Networking.IsHost )
			HostUpdate();
	}

	// ── Client-side game reconstruction ──

	/// <summary>Keep the local ChessGame aligned with the synced Phase/GameId/
	/// BoardFen — covers game starts, table resets, and the late-join snapshot
	/// in one place.</summary>
	void SyncLocalGame()
	{
		if ( Phase == PhaseIdle )
		{
			// Table cleared (host reset) — back to the start-position display
			if ( Game != null )
			{
				Game = null;
				_localGameId = 0;
				_premoveUci = null;
			}
			return;
		}

		if ( GameId == 0 || GameId == _localGameId ) return;

		_localGameId = GameId;
		Game = new ChessGame();
		_premoveUci = null;   // a premove must never outlive the game it was armed in
		_takebackEpoch = 0;
		_historyIntact = true;
		_clockLog.Clear();

		// Late joiner (or resync): the table is mid-game — adopt the snapshot.
		// Move history before this point is lost, see class remarks.
		if ( !string.IsNullOrEmpty( BoardFen ) && BoardFen != Game.Fen
			&& ChessGame.TryFromFen( BoardFen, out var snapshot ) )
		{
			Game = snapshot;
			_historyIntact = false;
		}

		// A late joiner can also arrive after resign/abandon — those don't live
		// in the FEN. The exact result doesn't matter locally (only players who
		// saw the game has the history), so just close the local game to match.
		if ( GameOver && !Game.IsGameOver )
			Game.Resign( whiteResigned: OverResult == "0-1" );

		_pgnWhiteName = Station?.WhiteName;
		_pgnBlackName = Station?.BlackName;

		// Seats captured at game start for the same reason as the names: by the time
		// the game is Over the seats may have emptied (resign/abandon vacate them),
		// and the archive needs to know who actually played.
		_pgnWhiteSteamId = Station?.WhiteSteamId ?? 0;
		_pgnBlackSteamId = Station?.BlackSteamId ?? 0;

		// Same reasoning: the players may re-pick a time control while the result is
		// still on display, and the PGN must record what this game was actually played at.
		_pgnTimeControl = Tc.PgnSpec;
	}

	// ── gamchess archive (M7) ──

	/// <summary>
	/// Post the finished game to the gamchess archive, once per client per game.
	///
	/// <para>This runs on the SEATED CLIENTS, not the host — deliberately. Move
	/// history lives in each seated client's own ChessGame, so the host usually has
	/// no PGN to submit. Both seats post the same <see cref="ClientGameId"/> and the
	/// server's unique constraint makes the second a no-op.</para>
	///
	/// <para>Entirely best-effort: gamchess being down must never be something a
	/// player notices. No await blocks the game ending, and a failure is a log line.</para>
	/// </summary>
	void TryArchive()
	{
		if ( !GameOver || Game == null ) return;
		if ( string.IsNullOrEmpty( ClientGameId ) || ClientGameId == _archivedId ) return;

		// Only the two players archive. Spectators have no PGN worth keeping and the
		// server would 403 them anyway — you may only archive a game you sat in.
		ulong me = Connection.Local?.SteamId ?? 0;
		if ( me == 0 || ( me != _pgnWhiteSteamId && me != _pgnBlackSteamId ) ) return;

		// A resynced/late-joined client holds the position but not the moves, so its
		// PGN would be a stub. Staying quiet leaves the archive to the other seat,
		// who almost certainly has the full history. Losing the game entirely is the
		// rarer, more acceptable failure than archiving a truncated one — the first
		// POST wins and can't be corrected.
		if ( !_historyIntact ) return;

		// Same rule, different cause: a LICHESS game's moves went to the relay and
		// never touched this ChessGame, so _historyIntact is still true and every
		// guard above passes — but BuildPgn() would produce headers with no moves,
		// no clocks and Result "*". Archiving that would be permanent (the POST is
		// idempotent on client_game_id, so it could never be corrected).
		//
		// lichess holds that game and gamchess relayed it. Wiring the relay's final
		// state into the archive is worth doing; posting a stub is not.
		if ( LichessGame ) return;

		// Claim it BEFORE awaiting: OnUpdate runs every frame and would otherwise
		// fire a POST per frame until the first one returned.
		_archivedId = ClientGameId;

		_ = ArchiveGame( ClientGameId, BuildPgn(), _pgnWhiteSteamId, _pgnBlackSteamId,
			string.IsNullOrEmpty( OverResult ) ? "*" : OverResult );
	}

	static async Task ArchiveGame( string id, string pgn, ulong white, ulong black, string result )
	{
		var res = await GamchessApi.PostGame( id, pgn, white, black, result );
		if ( res.Ok ) return;

		// Expected whenever gamchess is down or the player isn't on Steam. Info, not
		// a warning: nothing is broken and there is nothing for the player to do.
		Log.Info( $"[Gambit] game not archived: {res.Error}" );
	}

	// ── Host game lifecycle ──

	void HostUpdate()
	{
		if ( Station == null ) return;

		bool whiteSeated = Station.WhiteSteamId != 0;
		bool blackSeated = Station.BlackSteamId != 0;

		// An empty seat is never ready. Without this a player who readied up, stood, and
		// was replaced would hand their ready to the new occupant — who'd find themselves
		// in a game they never agreed to, on someone else's time control.
		if ( !whiteSeated ) WhiteReady = false;
		if ( !blackSeated ) BlackReady = false;

		// Same reasoning, and it matters more here: inheriting someone else's lichess
		// opt-in would mean a game played on the new occupant's real lichess account
		// because the previous occupant asked for it.
		if ( !whiteSeated ) WhiteLichess = false;
		if ( !blackSeated ) BlackLichess = false;

		switch ( Phase )
		{
			case PhaseIdle:
				// Both seated is no longer enough — both must also ready up, which is
				// what leaves a window to pick a time control.
				if ( whiteSeated && blackSeated && WhiteReady && BlackReady )
					HostStartFresh();
				break;

			case PhasePlaying:
				if ( whiteSeated && blackSeated )
				{
					HostTickClocks();
					break;
				}

				// A seat emptied mid-game (stand up or disconnect — ChessStation
				// has already reconciled occupancy): leaving is resigning once
				// the game actually began; an unmoved board just resets.
				if ( ( Game?.MoveCount ?? 0 ) == 0 )
					HostSetIdle();
				else
					NetAbandon( GameId, whiteLeft: !whiteSeated );
				break;

			case PhaseOver:
				// Result stays on display while anyone lingers; a vacated table resets.
				// Any restart — rematch or fresh pairing — goes through ready, which
				// HostEnd cleared, so nobody is dragged into a game by sitting still.
				if ( !whiteSeated && !blackSeated )
					HostSetIdle();
				else if ( whiteSeated && blackSeated && WhiteReady && BlackReady )
					HostStartFresh();
				break;
		}
	}

	/// <summary>
	/// Host-side: burn the ticking side's clock, and call the flag if it hits zero.
	/// Runs only while both seats are filled and a game is live.
	///
	/// <para>The mover pays their own latency: the host keeps burning their clock until
	/// the move relay lands. That is inherent to a host-authoritative clock and is the
	/// safe direction to be wrong in — the alternative is trusting a client's timing.</para>
	///
	/// <para>Known simplification: a flag is always a loss. FIDE scores it a draw when the
	/// opponent has no mating material, which needs material inspection the ChessGame seam
	/// doesn't expose today.</para>
	/// </summary>
	void HostTickClocks()
	{
		if ( Tc.IsUnlimited || Game == null || GameOver ) return;

		// A lichess game has exactly one clock authority, and it is lichess. If the
		// host kept burning its own copy, it would flag a player who is perfectly
		// fine on lichess's clock — the local ChessGame never advances during a
		// lichess game (moves go to the relay, not NetChessMove), so White's local
		// clock would run to zero every time. LichessGameController renders the
		// clocks lichess sends instead.
		if ( LichessGame ) return;

		bool white = Game.WhiteToMove;
		float remaining = ( white ? _whiteRemaining : _blackRemaining ) - Time.Delta;

		if ( remaining <= 0f )
		{
			if ( white ) _whiteRemaining = 0f; else _blackRemaining = 0f;
			PublishClocks();
			NetFlag( GameId, whiteFlagged: white );
			return;
		}

		if ( white ) _whiteRemaining = remaining; else _blackRemaining = remaining;

		// Throttled publish, faster once the ticking clock is low enough that the HUD
		// starts showing tenths — see ClockSyncInterval / ClockSyncIntervalLow.
		_sinceClockSync += Time.Delta;
		float interval = remaining < TimeControl.DecimalBelowSeconds
			? ClockSyncIntervalLow
			: ClockSyncInterval;
		if ( _sinceClockSync >= interval )
			PublishClocks();
	}

	/// <summary>Host-side: copy the precise clocks into the synced ones.</summary>
	void PublishClocks()
	{
		_sinceClockSync = 0f;
		WhiteClock = _whiteRemaining;
		BlackClock = _blackRemaining;
	}

	void HostStartFresh()
	{
		GameId++;
		ClientGameId = GamchessApi.NewClientGameId();

		// Freeze the lichess decision for this game. Read once, exactly as Tc is:
		// the flags are free to change again once the result is on display, and
		// this game must not change character halfway through. A control lichess
		// won't accept (bullet) can never be a lichess game, whatever the seats ask
		// for — gamchess refuses it too, this just avoids the round trip.
		LichessGame = BothWantLichess && LichessTable.CanMirror( Tc );
		BoardFen = null;
		LastMoveUci = null;
		OverReason = null;
		OverResult = null;

		// Both banks start full. Read Tc once: it is the control the whole game is
		// played at, and RequestSetTimeControlHost refuses to move it from here on.
		var tc = Tc;
		_whiteRemaining = tc.InitialSeconds;
		_blackRemaining = tc.InitialSeconds;
		_hostPly = 0;
		PublishClocks();

		// Consumed by the start, so a rematch needs a fresh pair of presses.
		WhiteReady = false;
		BlackReady = false;
		HostClearOffers();

		Phase = PhasePlaying;
	}

	/// <summary>Host-side: drop every standing offer. An offer belongs to the position
	/// it was made in — see the call in HostFold.</summary>
	void HostClearOffers()
	{
		WhiteDrawOffer = false;
		BlackDrawOffer = false;
		WhiteTakebackOffer = false;
		BlackTakebackOffer = false;
	}

	void HostSetIdle()
	{
		ClientGameId = null;
		BoardFen = null;
		LastMoveUci = null;
		OverReason = null;
		OverResult = null;
		WhiteReady = false;
		BlackReady = false;
		// An idle table is not a lichess game. Recomputed at HostStartFresh anyway, but
		// leaving it true here would have the seam keep resolving to a stale lichess
		// controller for a beat, and reads as a lichess table in the setup UI.
		LichessGame = false;
		HostClearOffers();
		_whiteRemaining = 0f;
		_blackRemaining = 0f;
		PublishClocks();
		Phase = PhaseIdle;
	}

	/// <summary>Host-side: fold the post-move state into the synced snapshot.</summary>
	void HostFold( string uci, string fenAfter )
	{
		if ( !Networking.IsHost ) return;

		BoardFen = fenAfter;
		LastMoveUci = uci;

		// Playing on IS declining — the ordinary way both offers die. Without this a
		// draw offered on move 4 would still be standing (and acceptable) on move 40,
		// long after the position that prompted it stopped existing.
		HostClearOffers();

		HostApplyIncrement( fenAfter );

		// Stamp the authoritative post-move clocks for the PGN. Only the host knows
		// them, and the host usually has no move history to archive — so it publishes
		// them to the seated clients, who do. Untimed games stamp nothing, which is
		// what keeps their PGN free of {[%clk]} noise.
		//
		// This is a broadcast raised from inside a broadcast handler (NetChessMove →
		// HostFold → here). Worth an eye on first run in the editor.
		if ( !Tc.IsUnlimited && Phase == PhasePlaying )
			NetClockStamp( GameId, _hostPly++, _whiteRemaining, _blackRemaining );

		// The host's own ChessGame just applied the move — if the rules say the
		// game ended (mate/stalemate/auto-draw), publish it.
		if ( Game != null && Game.IsGameOver && !GameOver )
			HostEnd( Game.ResultReason, Game.ResultString );
	}

	/// <summary>Host → everyone: the real clocks after ply <paramref name="ply"/>
	/// (0-based, ply 0 is White's first move), increment already applied.</summary>
	[Rpc.Broadcast]
	void NetClockStamp( int gameId, int ply, float white, float black )
	{
		if ( gameId != GameId || ply < 0 ) return;
		_clockLog[ply] = (white, black);
	}

	/// <summary>Host-side: credit the increment to whoever just moved. Driven off the
	/// post-move FEN rather than <c>Game</c>, so a host whose local rules fell back to
	/// a snapshot still banks the right side's time.</summary>
	void HostApplyIncrement( string fenAfter )
	{
		if ( Tc.IsUnlimited || Phase != PhasePlaying ) return;

		int inc = Tc.IncrementSeconds;
		if ( inc <= 0 ) return;

		// The side to move in fenAfter is the one who did NOT just move.
		if ( !TryFenWhiteToMove( fenAfter, out bool whiteToMove ) ) return;

		if ( whiteToMove ) _blackRemaining += inc;
		else _whiteRemaining += inc;

		PublishClocks();
	}

	/// <summary>Read the side-to-move field out of a FEN. False when the FEN is
	/// unreadable — callers must not guess a side.</summary>
	static bool TryFenWhiteToMove( string fen, out bool whiteToMove )
	{
		whiteToMove = false;
		if ( string.IsNullOrEmpty( fen ) ) return false;

		// "<placement> <side> <castling> ..." — field 1, space separated.
		int start = fen.IndexOf( ' ' );
		if ( start < 0 || start + 1 >= fen.Length ) return false;

		char side = fen[start + 1];
		if ( side == 'w' ) { whiteToMove = true; return true; }
		if ( side == 'b' ) { whiteToMove = false; return true; }
		return false;
	}

	void HostEnd( string reason, string result )
	{
		OverReason = reason;
		OverResult = result;

		// Ready never survives a game: without this the losing pair would be dropped
		// straight into a rematch they hadn't agreed to.
		WhiteReady = false;
		BlackReady = false;

		Phase = PhaseOver;
	}

	// ── Moves ──

	/// <summary>
	/// Local player makes a move (called by ChessBoardView after square picking).
	/// Validates against the local rules first — an illegal or out-of-turn move
	/// is refused without touching the network.
	/// </summary>
	/// <summary><see cref="IBoardGame"/> entry point — the board view calls this
	/// without caring which controller owns the board.</summary>
	public bool TryMakeMove( string uci ) => TryMakeLocalMove( uci );

	// ── Offers: draw and takeback ──
	//
	// The two-seat table has had neither until now, on the reasoning that the players
	// can just say so across the table. That was wrong about the draw in a way worth
	// recording: SAYING SO DOESN'T END THE GAME. There was no way to record a 1/2-1/2
	// at a table at all — agreeing meant one player resigning a drawn position, or the
	// game sitting there. And it was wrong about the takeback because the clocks are
	// real: a misclick in a 3+0 game cost the position with no way back.
	//
	// Every request is [Rpc.Host]: the host owns the offer flags and decides when an
	// agreement has happened, the same way it owns Phase and the clocks. None of them
	// takes a seat argument — the host derives the caller's seat from Rpc.Caller via
	// IsSeatedCaller, so a client cannot name the seat it acts for and cannot agree a
	// draw on the other player's behalf. (The older NetResign( whiteResigned ) does
	// take its caller's word for it; that is not a pattern worth copying.)

	/// <summary>Offers belong to the game actually being played at this table.
	///
	/// <para>While lichess owns the board this controller is a SHELL: it holds the seats
	/// and the ClientGameId, its ChessGame never advances (moves go to the relay, not
	/// NetChessMove), and the host stops ticking its clocks for exactly that reason. A
	/// draw agreed on it would end a table game that isn't happening, while the real one
	/// carried on at lichess. The HUD reads the lichess controller in that state and
	/// never calls these — this is the guard for when something else does.</para></summary>
	bool OffersLive => Playing && !LichessGame;

	/// <summary>The opponent has a draw offer standing.</summary>
	public bool DrawOffered =>
		OffersLive && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? BlackDrawOffer : WhiteDrawOffer );

	/// <summary>We have a draw offer standing.</summary>
	public bool DrawPending =>
		OffersLive && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? WhiteDrawOffer : BlackDrawOffer );

	/// <summary>Offer a draw, or accept one already offered.</summary>
	public void OfferDraw()
	{
		if ( !OffersLive || LocalSeat == null ) return;
		RequestDraw( GameId );
	}

	/// <summary>Decline the draw the opponent is offering.</summary>
	public void DeclineDraw()
	{
		if ( !OffersLive || LocalSeat == null ) return;
		RequestDeclineDraw( GameId );
	}

	/// <summary>The opponent is proposing a takeback.</summary>
	public bool TakebackOffered =>
		OffersLive && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? BlackTakebackOffer : WhiteTakebackOffer );

	/// <summary>We are proposing a takeback.</summary>
	public bool TakebackPending =>
		OffersLive && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? WhiteTakebackOffer : BlackTakebackOffer );

	/// <summary>A takeback is possible: a live game we're seated in, both sides have
	/// moved, and our own move history is intact.
	///
	/// <para>The history clause is the local one. A client whose board came from a FEN
	/// resync has no moves to rewind through and would have to be handed a position
	/// instead — the same reason such a client stays quiet at archive time. The HOST's
	/// history is what actually does the rewind, and the host is present for every game
	/// in its own lobby, so this is about the asker's board, not the truth.</para></summary>
	public bool CanTakeback =>
		OffersLive && LocalSeat != null && _historyIntact
		&& Game != null && Game.MoveCount >= 2;

	/// <summary>Propose a takeback, or accept one already proposed.</summary>
	public void OfferTakeback()
	{
		if ( !CanTakeback ) return;
		RequestTakeback( GameId );
	}

	/// <summary>Decline the takeback the opponent is proposing.</summary>
	public void DeclineTakeback()
	{
		if ( !OffersLive || LocalSeat == null ) return;
		RequestDeclineTakeback( GameId );
	}

	[Rpc.Host]
	void RequestDraw( int gameId )
	{
		if ( gameId != GameId || !OffersLive || !IsSeatedCaller( out var seat ) ) return;
		bool white = seat == ChessSeat.White;

		// Offering into a standing offer is accepting it — one gesture, like lichess.
		if ( white ? BlackDrawOffer : WhiteDrawOffer )
		{
			NetAgreeDraw( GameId );
			return;
		}

		if ( white ) WhiteDrawOffer = true; else BlackDrawOffer = true;
	}

	[Rpc.Host]
	void RequestDeclineDraw( int gameId )
	{
		if ( gameId != GameId || !OffersLive || !IsSeatedCaller( out var seat ) ) return;
		bool white = seat == ChessSeat.White;

		// Decline clears the OPPONENT's offer — the one being refused.
		if ( white ) BlackDrawOffer = false; else WhiteDrawOffer = false;
	}

	/// <summary>Host → everyone: the draw was agreed. Every client records it on its own
	/// ChessGame so the PGN it archives says 1/2-1/2, exactly as NetResign does.</summary>
	[Rpc.Broadcast]
	void NetAgreeDraw( int gameId )
	{
		if ( gameId != GameId || GameOver ) return;

		SyncLocalGame();
		Game?.AgreeDraw();

		if ( Networking.IsHost )
			HostEnd( "Agreement", "1/2-1/2" );
	}

	[Rpc.Host]
	void RequestTakeback( int gameId )
	{
		if ( gameId != GameId || !OffersLive || !IsSeatedCaller( out var seat ) ) return;
		bool white = seat == ChessSeat.White;

		if ( white ? BlackTakebackOffer : WhiteTakebackOffer )
		{
			HostApplyTakeback( acceptedByWhite: white );
			return;
		}

		if ( white ) WhiteTakebackOffer = true; else BlackTakebackOffer = true;
	}

	[Rpc.Host]
	void RequestDeclineTakeback( int gameId )
	{
		if ( gameId != GameId || !OffersLive || !IsSeatedCaller( out var seat ) ) return;
		bool white = seat == ChessSeat.White;

		if ( white ) BlackTakebackOffer = false; else WhiteTakebackOffer = false;
	}

	/// <summary>Host-side: rewind the game far enough to hand the PROPOSER their move
	/// back, then tell everyone.
	///
	/// <para>How far is the whole question, and it is not "one move". A takeback exists
	/// to undo a move you regret, so it must rewind until the proposer is on move: one
	/// ply if they've just moved and the opponent hasn't replied, two if the opponent
	/// already has. Rewinding a fixed one ply would hand the move back to whoever
	/// happened to be on move — sometimes the other player.</para>
	///
	/// <para>The host's own history does the work, and the host is in every game in its
	/// own lobby from ply 0. If its board ever fell back to a FEN snapshot there is
	/// nothing to rewind through and the request is dropped rather than guessed at.</para></summary>
	void HostApplyTakeback( bool acceptedByWhite )
	{
		WhiteTakebackOffer = false;
		BlackTakebackOffer = false;

		if ( !_historyIntact || Game == null ) return;

		// The proposer is whoever DIDN'T just accept.
		bool proposerIsWhite = !acceptedByWhite;

		// ABSOLUTE plies from the standard start, which is the space _clockLog is keyed
		// in — and stays absolute across a takeback only because TruncateToPly rewinds
		// in place instead of rebuilding a fresh game from a FEN.
		int ply = Game.MoveCount;

		// Rewind to the last position with the proposer on move: plies alternate, so an
		// even ply count means White is on move.
		int target = ( ply % 2 == 0 ) == proposerIsWhite ? ply - 2 : ply - 1;
		if ( target < 0 ) return;

		// Rewind our own copy first, to get the FEN to publish as the checksum. Every
		// client truncates in NetTakeback, including this one — where it lands on the
		// ply we're already at and no-ops.
		if ( !Game.TruncateToPly( target ) )
		{
			// Each undo is atomic but the walk is not, so a refusal partway leaves the
			// host holding a position nobody else has — a desync, not an inconvenience.
			// Publish whatever we actually ended up at so everyone converges on the host,
			// and leave the clocks alone (they belong to moves that may still exist).
			//
			// Unreachable as far as anyone can tell: the vendor only refuses to Cancel
			// while its board is browsing history, which a live game never does. Handled
			// anyway, because "can't happen" and "leaves the table split in two" is a bad
			// pairing.
			Log.Warning( $"[Gambit] takeback: couldn't rewind to ply {target} — resyncing at {Game.MoveCount}" );
			_hostPly = Game.MoveCount;
			NetTakeback( GameId, _takebackEpoch + 1, Game.MoveCount, Game.Fen, Game.LastMoveUci,
				_whiteRemaining, _blackRemaining );
			return;
		}

		// Clocks go back to what they were after the last surviving move. Ply 0 means
		// nothing survives, so the bank is the time control's own.
		float w = Tc.InitialSeconds, b = Tc.InitialSeconds;
		if ( target > 0 && _clockLog.TryGetValue( target - 1, out var stamp ) )
			( w, b ) = stamp;

		_whiteRemaining = w;
		_blackRemaining = b;
		_hostPly = target;

		NetTakeback( GameId, _takebackEpoch + 1, target, Game.Fen, Game.LastMoveUci, w, b );
	}

	/// <summary>Host → everyone: rewind to <paramref name="toPly"/>.
	///
	/// <para>Everyone rewinds their OWN board in place rather than adopting the FEN,
	/// which is what keeps the game archivable: <see cref="ChessGame.TruncateToPly"/>
	/// keeps the surviving moves, where rebuilding from a FEN would silently leave the
	/// table with a stub PGN for the rest of the game. The FEN rides along as the
	/// checksum and the fallback, the same bargain NetChessMove strikes.</para></summary>
	[Rpc.Broadcast]
	void NetTakeback( int gameId, int epoch, int toPly, string fenAfter, string lastMoveUci, float white, float black )
	{
		if ( gameId != GameId || GameOver ) return;

		SyncLocalGame();

		// Before the rewind, so any move still in flight from the epoch we're leaving is
		// dropped by NetChessMove rather than putting the undone move back.
		_takebackEpoch = epoch;

		// A premove aimed at a position that no longer exists must not survive the
		// rewind — it would fire into a board its owner never saw.
		_premoveUci = null;

		if ( _historyIntact && Game != null && Game.TruncateToPly( toPly ) && Game.Fen == fenAfter )
		{
			// Rewound with the history intact — still archivable.
		}
		else if ( ChessGame.TryFromFen( fenAfter, out var snapshot ) )
		{
			// No history to rewind through, or ours disagreed with the host's. Take the
			// position and stop claiming this client can archive the game — the same
			// bargain (and the same flag) as a FEN resync.
			Game = snapshot;
			_historyIntact = false;
		}
		else
		{
			Log.Warning( "[Gambit] takeback carried an unreadable FEN — board frozen until next move" );
			return;
		}

		// Stamps at or past the rewind point describe moves that no longer happened.
		// Keyed absolutely, like Game.MoveCount, so what survives still lines up with
		// the moves that survive — that is what keeps {[%clk]} on the right move, and
		// on the right side.
		var stale = new List<int>();
		foreach ( int p in _clockLog.Keys )
			if ( p >= toPly ) stale.Add( p );
		foreach ( int p in stale )
			_clockLog.Remove( p );

		if ( Networking.IsHost )
		{
			BoardFen = fenAfter;
			LastMoveUci = lastMoveUci;
			_whiteRemaining = white;
			_blackRemaining = black;
			WhiteClock = white;
			BlackClock = black;
		}
	}

	// ── Premove ──

	string _premoveUci;

	/// <inheritdoc/>
	public string PremoveUci => _premoveUci;

	/// <inheritdoc/>
	public void SetPremove( string uci )
	{
		if ( !Playing || LocalSeat == null ) return;
		if ( uci is not { Length: >= 4 } ) return;
		_premoveUci = uci;
	}

	/// <inheritdoc/>
	public void ClearPremove() => _premoveUci = null;

	/// <summary>Play the armed premove once the opponent's move has landed.
	///
	/// <para>Driven from <see cref="OnUpdate"/> rather than from the end of
	/// <see cref="ApplyRemoteMove"/>, which is where the opponent's move actually
	/// arrives. Firing there would raise a broadcast (NetChessMove) from inside a
	/// broadcast handler — the same shape as the NetClockStamp call this file already
	/// flags as worth an eye on. A premove is worth a frame (~16ms) to avoid nesting
	/// one relay inside another; it is not worth a networking mystery.</para>
	///
	/// <para>An illegal premove is DROPPED, not held: it was aimed at a position the
	/// opponent didn't play into, and firing it later at a position it was never meant
	/// for is how a premove hangs a queen two moves after you forgot about it.</para></summary>
	void FirePremove()
	{
		if ( _premoveUci == null ) return;

		if ( !Playing || LocalSeat == null ) { _premoveUci = null; return; }
		if ( !IsMyTurn ) return;

		string uci = _premoveUci;

		// Disarm BEFORE playing: TryMakeLocalMove can refuse, and a premove left armed
		// through its own refusal would retry every frame for the rest of the game.
		_premoveUci = null;

		// Use the answer. It was discarded here for two milestones, which made a dropped
		// premove indistinguishable from a played one — see IBoardGame.PremoveDropped.
		if ( !TryMakeLocalMove( uci ) )
			_premoveDropped = BoardGame.PremoveDroppedSeconds;
	}

	RealTimeUntil _premoveDropped;

	/// <summary>The last premove was refused, within the notice window.</summary>
	public bool PremoveDropped => (float)_premoveDropped > 0f;

	public bool TryMakeLocalMove( string uci )
	{
		if ( !IsMyTurn || Game == null ) return false;

		if ( !Game.ApplyUci( uci ) ) return false;

		NetChessMove( GameId, _takebackEpoch, uci, Game.Fen );
		return true;
	}

	/// <summary>Mover → everyone: apply this UCI; fenAfter doubles as checksum
	/// and resync snapshot. The host additionally folds it into BoardFen.</summary>
	[Rpc.Broadcast]
	void NetChessMove( int gameId, int epoch, string uci, string fenAfter )
	{
		if ( gameId != GameId ) return; // stale relay from a previous game

		// A move played from a position that no longer exists: it was in flight when a
		// takeback rewound the board under it. Applying it — or worse, adopting its FEN
		// through ApplyRemoteMove's resync — would put the undone move back and undo the
		// takeback, taking the move history with it.
		//
		// A client that hasn't SEEN the takeback yet has the old epoch too, so it accepts
		// the move and converges a moment later when NetTakeback truncates past it. The
		// guard drops only what is genuinely from a dead epoch.
		if ( epoch != _takebackEpoch ) return;

		bool isSelf = Rpc.Caller != null && Rpc.Caller == Connection.Local;
		if ( !isSelf )
			ApplyRemoteMove( uci, fenAfter );

		HostFold( uci, fenAfter );
	}

	void ApplyRemoteMove( string uci, string fenAfter )
	{
		SyncLocalGame(); // make sure Game matches GameId before touching it

		// Already at this position (we're the mover, or a duplicate delivery) —
		// applying again would fail and needlessly resync history away.
		if ( Game != null && Game.Fen == fenAfter ) return;

		if ( Game == null || !Game.ApplyUci( uci ) || Game.Fen != fenAfter )
		{
			// Missed context (join race, desync) — the FEN snapshot is authoritative.
			if ( ChessGame.TryFromFen( fenAfter, out var snapshot ) )
			{
				Game = snapshot;

				// A FEN snapshot has no moves behind it, so this client can no longer
				// archive the game and its MoveCount is no longer an absolute ply.
				// SyncLocalGame's own snapshot path has always said so; this one didn't,
				// which left _historyIntact lying — TryArchive would upload a stub PGN,
				// permanently (the POST is idempotent on client_game_id), and a takeback
				// would read the relative MoveCount as an absolute ply and rewind to the
				// wrong move.
				_historyIntact = false;
			}
			else
				Log.Warning( $"[Gambit] table relay carried an unreadable FEN — board frozen until next move" );
		}
	}

	// Sound used to live here — PlayMoveSound/CountPieces, called from
	// TryMakeLocalMove and ApplyRemoteMove. It moved to Gambit.Audio.TableSounds,
	// which watches the IBoardGame seam instead, because hanging it off this class
	// meant it only ever covered LOCAL games: a real lichess game at this table was
	// silent from M8 to M11. Don't add a Sound.Play back into this file — a new one
	// here would cover half the tables again, and nothing would look wrong.

	// ── Endings ──

	/// <summary>Local seated player resigns (HUD button, or leaving mid-game).</summary>
	public void ResignLocal()
	{
		if ( !Playing || LocalSeat == null ) return;
		NetResign( GameId, LocalSeat == ChessSeat.White );
	}

	[Rpc.Broadcast]
	void NetResign( int gameId, bool whiteResigned )
	{
		if ( gameId != GameId || GameOver ) return;

		SyncLocalGame();
		Game?.Resign( whiteResigned );

		if ( Networking.IsHost )
			HostEnd( "Resignation", whiteResigned ? "0-1" : "1-0" );
	}

	/// <summary>Host → everyone: a player left a live game; score it against them.</summary>
	[Rpc.Broadcast]
	void NetAbandon( int gameId, bool whiteLeft )
	{
		if ( gameId != GameId || GameOver ) return;

		SyncLocalGame();
		Game?.Resign( whiteLeft );

		if ( Networking.IsHost )
			HostEnd( whiteLeft ? "White left the board" : "Black left the board",
				whiteLeft ? "0-1" : "1-0" );
	}

	/// <summary>Host → everyone: a clock hit zero; score it against its owner.</summary>
	[Rpc.Broadcast]
	void NetFlag( int gameId, bool whiteFlagged )
	{
		if ( gameId != GameId || GameOver ) return;

		SyncLocalGame();
		Game?.Resign( whiteFlagged );

		if ( Networking.IsHost )
			HostEnd( whiteFlagged ? "White ran out of time" : "Black ran out of time",
				whiteFlagged ? "0-1" : "1-0" );
	}

	// ── Time control + ready ──

	/// <summary>Local seated player picks a time control (HUD). Idle tables only —
	/// the host enforces that; this is just the early-out.</summary>
	public void RequestSetTimeControl( int index )
	{
		if ( LocalSeat == null || Playing ) return;
		if ( !TimeControl.IsValidIndex( index ) ) return;
		RequestSetTimeControlHost( index );
	}

	[Rpc.Host]
	void RequestSetTimeControlHost( int index )
	{
		if ( Station == null || Phase == PhasePlaying ) return;
		// An arbitrary int off the wire — never index the menu with it unchecked.
		if ( !TimeControl.IsValidIndex( index ) ) return;
		if ( !IsSeatedCaller( out _ ) ) return;
		if ( index == TimeControlIndex ) return;

		TimeControlIndex = index;

		// Changing the terms retracts both agreements — you readied up for the old
		// control, not this one.
		WhiteReady = false;
		BlackReady = false;
	}

	// ── Lichess outcome reporting (M8) ──
	//
	// The host sets LichessGame at game start and then knows NOTHING about how the
	// lichess game goes: its own ChessGame never advances (moves go to the relay,
	// not NetChessMove), so it cannot see a mate, a resignation, a flag, or a
	// refusal. Without these two reports the table wedges: LichessGame stays true
	// so HostTickClocks never runs, and Phase stays Playing forever, so the table
	// never returns to idle and never offers a rematch.
	//
	// Reported by the SEATED clients, because they are the only ones who can see
	// lichess's answer. Both seats report; the guards make the second a no-op.

	/// <summary>A seated client: gamchess/lichess refused to start this table's
	/// game. Fall back to an ordinary local game.</summary>
	public void ReportLichessFailed()
	{
		if ( LocalSeat == null || !LichessGame ) return;
		ReportLichessFailedHost( GameId );
	}

	[Rpc.Host]
	void ReportLichessFailedHost( int gameId )
	{
		if ( gameId != GameId || !LichessGame ) return;
		if ( !IsSeatedCaller( out _ ) ) return;

		// Drop back to a normal local game rather than leaving a dead table: the
		// clocks start ticking from here (HostTickClocks stops early-returning),
		// the pieces already move, and the players get a real game instead of a
		// frozen board. "gamchess is never required" — this is what that means at
		// a table that asked for lichess and didn't get it.
		LichessGame = false;
	}

	/// <summary>The only endings a lichess game may report. Everything a client
	/// sends is an arbitrary string off the wire, and OverReason is [Sync]ed to
	/// every peer's HUD — so a seated modified client could otherwise paint
	/// whatever it liked onto both players' screens. Every other OverReason in this
	/// file comes from ChessGame.ResultReason's fixed vocabulary; this keeps that
	/// true. Must stay in step with LichessGameController.OverReason.
	///
	/// <para>"Aborted" and "Never started" are deliberately absent: those endings
	/// carry no result, so they arrive via ReportLichessFailed instead — an aborted
	/// game is one that never happened, not a draw.</para></summary>
	/// <para>A List, not an array: <c>System.Array</c>'s statics are a whitelist
	/// risk we can't test for here (<c>Array.Clone</c> is already an SB1000 blocker
	/// and the whitelist is per-member), while <c>List&lt;T&gt;.Contains</c> is
	/// proven all over this codebase.</para>
	static readonly List<string> LichessReasons = new()
	{
		"Checkmate", "Resignation", "Stalemate", "Out of time",
		"Draw", "Insufficient material", "Game over",
	};

	/// <summary>A seated client: lichess says this game is over. Close the table on
	/// lichess's verdict, since the host's own rules never saw a single move.</summary>
	public void ReportLichessResult( string result, string reason )
	{
		if ( LocalSeat == null || !LichessGame ) return;
		ReportLichessResultHost( GameId, result, reason );
	}

	[Rpc.Host]
	void ReportLichessResultHost( int gameId, string result, string reason )
	{
		if ( gameId != GameId || !LichessGame || GameOver ) return;
		if ( !IsSeatedCaller( out _ ) ) return;

		// Arbitrary strings off the wire — never display one we didn't expect.
		if ( result != "1-0" && result != "0-1" && result != "1/2-1/2" ) return;
		if ( !LichessReasons.Contains( reason ) ) reason = "Game over";

		HostEnd( reason, result );
	}

	/// <summary>Local seated player clears a FINISHED game to start a new one without
	/// standing up — the "New game" button. Over tables only; the host enforces it.
	/// <para>The board stays showing the result until this (or a stand-up) fires, so a
	/// player can study what happened first. This is the button that clears it.</para></summary>
	public void RequestNewGame()
	{
		if ( LocalSeat == null || !GameOver ) return;
		RequestNewGameHost( GameId );
	}

	[Rpc.Host]
	void RequestNewGameHost( int gameId )
	{
		if ( gameId != GameId || Phase != PhaseOver ) return;
		if ( !IsSeatedCaller( out _ ) ) return;
		HostSetIdle();
	}

	/// <summary>Local seated player toggles their ready flag (HUD).</summary>
	public void ToggleReady()
	{
		if ( LocalSeat == null || Playing ) return;
		RequestReadyHost( !ReadyFor( LocalSeat.Value ) );
	}

	/// <summary>Local seated player toggles "play my next game here on lichess"
	/// (HUD). Idle tables only — the host enforces that.</summary>
	public void ToggleLichess()
	{
		if ( LocalSeat == null || Playing ) return;
		RequestLichessHost( !LichessFor( LocalSeat.Value ) );
	}

	[Rpc.Host]
	void RequestLichessHost( bool want )
	{
		if ( Station == null || Phase == PhasePlaying ) return;
		if ( !IsSeatedCaller( out var seat ) ) return;

		if ( seat == ChessSeat.White ) WhiteLichess = want;
		else BlackLichess = want;

		// Opting in or out changes what the game IS — a real game on your lichess
		// account versus a casual one in a lobby. Nobody carries a ready across
		// that, for the same reason changing the time control retracts it.
		WhiteReady = false;
		BlackReady = false;
	}

	[Rpc.Host]
	void RequestReadyHost( bool ready )
	{
		if ( Station == null || Phase == PhasePlaying ) return;
		if ( !IsSeatedCaller( out var seat ) ) return;

		// Readying alone is allowed (and sticks) — HostUpdate simply won't start until
		// the other seat fills and matches it.
		if ( seat == ChessSeat.White ) WhiteReady = ready;
		else BlackReady = ready;
	}

	/// <summary>Host-side: which seat the RPC caller holds at this table, if any. The
	/// caller is read from <see cref="Rpc.Caller"/>, never from an argument — a client
	/// may not name the seat it is acting for.</summary>
	bool IsSeatedCaller( out ChessSeat seat )
	{
		seat = default;
		ulong id = Rpc.Caller?.SteamId ?? 0;
		if ( id == 0 || Station == null ) return false;

		if ( id == Station.WhiteSteamId ) { seat = ChessSeat.White; return true; }
		if ( id == Station.BlackSteamId ) { seat = ChessSeat.Black; return true; }
		return false;
	}

	string BuildPgn()
	{
		Game.SetHeader( "Event", "Terry's Gambit casual game" );
		Game.SetHeader( "Site", "Terry's Gambit (s&box)" );
		Game.SetHeader( "Date", DateTime.UtcNow.ToString( "yyyy.MM.dd" ) );
		Game.SetHeader( "White", string.IsNullOrEmpty( _pgnWhiteName ) ? "Anonymous" : _pgnWhiteName );
		Game.SetHeader( "Black", string.IsNullOrEmpty( _pgnBlackName ) ? "Anonymous" : _pgnBlackName );
		Game.SetHeader( "Result", Game.ResultString );
		// Captured at game start — see _pgnTimeControl.
		Game.SetHeader( "TimeControl", string.IsNullOrEmpty( _pgnTimeControl ) ? "-" : _pgnTimeControl );
		AttachClockComments();
		return Game.Pgn;
	}

	/// <summary>
	/// Annotate each move with the clock its mover had left, as PGN <c>{[%clk H:MM:SS]}</c>.
	/// Reads the host's stamps (<see cref="NetClockStamp"/>) — never this client's synced
	/// copy, which lags the increment.
	///
	/// <para>Ply parity identifies the mover: ply 0 is White's first. That holds because a
	/// game always starts from the standard position — a client whose board came from a FEN
	/// resync has no history and never reaches this code (<c>_historyIntact</c>).</para>
	///
	/// <para>A ply with no stamp is left bare rather than guessed at: a gap means a dropped
	/// relay, and a wrong clock on a move is worse than no clock.</para>
	/// </summary>
	void AttachClockComments()
	{
		if ( _clockLog.Count == 0 || Game == null ) return;

		for ( int ply = 0; ply < Game.MoveCount; ply++ )
		{
			if ( !_clockLog.TryGetValue( ply, out var stamp ) ) continue;

			float remaining = ply % 2 == 0 ? stamp.White : stamp.Black;
			Game.SetMoveComment( ply, $"[%clk {ChessGame.ClkField( remaining )}]" );
		}
	}
}
