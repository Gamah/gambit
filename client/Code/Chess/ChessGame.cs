using System;
using System.Collections.Generic;
using ChessLib = global::Chess;

namespace Gambit.Chess;

/// <summary>How a finished game ended, from White's perspective.</summary>
public enum GameResult { Ongoing, WhiteWon, BlackWon, Draw }

/// <summary>
/// The one seam between Gambit and the vendored Gera Chess Library (CLAUDE.md D2):
/// every caller — board view, game controllers, HUD, PGN import — talks to this
/// wrapper and never to global::Chess types, so the vendor stays swappable for
/// the compact hand-written move-gen fallback if the whitelist ever bites.
///
/// Moves in and out are UCI ("e2e4", "e7e8q" — castling as king move "e1g1"),
/// matching the wire format of NetChessMove.
/// </summary>
public sealed class ChessGame
{
	readonly ChessLib.ChessBoard _board;

	/// <summary>New game at the standard start position.</summary>
	public ChessGame()
	{
		// Auto-draw rules on: insufficient material, threefold repetition and the
		// fifty-move rule end a local game without either player having to claim.
		_board = new ChessLib.ChessBoard { AutoEndgameRules = ChessLib.AutoEndgameRules.All };
	}

	ChessGame( ChessLib.ChessBoard board ) => _board = board;

	/// <summary>Game from a FEN snapshot (spectator boards, late joiners). The
	/// resulting game has no move history — SAN list and PGN start empty.</summary>
	public static bool TryFromFen( string fen, out ChessGame game )
	{
		game = null;
		if ( string.IsNullOrWhiteSpace( fen ) ) return false;
		if ( !ChessLib.ChessBoard.TryLoadFromFen( fen, out var board ) ) return false;
		game = new ChessGame( board );
		return true;
	}

	/// <summary>
	/// Reconstruct the position <paramref name="ply"/> half-moves into a game given
	/// its PGN/movetext (used by the archive viewer's replay — it hands us a PGN + an
	/// <c>initialPly</c>; TV snapshots hand us an export PGN). The vendor parses the
	/// movetext, we navigate to the requested ply, snapshot its FEN, then rebuild a
	/// <b>clean</b> game from that FEN so the caller gets full move-gen with no
	/// navigation state to trip over. <paramref name="ply"/> is clamped to the moves
	/// available; pass <see cref="int.MaxValue"/> for the final position.
	/// </summary>
	public static bool TryFromPgnAtPly( string pgn, int ply, out ChessGame game )
	{
		game = null;
		if ( string.IsNullOrWhiteSpace( pgn ) ) return false;
		if ( !ChessLib.ChessBoard.TryLoadFromPgn( pgn, out var board ) || board == null ) return false;

		int count = board.ExecutedMoves.Count;
		if ( count == 0 ) return board.ToFen() is { } f0 && TryFromFen( f0, out game );

		// MoveIndex is the 0-based index of the displayed move; index k shows the
		// position after k+1 half-moves. Clamp ply into [0, count].
		int target = ply < 0 ? 0 : ply > count ? count : ply;
		board.MoveIndex = target - 1; // -1 = the start position (before any move)

		if ( board.ToFen() is not { } fen || !TryFromFen( fen, out game ) ) return false;

		// Carry the last displayed move as UCI so callers can highlight it (the move that
		// led to a puzzle, or the move that ended a finished game — the board view keys its
		// last-move highlight off this). Same class, so we can set the private field.
		if ( target >= 1 )
			game._lastMoveUci = UciOf( board.ExecutedMoves[target - 1] );
		return true;
	}

	/// <summary>UCI of a vendor move ("e2e4", "e7e8q").</summary>
	static string UciOf( ChessLib.Move move )
	{
		string uci = move.OriginalPosition.ToString() + move.NewPosition.ToString();
		if ( move.Parameter is ChessLib.MovePromotion promo )
			uci += char.ToLowerInvariant( promo.PromotionResult.AsChar );
		return uci;
	}

	/// <summary>Reconstruct the final position of a PGN/movetext (TV snapshots).</summary>
	public static bool TryFromPgn( string pgn, out ChessGame game ) =>
		TryFromPgnAtPly( pgn, int.MaxValue, out game );

	/// <summary>
	/// Every position of a PGN's movetext as (fen, lastMoveUci), in ply order — index 0 is the
	/// position after the first half-move, the last entry is the final position. Empty when the
	/// PGN has no moves. Parses once and walks the move cursor, so the spectator can replay a
	/// multi-move poll gap one move at a time (a smooth slide per move) instead of teleporting
	/// several pieces at once.
	/// </summary>
	public static List<(string fen, string lastMoveUci)> PgnPositions( string pgn )
	{
		var positions = new List<(string, string)>();
		if ( string.IsNullOrWhiteSpace( pgn ) ) return positions;
		if ( !ChessLib.ChessBoard.TryLoadFromPgn( pgn, out var board ) || board == null ) return positions;

		int count = board.ExecutedMoves.Count;
		for ( int ply = 1; ply <= count; ply++ )
		{
			// MoveIndex m shows the position after m+1 half-moves; m = ply-1 → after `ply` moves.
			board.MoveIndex = ply - 1;
			if ( board.ToFen() is not { } fen ) continue;
			positions.Add( (fen, UciOf( board.ExecutedMoves[ply - 1] )) );
		}
		return positions;
	}

	// ── State reads ──
	// The board view and HUD poll these every frame for every table, so the
	// values the vendor recomputes/copies on each call are cached here and
	// refreshed only when a move mutates the board. Fen in particular returns
	// the SAME string instance between moves — ChessBoardView uses that as its
	// cheap "position unchanged" check.

	string _fen;
	string _lastMoveUci;
	string _checkedKingSquare;
	bool _checkedKingDirty = true;
	int _moveCount;

	public string Fen => _fen ??= _board.ToFen();

	public bool WhiteToMove => _board.Turn == ChessLib.PieceColor.White;

	public bool IsGameOver => _board.IsEndGame;

	/// <summary>Side to move is in check (view highlights the king square).</summary>
	public bool IsCheck => _board.WhiteKingChecked || _board.BlackKingChecked;

	/// <summary>Square ("e1") of the currently checked king, or null.</summary>
	public string CheckedKingSquare
	{
		get
		{
			if ( _checkedKingDirty )
			{
				_checkedKingSquare =
					_board.WhiteKingChecked ? _board.WhiteKing.ToString()
					: _board.BlackKingChecked ? _board.BlackKing.ToString()
					: null;
				_checkedKingDirty = false;
			}
			return _checkedKingSquare;
		}
	}

	/// <summary>Executed moves in SAN, for the HUD move list. Copies — call on
	/// rebuild, not per frame.</summary>
	public IReadOnlyList<string> SanMoves => _board.MovesToSan;

	/// <summary>Number of half-moves applied to this game object.</summary>
	public int MoveCount => _moveCount;

	/// <summary>Last applied move as normalized UCI (promotions always carry
	/// their piece char), or null before the first move. FEN-loaded spectator
	/// games start with null and rely on the relayed lastMoveUci instead.</summary>
	public string LastMoveUci => _lastMoveUci;

	/// <summary>UCI of the move <paramref name="back"/> plies before the last (0 = the
	/// last move, 1 = the one before it), or null when history doesn't reach. Exists for
	/// the seated hands: a premove reply can land in the same observation as the move it
	/// answers, and the 2-ply jump's EARLIER move — the one LastMoveUci no longer names —
	/// is the one the other seat's hand still has to play.</summary>
	public string UciFromEnd( int back )
	{
		int i = _board.ExecutedMoves.Count - 1 - back;
		return back >= 0 && i >= 0 ? UciOf( _board.ExecutedMoves[i] ) : null;
	}

	void OnBoardMutated( string uci )
	{
		_fen = null;
		_checkedKingDirty = true;
		_lastMoveUci = uci;
		_moveCount++;
	}

	public GameResult Result
	{
		get
		{
			if ( !_board.IsEndGame ) return GameResult.Ongoing;
			var won = _board.EndGame.WonSide;
			if ( won == ChessLib.PieceColor.White ) return GameResult.WhiteWon;
			if ( won == ChessLib.PieceColor.Black ) return GameResult.BlackWon;
			return GameResult.Draw;
		}
	}

	/// <summary>Human-readable end reason ("Checkmate", "Stalemate", "Resigned",
	/// "Insufficient material"…), or null while the game runs.</summary>
	public string ResultReason => _board.EndGame?.EndgameType switch
	{
		ChessLib.EndgameType.Checkmate => "Checkmate",
		ChessLib.EndgameType.Resigned => "Resignation",
		ChessLib.EndgameType.Timeout => "Timeout",
		ChessLib.EndgameType.Stalemate => "Stalemate",
		ChessLib.EndgameType.DrawDeclared => "Draw agreed",
		ChessLib.EndgameType.InsufficientMaterial => "Insufficient material",
		ChessLib.EndgameType.FiftyMoveRule => "Fifty-move rule",
		ChessLib.EndgameType.Repetition => "Threefold repetition",
		null => null,
		_ => "Game over",
	};

	/// <summary>PGN result tag: "1-0", "0-1", "1/2-1/2" or "*" while running.</summary>
	public string ResultString => Result switch
	{
		GameResult.WhiteWon => "1-0",
		GameResult.BlackWon => "0-1",
		GameResult.Draw => "1/2-1/2",
		_ => "*",
	};

	/// <summary>FEN char of the piece on (file 0-7, rank 0-7), or '\0' for an
	/// empty square. Uppercase = White. The board view renders by diffing this.</summary>
	public char PieceAt( int file, int rank )
	{
		if ( file is < 0 or > 7 || rank is < 0 or > 7 ) return '\0';
		var piece = _board[file, rank];
		return piece?.ToFenChar() ?? '\0';
	}

	// ── Moves ──

	/// <summary>Legal destination squares for the piece on
	/// <paramref name="fromSquare"/> ("e2"), empty when it isn't the mover's
	/// piece or the square is empty. Castling shows as the king's g/c-file hop.</summary>
	public List<string> LegalTargets( string fromSquare )
	{
		var targets = new List<string>();
		if ( IsGameOver || !TryParseSquare( fromSquare, out var from ) ) return targets;
		if ( _board[from] == null || _board[from].Color != _board.Turn ) return targets;

		foreach ( var move in _board.Moves( from, allowAmbiguousCastle: false, generateSan: false ) )
		{
			var target = move.NewPosition.ToString();
			if ( !targets.Contains( target ) ) // promotions produce 4 moves per square
				targets.Add( target );
		}
		return targets;
	}

	/// <summary>
	/// Squares the piece on <paramref name="fromSquare"/> may be PREMOVED to —
	/// the moves worth arming while the opponent is still thinking.
	///
	/// <para>These are deliberately NOT legal moves, and can't be: a premove is aimed
	/// at a position that doesn't exist yet. The commonest premove of all — a
	/// recapture onto a square the opponent hasn't captured on yet — is illegal in
	/// every position where you'd want to arm it. So this is PURE geometric mobility:
	/// blockers ignored (they may be gone by the time it fires), pawn diagonals always
	/// offered, and squares holding your OWN pieces offered as well — a recapture aims
	/// at exactly those. Check, pins and turn order are not consulted.</para>
	///
	/// <para>That makes it permissive: it will happily arm a rook slide through a wall
	/// of pawns, or a queen onto a friendly knight that isn't going anywhere. It is not
	/// a promise the move will play. Legality is decided once, for real, by
	/// <see cref="ApplyUci"/> at the moment the premove fires, and a premove that
	/// doesn't survive contact is dropped. Being permissive costs a discarded premove;
	/// being strict cost the recapture the feature exists for, which is how the first
	/// version shipped unable to do the one thing anyone wanted it for.</para>
	///
	/// <para>Colour is taken from the PIECE, not from whose turn it is — during a
	/// premove it is by definition not your turn.</para>
	/// </summary>
	public List<string> PremoveTargets( string fromSquare )
	{
		var targets = new List<string>();
		if ( IsGameOver || !TryParseSquare( fromSquare, out var from ) ) return targets;

		var piece = _board[from];
		if ( piece == null ) return targets;

		bool white = piece.Color == ChessLib.PieceColor.White;
		int ff = from.X, fr = from.Y;

		void Add( int f, int r )
		{
			if ( f is < 0 or > 7 || r is < 0 or > 7 ) return;

			// Only the piece's own square is excluded, and only because a move to it
			// isn't a move. NOTHING else is filtered — occupancy least of all.
			//
			// A square holding YOUR OWN piece is offered, which looks wrong and is the
			// most important case in here. During the opponent's turn your pieces never
			// move, so the only way such a square can free up is if they CAPTURE the
			// piece standing on it — which makes "premove onto my own piece" precisely
			// the recapture, the commonest premove in chess. Filtering own-occupied
			// squares (the first version did) leaves the feature unable to do the one
			// thing it's for.
			if ( f == ff && r == fr ) return;

			targets.Add( $"{(char)( 'a' + f )}{(char)( '1' + r )}" );
		}

		void Ray( int df, int dr )
		{
			for ( int i = 1; i < 8; i++ ) Add( ff + df * i, fr + dr * i );
		}

		var type = piece.Type;
		int backRank = white ? 0 : 7;

		if ( type == ChessLib.PieceType.Pawn )
		{
			int dir = white ? 1 : -1;
			Add( ff, fr + dir );
			if ( fr == ( white ? 1 : 6 ) ) Add( ff, fr + 2 * dir );
			// Both diagonals, occupied or not: the capture you're waiting for
			// hasn't happened yet, and en passant lands on an empty square too.
			Add( ff - 1, fr + dir );
			Add( ff + 1, fr + dir );
		}
		else if ( type == ChessLib.PieceType.Knight )
		{
			int[] df = { 1, 2, 2, 1, -1, -2, -2, -1 };
			int[] dr = { 2, 1, -1, -2, -2, -1, 1, 2 };
			for ( int i = 0; i < 8; i++ ) Add( ff + df[i], fr + dr[i] );
		}
		else if ( type == ChessLib.PieceType.Bishop )
		{
			Ray( 1, 1 ); Ray( 1, -1 ); Ray( -1, 1 ); Ray( -1, -1 );
		}
		else if ( type == ChessLib.PieceType.Rook )
		{
			Ray( 1, 0 ); Ray( -1, 0 ); Ray( 0, 1 ); Ray( 0, -1 );
		}
		else if ( type == ChessLib.PieceType.Queen )
		{
			Ray( 1, 0 ); Ray( -1, 0 ); Ray( 0, 1 ); Ray( 0, -1 );
			Ray( 1, 1 ); Ray( 1, -1 ); Ray( -1, 1 ); Ray( -1, -1 );
		}
		else if ( type == ChessLib.PieceType.King )
		{
			for ( int f = -1; f <= 1; f++ )
				for ( int r = -1; r <= 1; r++ )
					Add( ff + f, fr + r );
			// Castling, as the same g/c-file hop LegalTargets reports. Only from
			// the home square, so it isn't offered for a king that has obviously
			// already moved.
			if ( ff == 4 && fr == backRank ) { Add( 6, fr ); Add( 2, fr ); }
		}

		return targets;
	}

	/// <summary>Whether from→to is a pawn move onto the last rank, i.e. the view
	/// must pop the promotion picker and append the piece char to the UCI.</summary>
	public bool IsPromotion( string fromSquare, string toSquare )
	{
		if ( !TryParseSquare( fromSquare, out var from ) || !TryParseSquare( toSquare, out _ ) )
			return false;
		var piece = _board[from];
		return piece?.Type == ChessLib.PieceType.Pawn && toSquare[1] is '1' or '8';
	}

	/// <summary>
	/// Validate and apply a UCI move ("e2e4", "e7e8q"). Returns false — with no
	/// state change — for anything illegal in the current position. A promotion
	/// without a piece char defaults to queen.
	/// </summary>
	public bool ApplyUci( string uci )
	{
		if ( IsGameOver || uci is null || uci.Length is < 4 or > 5 ) return false;
		if ( !TryParseSquare( uci[..2], out var from ) || !TryParseSquare( uci[2..4], out _ ) )
			return false;
		char promo = uci.Length == 5 ? char.ToLowerInvariant( uci[4] ) : '\0';
		if ( promo != '\0' && "qrbn".IndexOf( promo ) < 0 ) return false;

		if ( _board[from] == null || _board[from].Color != _board.Turn ) return false;

		string to = uci[2..4];
		foreach ( var move in _board.Moves( from, allowAmbiguousCastle: false, generateSan: false ) )
		{
			if ( move.NewPosition.ToString() != to ) continue;

			string applied = uci[..4];
			if ( move.Parameter is ChessLib.MovePromotion promotion )
			{
				char produced = char.ToLowerInvariant( promotion.PromotionResult.AsChar );
				if ( produced != ( promo == '\0' ? 'q' : promo ) ) continue;
				applied += produced; // normalize: promotions always carry the char
			}
			else if ( promo != '\0' )
			{
				return false; // promotion char on a non-promotion move
			}

			if ( !_board.Move( move ) ) return false;
			OnBoardMutated( applied );
			return true;
		}

		return false;
	}

	// ── Endings the rules can't see ──

	public void Resign( bool whiteResigned )
	{
		if ( !IsGameOver )
			_board.Resign( whiteResigned ? ChessLib.PieceColor.White : ChessLib.PieceColor.Black );
	}

	public void AgreeDraw()
	{
		if ( !IsGameOver )
			_board.Draw();
	}

	// ── Takeback ──

	/// <summary>
	/// Rewind to <paramref name="ply"/> half-moves, KEEPING the history of the moves
	/// that survive. Returns false if the position can't be rewound that far.
	///
	/// <para>This is what a takeback needs, and <see cref="TryFromPgnAtPly"/> is NOT:
	/// that rebuilds a CLEAN game from the FEN at a ply, so the result has no move list,
	/// a <c>[Variant "From Position"]</c> header and a <c>MoveCount</c> of 0. A takeback
	/// built on it looks right on the board and quietly destroys the game — the HUD's
	/// move list empties, and the archived PGN keeps only the moves played after the
	/// takeback, permanently, because the upload is idempotent on client_game_id.</para>
	///
	/// <para>So it undoes in place, one move at a time, through the vendored board's own
	/// <c>Cancel()</c> — which restores the captured piece, undoes castling/en-passant
	/// via the move's Parameter, pops <c>executedMoves</c> and clears any EndGame. Two
	/// properties fall out of that and BOTH are load-bearing: the SAN list stays real
	/// (so the game is still archivable), and <see cref="MoveCount"/> stays an ABSOLUTE
	/// ply count from the standard start — which is the index space the caller's clock
	/// log is keyed in.</para>
	/// </summary>
	public bool TruncateToPly( int ply )
	{
		if ( ply < 0 || ply > _moveCount ) return false;

		bool ok = true;
		while ( _moveCount > ply )
		{
			int before = _board.ExecutedMoves.Count;
			_board.Cancel();

			// Cancel() no-ops rather than throwing if it can't (nothing to undo, or the
			// board isn't displaying its last move). Spinning on that would hang the
			// frame, so stop and report failure instead.
			if ( _board.ExecutedMoves.Count >= before ) { ok = false; break; }

			_moveCount--;
		}

		// Refreshed on BOTH exits, and that is the point. Every undo is atomic but the
		// walk is not: bailing early having already cancelled a move or two would
		// otherwise leave a post-truncation MoveCount reporting a pre-truncation Fen
		// and LastMoveUci. The caller's failure path publishes exactly those two to
		// resync the table — so stale caches here would make the recovery the desync.
		_fen = null;
		_checkedKingDirty = true;
		_lastMoveUci = _moveCount > 0
			? UciOf( _board.ExecutedMoves[_moveCount - 1] )
			: null;
		return ok;
	}

	// ── PGN ──

	/// <summary>Set a PGN header, replacing any existing value.</summary>
	public void SetHeader( string name, string value )
	{
		if ( string.IsNullOrWhiteSpace( name ) || string.IsNullOrWhiteSpace( value ) ) return;
		if ( name.Equals( "fen", StringComparison.OrdinalIgnoreCase ) ) return; // vendor manages FEN itself
		_board.RemoveHeader( name );
		_board.AddHeader( name, value );
	}

	/// <summary>
	/// Attach a PGN brace comment to an already-played move, by 0-based ply (ply 0 is
	/// White's first). Written after the SAN as <c>{text}</c> — pass the text without
	/// braces, e.g. <c>[%clk 0:02:58]</c>. Purely decorative: nothing in the rules reads it.
	/// <para>Returns false when the ply isn't in this game's history, so a caller whose
	/// clock record has drifted out of step simply annotates nothing rather than throwing
	/// or mislabelling a move.</para>
	/// </summary>
	public bool SetMoveComment( int ply, string comment )
	{
		if ( ply < 0 ) return false;

		// ExecutedMoves hands back a fresh list, but the Move objects in it are the
		// board's own — mutating one here is what reaches the PGN writer.
		var moves = _board.ExecutedMoves;
		if ( ply >= moves.Count ) return false;

		moves[ply].Comment = comment;
		return true;
	}

	/// <summary>
	/// Format seconds as a PGN <c>%clk</c> field, matching what lichess's own PGN library
	/// reads and writes: <c>H:MM:SS</c> with an optional fraction — hours unpadded,
	/// minutes and seconds zero-padded to two, and trailing zeros stripped from the
	/// fraction so a whole second is plain <c>0:03:00</c>.
	///
	/// <para>Verified 2026-07-15 against two independent implementations that agree:
	/// dartchess (lichess-org's own, <c>lib/src/pgn.dart</c>) parses
	/// <c>(\d{1,5}):(\d{1,2}):(\d{1,2}(?:\.\d{0,3})?)</c> and strips trailing zeros when
	/// writing; python-chess (<c>chess/pgn.py</c>) writes
	/// <c>f"{seconds:06.3f}".rstrip("0").rstrip(".")</c>. Both cap the fraction at three
	/// decimals.</para>
	///
	/// <para>We emit at most CENTIseconds, not milliseconds. Three decimals is legal but
	/// would be false precision: the clock is decremented by a frame delta (~16ms), so a
	/// millisecond digit is noise, and lichess itself keeps clocks in centiseconds.
	/// Two decimals is a strict subset of the format, so every reader above still takes it.</para>
	///
	/// <para>Rounds to the nearest centisecond, which is what python-chess's
	/// <c>:06.3f</c> does at its own precision. Truncating instead would systematically
	/// shave a centisecond off, because float32 can't hold these values exactly — 9.73f
	/// is really 9.7299995, which floors to 9.72. Note this is the opposite choice from
	/// <c>TimeControl.Format</c>, deliberately: that one drives a live clock, where
	/// reading high is a lie, whereas this writes an archival record, where matching the
	/// reference implementations matters more than a 5ms bias. Never negative — a flagged
	/// clock is 0:00:00.</para>
	/// </summary>
	public static string ClkField( float seconds )
	{
		if ( float.IsNaN( seconds ) || seconds <= 0f ) return "0:00:00";

		int cs = (int)( seconds * 100f + 0.5f );   // nearest centisecond
		int total = cs / 100;
		int frac = cs % 100;

		string body = $"{total / 3600}:{( total % 3600 ) / 60:00}:{total % 60:00}";
		if ( frac == 0 ) return body;                      // trailing zeros stripped
		return frac % 10 == 0 ? $"{body}.{frac / 10}" : $"{body}.{frac:00}";
	}

	/// <summary>Full PGN (headers + SAN movetext + result) for POST /api/import.</summary>
	public string Pgn => _board.ToPgn();

	// ── Perft (correctness gate — see PerftCommand) ──

	/// <summary>Leaf-node count of the legal move tree at the given depth.</summary>
	public long Perft( int depth )
	{
		if ( depth <= 0 ) return 1;
		long nodes = PerftInner( _board, depth );
		// Move/Cancel churned the board behind the caches' back
		_fen = null;
		_checkedKingDirty = true;
		return nodes;
	}

	static long PerftInner( ChessLib.ChessBoard board, int depth )
	{
		var moves = board.Moves( allowAmbiguousCastle: false, generateSan: false );
		if ( depth == 1 ) return moves.Length;

		long nodes = 0;
		foreach ( var move in moves )
		{
			if ( !board.Move( move ) )
				throw new InvalidOperationException( $"perft: generated move rejected: {move}" );
			nodes += PerftInner( board, depth - 1 );
			board.Cancel();
		}
		return nodes;
	}

	// ── Helpers ──

	static bool TryParseSquare( string square, out ChessLib.Position position )
	{
		position = default;
		if ( square is not { Length: 2 } ) return false;
		if ( square[0] is < 'a' or > 'h' || square[1] is < '1' or > '8' ) return false;
		position = new ChessLib.Position( square );
		return true;
	}
}
