using Sandbox;

namespace Gambit.World;

/// <summary>
/// Hangs the lobby's east-wall boards — info (CenterInfoPanel), lichess
/// (LichessBoardPanel), and dev notes (DevNotesPanel) — each fronted by an
/// InfoStation engage target, so walking up and pressing E opens the interactive
/// InfoScreen ("Press E to view"; the Discord and lichess links there are
/// click-to-copy). Replaces the old spinning FacePlayer board at the ring center:
/// these are statically wall-mounted boards. Same editor-preview pattern as the
/// other walls: OnEnabled/OnValidate rebuild NotSaved GOs, plus a wall-dimension
/// watch in OnUpdate. Client-side only — nothing is networked.
///
/// <para>This component only places boards ALONG the wall. Their size comes from
/// WallBoardGeometry and their Z from their own per-frame floor anchor — see
/// <see cref="AddBoard"/>, which every board here goes through so none of them can
/// drift from the others.</para>
/// </summary>
public sealed class InfoWall : Component, Component.ExecuteInEditor
{
	/// <summary>World units between the wall's inner face (RoomSize / 2) and the
	/// panel plane.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	// There is deliberately no BoardScale knob here any more. It was a duplicate of
	// WallBoardGeometry.Master (both 2.2), which meant "the one place to change board
	// size" was actually two — and a board that skipped it (the lichess one, at a
	// hand-invented (1, 1.3, 1.1)) had no way to be brought back into line from
	// there. All three boards now take WallBoardGeometry.BoardScale.

	// Board positions along the wall, as a fraction of wall width.
	//
	// NOTE THE SIGN. s&box is Source-handed: +X forward, +Y LEFT, +Z up. A player
	// inside the room facing the east wall looks along +X, so their RIGHT is -Y and
	// a HIGHER YFrac sits FURTHER LEFT. (The old comment here claimed +Y was the
	// player's right; it isn't, and it put the lichess board on the wrong side.)
	//
	// Left to right, as the player sees them: Info (0.1), Lichess (-0.1),
	// DevNotes (-0.3).

	/// <summary>Info board center along the wall. Leftmost of the three.</summary>
	[Property] public float InfoYFrac { get; set; } = 0.1f;

	/// <summary>Lichess board center — immediately to the RIGHT of the info board
	/// (lower Y is further right; see the note above).</summary>
	[Property] public float LichessYFrac { get; set; } = -0.1f;

	/// <summary>Dev-notes board center. Rightmost, and currently always hidden
	/// (DevNotesPanel draws nothing with no notes), so it costs the row nothing to
	/// sit on the end.</summary>
	[Property] public float NotesYFrac { get; set; } = -0.3f;

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

		// Three boards, one size, one anchor — all from WallBoardGeometry. The Z here
		// is initial only: each panel floor-anchors itself every frame.
		//
		// The lichess and dev-notes boards sit half a unit further off the wall than
		// the info board so the (transparent-margined) quads never z-fight where they
		// overlap.
		AddBoard( "InfoPanel", InfoYFrac, wallX, facing )
			.AddComponent<Gambit.UI.CenterInfoPanel>().FloorClearance = FloorClearance;

		// The lichess board (M8): a title card showing link state and inviting a press
		// of E. Everything that matters — the copyable URL, the disclosure, unlink —
		// lives in the engaged InfoScreen, because none of it is readable from across
		// the room anyway.
		AddBoard( "LichessPanel", LichessYFrac, wallX - 0.5f, facing )
			.AddComponent<Gambit.UI.LichessBoardPanel>().FloorClearance = FloorClearance;

		AddBoard( "DevNotesPanel", NotesYFrac, wallX - 0.5f, facing )
			.AddComponent<Gambit.UI.DevNotesPanel>().FloorClearance = FloorClearance;

		// Engage stations at each board's foot: "Press E to view" opens InfoScreen
		// (camera stays put; cursor freed for the click-to-copy links).
		AddStation( "InfoStation", InfoYFrac, facing, InfoStation.StationKind.Info );
		AddStation( "LichessStation", LichessYFrac, facing, InfoStation.StationKind.Lichess );
		AddStation( "DevNotesStation", NotesYFrac, facing, InfoStation.StationKind.DevNotes );
	}

	/// <summary>Hang one wall board. Every board on this wall goes through here, so
	/// none of them can drift from the others: the size and the floor-anchor contract
	/// come from WallBoardGeometry and there is nowhere to override them.
	/// <para>The panel plane is local Y (width) / Z (height).</para></summary>
	GameObject AddBoard( string name, float yFrac, float x, Rotation facing )
	{
		var go = new GameObject( true, name );
		go.Parent = _root;
		go.LocalPosition = new Vector3( x, yFrac * WallWidth, 100f );
		go.LocalRotation = facing;
		go.LocalScale = WallBoardGeometry.BoardScale;
		go.AddComponent<WorldPanel>();
		return go;
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
