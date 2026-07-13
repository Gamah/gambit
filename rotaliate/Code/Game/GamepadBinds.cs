using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Rotaliate.Game;

/// <summary>
/// Controller binding layer. The remappable game actions (MoveUp/Down/Left/Right,
/// RotateCCW/CW) carry NO GamepadCode in Input.config; instead each physical button
/// we offer has its own gamepad-only "probe" action (PadUp, PadA, …). Game code asks
/// <see cref="Pressed"/> whether the button currently mapped to an action is pressed,
/// and the rebind UI scans <see cref="Buttons"/> with <c>Input.Pressed</c> to capture
/// which button the player pressed — all through public Input APIs.
/// </summary>
public static class GamepadBinds
{
	/// <summary>Offered physical buttons: probe-action name → display glyph.</summary>
	public static readonly (string Probe, string Label)[] Buttons =
	{
		("PadUp",    "D-Pad ↑"),
		("PadDown",  "D-Pad ↓"),
		("PadLeft",  "D-Pad ←"),
		("PadRight", "D-Pad →"),
		("PadA",     "A"),
		("PadB",     "B"),
		("PadX",     "X"),
		("PadY",     "Y"),
		("PadLB",    "LB"),
		("PadRB",    "RB"),
	};

	/// <summary>Factory default game-action → probe-action map.</summary>
	static readonly Dictionary<string, string> Defaults = new()
	{
		{ "MoveUp",    "PadUp"    },
		{ "MoveDown",  "PadDown"  },
		{ "MoveLeft",  "PadLeft"  },
		{ "MoveRight", "PadRight" },
		{ "RotateCCW", "PadA"     },
		{ "RotateCW",  "PadB"     },
	};

	/// <summary>The probe action currently driving <paramref name="action"/>, honoring
	/// the player's override, or null if the action has no controller binding.</summary>
	public static string ProbeFor( string action )
	{
		var data = PlayerData.Load();
		if ( data?.GamepadBindings != null && data.GamepadBindings.TryGetValue( action, out var p ) && !string.IsNullOrEmpty( p ) )
			return p;
		return Defaults.TryGetValue( action, out var d ) ? d : null;
	}

	/// <summary>True if the gamepad button mapped to <paramref name="action"/> was just pressed.</summary>
	public static bool Pressed( string action )
	{
		var probe = ProbeFor( action );
		return probe != null && Input.Pressed( probe );
	}

	/// <summary>Display glyph for a probe action ("PadA" → "A").</summary>
	public static string LabelOf( string probe )
		=> Buttons.FirstOrDefault( b => b.Probe == probe ).Label ?? "—";

	/// <summary>Display glyph for whatever button drives <paramref name="action"/>.</summary>
	public static string DisplayFor( string action )
	{
		var probe = ProbeFor( action );
		return probe == null ? "—" : LabelOf( probe );
	}
}
