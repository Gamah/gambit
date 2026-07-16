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
	/// <summary>Menu label, e.g. "Blitz 3+0".</summary>
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
		// 3+0 estimates at exactly 180 — the FIRST second of lichess's Blitz band
		// (scalachess: Blitz is `180 to 479`, and byTime uses an inclusive
		// `range.contains`, verified against scalachess master 2026-07-16). So it is
		// challengeable with nothing to spare: drop the initial bank by one second,
		// or shave the increment when there is none left to shave, and the default
		// table silently stops being playable on lichess at all.
		new TimeControl( "Blitz 3+0", 180, 0 ),
		new TimeControl( "Rapid 10+0", 600, 0 ),
		new TimeControl( "Classical 30+0", 1800, 0 ),
		new TimeControl( "Unlimited", 0, 0 ),
	};

	/// <summary>Index of the control a fresh table starts on (Blitz 3+0).</summary>
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
	/// Below this many seconds left, a clock reads in tenths rather than m:ss. It also
	/// sets how fast the host has to publish the clocks — a tenths display needs several
	/// updates per digit or it visibly skips — so <c>LocalGameController</c> reads this
	/// same constant rather than keeping its own copy in step.
	/// </summary>
	public const float DecimalBelowSeconds = 60f;

	/// <summary>
	/// Below this many seconds left, you are in trouble: the ticking seat's clock turns
	/// red and (at your own table, for your own clock) starts beeping once a second.
	///
	/// <para>Deliberately NOT <see cref="DecimalBelowSeconds"/>. That one decides when
	/// tenths are legible; this one decides when you're in trouble. A whole bullet game
	/// is played under sixty seconds, and colouring all of it red — or beeping through
	/// all of it — would say nothing.</para>
	///
	/// <para>Lives here rather than on the HUD because it is no longer only a colour:
	/// the panic sound and the red text have to agree on where panic starts, and two
	/// copies of 10f in two files is how they'd quietly stop agreeing.</para>
	/// </summary>
	public const float PanicSeconds = 10f;

	/// <summary>
	/// A remaining-time bank as a clock face. Minutes:seconds normally, dropping to
	/// tenths under <see cref="DecimalBelowSeconds"/>, where a whole second is an age —
	/// the entire bullet time control lives down there. Never renders negative: a flagged
	/// clock reads 0:00, which also makes the flag legible against the ticking 0.4/0.3.
	/// </summary>
	public static string Format( float seconds )
	{
		if ( float.IsNaN( seconds ) || seconds <= 0f ) return "0:00";

		// Truncate, don't round — at every scale, a clock that reads higher than the time
		// actually left is a lie. Note "{seconds:0.0}" would NOT do: .NET rounds it, so
		// 59.96 would render "60.0" — a nonsense reading of a clock that has under a
		// minute on it.
		if ( seconds < DecimalBelowSeconds )
		{
			int tenths = (int)( seconds * 10f );
			return $"{tenths / 10}.{tenths % 10}";
		}

		// Same reasoning one scale up: showing 1:00 while a hair under a minute remains
		// would let the clock read 1:00 twice in a row. Truncating ticks 1:00 → 0:59.
		int total = (int)seconds;
		return $"{total / 60}:{total % 60:00}";
	}
}
