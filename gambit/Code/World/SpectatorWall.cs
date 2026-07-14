using Sandbox;

namespace Gambit.World;

/// <summary>
/// Hangs the giant spectator board over the lobby's north wall (PLAN.md M5 —
/// SpectatorBoard rewrite; mirrors rotaliate-client's floating SpectatorBoard) and
/// fronts it with a walk-up control board (SpectatorInfoPanel) at eye height that carries
/// the SpectatorStation engage target ("Press E to spectate") and opens the interactive
/// channel picker. The big board is a display-only 3D chess set (SpectatorBoard3D) driven
/// by a single
/// <see cref="Gambit.Game.SpectatorController"/> living on this wall — it mirrors a live
/// sbox game, streams lichess TV (polled), or watches a game by id.
///
/// The board <b>floats above the wall top</b>, centred on the wall's width and facing
/// back into the room, sized to read from across the lobby — not flat against the wall.
/// Its bottom edge always clears the wall top by <see cref="ClearAboveWall"/> regardless
/// of <see cref="BoardCellSize"/> (the vertical centre is wall-top + clearance + half the
/// board's world height, which tracks the cell size).
///
/// Same editor-preview + wall-dimension-watch pattern as InfoWall/SettingsWall:
/// OnEnabled/OnValidate rebuild NotSaved GOs; OnUpdate rebuilds on a room resize.
/// Client-side only — nothing is networked (each client reads its own synced copy of
/// the relayed FEN, or polls lichess itself).
/// </summary>
public sealed class SpectatorWall : Component, Component.ExecuteInEditor
{
	/// <summary>Intrinsic pixel size of a SpectatorSeatPanel's &lt;root&gt; — one player's tag
	/// (name + rating | clock) that hugs a corner of the 3D board.</summary>
	const float SeatPxWidth = 360f;
	const float SeatPxHeight = 170f;

	/// <summary>WorldPanel px→world-unit factor (Sandbox ScreenToWorldScale) — a panel of P px
	/// renders P × scale × this many world units wide; used to place the seat tags at the board
	/// corners by their own world size.</summary>
	const float PxToWorld = 0.05f;

	/// <summary>World units the board sits in front of the wall's inner face
	/// (RoomSize / 2), toward the room.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	/// <summary>World-unit edge length of one board square. The 3D board is 8× this wide; it
	/// floats high above the wall and must read from across the room, so it's large.
	/// SpectatorBoard3D is sized directly to this and its GO is left <b>unscaled</b>, so the
	/// board light's world-unit radius needs no transform-scale compensation. The board grows
	/// upward from a fixed bottom edge (see <see cref="ClearAboveWall"/>), so changing this
	/// keeps the bottom in place.</summary>
	[Property] public float BoardCellSize { get; set; } = 56f;

	/// <summary>Degrees the board leans back from vertical (top toward the wall) so pieces,
	/// standing out of the near-vertical face, cast shadows across it under the board light.
	/// Tuned in-editor; flip the sign if the face ends up angled the wrong way.</summary>
	[Property] public float TiltDegrees { get; set; } = 7f;

	/// <summary>Uniform scale multiplier on each corner seat tag (name + rating | clock).</summary>
	[Property] public float SeatBoardScale { get; set; } = 6f;

	/// <summary>Gap between the 3D board's edge and the near edge of a seat tag — the tags sit
	/// just outside the board corners so they never cover the pieces.</summary>
	[Property] public float SeatGap { get; set; } = 10f;

	/// <summary>Gap between the wall top and the bottom edge of the floating board. The
	/// board's clearance tracks the scale automatically (centre = wall-top + this +
	/// half the board's world height).</summary>
	[Property] public float ClearAboveWall { get; set; } = 18f;

	/// <summary>Board centre along the wall, as a fraction of wall width (0 = centred).</summary>
	[Property] public float BoardYFrac { get; set; } = 0f;

	/// <summary>World units between the walk-up control board's content bottom edge and the
	/// floor (passed to SpectatorInfoPanel's floor anchor — same as the info board so they
	/// read as one size).</summary>
	[Property] public float InfoFloorClearance { get; set; } = 30f;

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

		// North wall runs along X at +RoomSize/2; panels face -Y, back into the room
		// (one wall clockwise from the old west wall — the west wall's neighbour on the
		// player's right). Width runs along X, depth along Y.
		var facing = Rotation.FromYaw( -90f );
		float wallY = WallWidth * 0.5f - WallInset;
		float centreX = BoardYFrac * WallWidth;

		_root = new GameObject( true, "SpectatorWall" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;

		// The one controller that feeds the board — lives on the wall root so both the
		// display panel and the engaged screen find it via SpectatorController.Instance.
		_root.AddComponent<Gambit.Game.SpectatorController>();

		// Floating display board: a real 3D chess set (SpectatorBoard3D), centred on the wall
		// width and hovering just above the wall top so the whole lobby can watch one game from
		// across the room. SpectatorBoard3D builds the board FLAT (surface normal = local +Z,
		// pieces standing up in +Z); we stand it up here and lean it back so the pieces throw
		// shadows across the face:
		//   • rotate +90° about the wall-horizontal axis (+X) → the flat +Z normal swings to
		//     -Y (into the room) and the board's rank axis (+Y) swings to +Z (world up), so
		//     rank 1 lands at the bottom and the a-file on the viewer's left (White bottom-left);
		//   • take TiltDegrees off that angle so the top leans back toward the wall.
		// Its bottom edge clears the wall top by ClearAboveWall (the board's world half-height
		// is just half its edge length, since it's built at world size and left unscaled).
		float boardSize = BoardCellSize * 8f;
		float boardZ = WallHeight + ClearAboveWall + boardSize * 0.5f;

		var board = new GameObject( true, "SpectatorBoard" );
		board.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		board.Parent = _root;
		board.LocalPosition = new Vector3( centreX, wallY, boardZ );
		board.LocalRotation = Rotation.FromAxis( Vector3.Forward, 90f - TiltDegrees );
		// Add disabled so we can set the size before it builds (OnEnabled), then enable — a
		// [Property] set from code doesn't re-run OnValidate, so building at the final size in
		// one pass beats building at the default and rebuilding.
		var board3d = board.AddComponent<Gambit.World.SpectatorBoard3D>( false );
		board3d.CellSize = BoardCellSize;
		board3d.Enabled = true;

		// Two player tags, one per corner of the 3D board (replacing the single overhead
		// scoreboard that tried to show both sides at once and read poorly). The board hangs
		// White at the bottom (rank 1 at low Z) with the a-file on the viewer's left (world −X),
		// so: White → bottom-right, Black → top-left. Each tag sits just OUTSIDE its corner
		// (SeatGap) so it never covers the pieces. Flat WorldPanels facing the room so the text
		// reads squarely (unlike the tilted board).
		float halfBoard = boardSize * 0.5f;
		float seatHalfW = SeatPxWidth * SeatBoardScale * PxToWorld * 0.5f;
		float seatHalfH = SeatPxHeight * SeatBoardScale * PxToWorld * 0.5f;
		// Right/left edge, tag's outer edge flush with the board edge; below/above by SeatGap.
		float seatX = halfBoard - seatHalfW;
		float belowZ = boardZ - halfBoard - SeatGap - seatHalfH;
		float aboveZ = boardZ + halfBoard + SeatGap + seatHalfH;

		AddSeatTag( "SpectatorWhiteTag", new Vector3( centreX + seatX, wallY, belowZ ), facing, white: true );
		AddSeatTag( "SpectatorBlackTag", new Vector3( centreX - seatX, wallY, aboveZ ), facing, white: false );

		// Walk-up control board at walk-up height, directly under the floating board: shows the
		// current source, players, time control/move, and live status, and invites "Press E to
		// spectate". It carries the engage station so the visible sign and the walk-up target
		// line up (the player-distance check is horizontal, so Z is free — the panel floor-
		// anchors itself). Sized to match the east-wall info board (WallBoardGeometry): default
		// PanelSize + shared BoardScale + floor anchor.
		var info = new GameObject( true, "SpectatorInfoBoard" );
		info.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		info.Parent = _root;
		info.LocalPosition = new Vector3( centreX, wallY, 100f ); // initial only — panel floor-anchors
		info.LocalRotation = facing;
		info.LocalScale = WallBoardGeometry.BoardScale;
		info.AddComponent<WorldPanel>();
		info.AddComponent<Gambit.UI.SpectatorInfoPanel>().FloorClearance = InfoFloorClearance;
		info.AddComponent<SpectatorStation>();
	}

	/// <summary>A single corner player tag (SpectatorSeatPanel) — a flat WorldPanel facing the
	/// room, sized by SeatBoardScale.</summary>
	void AddSeatTag( string name, Vector3 localPos, Rotation facing, bool white )
	{
		var tag = new GameObject( true, name );
		tag.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		tag.Parent = _root;
		tag.LocalPosition = localPos;
		tag.LocalRotation = facing;
		tag.LocalScale = new Vector3( 1f, 1f, 1f ) * SeatBoardScale;
		var panel = tag.AddComponent<WorldPanel>();
		panel.PanelSize = new Vector2( SeatPxWidth, SeatPxHeight );
		tag.AddComponent<Gambit.UI.SpectatorSeatPanel>().White = white;
	}

	void Clear()
	{
		if ( _root.IsValid() )
			_root.Destroy();
		_root = null;
	}
}
