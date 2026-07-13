using Gambit.Chess;
using Gambit.World;

namespace Gambit.Game;

/// <summary>
/// The slice of a game controller that <see cref="Gambit.World.ChessBoardView"/>
/// needs to render a position and turn cursor clicks into moves. Abstracting it
/// lets one board view drive either the local two-seat game
/// (<see cref="LocalGameController"/>) or an in-sbox lichess game
/// (<see cref="LichessPlayController"/>) with no per-source branching in the view.
///
/// When no lichess game is live the view resolves this to the local controller,
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
}
