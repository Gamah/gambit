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

	// Three boards again, on the ODD row this wall was originally laid out for:
	// +0.24 / 0 / -0.24. It went to an even +0.13 / -0.13 pair when the music board was
	// deleted (music is M/N keys now, see Gambit.UI.MusicScreen); MORE takes that vacated
	// outer slot rather than opening a fourth position, so the row is centred either way
	// and no board moves relative to the wall's middle.
	//
	// Both are also written into lobby.scene, and the code defaults match the scene on
	// purpose — that is the fix, not tidiness. This row had already been bitten by
	// CLAUDE.md's "a new [Property] gets the code default while the ones already in the
	// scene get the scene's" hazard: the scene stated Host/World as +0.12/-0.12 but never
	// gained a MusicXFrac, so Music kept the code default -0.26 and the row rendered
	// +96 / -96 / -208. Nobody chose that; it was the residue of two edits meeting. Keep
	// the two in sync when retuning, or the next board added here inherits the same trap.

	/// <summary>Host-settings board center along the wall, as a fraction of wall
	/// width (+X is the player's left / toward the east wall when facing the south
	/// wall from inside) — host sits closest to the east wall.</summary>
	[Property] public float HostXFrac { get; set; } = 0.24f;

	/// <summary>World-settings board center along the wall, as a fraction of wall width.</summary>
	[Property] public float LocalXFrac { get; set; } = 0f;

	/// <summary>MORE board center along the wall — the slot the music board used to hold,
	/// furthest from the east wall. Written into lobby.scene alongside the other two: this is
	/// a NEW property, and the hazard above is exactly that a scene which never gained a key
	/// leaves it on the code default while its neighbours take the scene's.</summary>
	[Property] public float MoreXFrac { get; set; } = -0.24f;

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
		MakeMoreBoard( new Vector3( MoreXFrac * WallWidth, wallY, z ), facing );
	}

	/// <summary>
	/// The MORE board — the Discord invite and the "our other games" link, both click-to-copy
	/// (there is no API to open a URL from game code). It hangs on this wall because it is
	/// where the music board used to be, not because it is a setting: it carries an
	/// <see cref="InfoStation"/> rather than a <see cref="SettingsStation"/>, so E opens the
	/// InfoScreen viewer that already knows how to render copyable links, and no engage
	/// plumbing is duplicated for it.
	/// </summary>
	void MakeMoreBoard( Vector3 localPos, Rotation localRot )
	{
		var go = new GameObject( true, "MoreBoard" );
		go.Parent = _root;
		go.LocalPosition = localPos;
		go.LocalRotation = localRot;
		go.LocalScale = WallBoardGeometry.BoardScale;
		go.AddComponent<WorldPanel>();
		go.AddComponent<Gambit.UI.MoreBoardPanel>().FloorClearance = FloorClearance;

		var station = go.AddComponent<InfoStation>();
		station.Kind = InfoStation.StationKind.More;
		station.InteractRange = InteractRange;
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
