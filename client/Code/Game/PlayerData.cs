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
	/// <summary>How chess boards render for this client, and how a seated player sees them (M16).
	/// Three values:
	/// <list type="bullet">
	/// <item><c>"2d"</c> — every board (tables + the north spectator wall) draws as flat top-down
	/// glyph sprites on a white/brown board; seated camera looks straight down; seated hands AND
	/// bodies are off.</item>
	/// <item><c>"3d-clean"</c> — today's 3D pieces, seated hands off.</item>
	/// <item><c>"3d-arms"</c> — today's 3D pieces, seated hands on. The pre-M16 default behaviour,
	/// so it stays the default and nothing changes for existing players.</item>
	/// </list>
	/// Purely per-client and local (like the old terry-arms toggle, voice range, TTS) — nothing is
	/// networked. Stored as a string (like <see cref="ColorScheme"/>/<see cref="LichessTvChannel"/>)
	/// so a hand-edited value survives; <see cref="ClampPlayMode"/> coerces on read. Drives
	/// <see cref="Gambit.World.SeatedHandSpikes.HandsOn"/>, <see cref="Gambit.World.ChessSetBuilder.FlatMode"/>
	/// and <see cref="Gambit.World.SeatedTerry.ForceHidden"/> (all applied in ChessRing).
	/// <para>No migration: an old save's now-unknown <c>TerryMovesPieces</c> key is dropped on load,
	/// and a missing <c>PlayMode</c> deserializes to the default <c>3d-arms</c> = hands on = today's
	/// behaviour.</para></summary>
	public string PlayMode { get; set; } = "3d-arms";
	/// <summary>Pop re-pick frequency as a multiplier on the floor's base interval
	/// (0.25–3×; higher = pops change faster).</summary>
	public float FloorPopRate { get; set; } = 1f;

	// ── Lichess TV on the north wall (M9) ──
	/// <summary>Is lichess TV one of the sources the north wall cycles through?
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

	// ── Proximity voice (M12) ──
	/// <summary>How far this client HEARS others while seated at a table, in world units.
	/// <para>Voice range is necessarily a receive-side, client-local setting: s&amp;box
	/// applies the 3D falloff on the RECEIVER (each remote avatar's proxy Voice component
	/// spatialises the incoming audio), so "how far voices carry to me" is my choice, not
	/// the speaker's — which is exactly why it lives on the per-client world-settings board
	/// and needs no networking. Two ranges because the room reads differently seated vs
	/// roaming: a tighter range at a table keeps the ring's chatter out of your game, a
	/// wider one while walking lets you talk as you move. Applied by
	/// <see cref="Gambit.World.VoiceScreen"/>.</para></summary>
	public float VoiceRangeAtTable { get; set; } = 300f;

	/// <summary>How far this client HEARS others while roaming, in world units. See
	/// <see cref="VoiceRangeAtTable"/> for why range is a receive-side, per-client value.</summary>
	public float VoiceRangeRoaming { get; set; } = 600f;

	// ── Spoken moves / TTS (M12) ──
	/// <summary>Read out every move (both sides, not just yours) played on the board you're
	/// seated at, via the engine's speech synthesiser. Client-local; your own table only (not
	/// the TV wall, not other boards). Default off. See <see cref="Gambit.Audio.MoveTts"/>.</summary>
	public bool MoveTtsEnabled { get; set; } = false;

	/// <summary>Full name of the installed voice to speak moves with, or empty for the
	/// synthesiser's default. A stored name that isn't installed on this machine falls back
	/// to the default at speak time.</summary>
	public string MoveTtsVoice { get; set; } = "";

	/// <summary>Spoken-move volume, 0–1.</summary>
	public float MoveTtsVolume { get; set; } = 1f;

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
	/// <summary>The three play modes (M16), in picker order.</summary>
	public static readonly string[] PlayModes = { "2d", "3d-clean", "3d-arms" };

	/// <summary>Coerce a stored/hand-edited play mode to one of the three, defaulting to
	/// <c>3d-arms</c> (today's behaviour) for anything unrecognised — same guard shape as the
	/// slider clamps below, for a string.</summary>
	public static string ClampPlayMode( string v )
	{
		foreach ( var m in PlayModes )
			if ( m == v ) return v;
		return "3d-arms";
	}

	// Slider ranges; clamping on use guards hand-edited JSON.
	public static float ClampLightScale( float v ) => Math.Clamp( v, 0f, 1.5f );
	public static float ClampPopRate( float v ) => Math.Clamp( v, 0.25f, 3f );
	public static float ClampVoiceRange( float v ) => Math.Clamp( v, 150f, 1200f );
	public static float ClampUnit( float v ) => Math.Clamp( v, 0f, 1f );

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
