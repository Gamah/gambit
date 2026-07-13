using Sandbox;

namespace Rotaliate.World;

/// <summary>
/// Hangs the info board (CenterInfoPanel) and the dev-notes board (DevNotesPanel)
/// on the lobby's east wall, each fronted by an InfoStation engage target so walking
/// up and pressing E opens the interactive InfoScreen ("Press E to view"; the Discord
/// link there is click-to-copy). Replaces the old
/// spinning FacePlayer board at the ring center: the panels are statically
/// wall-mounted like the LeaderboardWall boards. Same editor-preview pattern as
/// LeaderboardWall: OnEnabled/OnValidate rebuild NotSaved GOs, plus a
/// wall-dimension watch in OnUpdate. Client-side only — nothing is networked.
/// Both panels keep their content-measured floor anchor (their own OnUpdate
/// drives local Z), so this component only places them along the wall.
/// </summary>
public sealed class InfoWall : Component, Component.ExecuteInEditor
{
	/// <summary>World units between the wall's inner face (RoomSize / 2) and the
	/// panel plane.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	/// <summary>Scale multiplier on both boards' WorldPanels (GO scale (1, 1.3, 2) × this,
	/// same convention as the old center board). Both boards share it so they read as a
	/// matched pair; 2.2 keeps the info board's content under the wall top (150).</summary>
	[Property] public float BoardScale { get; set; } = 2.2f;

	/// <summary>Info board center along the wall, as a fraction of wall width
	/// (+Y is the player's right when facing the east wall from inside).</summary>
	[Property] public float InfoYFrac { get; set; } = 0.1f;

	/// <summary>Dev-notes board center along the wall, as a fraction of wall width.</summary>
	[Property] public float NotesYFrac { get; set; } = -0.1f;

	/// <summary>World units between each board's content bottom edge and the floor
	/// (passed to the panels' floor anchor).</summary>
	[Property] public float FloorClearance { get; set; } = 30f;

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

		// East wall runs along Y; panels face -X, back into the room
		var facing = Rotation.FromYaw( 180f );
		float wallX = WallWidth * 0.5f - WallInset;

		_root = new GameObject( true, "InfoWall" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;

		// Panel plane is local Y (width) / Z (height); the (1, 1.3, 2.6) aspect stretch
		// is the old center board's with an extra 1.3× height, CenterInfoPanel's halved
		// px values compensating. Z here is initial only — each panel's OnUpdate
		// floor-anchors it via FloorClearance.
		var info = new GameObject( true, "InfoPanel" );
		info.Parent = _root;
		info.LocalPosition = new Vector3( wallX, InfoYFrac * WallWidth, 100f );
		info.LocalRotation = facing;
		info.LocalScale = new Vector3( 1f, 1.8f, 2.4f ) * BoardScale;
		info.AddComponent<WorldPanel>();
		info.AddComponent<Rotaliate.UI.CenterInfoPanel>().FloorClearance = FloorClearance;

		// Half a unit further off the wall than the info board so the (transparent-
		// margined) quads never z-fight where they overlap
		var notes = new GameObject( true, "DevNotesPanel" );
		notes.Parent = _root;
		notes.LocalPosition = new Vector3( wallX - 0.5f, NotesYFrac * WallWidth, 100f );
		notes.LocalRotation = facing;
		notes.LocalScale = new Vector3( 1f, 1.8f, 2.4f ) * BoardScale;
		notes.AddComponent<WorldPanel>();
		notes.AddComponent<Rotaliate.UI.DevNotesPanel>().FloorClearance = FloorClearance;

		// Engage stations at each board's foot: "Press E to view" opens InfoScreen
		// (camera stays put; cursor freed for the click-to-copy Discord link).
		AddStation( "InfoStation", InfoYFrac, facing, InfoStation.StationKind.Info );
		AddStation( "DevNotesStation", NotesYFrac, facing, InfoStation.StationKind.DevNotes );
	}

	void AddStation( string name, float yFrac, Rotation facing, InfoStation.StationKind kind )
	{
		var go = new GameObject( true, name );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = _root;
		go.LocalPosition = new Vector3( WallWidth * 0.5f - WallInset, yFrac * WallWidth, WallHeight * 0.5f );
		go.LocalRotation = facing;
		go.AddComponent<InfoStation>().Kind = kind;
	}

	void Clear()
	{
		if ( _root.IsValid() )
			_root.Destroy();
		_root = null;
	}
}
