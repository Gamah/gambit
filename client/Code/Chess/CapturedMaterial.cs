using System;
using System.Collections.Generic;

namespace Gambit.Chess;

/// <summary>
/// What each side has lost, read out of a position rather than counted as it
/// happened. Drives the captured-piece trays on each table (M11).
///
/// <para><b>Why derive instead of tally.</b> ChessBoardView rebuilds from the FEN
/// alone and keeps no history: a late joiner or a FEN resync starts with an empty
/// board and the first sync is all-additions. A tray fed by capture EVENTS would be
/// empty for everyone who didn't watch the whole game — which is most spectators,
/// and every player at a table that resynced. Making the tray a pure function of
/// the position means it is simply always right, and the capture animation can be a
/// transient nicety on top rather than the source of truth.</para>
///
/// <para>Deliberately Sandbox-free, so it runs in a plain dotnet harness on the dev
/// host — the promotion arithmetic below is exactly the kind of thing that looks
/// obviously correct in review and isn't. See CLAUDE.md's note on isolating code
/// from Sandbox to make it testable.</para>
/// </summary>
public static class CapturedMaterial
{
	/// <summary>Tray fill order, by descending piece value. Pawns LAST is the whole
	/// reason for a fixed order: they are most of what dies, so appending one must
	/// not shove everything else along to a new slot.</summary>
	public const string Order = "qrbnp";

	/// <summary>Slots in one player's tray. <b>15, not 16</b>: that is the most you
	/// can lose — 8 pawns + 2 knights + 2 bishops + 2 rooks + 1 queen. The king is
	/// never captured, so a 16th slot could only ever stay empty.</summary>
	public const int MaxSlots = 15;

	/// <summary>Conventional value of a piece type, in pawns. Lowercase type char; the
	/// king is 0 because it is never captured and never traded — including it would add
	/// the same number to both sides forever.</summary>
	public static int Value( char type ) => type switch
	{
		'q' => 9,
		'r' => 5,
		'b' => 3,
		'n' => 3,
		'p' => 1,
		_ => 0,
	};

	/// <summary>
	/// Material balance in pawns: positive means WHITE is ahead, negative Black, zero
	/// level. <paramref name="squares"/> is the same 64-entry board <see cref="Lost"/>
	/// takes.
	///
	/// <para><b>Counted from the pieces ON the board, deliberately — not by valuing what
	/// <see cref="Lost"/> returns.</b> They are not the same sum, and the difference is
	/// promotion. Lost carries a documented lie: a captured piece that had itself been
	/// promoted reads as a captured pawn, because a FEN cannot say which queen was born
	/// one. Valuing that list would inherit the lie and report a player 8 points poorer
	/// than they are. Summing the board has no such problem — a promoted queen simply IS
	/// a queen, worth 9, and needs no forgiveness arithmetic at all.</para>
	///
	/// <para>So the tray and the bar are derived two different ways from the same
	/// position, and that is correct rather than an inconsistency to tidy up: the tray
	/// answers "what did you lose", which is about history it cannot fully know, and the
	/// bar answers "who is ahead now", which the position states outright.</para>
	/// </summary>
	public static int Advantage( char[] squares )
	{
		int score = 0;
		for ( int sq = 0; sq < squares.Length; sq++ )
		{
			char c = squares[sq];
			if ( c == '\0' ) continue;
			int v = Value( char.ToLowerInvariant( c ) );
			score += char.IsUpper( c ) ? v : -v;
		}
		return score;
	}

	/// <summary>How many of a type each side starts with. Lowercase type char.</summary>
	public static int StartCount( char type ) => type switch
	{
		'q' => 1,
		'r' => 2,
		'b' => 2,
		'n' => 2,
		'p' => 8,
		_ => 0,
	};

	/// <summary>
	/// The pieces <paramref name="white"/> has lost, in <see cref="Order"/>, as FEN
	/// chars of their own colour. <paramref name="squares"/> is a 64-entry board
	/// (index = rank*8 + file) of FEN chars, '\0' for empty.
	///
	/// <para><b>Promotion is why this isn't just start-minus-current.</b> A promoted
	/// queen drops the pawn count without anything being taken, so the naive diff
	/// reports a phantom captured pawn AND a negative queen count. Count the surplus
	/// across the promotable types and forgive that many pawns.</para>
	///
	/// <para><b>Known and accepted:</b> capturing a piece that was itself promoted
	/// shows as a captured pawn, because the position cannot say which queen was
	/// born a pawn — the information isn't in a FEN. This is the same material diff
	/// lichess's own UI computes, and it is the price of deriving from the position
	/// instead of keeping a history the view doesn't have.</para>
	/// </summary>
	public static List<char> Lost( char[] squares, bool white )
	{
		var cur = new int[Order.Length];
		for ( int sq = 0; sq < squares.Length; sq++ )
		{
			char c = squares[sq];
			if ( c == '\0' || char.IsUpper( c ) != white ) continue;
			int i = Order.IndexOf( char.ToLowerInvariant( c ) );
			if ( i >= 0 ) cur[i]++;
		}

		int promotions = 0;
		for ( int i = 0; i < Order.Length; i++ )
		{
			if ( Order[i] == 'p' ) continue;
			promotions += Math.Max( 0, cur[i] - StartCount( Order[i] ) );
		}

		var lost = new List<char>();
		for ( int i = 0; i < Order.Length; i++ )
		{
			char t = Order[i];
			int n = StartCount( t ) - cur[i];
			// Every promotion cost this side a pawn that nobody took.
			if ( t == 'p' ) n -= promotions;
			for ( int k = 0; k < n && lost.Count < MaxSlots; k++ )
				lost.Add( white ? char.ToUpperInvariant( t ) : t );
		}
		return lost;
	}
}
