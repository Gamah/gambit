using Gambit.Chess;
using Gambit.Game;
using Gambit.World;
using Sandbox;

namespace Gambit.Audio;

/// <summary>
/// Everything a table makes a noise about (M11), watched off the
/// <see cref="IBoardGame"/> seam — so it covers a local two-seat game and a real
/// lichess game with one implementation and no per-source branching.
///
/// <para><b>Why this is a watcher and not a call in the controllers.</b> Sound used to
/// hang off <see cref="LocalGameController"/>, which meant the M8 headline feature — a
/// real lichess game at this table — played in COMPLETE silence for two milestones: no
/// move, no capture, nothing. Nothing looked wrong in any diff, because the code that
/// was there was correct; it just only covered half the tables. Watching the seam is
/// what makes that class of bug impossible rather than merely fixed: a third kind of
/// game gets these sounds by existing, without anyone remembering to add them.</para>
///
/// <para><b>The gate: your table is 2D, the room's tables are 3D, and the room must not
/// become a slot machine with six tables.</b> Which sounds cross the room is decided in
/// <see cref="SoundPlayer"/>, not here — check, offers and the clock have no positional
/// variant at all and simply never play at someone else's board.</para>
///
/// <para>Runs on every client for every station, and is purely local: it reads state
/// that is already replicated and plays a sound. Nothing here is networked, nothing
/// here is authoritative, and a missed frame costs one sound.</para>
/// </summary>
public sealed class TableSounds : Component
{
	/// <summary>The two candidate sources, wired by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }
	[Property] public LocalGameController Controller { get; set; }
	[Property] public LichessGameController Lichess { get; set; }

	/// <summary>Whichever game owns this board — resolved exactly as
	/// <see cref="ChessBoardView.Source"/> does, and for the same reason. If these two
	/// ever disagree, the board and its sounds are describing different games.</summary>
	IBoardGame Source => Lichess is { Engaged: true } ? Lichess : Controller;

	/// <summary>Am I the one sitting here? Decides 2D vs positional for everything.</summary>
	bool Mine => ChessStation.Active == Station;

	// ── Watched state ──
	//
	// All nullable/sentinel so that the FIRST observation of anything is silent. That
	// is load-bearing: walking into a lobby with six live tables must not play six
	// game-over sounds at you, and sitting down at a table mid-game must not replay it.

	object _lastSource;      // identity, not value — see OnUpdate
	string _lastFen;
	int _lastPly;
	bool _lastOver;
	bool _lastDrawOffered;
	bool _lastTakebackOffered;
	int _lastPanicSecond = -1;

	protected override void OnUpdate()
	{
		var src = Source;

		// The board changed hands (a lichess game engaged, or was handed back when it
		// ended). Every tracked value describes the OLD game, and comparing across the
		// swap would invent transitions out of the difference between two unrelated
		// games — a move sound for the FEN jump, and a second game-over for a result
		// both controllers are briefly holding at once.
		//
		// ADOPT the new source's state rather than zeroing: zeroing makes "not over"
		// the baseline, so a swap onto a source that is ALREADY over reads as a game
		// ending and announces it a second time.
		if ( !ReferenceEquals( src, _lastSource ) )
		{
			_lastSource = src;
			Baseline( src );
			return;
		}

		if ( src == null ) return;

		WatchMove( src );
		WatchGameOver( src );
		WatchOffers( src );
		WatchPanic( src );
	}

	/// <summary>Take the source's current state as read, silently. Whatever is true right
	/// now is the starting point, so nothing that is already the case announces itself.</summary>
	void Baseline( IBoardGame src )
	{
		_lastFen = src?.Game?.Fen;
		_lastPly = src?.Game?.MoveCount ?? 0;
		_lastOver = src?.GameOver ?? false;
		_lastDrawOffered = src is { LocalSeat: not null, DrawOffered: true };
		_lastTakebackOffered = src is { LocalSeat: not null, TakebackOffered: true };
		_lastPanicSecond = -1;
	}

	/// <summary>
	/// A move landed on this board.
	///
	/// <para>The classification — was that a move at all, who played it, did it take
	/// something — is <see cref="BoardDiff"/>, which lives under Code/Chess so it can be
	/// run on the dev host against real games. What's left here is only the part that
	/// genuinely needs the engine: where the table is, and whether I'm sitting at it.</para>
	///
	/// <para>Two moves arriving in one frame (a lichess poll can carry several) make ONE
	/// sound, not several. Deliberate — the alternative is a burst that reads as a
	/// glitch, and the player only missed the ones they weren't there for.</para>
	/// </summary>
	void WatchMove( IBoardGame src )
	{
		var game = src.Game;
		if ( game == null ) { _lastFen = null; _lastPly = 0; return; }

		string fen = game.Fen;
		int ply = game.MoveCount;

		var change = BoardDiff.Between( _lastFen, _lastPly, fen, ply,
			out bool whiteMoved, out bool capture );

		_lastFen = fen;
		_lastPly = ply;

		if ( change != BoardChange.Move ) return;

		SoundPlayer.PlayMove( whiteMoved, capture, game.IsCheck, Mine, WorldPosition );
	}

	/// <summary>The game ended — however it ended, and including a flag. There is no
	/// separate flag sound: a clock running out IS the game ending, and firing both
	/// would just be the game-over sound with a grace note.</summary>
	void WatchGameOver( IBoardGame src )
	{
		bool over = src.GameOver;
		if ( over && !_lastOver )
			SoundPlayer.PlayGameOver( Mine, WorldPosition );
		_lastOver = over;
	}

	/// <summary>The opponent asked for a draw or a takeback. Only ever for the player
	/// being asked — <c>LocalSeat</c> is non-null only while sitting here, and the two
	/// Offered flags mean "the OTHER side is asking", so a spectator can't trip this and
	/// the asker doesn't hear their own question.</summary>
	void WatchOffers( IBoardGame src )
	{
		bool seated = src.LocalSeat != null;
		bool draw = seated && src.DrawOffered;
		bool takeback = seated && src.TakebackOffered;

		if ( ( draw && !_lastDrawOffered ) || ( takeback && !_lastTakebackOffered ) )
			SoundPlayer.PlayOffer();

		_lastDrawOffered = draw;
		_lastTakebackOffered = takeback;
	}

	/// <summary>
	/// Your clock is under <see cref="TimeControl.PanicSeconds"/>: one beep per whole
	/// second, at your own table only.
	///
	/// <para>Keyed on the displayed SECOND rather than a timer, which is what keeps it
	/// honest on a clock nobody here runs. Neither kind of game ticks locally — a local
	/// table renders the host's <c>[Sync]</c> copy and a lichess table renders lichess's
	/// — so both arrive in jumps, and a local accumulator would drift away from the
	/// number on screen. Counting the number itself means the beep lands with the digit,
	/// and a stalled clock beeps once rather than forever.</para>
	///
	/// <para>Gated on it being your MOVE. The panic is that your own time is burning; the
	/// clock sitting on 4 seconds while your opponent thinks is not an emergency, and
	/// beeping through their think would make it one.</para>
	///
	/// <para><b>Known gap, stated rather than papered over: at a LICHESS table this beeps
	/// about once, not once a second.</b> lichess only sends a clock when a MOVE happens,
	/// so <c>LocalSeatClock</c> is frozen at the value from the opponent's last move for
	/// the whole of your think — the second never advances, so neither does this. It is
	/// the same staleness the HUD's red clock already has at a lichess table, and it is
	/// NOT fixable by counting down locally here: that is what the TV wall does, and
	/// CLAUDE.md's TV-clock section is the record of it reading HIGH by the network
	/// latency for two milestones while three places claimed it read low. A local
	/// countdown to decide when to make an urgent noise would be the same mistake with
	/// worse consequences — beeping at a player who has more time than we think.</para>
	/// </summary>
	void WatchPanic( IBoardGame src )
	{
		if ( src.LocalSeatClock is not { } left || !src.IsMyTurn || !Mine
			|| left >= TimeControl.PanicSeconds || left <= 0f )
		{
			_lastPanicSecond = -1;
			return;
		}

		// Ceil, so 9.4 left is "9" — the same second the clock face is showing. Floor
		// would beep a second early against a display that truncates.
		int second = (int)System.MathF.Ceiling( left );
		if ( second == _lastPanicSecond ) return;

		_lastPanicSecond = second;
		SoundPlayer.PlayPanic();
	}

}
