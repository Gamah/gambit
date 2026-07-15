using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Gambit.Game;

public sealed class PlayerData
{
	/// <summary>The name to show for this player everywhere (name tags, seat labels, PGN
	/// headers): the lichess account name once linked, otherwise the Steam persona name.
	/// Single source of truth so signing in never leaves a stale name on one surface
	/// while another shows the lichess name. A method, not a get-only property, so
	/// System.Text.Json doesn't write it back into the saved JSON.
	///
	/// <para>There is no free-form username any more (M7). s&amp;box is Steam-gated, so
	/// every player arrives with a name and an identity already — issue #7 §2: "no
	/// anonymous/guest concept is needed". Gambit has no username of its own; names come
	/// from Steam and from lichess. "Anonymous" in this codebase now means exactly one
	/// thing: not lichess-linked.</para>
	///
	/// <para>An old save's <c>Username</c> key is simply ignored on load — System.Text.Json
	/// drops unknown members — so this needs no migration.</para></summary>
	public string DisplayName() =>
		!string.IsNullOrEmpty( LichessUsername ) ? LichessUsername : ( Connection.Local?.DisplayName ?? "" );

	// ── Lichess identity (populated by LichessAuth in M3) ──
	// The token is a SECRET credential (PLAN D3): never [Sync]/RPC it, never log
	// it unredacted. It lives only in this local FileSystem.Data JSON.
	public string LichessToken { get; set; } = "";
	/// <summary>Account name the token belongs to (from GET /api/account).</summary>
	public string LichessUsername { get; set; } = "";
	/// <summary>A representative rating for the name tag (rapid → classical → blitz),
	/// 0 when unknown/unrated. Not a secret — safe to show and sync.</summary>
	public int LichessRating { get; set; } = 0;

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
	/// <summary>Whether we've offered lichess sign-in at least once. Before M7 the
	/// splash auto-opened because a brand-new player had no name and had to pick one;
	/// now Steam already supplies the name, so the splash is purely an offer — shown
	/// once, then never again. Nagging every launch would be noise: not being
	/// lichess-linked is a perfectly good steady state (local play, puzzles,
	/// spectating all work). See <see cref="MarkSignInPromptSeen"/>.</summary>
	public bool SignInPromptSeen { get; set; } = false;
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

	/// <summary>Record that we've offered lichess sign-in, so the lobby stops
	/// auto-popping the splash. Idempotent.</summary>
	public static void MarkSignInPromptSeen()
	{
		var data = Load() ?? new PlayerData();
		if ( data.SignInPromptSeen ) return;
		data.SignInPromptSeen = true;
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
