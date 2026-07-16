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

	/// <summary>World units between each board's content bottom edge and the floor (passed to
	/// the panels' floor anchor — same as the info board, so they line up).</summary>
	[Property] public float FloorClearance { get; set; } = 30f;

	// The three boards are an EVEN row centred on the wall: +0.24 / 0 / -0.24.
	//
	// All three are also written into lobby.scene, and the code defaults match the
	// scene on purpose — that is the fix, not tidiness. This row had already been
	// bitten by CLAUDE.md's "a new [Property] gets the code default while the ones
	// already in the scene get the scene's" hazard: the scene stated Host/World as
	// +0.12/-0.12 but never gained a MusicXFrac, so Music kept the code default
	// -0.26 and the row rendered +96 / -96 / -208. Nobody chose that; it was the
	// residue of two edits meeting. Keep the two in sync when retuning, or the next
	// board added here inherits the same trap.

	/// <summary>Host-settings board center along the wall, as a fraction of wall
	/// width (+X is the player's left / toward the east wall when facing the south
	/// wall from inside) — host sits closest to the east wall.</summary>
	[Property] public float HostXFrac { get; set; } = 0.24f;

	/// <summary>World-settings board center along the wall, as a fraction of wall width.</summary>
	[Property] public float LocalXFrac { get; set; } = 0f;

	/// <summary>Music board center along the wall, as a fraction of wall width.</summary>
	[Property] public float MusicXFrac { get; set; } = -0.24f;

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
		// Initial Z only — each panel floor-anchors itself in OnUpdate (WallBoardGeometry), so
		// the settings boards read as the same size and sit at the same floor anchor as the
		// east-wall info board.
		float z = 100f;

		_root = new GameObject( true, "SettingsWall" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;

		MakeBoard( "WorldSettingsBoard", new Vector3( LocalXFrac * WallWidth, wallY, z ), facing, SettingsStation.StationKind.World );
		// Half a unit further off the wall so the (transparent-margined) quads never
		// z-fight where they overlap
		MakeBoard( "HostSettingsBoard", new Vector3( HostXFrac * WallWidth, wallY + 0.5f, z ), facing, SettingsStation.StationKind.Host );
		MakeBoard( "MusicBoard", new Vector3( MusicXFrac * WallWidth, wallY + 1f, z ), facing, SettingsStation.StationKind.Music );
	}

	void MakeBoard( string name, Vector3 localPos, Rotation localRot, SettingsStation.StationKind kind )
	{
		var go = new GameObject( true, name );
		go.Parent = _root;
		go.LocalPosition = localPos;
		go.LocalRotation = localRot;
		go.LocalScale = WallBoardGeometry.BoardScale; // shared wall-board size (matches the info board)
		go.AddComponent<WorldPanel>();
		var panel = go.AddComponent<Gambit.UI.WallSettingsPanel>();
		panel.Kind = kind;
		panel.FloorClearance = FloorClearance;

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
