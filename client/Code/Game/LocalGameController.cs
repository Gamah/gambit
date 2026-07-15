using System;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Chess;
using Gambit.World;
using Sandbox;
using Sandbox.UI; // Clipboard

namespace Gambit.Game;

/// <summary>
/// Anonymous two-seat chess at one table (PLAN.md D1/D7). One instance per
/// station, added by ChessRing next to ChessStation, replicating with the
/// network-spawned station GO.
///
/// Flow: the host starts a game the moment both seats fill. The mover's client
/// validates its move against the embedded rules (ChessGame), applies it, and
/// relays it with <see cref="NetChessMove"/>; every other client applies the
/// UCI to its own ChessGame (falling back to the FEN snapshot on any mismatch),
/// and the host folds the FEN into the synced <see cref="BoardFen"/> so late
/// joiners can reconstruct the position. Mate/stalemate/auto-draws are detected
/// independently on every client by the deterministic rules; only resignation
/// and seat abandonment need their own RPCs.
///
/// Move history (SAN, for the HUD list and the archived PGN) lives in each
/// client's own ChessGame — players seated from move 1 have all of it; a FEN
/// resync or late join keeps the position but loses the history, which only
/// costs a shorter move list (and means that client won't archive the game).
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
	bool _historyIntact;    // false once our ChessGame came from a FEN snapshot — no moves to archive
	string _archivedId;     // ClientGameId this client has already POSTed

	// Host-only bookkeeping
	(ulong White, ulong Black) _endedPair; // pair seated when the last game ended — don't auto-restart them

	/// <summary>A game has started at this table and hasn't been cleared yet
	/// (over or not) — the view renders Game instead of the start position.</summary>
	public bool HasGame => Game != null;

	/// <summary>Local player's seat at this table, or null when not seated here.</summary>
	public ChessSeat? LocalSeat =>
		ChessStation.Active == Station && Station != null ? ChessStation.ActiveSeat : null;

	/// <summary>It's the local seated player's turn in a live game.</summary>
	public bool IsMyTurn =>
		Playing && Game != null && LocalSeat != null
		&& Game.WhiteToMove == ( LocalSeat == ChessSeat.White );

	protected override void OnUpdate()
	{
		SyncLocalGame();
		TryArchive();

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
			}
			return;
		}

		if ( GameId == 0 || GameId == _localGameId ) return;

		_localGameId = GameId;
		Game = new ChessGame();
		_historyIntact = true;

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
		var pair = (Station.WhiteSteamId, Station.BlackSteamId);

		switch ( Phase )
		{
			case PhaseIdle:
				// A game starts the moment both seats fill — unless the board is spoken
				if ( whiteSeated && blackSeated )
					HostStartFresh();
				break;

			case PhasePlaying:
				if ( whiteSeated && blackSeated ) break;

				// A seat emptied mid-game (stand up or disconnect — ChessStation
				// has already reconciled occupancy): leaving is resigning once
				// the game actually began; an unmoved board just resets.
				if ( ( Game?.MoveCount ?? 0 ) == 0 )
					HostSetIdle();
				else
					NetAbandon( GameId, whiteLeft: !whiteSeated );
				break;

			case PhaseOver:
				// Result stays on display while anyone lingers; the same pair
				// needs the New Game button (RequestNewGame), a new pairing
				// starts straight away, a vacated table resets.
				if ( !whiteSeated && !blackSeated )
					HostSetIdle();
				else if ( whiteSeated && blackSeated && pair != _endedPair )
					HostStartFresh();
				break;
		}
	}

	void HostStartFresh()
	{
		GameId++;
		ClientGameId = GamchessApi.NewClientGameId();
		BoardFen = null;
		LastMoveUci = null;
		OverReason = null;
		OverResult = null;
		_endedPair = default;
		Phase = PhasePlaying;
	}

	void HostSetIdle()
	{
		ClientGameId = null;
		BoardFen = null;
		LastMoveUci = null;
		OverReason = null;
		OverResult = null;
		_endedPair = default;
		Phase = PhaseIdle;
	}

	/// <summary>Host-side: fold the post-move state into the synced snapshot.</summary>
	void HostFold( string uci, string fenAfter )
	{
		if ( !Networking.IsHost ) return;

		BoardFen = fenAfter;
		LastMoveUci = uci;

		// The host's own ChessGame just applied the move — if the rules say the
		// game ended (mate/stalemate/auto-draw), publish it.
		if ( Game != null && Game.IsGameOver && !GameOver )
			HostEnd( Game.ResultReason, Game.ResultString );
	}

	void HostEnd( string reason, string result )
	{
		OverReason = reason;
		OverResult = result;
		_endedPair = (Station.WhiteSteamId, Station.BlackSteamId);
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

	public bool TryMakeLocalMove( string uci )
	{
		if ( !IsMyTurn || Game == null ) return false;

		string fenBefore = Game.Fen;
		if ( !Game.ApplyUci( uci ) ) return false;

		PlayMoveSound( fenBefore, Game.Fen );
		NetChessMove( GameId, uci, Game.Fen );
		return true;
	}

	/// <summary>Mover → everyone: apply this UCI; fenAfter doubles as checksum
	/// and resync snapshot. The host additionally folds it into BoardFen.</summary>
	[Rpc.Broadcast]
	void NetChessMove( int gameId, string uci, string fenAfter )
	{
		if ( gameId != GameId ) return; // stale relay from a previous game

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

		string fenBefore = Game?.Fen;

		if ( Game == null || !Game.ApplyUci( uci ) || Game.Fen != fenAfter )
		{
			// Missed context (join race, desync) — the FEN snapshot is authoritative.
			if ( ChessGame.TryFromFen( fenAfter, out var snapshot ) )
				Game = snapshot;
			else
				Log.Warning( $"[Gambit] table relay carried an unreadable FEN — board frozen until next move" );
		}

		if ( fenBefore != null )
			PlayMoveSound( fenBefore, fenAfter );
	}

	/// <summary>Tick for White's move, tock for Black's, pop for any capture —
	/// 2D at the table I'm seated at, positional for everyone else's.</summary>
	void PlayMoveSound( string fenBefore, string fenAfter )
	{
		bool capture = CountPieces( fenBefore ) != CountPieces( fenAfter );
		// fenAfter's side-to-move is the player who did NOT just move
		bool whiteMoved = fenAfter != null && fenAfter.Contains( " b " );
		bool mine = ChessStation.Active == Station;

		if ( capture )
		{
			if ( mine ) Audio.SoundPlayer.PlayPop();
			else Audio.SoundPlayer.PlayPopAt( WorldPosition );
		}
		else if ( mine )
		{
			if ( whiteMoved ) Audio.SoundPlayer.PlayTick();
			else Audio.SoundPlayer.PlayTock();
		}
		else
		{
			Audio.SoundPlayer.PlayTickAt( WorldPosition );
		}
	}

	static int CountPieces( string fen )
	{
		if ( string.IsNullOrEmpty( fen ) ) return -1;
		int count = 0;
		foreach ( var c in fen )
		{
			if ( c == ' ' ) break; // placement field only
			if ( char.IsLetter( c ) ) count++;
		}
		return count;
	}

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

	/// <summary>Seated player wants a rematch after game over (HUD button).</summary>
	public void RequestNewGame()
	{
		if ( LocalSeat == null || !GameOver ) return;
		RequestNewGameHost();
	}

	[Rpc.Host]
	void RequestNewGameHost()
	{
		if ( !GameOver || Station == null ) return;
		if ( Station.WhiteSteamId == 0 || Station.BlackSteamId == 0 ) return;
		// Only the two players at the table get a say
		if ( Rpc.Caller.SteamId != Station.WhiteSteamId && Rpc.Caller.SteamId != Station.BlackSteamId ) return;

		HostStartFresh();
	}

	string BuildPgn()
	{
		Game.SetHeader( "Event", "Terry's Gambit casual game" );
		Game.SetHeader( "Site", "Terry's Gambit (s&box)" );
		Game.SetHeader( "Date", DateTime.UtcNow.ToString( "yyyy.MM.dd" ) );
		Game.SetHeader( "White", string.IsNullOrEmpty( _pgnWhiteName ) ? "Anonymous" : _pgnWhiteName );
		Game.SetHeader( "Black", string.IsNullOrEmpty( _pgnBlackName ) ? "Anonymous" : _pgnBlackName );
		Game.SetHeader( "Result", Game.ResultString );
		return Game.Pgn;
	}
}
