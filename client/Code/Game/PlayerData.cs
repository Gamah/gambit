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
	/// <para>An old save's now-unknown keys are simply ignored on load — System.Text.Json
	/// drops unknown members — so a removed field never needs a migration.</para></summary>
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

	// ── Lichess TV on the west wall (M9) ──
	/// <summary>Is lichess TV one of the sources the west wall cycles through?
	/// <para><b>Default on.</b> TV needs no lichess account and no linking — the feed
	/// is anonymous — so there is nothing to opt into. Someone who doesn't want it
	/// turns it off and it stays off; the wall keeps mirroring real tables either way,
	/// because that was its job before TV existed.</para>
	/// <para>Named …Enabled, not …Tv: a property called <c>LichessTv</c> would shadow
	/// the <see cref="LichessTv"/> class inside this type.</para></summary>
	public bool LichessTvEnabled { get; set; } = true;

	/// <summary>Follow the lobby admin's suggested channel (default), or use
	/// <see cref="LichessTvChannel"/>.
	/// <para>The host SUGGESTS; it never dictates. A player who has picked a channel
	/// keeps it when the admin changes theirs.</para></summary>
	public bool LichessTvFollowHost { get; set; } = true;

	/// <summary>This client's own channel pick, used when
	/// <see cref="LichessTvFollowHost"/> is false. Coerced through
	/// <see cref="LichessTv.Coerce"/> on read — a stored value can outlive the list
	/// that produced it.</summary>
	public string LichessTvChannel { get; set; } = LichessTv.DefaultChannel;

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

	// Defaults for a player who has never saved anything. Held rather than rebuilt
	// because Load() returns null (not a default) when there's no file, and Current is
	// read every frame by the spectator wall — a `?? new PlayerData()` there would
	// allocate 60 times a second forever.
	static PlayerData _defaults;

	// Load() memoizes only on SUCCESS, so without this a player who has never saved
	// re-enters the lock and stats the filesystem on every call — and Current is read
	// per-frame. Sticky: once we know there's nothing to read, only a Save (which sets
	// _cache directly) changes the answer.
	static bool _loadFailed;

	/// <summary>Settings as they currently apply: the saved file, or the defaults.
	/// <para>Never null, and genuinely safe to read per-frame — no allocation and no
	/// I/O after the first call. Use <see cref="Load"/> instead when you need to know
	/// whether anything was actually saved.</para></summary>
	public static PlayerData Current => Load() ?? ( _defaults ??= new PlayerData() );

	public static PlayerData Load()
	{
		if ( _cache != null ) return _cache;
		if ( _loadFailed ) return null;
		lock ( _io )
		{
			if ( _cache != null ) return _cache;
			if ( _loadFailed ) return null;
			try
			{
				if ( !FileSystem.Data.FileExists( FileName ) )
				{
					_loadFailed = true;
					return null;
				}
				var json = FileSystem.Data.ReadAllText( FileName );
				_cache = JsonSerializer.Deserialize<PlayerData>( json );
				// Deserialize returns null for a file containing literal "null".
				_loadFailed = _cache == null;
				return _cache;
			}
			catch
			{
				_loadFailed = true;
				return null;
			}
		}
	}

	public void Save()
	{
		lock ( _io )
		{
			_cache = this;
			// There is now something to read, so clear the sticky "nothing to read".
			// _cache short-circuits Load() anyway; this keeps the two from disagreeing.
			_loadFailed = false;
			var json = JsonSerializer.Serialize( this );
			FileSystem.Data.WriteAllText( FileName, json );
		}
	}
}
