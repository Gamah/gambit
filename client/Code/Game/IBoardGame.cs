using Gambit.Chess;
using Gambit.World;

namespace Gambit.Game;

/// <summary>
/// The slice of a game controller that <see cref="Gambit.World.ChessBoardView"/>
/// needs to render a position and turn cursor clicks into moves. Abstracting it
/// lets one board view drive either the local two-seat game
/// (<see cref="LocalGameController"/>) with no per-source branching in the view.
///
/// The view resolves this to the local controller,
/// so M2 behaviour is byte-for-byte unchanged.
/// </summary>
public interface IBoardGame
{
	/// <summary>Current rules/position, or null before any game starts.</summary>
	ChessGame Game { get; }

	/// <summary>A game is live right now (accept input, run the clock).</summary>
	bool Playing { get; }

	/// <summary>It's the local seated player's move.</summary>
	bool IsMyTurn { get; }

	/// <summary>The side the local player controls here, or null when not seated/playing.</summary>
	ChessSeat? LocalSeat { get; }

	/// <summary>UCI of the last move — used for the last-move highlight when the
	/// live <see cref="ChessGame"/> has no history of its own (FEN resync/late join).</summary>
	string LastMoveUci { get; }

	/// <summary>Submit a move (already validated against the local rules by the
	/// view). Returns false if it wasn't accepted (out of turn, illegal, busy).</summary>
	bool TryMakeMove( string uci );

	/// <summary>The move armed to play the instant it becomes legal, as UCI, or null.
	/// ONE, deliberately — a queue would need a plan for the moment move two turns out
	/// to be illegal.
	///
	/// <para>On the seam because premove belongs to every game with a clock, not just
	/// lichess. It was lichess-only at first, on the reasoning that a premove buys back
	/// network latency and the local game's opponent is sitting across the table — which
	/// mistook the point. A premove's real payment is your OWN clock: it plays in zero
	/// time instead of costing you the second it takes to notice and click. That's worth
	/// as much in a 3+0 game at this table as in one on lichess.</para></summary>
	string PremoveUci { get; }

	/// <summary>Arm a premove. Callers decide whether it's the right moment (the view
	/// only offers it while the opponent is on move); this only sanity-checks.</summary>
	void SetPremove( string uci );

	void ClearPremove();
}
