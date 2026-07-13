using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sandbox;

namespace Rotaliate.Game;

public sealed class PlayerData
{
	/// <summary>Per-backend identity. The LIVE and TEST servers are separate
	/// databases, so a GUID/username enrolled on one is meaningless on the other —
	/// each instance keeps its own identity. Keyed by <see cref="Rotaliate.Api.ApiClient.BaseUrl"/>.</summary>
	public sealed class Identity
	{
		public string Guid { get; set; }
		public string Username { get; set; } = "";
		public string PlayerTag { get; set; } = "";
		// SteamID64 already linked to this account server-side; lets us skip re-minting
		// a Facepunch token + re-PUTting the link on every cabinet entry (issue #71).
		public string LinkedSteamId { get; set; } = "";
	}

	/// <summary>Identity per backend URL (LIVE / TEST). Settings below are shared
	/// across instances; only the identity is split.</summary>
	public Dictionary<string, Identity> Identities { get; set; } = new();

	// Legacy flat identity from pre-migration player.json — read once on Load() and
	// folded into Identities under the LIVE URL, then dropped (WhenWritingNull).
	[JsonPropertyName( "Guid" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public string LegacyGuid { get; set; }
	[JsonPropertyName( "Username" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public string LegacyUsername { get; set; }
	[JsonPropertyName( "PlayerTag" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public string LegacyPlayerTag { get; set; }

	// Effective identity for the currently selected backend. All callers use these;
	// the per-instance routing is invisible to them.
	[JsonIgnore]
	public string Guid
	{
		get => Identities.TryGetValue( CurrentKey, out var id ) ? id.Guid : null;
		set => Current().Guid = value;
	}
	[JsonIgnore]
	public string Username
	{
		get => Identities.TryGetValue( CurrentKey, out var id ) ? id.Username : "";
		set => Current().Username = value;
	}
	/// <summary>8-hex-char public identifier (server-computed); replaces the GUID in
	/// shared payloads. Changes with the username — refreshed from every endpoint
	/// that returns it.</summary>
	[JsonIgnore]
	public string PlayerTag
	{
		get => Identities.TryGetValue( CurrentKey, out var id ) ? id.PlayerTag : "";
		set => Current().PlayerTag = value;
	}
	/// <summary>SteamID64 already linked to the current backend's account, so the link
	/// (and its Facepunch token) is minted once, not on every cabinet entry (issue #71).</summary>
	[JsonIgnore]
	public string LinkedSteamId
	{
		get => Identities.TryGetValue( CurrentKey, out var id ) ? id.LinkedSteamId : "";
		set => Current().LinkedSteamId = value;
	}

	static string CurrentKey => Rotaliate.Api.ApiClient.BaseUrl;

	Identity Current()
	{
		if ( !Identities.TryGetValue( CurrentKey, out var id ) )
			Identities[CurrentKey] = id = new Identity();
		return id;
	}

	public string ColorScheme { get; set; } = "normal";
	public string LayoutSwap { get; set; } = "standard";
	/// <summary>Cube-board explodiness: "slide" (none), "explode" (finish only),
	/// or "match" (resolved cubes shatter into mini cubes).</summary>
	public string CompleteEffect { get; set; } = "match";
	/// <summary>Whether exploding cubes (finish debris and match shards) fall.</summary>
	public bool ExplodeGravity { get; set; } = false;
	/// <summary>User multipliers on the ArcadeRing's scene-tuned CubeSize /
	/// PlayCameraBackoff / PlayCameraRise — the Settings sliders store the offset from
	/// the ring's value rather than an absolute, so scene retuning keeps working.
	/// Cube size allows ±25%; the camera values 0–1000% (the scene defaults are only
	/// ~10 units, so a wide multiplier is what gives a usable travel range).</summary>
	public float CubeSizeScale { get; set; } = 1f;
	public float CameraBackoffScale { get; set; } = 1f;
	public float CameraRiseScale { get; set; } = 1f;

	// ── World settings (issue #49, south-wall settings board) ──
	/// <summary>Room light hue as "#RRGGBB"; empty = the scene light's own color.</summary>
	public string WorldLightColor { get; set; } = "";
	/// <summary>Multiplier on the scene room light's brightness (0–1.5).</summary>
	public float WorldLightBrightness { get; set; } = 1f;
	/// <summary>Multiplier on ArcadeRing.MarqueeBrightness (0–1.5; 0 = off).</summary>
	public float MarqueeLightBrightness { get; set; } = 1f;
	/// <summary>Sounds from the cabinet the local player is engaged at (2D set).</summary>
	public bool MyCabinetSounds { get; set; } = true;
	/// <summary>Positional sounds from other players' cabinets.</summary>
	public bool RemoteCabinetSounds { get; set; } = true;
	/// <summary>Never run the attract demo on the OG cabinet.</summary>
	public bool DemoSkip { get; set; } = false;
	/// <summary>Whether the player has ever closed the east-wall info ("Welcome")
	/// panel. False until they dismiss it once; the lobby auto-pops it on load while
	/// this is false. See <see cref="MarkInfoPanelSeen"/>.</summary>
	public bool InfoPanelSeen { get; set; } = false;
	/// <summary>Show the giant spectator board mirroring cabinet 0's active game.</summary>
	public bool SpectatorBoard { get; set; } = true;
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

	/// <summary>Distinctness gate for the daily_solves / hourly_solves stats: keys
	/// ("daily:&lt;seed&gt;" / "hourly:&lt;seed&gt;") of boards already counted, capped to
	/// the most recent <see cref="CountedBoardsCap"/>. Per-device, so a board replayed
	/// on a second machine may recount — harmless (only ever unlocks slightly early).</summary>
	public List<string> CountedBoards { get; set; } = new();
	const int CountedBoardsCap = 64;

	/// <summary>Record a finished daily/hourly board; returns true the first time a given
	/// board (by key) is seen, so the caller increments the distinct stat only on new ones.</summary>
	public bool MarkBoardCounted( string key )
	{
		if ( string.IsNullOrEmpty( key ) || CountedBoards.Contains( key ) ) return false;
		CountedBoards.Add( key );
		if ( CountedBoards.Count > CountedBoardsCap )
			CountedBoards.RemoveRange( 0, CountedBoards.Count - CountedBoardsCap );
		Save();
		return true;
	}

	/// <summary>Record that the player has closed the info ("Welcome") panel, so the
	/// lobby stops auto-popping it. Creates player.json if none exists yet (brand-new
	/// player who read the welcome before enrolling). Idempotent.</summary>
	public static void MarkInfoPanelSeen()
	{
		var data = Load() ?? new PlayerData();
		if ( data.InfoPanelSeen ) return;
		data.InfoPanelSeen = true;
		data.Save();
	}

	// Slider ranges; clamping on use guards hand-edited JSON.
	public static float ClampScale( float v ) => Math.Clamp( v, 0.75f, 1.25f );
	public static float ClampCameraScale( float v ) => Math.Clamp( v, 0f, 10f );
	public static float ClampLightScale( float v ) => Math.Clamp( v, 0f, 1.5f );
	public static float ClampPopRate( float v ) => Math.Clamp( v, 0.25f, 3f );

	const string FileName = "player.json";

	static PlayerData _cache;
	// Guards (de)serialization of the shared _cache so a Serialize on one thread can never
	// enumerate a dictionary that another thread is mutating ("Collection was modified").
	// Monitor is reentrant, so MigrateLegacyIdentity → Save re-entering is fine.
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
				_cache?.MigrateLegacyIdentity();
				return _cache;
			}
			catch
			{
				return null;
			}
		}
	}

	/// <summary>Fired when the local identity is deleted — SplashScreen re-shows enrollment.</summary>
	public static Action OnIdentityCleared;

	/// <summary>Pre-migration player.json stored a single flat identity; fold it into
	/// the LIVE instance (the old default backend) so the existing player keeps their
	/// name there, then drop the legacy fields.</summary>
	void MigrateLegacyIdentity()
	{
		if ( string.IsNullOrEmpty( LegacyGuid ) ) return;
		if ( !Identities.ContainsKey( Rotaliate.Api.ApiClient.ProdUrl ) )
			Identities[Rotaliate.Api.ApiClient.ProdUrl] = new Identity
			{
				Guid = LegacyGuid,
				Username = LegacyUsername ?? "",
				PlayerTag = LegacyPlayerTag ?? "",
			};
		LegacyGuid = LegacyUsername = LegacyPlayerTag = null;
		Save();
	}

	/// <summary>Delete the player GUID (and username) for the current backend so the
	/// next launch — or the splash screen, immediately — re-enrolls. Other instances'
	/// identities and all settings are kept.</summary>
	public static void ClearIdentity()
	{
		var data = Load() ?? new PlayerData();
		data.Identities.Remove( CurrentKey );
		data.Save();
		OnIdentityCleared?.Invoke();
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
