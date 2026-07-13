using System;
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
/// Move history (SAN, for the HUD list and the PGN import) lives in each
/// client's own ChessGame — players seated from move 1 have all of it; a FEN
/// resync or late join keeps the position but loses the history, which only
/// costs a shorter move list (spectators can't import anyway).
/// </summary>
public sealed class LocalGameController : Component
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
	string _pgnWhiteName;  // seat names captured at game start (seats may empty before import)
	string _pgnBlackName;

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
				LichessUrl = null;
				_importError = null;
			}
			return;
		}

		if ( GameId == 0 || GameId == _localGameId ) return;

		_localGameId = GameId;
		Game = new ChessGame();

		// Late joiner (or resync): the table is mid-game — adopt the snapshot.
		// Move history before this point is lost, see class remarks.
		if ( !string.IsNullOrEmpty( BoardFen ) && BoardFen != Game.Fen
			&& ChessGame.TryFromFen( BoardFen, out var snapshot ) )
			Game = snapshot;

		// A late joiner can also arrive after resign/abandon — those don't live
		// in the FEN. The exact result doesn't matter locally (only players who
		// saw the game can import), so just close the local game to match.
		if ( GameOver && !Game.IsGameOver )
			Game.Resign( whiteResigned: OverResult == "0-1" );

		_pgnWhiteName = Station?.WhiteName;
		_pgnBlackName = Station?.BlackName;

		// New game voids any previous import result for this table
		LichessUrl = null;
		_importError = null;
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
				// A game starts the moment both seats fill
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
		BoardFen = null;
		LastMoveUci = null;
		OverReason = null;
		OverResult = null;
		_endedPair = default;
		Phase = PhasePlaying;
	}

	void HostSetIdle()
	{
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

	// ── Lichess PGN import (first live lichess call — PLAN.md M2 step,
	//    validates the sbproj HttpAllowList) ──

	/// <summary>Shareable lichess URL for the finished game, once imported.</summary>
	public string LichessUrl { get; private set; }

	/// <summary>True while the POST is in flight (HUD shows a spinner-ish state).</summary>
	public bool Importing { get; private set; }

	/// <summary>Last import failure, for the HUD. Null when none.</summary>
	public string ImportError => _importError;
	string _importError;

	/// <summary>Time since the import URL was copied — HUD shows brief feedback
	/// (same pattern as DiscordButton).</summary>
	public RealTimeSince SinceUrlCopied { get; private set; } = 999f;

	/// <summary>POST the finished game's PGN to lichess (unauthenticated import,
	/// 100 games/hour/IP) and keep the returned URL for click-to-copy. Routed
	/// through <see cref="LichessApi"/>, which owns the single-flight + 60s-429
	/// rate discipline and — the actual M2 fix — sends <c>Accept: application/json</c>
	/// so lichess returns {id,url} instead of the game's HTML page (PLAN.md M2
	/// carry-in; confirmed in-editor: the old call got HTTP 200 + HTML).</summary>
	public async void ImportToLichess()
	{
		if ( Game == null || !Game.IsGameOver || Importing || LichessUrl != null ) return;
		if ( Game.MoveCount == 0 ) { _importError = "No move history on this client to import"; return; }

		var pgn = BuildPgn();
		Importing = true;
		_importError = null;
		try
		{
			var res = await LichessApi.ImportPgn( pgn );
			if ( !res.Ok )
			{
				_importError = res.Error ?? "lichess import failed";
				Log.Warning( $"[Gambit] import failed ({res.Status}): {LichessApi.Truncate( res.Body, 200 )}" );
				return;
			}

			var url = LichessApi.Deserialize<LichessImport>( res.Body )?.url;
			if ( string.IsNullOrEmpty( url ) )
			{
				_importError = "lichess sent an unexpected reply";
				Log.Warning( $"[Gambit] import reply had no url: {LichessApi.Truncate( res.Body, 200 )}" );
				return;
			}

			LichessUrl = url;
			Log.Info( $"[Gambit] game imported: {url}" );
		}
		finally
		{
			Importing = false;
		}
	}

	/// <summary>Copy the imported game URL — no API exists to open a browser
	/// in-game (CLAUDE.md), so click-to-copy like the Discord invite.</summary>
	public void CopyLichessUrl()
	{
		if ( string.IsNullOrEmpty( LichessUrl ) ) return;
		Clipboard.SetText( LichessUrl );
		SinceUrlCopied = 0f;
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
