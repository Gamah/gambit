using System;
using ChessLib = global::Chess;

namespace Gambit.Chess;

/// <summary>How hard the built-in computer opponent plays. Stored as an int in the
/// networked seat state (ChessStation), so the numeric values are load-bearing —
/// 0 means "this seat is a human / empty", never a bot.</summary>
public enum BotLevel { None = 0, Easy = 1, Medium = 2, Hard = 3 }

/// <summary>
/// The built-in chess engine — the opponent for a solo player who hasn't linked (or
/// registered) a lichess account. It is a negamax alpha-beta search over the SAME
/// vendored move generation the rules already use, so it lives here as a
/// <c>partial ChessGame</c>: that is the one place allowed to touch <c>_board</c> and
/// <c>UciOf</c>, and — being under <c>Code/Chess/</c> with no Sandbox dependency — it
/// runs in the dotnet harness, where it is actually proven (finds a mate in one,
/// grabs a hanging queen, and Hard beats Easy over a match). See CLAUDE.md's
/// "Three things DO run here".
///
/// <para>Deliberately modest and SELF-CONTAINED. No opening book, no transposition
/// table, no threads (the whitelist forbids them anyway). The search is bounded two
/// ways so a bot move can never freeze a frame indefinitely: a fixed depth per level,
/// and a hard node budget that makes a pathological position return its best-so-far
/// instead of searching on. The host drives it (LocalGameController.HostDriveBot), so
/// the cost lands on the one machine that already ticks every table's clock.</para>
///
/// <para>Randomness is a hand-rolled xorshift seeded by the caller, never
/// <c>System.Random</c> — same reason SHA-256 is hand-rolled: it sidesteps any
/// whitelist doubt and stays deterministic for the harness. The lower levels ADD
/// noise and the occasional outright blunder on purpose; that is what separates Easy
/// from Hard, not just search depth.</para>
/// </summary>
public sealed partial class ChessGame
{
	// Centipawn piece values, indexed by lowercase FEN char. King is 0 — its safety
	// is expressed through the piece-square table, not a material count that would
	// swamp everything else.
	static int PieceValue( char fen ) => char.ToLowerInvariant( fen ) switch
	{
		'p' => 100,
		'n' => 320,
		'b' => 330,
		'r' => 500,
		'q' => 900,
		'k' => 0,
		_ => 0,
	};

	const int Inf = 1_000_000;
	const int MateScore = 30_000; // distance-adjusted so a faster mate scores higher

	/// <summary>Per-level search parameters. Depth and the node cap bound the cost;
	/// noise and the blunder chance weaken the play below "best move it can find".</summary>
	readonly struct BotConfig
	{
		public readonly int Depth;
		public readonly bool Quiescence;
		public readonly int NoiseCp;          // ± centipawns of random perturbation on each root move
		public readonly float RandomMoveChance; // odds of ignoring the search and blundering a random legal move
		public readonly int NodeCap;

		public BotConfig( int depth, bool quiescence, int noiseCp, float randomMoveChance, int nodeCap )
		{
			Depth = depth;
			Quiescence = quiescence;
			NoiseCp = noiseCp;
			RandomMoveChance = randomMoveChance;
			NodeCap = nodeCap;
		}
	}

	// Depths are deliberately shallow: the vendored move generator filters checks by
	// make/unmake, so it is far heavier than a bitboard engine, and this search runs on a
	// worker thread whose wall-time is charged to the bot's own clock. Depth 1/2/3 keeps a
	// move well under a second while still laddering cleanly, and the node cap bounds the
	// worst case. Bump these only alongside a fresh harness timing run.
	static BotConfig Config( BotLevel level ) => level switch
	{
		// Easy: one ply, no capture-search (so it grabs defended pieces and hangs its own
		// to a recapture it never looked for), heavy noise and a real chance of an outright
		// random blunder — the most beatable setting.
		BotLevel.Easy => new BotConfig( depth: 1, quiescence: false, noiseCp: 130, randomMoveChance: 0.18f, nodeCap: 20_000 ),
		// Medium: two plies, NO capture-search, mild noise — so it still loses material to a
		// recapture it didn't extend into, and misjudges the odd trade. Clearly a step up
		// from Easy, clearly below Hard.
		BotLevel.Medium => new BotConfig( depth: 2, quiescence: false, noiseCp: 40, randomMoveChance: 0f, nodeCap: 30_000 ),
		// Hard: two plies PLUS a quiescence (capture) search and no noise. The quiescence,
		// not extra depth, is the jump — it stops Hard hanging material to a simple recapture
		// and lets it read a tactic a ply or two deep. Depth is capped at 2 on purpose: the
		// vendored move generator filters legality by make/unmake, so it is far too slow for
		// a deep search, and this runs on the bot's own clock. The harness pins the payoff:
		// Hard must find a mate in one and decline a poisoned capture.
		_ => new BotConfig( depth: 2, quiescence: true, noiseCp: 0, randomMoveChance: 0f, nodeCap: 50_000 ),
	};

	int _nodeBudget;

	/// <summary>
	/// Choose a move for the side to move at the current position, as UCI ("e2e4",
	/// "e7e8q"), or null if the game is over (no legal moves). <paramref name="seed"/>
	/// seeds the perturbation so a caller can vary play across games while keeping the
	/// harness deterministic.
	/// </summary>
	public string BestMove( BotLevel level, int seed )
	{
		if ( IsGameOver ) return null;

		var moves = _board.Moves( allowAmbiguousCastle: false, generateSan: false );
		if ( moves.Length == 0 ) return null;

		var cfg = Config( level );
		var rng = new Rng( (ulong)seed ^ 0x9E3779B97F4A7C15UL );

		// The occasional pure blunder — the thing that most makes Easy feel beatable.
		if ( cfg.RandomMoveChance > 0f && rng.NextFloat() < cfg.RandomMoveChance )
		{
			string blunder = UciOf( moves[rng.NextInt( moves.Length )] );
			InvalidateAfterSearch();
			return blunder;
		}

		Order( moves );
		_nodeBudget = cfg.NodeCap;

		string bestUci = UciOf( moves[0] );
		int pureBest = -Inf;   // best TRUE score — drives pruning, so a cutoff is sound
		int bestPick = -Inf;   // best NOISY score — drives selection, so lower levels wobble
		int alpha = -Inf;
		const int beta = Inf;

		foreach ( var move in moves )
		{
			_board.Move( move );
			int score = -Negamax( cfg.Depth - 1, -beta, -alpha, cfg, ref rng, 1 );
			_board.Cancel();

			if ( score > pureBest ) pureBest = score;
			if ( pureBest > alpha ) alpha = pureBest;

			int pick = cfg.NoiseCp > 0 ? score + rng.NextRange( cfg.NoiseCp ) : score;
			if ( pick > bestPick )
			{
				bestPick = pick;
				bestUci = UciOf( move );
			}
		}

		InvalidateAfterSearch();
		return bestUci;
	}

	int Negamax( int depth, int alpha, int beta, in BotConfig cfg, ref Rng rng, int ply )
	{
		// Node budget: a bounded search can't hang a frame. Bail to a static read.
		if ( --_nodeBudget <= 0 ) return Evaluate();

		var moves = _board.Moves( allowAmbiguousCastle: false, generateSan: false );
		if ( moves.Length == 0 )
		{
			// No legal move: checkmate if the mover is in check, else stalemate. The
			// mate score is pulled toward zero by ply so a mate in 1 beats a mate in 3.
			bool inCheck = _board.Turn == ChessLib.PieceColor.White
				? _board.WhiteKingChecked
				: _board.BlackKingChecked;
			return inCheck ? -MateScore + ply : 0;
		}

		if ( depth <= 0 )
			return cfg.Quiescence ? Quiesce( alpha, beta, ref rng ) : Evaluate();

		Order( moves );

		int best = -Inf;
		foreach ( var move in moves )
		{
			_board.Move( move );
			int score = -Negamax( depth - 1, -beta, -alpha, cfg, ref rng, ply + 1 );
			_board.Cancel();

			if ( score > best ) best = score;
			if ( best > alpha ) alpha = best;
			if ( alpha >= beta ) break; // fail-high: the opponent won't allow this line
		}
		return best;
	}

	/// <summary>Quiescence search: at the horizon, keep resolving CAPTURES so the
	/// static eval isn't taken in the middle of a trade (the horizon effect). Standing
	/// pat lets a side stop capturing when it's already ahead.</summary>
	int Quiesce( int alpha, int beta, ref Rng rng )
	{
		if ( --_nodeBudget <= 0 ) return Evaluate();

		int stand = Evaluate();
		if ( stand >= beta ) return beta;
		if ( stand > alpha ) alpha = stand;

		var moves = _board.Moves( allowAmbiguousCastle: false, generateSan: false );
		Order( moves );

		foreach ( var move in moves )
		{
			// Captures (incl. en passant) and promotions only — the forcing moves.
			if ( move.CapturedPiece == null && move.Parameter is not ChessLib.MovePromotion )
				continue;

			_board.Move( move );
			int score = -Quiesce( -beta, -alpha, ref rng );
			_board.Cancel();

			if ( score >= beta ) return beta;
			if ( score > alpha ) alpha = score;
		}
		return alpha;
	}

	/// <summary>Order moves best-first so alpha-beta prunes hard: winning captures
	/// (MVV-LVA — grab the fattest piece with the cheapest attacker) then promotions,
	/// then the rest. A simple insertion sort — the list is short (~35).</summary>
	static void Order( ChessLib.Move[] moves )
	{
		for ( int i = 1; i < moves.Length; i++ )
		{
			var m = moves[i];
			int key = MoveScore( m );
			int j = i - 1;
			while ( j >= 0 && MoveScore( moves[j] ) < key )
			{
				moves[j + 1] = moves[j];
				j--;
			}
			moves[j + 1] = m;
		}
	}

	static int MoveScore( ChessLib.Move m )
	{
		int score = 0;
		if ( m.CapturedPiece != null )
			score += 1000 + PieceValue( m.CapturedPiece.ToFenChar() ) * 8 - PieceValue( m.Piece.ToFenChar() );
		if ( m.Parameter is ChessLib.MovePromotion )
			score += 900;
		return score;
	}

	/// <summary>Static evaluation in centipawns, from the SIDE-TO-MOVE's perspective
	/// (negamax convention). Material plus a piece-square positional term.</summary>
	int Evaluate()
	{
		int white = 0; // white-minus-black running total (positive = good for White)
		for ( int rank = 0; rank < 8; rank++ )
		{
			for ( int file = 0; file < 8; file++ )
			{
				var piece = _board[file, rank];
				if ( piece == null ) continue;

				char fen = piece.ToFenChar();
				bool isWhite = char.IsUpper( fen );
				int val = PieceValue( fen ) + SquareBonus( char.ToLowerInvariant( fen ), file, rank, isWhite );
				white += isWhite ? val : -val;
			}
		}
		return _board.Turn == ChessLib.PieceColor.White ? white : -white;
	}

	/// <summary>Piece-square bonus. Tables are written from White's side (rank 0 =
	/// White's back rank); Black reads the same table mirrored top-to-bottom.</summary>
	static int SquareBonus( char type, int file, int rank, bool isWhite )
	{
		int r = isWhite ? rank : 7 - rank;
		int idx = r * 8 + file;
		return type switch
		{
			'p' => PawnPst[idx],
			'n' => KnightPst[idx],
			'b' => BishopPst[idx],
			'r' => RookPst[idx],
			'q' => QueenPst[idx],
			'k' => KingPst[idx],
			_ => 0,
		};
	}

	// Classic middlegame piece-square tables (index 0 = a1, 63 = h8), White's view.
	static readonly int[] PawnPst =
	{
		 0,  0,  0,  0,  0,  0,  0,  0,
		 5, 10, 10,-20,-20, 10, 10,  5,
		 5, -5,-10,  0,  0,-10, -5,  5,
		 0,  0,  0, 20, 20,  0,  0,  0,
		 5,  5, 10, 25, 25, 10,  5,  5,
		10, 10, 20, 30, 30, 20, 10, 10,
		50, 50, 50, 50, 50, 50, 50, 50,
		 0,  0,  0,  0,  0,  0,  0,  0,
	};
	static readonly int[] KnightPst =
	{
		-50,-40,-30,-30,-30,-30,-40,-50,
		-40,-20,  0,  5,  5,  0,-20,-40,
		-30,  5, 10, 15, 15, 10,  5,-30,
		-30,  0, 15, 20, 20, 15,  0,-30,
		-30,  5, 15, 20, 20, 15,  5,-30,
		-30,  0, 10, 15, 15, 10,  0,-30,
		-40,-20,  0,  0,  0,  0,-20,-40,
		-50,-40,-30,-30,-30,-30,-40,-50,
	};
	static readonly int[] BishopPst =
	{
		-20,-10,-10,-10,-10,-10,-10,-20,
		-10,  5,  0,  0,  0,  0,  5,-10,
		-10, 10, 10, 10, 10, 10, 10,-10,
		-10,  0, 10, 10, 10, 10,  0,-10,
		-10,  5,  5, 10, 10,  5,  5,-10,
		-10,  0,  5, 10, 10,  5,  0,-10,
		-10,  0,  0,  0,  0,  0,  0,-10,
		-20,-10,-10,-10,-10,-10,-10,-20,
	};
	static readonly int[] RookPst =
	{
		  0,  0,  0,  5,  5,  0,  0,  0,
		 -5,  0,  0,  0,  0,  0,  0, -5,
		 -5,  0,  0,  0,  0,  0,  0, -5,
		 -5,  0,  0,  0,  0,  0,  0, -5,
		 -5,  0,  0,  0,  0,  0,  0, -5,
		 -5,  0,  0,  0,  0,  0,  0, -5,
		  5, 10, 10, 10, 10, 10, 10,  5,
		  0,  0,  0,  0,  0,  0,  0,  0,
	};
	static readonly int[] QueenPst =
	{
		-20,-10,-10, -5, -5,-10,-10,-20,
		-10,  0,  5,  0,  0,  0,  0,-10,
		-10,  5,  5,  5,  5,  5,  0,-10,
		  0,  0,  5,  5,  5,  5,  0, -5,
		 -5,  0,  5,  5,  5,  5,  0, -5,
		-10,  0,  5,  5,  5,  5,  0,-10,
		-10,  0,  0,  0,  0,  0,  0,-10,
		-20,-10,-10, -5, -5,-10,-10,-20,
	};
	// Middlegame king: stay tucked back and castled, off the centre.
	static readonly int[] KingPst =
	{
		 20, 30, 10,  0,  0, 10, 30, 20,
		 20, 20,  0,  0,  0,  0, 20, 20,
		-10,-20,-20,-20,-20,-20,-20,-10,
		-20,-30,-30,-40,-40,-30,-30,-20,
		-30,-40,-40,-50,-50,-40,-40,-30,
		-30,-40,-40,-50,-50,-40,-40,-30,
		-30,-40,-40,-50,-50,-40,-40,-30,
		-30,-40,-40,-50,-50,-40,-40,-30,
	};

	/// <summary>The search churns <c>_board</c> through Move/Cancel behind the read
	/// caches' back, exactly as Perft does — reset them so the next state read rebuilds.
	/// The board itself is restored (every Move is paired with a Cancel).</summary>
	void InvalidateAfterSearch()
	{
		_fen = null;
		_checkedKingDirty = true;
	}

	/// <summary>A tiny deterministic xorshift RNG. Hand-rolled rather than
	/// <c>System.Random</c> for the same reason SHA-256 is: no whitelist doubt, and it
	/// reproduces exactly in the harness from a seed.</summary>
	struct Rng
	{
		ulong _s;
		public Rng( ulong seed ) => _s = seed == 0 ? 0xDEADBEEFUL : seed;

		public ulong Next()
		{
			_s ^= _s << 13;
			_s ^= _s >> 7;
			_s ^= _s << 17;
			return _s;
		}

		/// <summary>A float in [0, 1).</summary>
		public float NextFloat() => ( Next() >> 40 ) / (float)( 1UL << 24 );

		/// <summary>An int in [0, n).</summary>
		public int NextInt( int n ) => n <= 1 ? 0 : (int)( Next() % (ulong)n );

		/// <summary>An int in [-mag, +mag].</summary>
		public int NextRange( int mag ) => NextInt( 2 * mag + 1 ) - mag;
	}
}
