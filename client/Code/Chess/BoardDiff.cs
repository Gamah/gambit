namespace Gambit.Chess;

/// <summary>What happened between two observations of the same board.</summary>
public enum BoardChange
{
	/// <summary>Nothing moved.</summary>
	None,

	/// <summary>A move was played.</summary>
	Move,

	/// <summary>The position changed, but NOT by playing a move — a takeback rewound
	/// it, the table reset for a new game, or a FEN snapshot resynced it. All three
	/// count DOWN in plies, which is what separates them from a move.</summary>
	Rewind,
}

/// <summary>
/// Classify the difference between two observed positions: did a move happen, who
/// played it, and did it take something?
///
/// <para><b>Sandbox-free on purpose</b>, per CLAUDE.md's rule about isolating code that
/// can actually be run on the dev host — same reasoning as
/// <see cref="CapturedMaterial"/>. Its only caller is a Component
/// (<c>Gambit.Audio.TableSounds</c>), so left as a private method in there NONE of this
/// could have been executed here, and every one of the cases below would have been
/// settled by reading it and believing myself. The en-passant and capture-promotion
/// cases in particular look obviously right and are exactly where a piece-count diff
/// gets interesting.</para>
///
/// <para>Takes plies alongside the FENs because a FEN diff alone cannot tell a move from
/// a takeback: both say "the position is different now". Only the ply count says which
/// direction it went.</para>
/// </summary>
public static class BoardDiff
{
	/// <summary>
	/// Compare two observations. <paramref name="fenBefore"/> null means we have never
	/// seen this board before — always <see cref="BoardChange.None"/>, because the first
	/// sight of a game is not an event in it. That is what stops walking into a lobby of
	/// live tables playing a move sound for each.
	/// </summary>
	public static BoardChange Between( string fenBefore, int plyBefore,
		string fenAfter, int plyAfter, out bool whiteMoved, out bool capture )
	{
		whiteMoved = false;
		capture = false;

		if ( string.IsNullOrEmpty( fenBefore ) || string.IsNullOrEmpty( fenAfter ) )
			return BoardChange.None;
		if ( fenBefore == fenAfter ) return BoardChange.None;

		// The position moved but the ply didn't advance: a takeback, a reset to the
		// start position, or a resync onto a FEN with no history (MoveCount 0). None of
		// them is a move and none of them should make a move's noise.
		if ( plyAfter <= plyBefore ) return BoardChange.Rewind;

		// The side to move AFTER a move is the player who did NOT just make it.
		whiteMoved = SideToMoveIsBlack( fenAfter );

		// A capture is the only way a piece leaves the board. Counting them covers en
		// passant for free — the victim isn't on the destination square, but it is
		// still one fewer piece — where "is something standing on the target square"
		// would miss it.
		capture = CountPieces( fenBefore ) != CountPieces( fenAfter );

		return BoardChange.Move;
	}

	/// <summary>Read the side-to-move field.
	///
	/// <para>This replaced <c>fen.Contains(" b ")</c>, and — checked rather than assumed —
	/// <b>that was not a bug</b>: no other FEN field can ever be a lone "b" (the
	/// en-passant square is always two characters, castling is "-" or a KQkq subset, the
	/// rest are numbers), so it always matched the side field and nothing else. It is
	/// replaced for being accidentally correct rather than obviously correct: it reads
	/// like a substring search that got lucky, and the next person to widen the FEN — a
	/// variant, an X-FEN castling field — would have to re-derive that argument to know
	/// whether they'd broken it. Say what it means instead.</para></summary>
	static bool SideToMoveIsBlack( string fen )
	{
		int sp = fen.IndexOf( ' ' );
		return sp >= 0 && sp + 1 < fen.Length && fen[sp + 1] == 'b';
	}

	/// <summary>Pieces on the board. Placement field ONLY — the fields after it carry
	/// letters and digits of their own (side, castling rights, the en-passant square),
	/// and a halfmove clock ticking 9 → 10 would otherwise read as a capture.</summary>
	public static int CountPieces( string fen )
	{
		if ( string.IsNullOrEmpty( fen ) ) return -1;

		int n = 0;
		foreach ( char c in fen )
		{
			if ( c == ' ' ) break;
			if ( char.IsLetter( c ) ) n++;
		}
		return n;
	}
}
