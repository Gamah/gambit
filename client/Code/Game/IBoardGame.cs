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

	// ── Offers ──
	//
	// A draw and a takeback are the two things you ask the OPPONENT for, and both
	// have the same three-state shape: they're offering, we're offering, or nobody
	// is. On the seam for the same reason as premove — they belong to any game you
	// can play at, and a HUD that branches per source is how a feature ends up
	// rendering for one kind of game and invisibly not for the other.
	//
	// Offering when one is already standing IS accepting, on both sides: that's
	// lichess's own shape (draw/yes and takeback/yes are one call each), and the
	// local game copies it rather than inventing a second vocabulary for the same
	// gesture at the next table along.

	/// <summary>The OTHER side has a draw offer standing.</summary>
	bool DrawOffered { get; }

	/// <summary>WE have a draw offer standing — the button waits rather than re-asking.</summary>
	bool DrawPending { get; }

	/// <summary>Offer a draw, or accept the one already offered.</summary>
	void OfferDraw();

	/// <summary>Decline the draw the opponent is offering.</summary>
	void DeclineDraw();

	/// <summary>A takeback is possible at all right now. False before both sides have
	/// moved: lichess drops such a proposal while still answering 200, so the control
	/// is hidden rather than shown dead — and the local game keeps the same rule so
	/// the two tables behave alike.</summary>
	bool CanTakeback { get; }

	/// <summary>The OTHER side is proposing a takeback.</summary>
	bool TakebackOffered { get; }

	/// <summary>WE are proposing a takeback.</summary>
	bool TakebackPending { get; }

	/// <summary>Propose a takeback, or accept the one already proposed.</summary>
	void OfferTakeback();

	/// <summary>Decline the takeback the opponent is proposing.</summary>
	void DeclineTakeback();
}
