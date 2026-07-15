using System;
using System.Collections.Generic;

namespace Gambit.Game;

/// <summary>
/// One selectable time control for a table: an initial bank per side plus a
/// per-move increment (Fischer). <see cref="All"/> is the fixed menu the seated
/// board panel offers; a table stores only its index into that list, which is
/// what crosses the wire as <c>LocalGameController.TimeControlIndex</c>.
///
/// <para>Deliberately Sandbox-free — this is plain C# with no engine types, so it
/// is exercised by the standalone dotnet harness (scripts/timecontrol_check) on a
/// host with no s&amp;box toolchain.</para>
/// </summary>
public readonly struct TimeControl
{
	/// <summary>Menu label, e.g. "Blitz 3+2".</summary>
	public readonly string Name;

	/// <summary>Starting bank per side, in seconds. 0 = untimed.</summary>
	public readonly int InitialSeconds;

	/// <summary>Seconds added to a player's clock after each of their moves.</summary>
	public readonly int IncrementSeconds;

	public TimeControl( string name, int initialSeconds, int incrementSeconds )
	{
		Name = name;
		InitialSeconds = initialSeconds;
		IncrementSeconds = incrementSeconds;
	}

	/// <summary>No clock runs and no side can flag.</summary>
	public bool IsUnlimited => InitialSeconds <= 0;

	/// <summary>
	/// The menu, in ascending order. The index into this list is what a table syncs,
	/// so <b>append only</b> — reordering or removing an entry silently repoints every
	/// table already holding an index (and any archived PGN written from one).
	/// </summary>
	public static readonly IReadOnlyList<TimeControl> All = new[]
	{
		new TimeControl( "Bullet 1+0", 60, 0 ),
		new TimeControl( "Blitz 3+2", 180, 2 ),
		new TimeControl( "Rapid 10+0", 600, 0 ),
		new TimeControl( "Classical 30+0", 1800, 0 ),
		new TimeControl( "Unlimited", 0, 0 ),
	};

	/// <summary>Index of the control a fresh table starts on (Blitz 3+2).</summary>
	public const int DefaultIndex = 1;

	/// <summary>Is this a selectable index? The RPC guard — a client sends an int.</summary>
	public static bool IsValidIndex( int index ) => index >= 0 && index < All.Count;

	/// <summary>The control at <paramref name="index"/>, clamped to the menu. Never
	/// throws: the index arrives over the network, and a table rendering the wrong
	/// time control beats a HUD that throws every frame.</summary>
	public static TimeControl At( int index ) =>
		All[Math.Clamp( index, 0, All.Count - 1 )];

	/// <summary>
	/// PGN <c>[TimeControl]</c> tag value per the PGN spec (§9.6): "seconds+increment"
	/// for a Fischer control, "-" for untimed.
	/// </summary>
	public string PgnSpec => IsUnlimited ? "-" : $"{InitialSeconds}+{IncrementSeconds}";

	/// <summary>
	/// A remaining-time bank as a clock face. Minutes:seconds normally, dropping to
	/// tenths under ten seconds where they start to matter. Never renders negative —
	/// a flagged clock reads 0:00.
	/// </summary>
	public static string Format( float seconds )
	{
		if ( float.IsNaN( seconds ) || seconds <= 0f ) return "0:00";

		if ( seconds < 10f )
			return $"{seconds:0.0}";

		// Round down: showing 1:00 while a hair under a minute remains would let the
		// clock read 1:00 twice in a row. Truncating means it ticks 1:00 → 0:59.
		int total = (int)seconds;
		int mins = total / 60;
		int secs = total % 60;
		return $"{mins}:{secs:00}";
	}
}
