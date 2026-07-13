using System;
using Sandbox;

namespace Gambit.Chess;

/// <summary>
/// Correctness gate for the vendored chess rules (PLAN.md D2): perft counts the
/// legal-move tree from reference positions and compares against the published
/// node counts (chessprogramming.org/Perft_Results). Run `gambit_perft` in the
/// console — every line must PASS before trusting the rules for real games.
/// The same suite passes on the dev host in a plain dotnet harness; this
/// command re-proves it inside the s&box sandbox.
/// </summary>
public static class PerftCommand
{
	sealed class PerftCase
	{
		public string Name;
		public string Fen; // null = standard start position
		public long[] ExpectedByDepth;
	}

	// Depth caps per position keep the full default run to a few seconds.
	static readonly PerftCase[] Cases =
	{
		new() { Name = "startpos", Fen = null,
			ExpectedByDepth = new long[] { 20, 400, 8902, 197281 } },
		new() { Name = "kiwipete", Fen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
			ExpectedByDepth = new long[] { 48, 2039, 97862 } },
		new() { Name = "position3", Fen = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
			ExpectedByDepth = new long[] { 14, 191, 2812, 43238 } },
		new() { Name = "position4", Fen = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
			ExpectedByDepth = new long[] { 6, 264, 9467 } },
		new() { Name = "position5", Fen = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
			ExpectedByDepth = new long[] { 44, 1486, 62379 } },
		new() { Name = "position6", Fen = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
			ExpectedByDepth = new long[] { 46, 2079, 89890 } },
	};

	/// <summary>Run the perft suite up to <paramref name="depth"/> (each case
	/// additionally capped at its reference table). Depth 3 ≈ seconds, depth 4
	/// runs the two big depth-4 tables and takes noticeably longer — the game
	/// freezes while it runs; it's a dev gate, not a spectator sport.</summary>
	[ConCmd( "gambit_perft" )]
	public static void Run( int depth = 3 )
	{
		depth = Math.Clamp( depth, 1, 4 );
		Log.Info( $"[Gambit] perft suite to depth {depth} — the game hitches until it finishes" );

		int failures = 0;
		RealTimeSince total = 0;

		foreach ( var perftCase in Cases )
		{
			int caseDepth = Math.Min( depth, perftCase.ExpectedByDepth.Length );
			for ( int d = 1; d <= caseDepth; d++ )
			{
				var game = new ChessGame();
				if ( perftCase.Fen != null && !ChessGame.TryFromFen( perftCase.Fen, out game ) )
				{
					Log.Error( $"[Gambit] perft {perftCase.Name}: FEN failed to load" );
					failures++;
					continue;
				}

				RealTimeSince elapsed = 0;
				long nodes = game.Perft( d );
				long expected = perftCase.ExpectedByDepth[d - 1];

				if ( nodes == expected )
					Log.Info( $"[Gambit] PASS {perftCase.Name} depth {d}: {nodes} nodes ({elapsed.Relative:0.00}s)" );
				else
				{
					Log.Error( $"[Gambit] FAIL {perftCase.Name} depth {d}: {nodes} nodes, expected {expected}" );
					failures++;
				}
			}
		}

		if ( failures == 0 )
			Log.Info( $"[Gambit] perft ALL PASS in {total.Relative:0.00}s — chess rules are trustworthy" );
		else
			Log.Error( $"[Gambit] perft: {failures} FAILURES — do NOT trust game results; see PLAN.md D2 fallback" );
	}
}
