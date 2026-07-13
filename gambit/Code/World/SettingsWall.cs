using System;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Hangs the two settings boards (WallSettingsPanel: local world settings + host
/// settings, issue #49) on the lobby's south wall. Same editor-preview pattern as
/// the other walls: OnEnabled/OnValidate rebuild NotSaved GOs, plus a
/// wall-dimension watch in OnUpdate. Boards are client-side only and display-only;
/// interaction is the cabinet-style engage flow — each board gets a SettingsStation
/// + camera anchor, and the editable UI is the SettingsScreen ScreenPanel shown
/// while locked on. (The room-light brightness slider is now applied by
/// RoomLightOrbit, the single writer of the RoomLight colour.)
/// </summary>
public sealed class SettingsWall : Component, Component.ExecuteInEditor
{
	/// <summary>World units between the wall's inner face (RoomSize / 2) and the
	/// panel plane.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	/// <summary>Board height as a fraction of wall height, centered vertically.</summary>
	[Property] public float BoardHeightFrac { get; set; } = 0.92f;

	/// <summary>Host-settings board center along the wall, as a fraction of wall
	/// width (+X is the player's left / toward the east wall when facing the south
	/// wall from inside) — host sits closest to the east wall.</summary>
	[Property] public float HostXFrac { get; set; } = 0.26f;

	/// <summary>World-settings board center along the wall, as a fraction of wall width.</summary>
	[Property] public float LocalXFrac { get; set; } = 0f;

	/// <summary>Music board center along the wall, as a fraction of wall width.</summary>
	[Property] public float MusicXFrac { get; set; } = -0.26f;

	/// <summary>Approximate world size of a WorldPanel quad per unit of GO scale —
	/// the same ~36-units-at-scale-1 intrinsic size as the cabinet screens.</summary>
	[Property] public float PanelUnitWidth { get; set; } = 36f;

	/// <summary>Distance from the board plane to its locked-camera anchor — sets how
	/// much of the FOV the board fills while engaged (same trig as the cabinets).</summary>
	[Property] public float ViewDistance { get; set; } = 140f;

	/// <summary>Horizontal walk-up range for the "Press E" prompt.</summary>
	[Property] public float InteractRange { get; set; } = 130f;

	GameObject _root;
	Vector2 _builtWall;

	LobbyRoom Room => Components.Get<LobbyRoom>();
	float WallWidth => Room?.RoomSize ?? 800f;
	float WallHeight => Room?.WallHeight ?? 150f;

	protected override void OnEnabled() => Rebuild();

	protected override void OnValidate() => Rebuild();

	/// <summary>Re-run the build after a code hotload (Editor/HotloadRebuild.cs).</summary>
	public void RebuildPreview() => Rebuild();

	protected override void OnDisabled() => Clear();

	protected override void OnUpdate()
	{
		if ( _builtWall != new Vector2( WallWidth, WallHeight ) )
			Rebuild();
	}

	void Rebuild()
	{
		if ( !Active ) return;
		Clear();
		_builtWall = new Vector2( WallWidth, WallHeight );

		// South wall runs along X at -RoomSize/2; panels face +Y, back into the room
		var facing = Rotation.FromYaw( 90f );
		float wallY = -( WallWidth * 0.5f - WallInset );
		float scale = BoardHeightFrac * WallHeight / PanelUnitWidth;
		float z = WallHeight * 0.5f;

		_root = new GameObject( true, "SettingsWall" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;

		MakeBoard( "WorldSettingsBoard", new Vector3( LocalXFrac * WallWidth, wallY, z ), facing, scale, SettingsStation.StationKind.World );
		// Half a unit further off the wall so the (transparent-margined) quads never
		// z-fight where they overlap
		MakeBoard( "HostSettingsBoard", new Vector3( HostXFrac * WallWidth, wallY + 0.5f, z ), facing, scale, SettingsStation.StationKind.Host );
		MakeBoard( "MusicBoard", new Vector3( MusicXFrac * WallWidth, wallY + 1f, z ), facing, scale, SettingsStation.StationKind.Music );
	}

	void MakeBoard( string name, Vector3 localPos, Rotation localRot, float scale, SettingsStation.StationKind kind )
	{
		var go = new GameObject( true, name );
		go.Parent = _root;
		go.LocalPosition = localPos;
		go.LocalRotation = localRot;
		go.LocalScale = scale; // uniform — glyphs must not stretch
		go.AddComponent<WorldPanel>();
		go.AddComponent<Gambit.UI.WallSettingsPanel>().Kind = kind;

		var station = go.AddComponent<SettingsStation>();
		station.Kind = kind;
		station.InteractRange = InteractRange;
	}

	void Clear()
	{
		if ( _root.IsValid() )
			_root.Destroy();
		_root = null;
	}
}
