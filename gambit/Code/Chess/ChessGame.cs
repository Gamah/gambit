using System;
using System.Collections.Generic;
using ChessLib = global::Chess;

namespace Gambit.Chess;

/// <summary>How a finished game ended, from White's perspective.</summary>
public enum GameResult { Ongoing, WhiteWon, BlackWon, Draw }

/// <summary>
/// The one seam between Gambit and the vendored Gera Chess Library (PLAN.md D2):
/// every caller — board view, game controllers, HUD, PGN import — talks to this
/// wrapper and never to global::Chess types, so the vendor stays swappable for
/// the compact hand-written move-gen fallback if the whitelist ever bites.
///
/// Moves in and out are UCI ("e2e4", "e7e8q" — castling as king move "e1g1"),
/// matching both lichess's Board API and the wire format of NetChessMove.
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
	/// its PGN/movetext (M5 — lichess puzzles hand us <c>game.pgn</c> + an
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

	// ── PGN ──

	/// <summary>Set a PGN header, replacing any existing value.</summary>
	public void SetHeader( string name, string value )
	{
		if ( string.IsNullOrWhiteSpace( name ) || string.IsNullOrWhiteSpace( value ) ) return;
		if ( name.Equals( "fen", StringComparison.OrdinalIgnoreCase ) ) return; // vendor manages FEN itself
		_board.RemoveHeader( name );
		_board.AddHeader( name, value );
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
