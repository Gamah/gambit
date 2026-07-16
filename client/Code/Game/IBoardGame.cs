using Gambit.Chess;
using Gambit.World;

namespace Gambit.Game;

/// <summary>Constants shared by everything implementing <see cref="IBoardGame"/> —
/// one copy, so the two kinds of table can't drift apart on a number that is supposed
/// to make them behave alike.</summary>
public static class BoardGame
{
	/// <summary>How long the "premove dropped" notice stands. Long enough to read
	/// after looking back at the board, short enough that it's gone before it could be
	/// mistaken for a comment on your NEXT premove.</summary>
	public const float PremoveDroppedSeconds = 4f;
}

/// <summary>
/// The slice of a game controller that <see cref="Gambit.World.ChessBoardView"/>
/// needs to render a position and turn cursor clicks into moves, and that
/// <see cref="Gambit.Audio.TableSounds"/> needs to make a noise about it.
/// Abstracting it lets one board view and one sound watcher drive either the local
/// two-seat game (<see cref="LocalGameController"/>) or a real lichess game
/// (<see cref="LichessGameController"/>) with no per-source branching in either.
///
/// <para><b>This seam is the reason a feature can't ship for half the tables</b>, and
/// it has already caught that twice. M8 added a whole second kind of game with no
/// renderer change at all — but sound was NOT on the seam, it hung off
/// LocalGameController, and so a real lichess game at a table, the M8 headline
/// feature, played in complete silence for two whole milestones without one line of
/// code looking wrong. Anything that reacts to a move, a result or a clock belongs
/// up here. If you find yourself typing <c>LocalGameController</c> in a new reactive
/// feature, that is the mistake happening again.</para>
/// </summary>
public interface IBoardGame
{
	/// <summary>Current rules/position, or null before any game starts.</summary>
	ChessGame Game { get; }

	/// <summary>A game is live right now (accept input, run the clock).</summary>
	bool Playing { get; }

	/// <summary>The game at this board has ENDED and its result is still on display.
	///
	/// <para>Not just <c>!Playing</c>: an idle table, a table mid-setup and a table
	/// showing a result are all not-playing, and only the last one is a game that just
	/// finished. <see cref="Gambit.Audio.TableSounds"/> needs the difference — a sound
	/// that fired on !Playing would fire every time anyone stood up.</para></summary>
	bool GameOver { get; }

	/// <summary>Seconds left on the LOCAL player's own clock, or null when they aren't
	/// seated here, no game is live, or the game is untimed.
	///
	/// <para>On the seam so the panic beep has one source of truth for "my clock". The
	/// alternative — reading <c>LocalGameController.ClockFor</c> — is wrong during a
	/// lichess game by construction: the host FREEZES its copy (HostTickClocks
	/// early-returns on LichessGame) precisely so it can't flag a player who is fine on
	/// lichess's clock, so it would sit at its start value and never panic at all.</para></summary>
	float? LocalSeatClock { get; }

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

	/// <summary>A premove fired and was REFUSED — show it, briefly.
	///
	/// <para>Exists because the failure is invisible and looks exactly like success.
	/// Both controllers used to throw away the bool from their TryMakeMove, which
	/// returns false silently: no error, no log, no sound. The only observable was the
	/// premove highlight disappearing — <b>which is also what it does when it works</b>.
	/// You watch your premove vanish and believe you moved.</para>
	///
	/// <para>Self-clearing after <see cref="BoardGame.PremoveDroppedSeconds"/> rather
	/// than needing a dismissal: it is news about a move that isn't happening, and the
	/// board behind it already tells the whole story.</para></summary>
	bool PremoveDropped { get; }

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
