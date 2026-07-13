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
	}

	// Light hue choices; "" = keep the default (scene hue / neutral white)
	public static readonly (string Name, string Hex)[] Swatches =
	{
		("AUTO", ""),
		("WHITE", "#FFFFFF"),
		("WARM", "#FFD9A8"),
		("RED", "#FF5848"),
		("YELLOW", "#FFE066"),
		("GREEN", "#58E87A"),
		("CYAN", "#4CD2FF"),
		("BLUE", "#5878FF"),
		("PURPLE", "#B468FF"),
	};

	// Brightness ticks in percent of the scene-tuned baseline; 0 = off
	public static readonly int[] Ticks = { 0, 15, 30, 45, 60, 75, 90, 105, 120, 135, 150 };

	// Floor pop-frequency steps (× the base interval), matching the brightness slider's
	// tick count; 1× = the scene-tuned default.
	public static readonly float[] PopRates =
		{ 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.25f, 2.5f, 2.75f, 3f };

	public const int MinCabinets = 2;
	public const int MaxCabinets = 16;

	public static List<SettingRow> BuildLocalRows()
	{
		var rows = new List<SettingRow>();
		var data = PlayerData.Load() ?? new PlayerData();

		// "Room theme" drives the wall-board UI palette (WallTheme); the room light
		// itself is always white now — only its brightness is tunable.
		rows.Add( SwatchRow( "ROOM THEME", data.WorldLightColor,
			hex => Mutate( d => d.WorldLightColor = hex ) ) );
		rows.Add( TickRow( "ROOM LIGHT BRIGHTNESS", data.WorldLightBrightness,
			v => Mutate( d => d.WorldLightBrightness = v ) ) );
		// Marquee colour is fixed to pure white now (MarqueeGlow); only brightness is tunable.
		rows.Add( TickRow( "MARQUEE LIGHT BRIGHTNESS", data.MarqueeLightBrightness,
			v => Mutate( d => d.MarqueeLightBrightness = v ) ) );
		rows.Add( ToggleRow( "CHECKERBOARD FLOOR", data.CheckerboardFloor,
			v => Mutate( d => d.CheckerboardFloor = v ) ) );
		rows.Add( RateRow( "POP FREQUENCY", PlayerData.ClampPopRate( data.FloorPopRate ), PopRates,
			v => Mutate( d => d.FloorPopRate = v ) ) );
		rows.Add( ToggleRow( "MY CABINET SOUNDS", data.MyCabinetSounds,
			v => Mutate( d => d.MyCabinetSounds = v ) ) );
		rows.Add( ToggleRow( "OTHER CABINET SOUNDS", data.RemoteCabinetSounds,
			v => Mutate( d => d.RemoteCabinetSounds = v ) ) );
		rows.Add( ToggleRow( "SKIP ATTRACT DEMO", data.DemoSkip,
			v => Mutate( d => d.DemoSkip = v ) ) );
		// Spectator board — giant cube board mirroring cabinet 0's active game over
		// the leaderboard wall. Per-client (each player chooses whether to see it).
		rows.Add( ToggleRow( "SPECTATOR BOARD", data.SpectatorBoard,
			v => Mutate( d => d.SpectatorBoard = v ) ) );
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

		var ring = ArcadeRing.Instance;
		bool locked = AnyStationOccupied( scene ) || ( ring?.Rebuilding ?? false );
		int current = ring?.StationCount ?? 0;
		int pending = ring?.PendingStationCount ?? current;

		var row = new SettingRow
		{
			Label = pending != current ? $"CABINETS — {current} → {pending}" : $"CABINETS — {current}",
		};
		for ( int n = MinCabinets; n <= MaxCabinets; n++ )
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
			rows.Add( new SettingRow { Label = "Cabinet count locked while cabinets are occupied" } );
		else if ( pending != current )
			rows.Add( new SettingRow { Label = "Applies when you close this panel" } );
		else if ( ring?.Rebuilding ?? false )
			rows.Add( new SettingRow { Label = "Rebuilding the ring…" } );

		rows.Add( TargetUrlRow( scene ) );
		return rows;
	}

	// Host picks the backend every client talks to (replicated via LobbyNetworkManager.TargetUrl).
	static readonly (string Label, string Url)[] Instances =
	{
		("LIVE", Gambit.Api.ApiClient.ProdUrl),
		("TEST", Gambit.Api.ApiClient.TestUrl),
	};

	/// <summary>Label for the active backend, for the read-only wall board.</summary>
	public static string TargetServerLabel()
	{
		foreach ( var (label, url) in Instances )
			if ( Gambit.Api.ApiClient.BaseUrl == url ) return label;
		return "CUSTOM";
	}

	static SettingRow TargetUrlRow( Scene scene )
	{
		var net = LobbyNetworkManager.Instance;
		var row = new SettingRow { Label = "TARGET SERVER" };
		foreach ( var (label, url) in Instances )
		{
			var target = url;
			row.Cells.Add( new SettingCell
			{
				Label = label,
				Selected = Gambit.Api.ApiClient.BaseUrl == target,
				Activate = net == null ? null : () =>
				{
					// Host-routed: the server is authoritative, then [Sync] fans it back to everyone.
					net.RequestSetTargetUrl( target );
					SettingsVersion++;
				},
			} );
		}
		return row;
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

	static SettingRow TickRow( string label, float current, Action<float> set )
	{
		int pct = Pct( current );
		var row = new SettingRow { Label = $"{label} — {pct}%" };
		foreach ( var t in Ticks )
		{
			int v = t;
			row.Cells.Add( new SettingCell
			{
				Css = v <= pct ? "tick filled" : "tick",
				Selected = Math.Abs( v - pct ) <= 7,
				Activate = () => set( v / 100f ),
			} );
		}
		return row;
	}

	// Stepped float "slider" (filled-tick bar like TickRow) for an arbitrary value set,
	// e.g. the floor pop frequency. Selected = nearest tick; ticks up to current fill.
	static SettingRow RateRow( string label, float current, float[] steps, Action<float> set )
	{
		var row = new SettingRow { Label = $"{label} — {current:0.##}×" };
		float half = steps.Length > 1 ? ( steps[1] - steps[0] ) * 0.5f : 0.01f;
		foreach ( var s in steps )
		{
			float v = s;
			row.Cells.Add( new SettingCell
			{
				Css = v <= current + 0.001f ? "tick filled" : "tick",
				Selected = MathF.Abs( v - current ) <= half,
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
		foreach ( var station in scene.GetAllComponents<ArcadeStation>() )
			if ( station.Occupied ) return true;
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
