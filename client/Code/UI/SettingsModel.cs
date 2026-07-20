using System;
using System.Collections.Generic;
using Gambit.Game;
using Gambit.World;
using Sandbox;

namespace Gambit.UI;

/// <summary>
/// Shared model behind the world/host settings (issue #49): the editable rows
/// rendered by the engaged SettingsScreen, the compact status lines shown on the
/// wall boards (WallSettingsPanel), and the change counter the light appliers
/// (SettingsWall, MarqueeGlow) key on.
/// </summary>
public static class SettingsModel
{
	/// <summary>Bumped on every settings change — appliers and BuildHash key on it.</summary>
	public static int SettingsVersion { get; private set; } = 1;

	public class SettingCell
	{
		public string Label = "";
		public string Css = "";
		public string Style = "";
		public bool Selected;
		public Action Activate;
	}

	public class SettingRow
	{
		public string Label;
		public List<SettingCell> Cells = new();

		/// <summary>When set, this row is a real draggable <c>SliderControl</c> rather
		/// than a strip of clickable cells (SettingsScreen renders one or the other).
		/// Used for the continuous world settings — brightness, pop rate, voice range —
		/// which were stepped tick-bars copied from rotaliate before M12.</summary>
		public SliderSpec Slider;
	}

	/// <summary>A continuous setting rendered as a real draggable <c>SliderControl</c>.
	/// No step — these are smooth (brightness, pop rate, voice range); the old
	/// rotaliate-style tick bars were the thing M12 replaced. <see cref="OnChange"/>
	/// persists on every change; the file is tiny, so a drag's worth of writes is fine.</summary>
	public class SliderSpec
	{
		public float Min, Max, Value;
		public Action<float> OnChange;
	}

	// Light hue choices; "" = keep the default (scene hue / neutral white)
	public static readonly (string Name, string Hex)[] Swatches =
	{
		("AUTO", ""),
		// A light grey, not pure white: the chip reads as "a little grey" rather than a
		// blinding square, and it keeps the WHITE theme visibly LIGHTER than the darker-grey
		// AUTO/default (WallTheme.DefaultAccent) instead of identical to it.
		("WHITE", "#D0D0D0"),
		("WARM", "#FFD9A8"),
		("RED", "#FF5848"),
		("YELLOW", "#FFE066"),
		("GREEN", "#58E87A"),
		("CYAN", "#4CD2FF"),
		("BLUE", "#5878FF"),
		("PURPLE", "#B468FF"),
	};

	// Proximity-voice hearing range bounds in world units (M12). The room is ~800u across
	// and two seated opponents sit ~50u apart, so this spans "just my table" to "much of
	// the ring". The slider is continuous between these; PlayerData.ClampVoiceRange holds
	// the same window.
	public const float VoiceRangeMin = 150f;
	public const float VoiceRangeMax = 1200f;

	public const int MinBoards = 2;
	public const int MaxBoards = 16;

	public static List<SettingRow> BuildLocalRows()
	{
		var rows = new List<SettingRow>();
		var data = PlayerData.Load() ?? new PlayerData();

		// "Room theme" drives the wall-board UI palette (WallTheme); the room light
		// itself is always white now — only its brightness is tunable.
		rows.Add( SwatchRow( "ROOM THEME", data.WorldLightColor,
			hex => Mutate( d => d.WorldLightColor = hex ) ) );
		rows.Add( SliderRow( $"ROOM LIGHT BRIGHTNESS — {Pct( data.WorldLightBrightness )}%",
			0f, 1.5f, PlayerData.ClampLightScale( data.WorldLightBrightness ),
			v => Mutate( d => d.WorldLightBrightness = v ) ) );
		// Table light colour is fixed to pure white (MarqueeGlow); only brightness is tunable.
		rows.Add( SliderRow( $"TABLE LIGHT BRIGHTNESS — {Pct( data.MarqueeLightBrightness )}%",
			0f, 1.5f, PlayerData.ClampLightScale( data.MarqueeLightBrightness ),
			v => Mutate( d => d.MarqueeLightBrightness = v ) ) );
		rows.Add( ToggleRow( "CHECKERBOARD FLOOR", data.CheckerboardFloor,
			v => Mutate( d => d.CheckerboardFloor = v ) ) );
		rows.Add( SliderRow( $"POP FREQUENCY — {PlayerData.ClampPopRate( data.FloorPopRate ):0.##}×",
			0.25f, 3f, PlayerData.ClampPopRate( data.FloorPopRate ),
			v => Mutate( d => d.FloorPopRate = v ) ) );
		rows.Add( ToggleRow( "MY BOARD SOUNDS", data.MyCabinetSounds,
			v => Mutate( d => d.MyCabinetSounds = v ) ) );
		rows.Add( ToggleRow( "OTHER BOARD SOUNDS", data.RemoteCabinetSounds,
			v => Mutate( d => d.RemoteCabinetSounds = v ) ) );
		// How chess boards render for this client (M16): flat 2D glyphs, clean 3D, or 3D with the
		// seated hands animating moves. Client-local and cosmetic; 3D+ARMS is the pre-M16 default.
		rows.Add( PickerRow( "PLAY MODE",
			new[] { ("2d", "2D"), ("3d-clean", "3D"), ("3d-arms", "3D + ARMS") },
			PlayerData.ClampPlayMode( data.PlayMode ),
			v => Mutate( d => d.PlayMode = v ) ) );

		// Proximity-voice hearing range (M12): how far THIS client hears others, split by whether
		// you're seated or roaming. Range is a receive-side, per-client value (the falloff is applied
		// on the receiver), which is why it belongs here on the world board rather than being networked.
		rows.Add( SliderRow( $"VOICE RANGE — SEATED — {PlayerData.ClampVoiceRange( data.VoiceRangeAtTable ):0}u",
			VoiceRangeMin, VoiceRangeMax, PlayerData.ClampVoiceRange( data.VoiceRangeAtTable ),
			v => Mutate( d => d.VoiceRangeAtTable = v ) ) );
		rows.Add( SliderRow( $"VOICE RANGE — ROAMING — {PlayerData.ClampVoiceRange( data.VoiceRangeRoaming ):0}u",
			VoiceRangeMin, VoiceRangeMax, PlayerData.ClampVoiceRange( data.VoiceRangeRoaming ),
			v => Mutate( d => d.VoiceRangeRoaming = v ) ) );

		// Spoken moves / TTS (M12): read out the notation of EVERY move (both sides) played on
		// the board you're seated at — not just your own moves. Client-local, your own table
		// only (not the TV wall, not other boards).
		rows.Add( ToggleRow( "SPEAK MOVES AT MY TABLE", data.MoveTtsEnabled,
			v => Mutate( d => d.MoveTtsEnabled = v ) ) );
		rows.Add( VoiceRow( data.MoveTtsVoice, Gambit.Audio.MoveTts.Voices,
			v => Mutate( d => d.MoveTtsVoice = v ) ) );
		rows.Add( SliderRow( $"MOVE VOICE VOLUME — {(int)MathF.Round( PlayerData.ClampUnit( data.MoveTtsVolume ) * 100 )}%",
			0f, 1f, PlayerData.ClampUnit( data.MoveTtsVolume ),
			v => Mutate( d => d.MoveTtsVolume = v ) ) );

		// NOTE: lichess TV (M9) is deliberately NOT here — not the on/off, not the
		// channel, not the lobby's suggestion. It all lives on the spectator board,
		// which is the thing it controls and the thing you are looking at when you
		// care. Splitting it across two walls was the first attempt and it was wrong:
		// you picked a channel on the south wall for a board on the north one.
		return rows;
	}


	public static List<SettingRow> BuildHostRows( Scene scene )
	{
		var rows = new List<SettingRow>();
		if ( !LobbyNetworkManager.LocalIsAdmin )
		{
			rows.Add( new SettingRow { Label = "Only the lobby admin can change these" } );
			return rows;
		}

		var ring = ChessRing.Instance;
		bool locked = AnyStationOccupied( scene ) || ( ring?.Rebuilding ?? false );
		int current = ring?.StationCount ?? 0;
		int pending = ring?.PendingStationCount ?? current;

		var row = new SettingRow
		{
			Label = pending != current ? $"BOARDS — {current} → {pending}" : $"BOARDS — {current}",
		};
		for ( int n = MinBoards; n <= MaxBoards; n++ )
		{
			int count = n;
			row.Cells.Add( new SettingCell
			{
				Label = count.ToString(),
				Css = "num",
				Selected = count == pending,
				Activate = locked ? null : () =>
				{
					// Routed through the host: the admin may not be the network host (dedi server).
					LobbyNetworkManager.Instance?.RequestSetStationCount( count );
					SettingsVersion++;
				},
			} );
		}
		rows.Add( row );

		if ( AnyStationOccupied( scene ) )
			rows.Add( new SettingRow { Label = "Board count locked while seats are taken" } );
		else if ( pending != current )
			rows.Add( new SettingRow { Label = "Applies when you close this panel" } );
		else if ( ring?.Rebuilding ?? false )
			rows.Add( new SettingRow { Label = "Rebuilding the ring…" } );

		// NOTE: the lobby's TV channel is NOT here either. The admin sets it on the
		// spectator board, using the same picker everyone else uses — see
		// SpectatorScreen. A host row here would have been a second place to set one
		// thing, on a wall away from the board it changes.
		return rows;
	}

	static SettingRow SwatchRow( string label, string current, Action<string> set )
	{
		var row = new SettingRow { Label = label };
		foreach ( var (_, hex) in Swatches )
		{
			string h = hex;
			row.Cells.Add( new SettingCell
			{
				Label = h == "" ? "—" : "",
				Css = "swatch",
				Style = h == "" ? "" : $"background-color: {h};",
				Selected = string.Equals( current ?? "", h, StringComparison.OrdinalIgnoreCase ),
				Activate = () => set( h ),
			} );
		}
		return row;
	}

	// A continuous value rendered as a real draggable slider (SettingsScreen turns the
	// Slider spec into a SliderControl). The label already carries the formatted value,
	// which recomputes as the slider moves because Mutate bumps SettingsVersion and the
	// screen rebuilds its rows. Replaces the old rotaliate-style tick bars (M12).
	static SettingRow SliderRow( string label, float min, float max, float value, Action<float> set )
	{
		return new SettingRow
		{
			Label = label,
			Slider = new SliderSpec { Min = min, Max = max, Value = value, OnChange = set },
		};
	}

	// The TTS voice picker: one tap-to-cycle pill showing the current voice's short name,
	// rather than one cell per voice — a machine can have many installed voices and a row of
	// full names ("Microsoft David Desktop", …) would overflow the panel. Cycling is fixed
	// width whatever the count. The stored value is the FULL name (TrySetVoice needs it); the
	// pill shows the short form.
	static SettingRow VoiceRow( string current, IReadOnlyList<string> voices, Action<string> set )
	{
		if ( voices.Count == 0 )
			return new SettingRow { Label = "TTS VOICE — none installed" };

		// Manual index (IReadOnlyList has no IndexOf): find the stored voice, or fall back to
		// the first when it isn't installed on this machine.
		int idx = 0;
		for ( int k = 0; k < voices.Count; k++ )
			if ( voices[k] == current ) { idx = k; break; }

		var row = new SettingRow { Label = "TTS VOICE" };
		row.Cells.Add( new SettingCell
		{
			Label = Gambit.Audio.MoveTts.Short( voices[idx] ),
			Css = "toggle",
			Selected = true,
			Activate = () => set( voices[( idx + 1 ) % voices.Count] ),
		} );
		return row;
	}

	// A multi-value picker: one clickable cell per option, the current one Selected. Modelled on
	// the host BOARDS cell loop and SwatchRow — SettingsScreen already renders a multi-cell .cells
	// row, so no UI change is needed. Each cell's value is captured per-iteration (the lambda
	// outlives the loop), and Activate routes through the caller's setter (→ Mutate → SettingsVersion++).
	static SettingRow PickerRow( string label, (string Value, string CellLabel)[] options,
		string current, Action<string> set )
	{
		var row = new SettingRow { Label = label };
		foreach ( var (value, cellLabel) in options )
		{
			string v = value; // capture per iteration
			row.Cells.Add( new SettingCell
			{
				Label = cellLabel,
				Css = "toggle",
				Selected = v == current,
				Activate = () => set( v ),
			} );
		}
		return row;
	}

	static SettingRow ToggleRow( string label, bool current, Action<bool> set )
	{
		var row = new SettingRow { Label = label };
		row.Cells.Add( new SettingCell
		{
			Label = current ? "ON" : "OFF",
			Css = "toggle",
			Selected = true,
			Activate = () => set( !current ),
		} );
		return row;
	}

	static void Mutate( Action<PlayerData> change )
	{
		var data = PlayerData.Load() ?? new PlayerData();
		change( data );
		data.Save();
		SettingsVersion++;
	}

	public static bool AnyStationOccupied( Scene scene )
	{
		if ( scene == null ) return false;
		foreach ( var station in scene.GetAllComponents<ChessStation>() )
			if ( station.AnySeatTaken ) return true;
		return false;
	}

	// ── Status summaries for the read-only wall boards ──

	public static int Pct( float v ) =>
		(int)MathF.Round( PlayerData.ClampLightScale( v ) * 100f );

	public static string ColorName( string hex )
	{
		foreach ( var (name, h) in Swatches )
			if ( string.Equals( hex ?? "", h, StringComparison.OrdinalIgnoreCase ) ) return name;
		return "CUSTOM";
	}
}
