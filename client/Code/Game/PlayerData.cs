using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Gambit.Game;

public sealed class PlayerData
{
	/// <summary>The name to show for this player everywhere (name tags, seat labels, PGN
	/// headers): the Steam persona name. A method, not a get-only property, so
	/// System.Text.Json doesn't write it back into the saved JSON.
	///
	/// <para>Gambit has no username of its own. s&amp;box is Steam-gated, so every
	/// player arrives with a name and an identity already — issue #7 §2: "no
	/// anonymous/guest concept is needed". Names come from Steam, full stop.</para>
	///
	/// <para>An old save's <c>Username</c>/<c>Lichess*</c> keys are simply ignored on
	/// load — System.Text.Json drops unknown members — so this needs no migration.</para></summary>
	public string DisplayName() => Connection.Local?.DisplayName ?? "";

	/// <summary>Board/piece color theme (Theme/Colors.cs; also keys the floor pops).</summary>
	public string ColorScheme { get; set; } = "normal";

	// ── World settings (issue #49, south-wall settings board) ──
	/// <summary>Room light hue as "#RRGGBB"; empty = the scene light's own color.</summary>
	public string WorldLightColor { get; set; } = "";
	/// <summary>Multiplier on the scene room light's brightness (0–1.5).</summary>
	public float WorldLightBrightness { get; set; } = 1f;
	/// <summary>Multiplier on ChessRing.MarqueeBrightness — the overhead table
	/// lights (0–1.5; 0 = off).</summary>
	public float MarqueeLightBrightness { get; set; } = 1f;
	/// <summary>Sounds from the board the local player is seated at (2D set).</summary>
	public bool MyCabinetSounds { get; set; } = true;
	/// <summary>Positional sounds from other players' boards.</summary>
	public bool RemoteCabinetSounds { get; set; } = true;
	/// <summary>Whether the player has ever closed the east-wall info ("Welcome")
	/// panel. False until they dismiss it once; the lobby auto-pops it on load while
	/// this is false. See <see cref="MarkInfoPanelSeen"/>.</summary>
	public bool InfoPanelSeen { get; set; } = false;
	/// <summary>Show the decorative checkerboard floor (with its colour pops).</summary>
	public bool CheckerboardFloor { get; set; } = true;
	/// <summary>Pop re-pick frequency as a multiplier on the floor's base interval
	/// (0.25–3×; higher = pops change faster).</summary>
	public float FloorPopRate { get; set; } = 1f;

	/// <summary>Keyboard rebinds: game action → keyboard key override.</summary>
	public Dictionary<string, string> Bindings { get; set; } = new();

	/// <summary>Controller rebinds: game action → probe-action name (the per-button
	/// gamepad action it should listen to, e.g. "MoveUp" → "PadUp"). Empty = the
	/// default map in <see cref="GamepadBinds"/>. The game actions themselves carry
	/// no GamepadCode; gamepad input is routed through the dedicated Pad* probe
	/// actions so it can be remapped (and captured during rebind) via public APIs.</summary>
	public Dictionary<string, string> GamepadBindings { get; set; } = new();

	/// <summary>Record that the player has closed the info ("Welcome") panel, so the
	/// lobby stops auto-popping it. Creates player.json if none exists yet. Idempotent.</summary>
	public static void MarkInfoPanelSeen()
	{
		var data = Load() ?? new PlayerData();
		if ( data.InfoPanelSeen ) return;
		data.InfoPanelSeen = true;
		data.Save();
	}
	// Slider ranges; clamping on use guards hand-edited JSON.
	public static float ClampLightScale( float v ) => Math.Clamp( v, 0f, 1.5f );
	public static float ClampPopRate( float v ) => Math.Clamp( v, 0.25f, 3f );

	const string FileName = "player.json";

	static PlayerData _cache;
	// Guards (de)serialization of the shared _cache so a Serialize on one thread can never
	// enumerate a dictionary that another thread is mutating ("Collection was modified").
	static readonly object _io = new();

	public static PlayerData Load()
	{
		if ( _cache != null ) return _cache;
		lock ( _io )
		{
			if ( _cache != null ) return _cache;
			try
			{
				if ( !FileSystem.Data.FileExists( FileName ) ) return null;
				var json = FileSystem.Data.ReadAllText( FileName );
				_cache = JsonSerializer.Deserialize<PlayerData>( json );
				return _cache;
			}
			catch
			{
				return null;
			}
		}
	}

	public void Save()
	{
		lock ( _io )
		{
			_cache = this;
			var json = JsonSerializer.Serialize( this );
			FileSystem.Data.WriteAllText( FileName, json );
		}
	}
}
