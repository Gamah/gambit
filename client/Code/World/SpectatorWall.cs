using System;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Hangs the giant spectator board over the lobby's north wall (M5 —
/// SpectatorBoard rewrite; mirrors rotaliate-client's floating SpectatorBoard) and
/// fronts it with a walk-up control board (SpectatorInfoPanel) at eye height that carries
/// the SpectatorStation engage target ("Press E to spectate") and opens the interactive
/// channel picker. The big board is a display-only 3D chess set (SpectatorBoard3D) driven
/// by a single
/// <see cref="Gambit.Game.SpectatorController"/> living on this wall — it mirrors a live
/// sbox game happening at one of the tables.
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
/// the host-folded FEN from a live table).
/// </summary>
public sealed class SpectatorWall : Component, Component.ExecuteInEditor
{
	/// <summary>Intrinsic pixel size of a SpectatorSeatPanel's &lt;root&gt; — name on line 1,
	/// rating+clock on line 2.
	///
	/// <para>FIXED, and sized against the WORST name either source can produce, because
	/// the name is the one field whose length we don't control and
	/// <c>white-space: nowrap</c> means an over-long one clips rather than wraps:</para>
	/// <list type="bullet">
	/// <item><b>Steam</b> — a persona name is up to <b>32 chars</b>, with no title. This is
	/// the binding case.</item>
	/// <item><b>lichess</b> — a username is up to 20 chars, plus a title of up to 3
	/// ("WGM"/"WCM"/"BOT") and its gap: ~24 equivalent.</item>
	/// </list>
	/// <para>32 chars of 30px bold ≈ 18px/char ≈ 576px, plus the 22px chip and its 14px
	/// gap, plus 40px padding each side = ~692. Rounded up to 760 for headroom — the old
	/// 640 was sized for the lichess case only and would have clipped a long Steam name.
	/// The uniform scale is then chosen to fit this fixed-width tag into the space beside
	/// the board, so a wider tag means a slightly smaller one in a tight room (text stays
	/// proportional and readable) rather than a clipped one.</para></summary>
	const float SeatPxWidth = 760f;
	const float SeatPxHeight = 200f;

	/// <summary>WorldPanel px→world-unit factor (Sandbox ScreenToWorldScale) — a panel of P px
	/// renders P × scale × this many world units wide; used to size/place the seat tags.</summary>
	const float PxToWorld = 0.05f;

	/// <summary>Keep a seat tag this far off the side wall when fitting it beside the board.</summary>
	const float SeatWallMargin = 10f;

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

	/// <summary>Degrees the board leans inward from vertical — the top swings out over the room
	/// (pivoting off the fixed bottom edge) so the face angles down toward players on the floor.
	/// Tuned in-editor; negate to lean back toward the wall instead.</summary>
	[Property] public float TiltDegrees { get; set; } = 7f;

	/// <summary>MAX uniform scale of a player-tag strip. The build shrinks below this if needed to
	/// fit the fixed-width tag beside the board, so this just caps the text size in a roomy layout.</summary>
	[Property] public float SeatBoardScale { get; set; } = 5f;

	/// <summary>Gap between a player's board edge and the near edge of their tag, as a fraction of a
	/// board square (0.5 = half a square). The tag sits just outside the edge, coplanar with the
	/// board.</summary>
	[Property] public float SeatEdgeCells { get; set; } = 0.5f;

	/// <summary>How far up the board (as a fraction of its height) each tag sits, measured from
	/// that player's own edge — 0 = at the edge, 0.5 = centred. Default 1/3 biases each tag toward
	/// its own half.</summary>
	[Property] public float SeatEdgeBias { get; set; } = 1f / 3f;

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
		// width and hovering above the wall top so the whole lobby can watch one game from across
		// the room. SpectatorBoard3D builds the board FLAT (surface normal = local +Z, ranks along
		// local +Y with rank 1 at −Y, pieces standing up in +Z). We stand it up and tilt it
		// INWARD, pivoting off its bottom edge:
		//   • rotate +90° about the wall-horizontal axis (+X) stands the flat board up — the rank
		//     axis (+Y) swings to world up, so rank 1 lands at the bottom and the a-file on the
		//     viewer's left (White bottom-left);
		//   • ADD TiltDegrees so the top leans into the room (−Y) and the face angles DOWN toward
		//     players on the floor (flip the sign to lean back toward the wall instead);
		//   • anchor the bottom-edge centre at wall-top + ClearAboveWall and offset back to the
		//     board centre through the tilt, so the tilt pivots off the bottom edge (bottom stays
		//     put, top swings out) rather than about the board centre.
		float boardSize = BoardCellSize * 8f;
		float halfBoard = boardSize * 0.5f;
		var boardRot = Rotation.FromAxis( Vector3.Forward, 90f + TiltDegrees );
		var bottomAnchor = new Vector3( centreX, wallY, WallHeight + ClearAboveWall );
		var boardCentre = bottomAnchor + boardRot * new Vector3( 0f, halfBoard, 0f );

		var board = new GameObject( true, "SpectatorBoard" );
		board.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		board.Parent = _root;
		board.LocalPosition = boardCentre;
		board.LocalRotation = boardRot;
		// Add disabled so we can set the size before it builds (OnEnabled), then enable — a
		// [Property] set from code doesn't re-run OnValidate, so building at the final size in
		// one pass beats building at the default and rebuilding.
		var board3d = board.AddComponent<Gambit.World.SpectatorBoard3D>( false );
		board3d.CellSize = BoardCellSize;
		board3d.Enabled = true;

		// Two player tags (replacing the single overhead scoreboard that tried to show both sides
		// at once and read poorly). Each lies coplanar with the board and sits just OUTSIDE its
		// player's RIGHT edge — White faces up the board so its right is the +X (h-file) edge; Black
		// faces down so its right is the −X (a-file) edge. Vertically each is biased toward its own
		// player's half (SeatEdgeBias up the board from that player's edge), not centred. Placed in
		// the board's local frame (files X, ranks Y, out-of-face +Z) and transformed through the
		// board's own position/rotation, so they track the tilt automatically. Each tag is a
		// FIXED-width panel (SeatPxWidth, sized for a long name); we pick the uniform
		// scale that fits it into the gap between the board edge and the side wall — so the content
		// always fits and the world size just adapts to the room. Offset from the edge by half a
		// square (SeatEdgeCells). Both tags share the tighter side's fit so they match.
		float edgeGap = BoardCellSize * SeatEdgeCells;
		float sideSpace = ( WallWidth * 0.5f - MathF.Abs( centreX ) - WallInset ) - halfBoard
			- edgeGap - SeatWallMargin;
		float fitScale = MathF.Max( MathF.Min( SeatBoardScale, sideSpace / ( SeatPxWidth * PxToWorld ) ), 0.1f );
		float tagWorldW = SeatPxWidth * fitScale * PxToWorld;
		float outX = halfBoard + edgeGap + tagWorldW * 0.5f; // tag centre just outside the edge
		// Vertical bias: SeatEdgeBias of the board's height in from each player's edge (0 = at the
		// edge, 0.5 = centred). White (bottom) sits below centre, Black (top) above.
		float seatY = halfBoard - SeatEdgeBias * boardSize;

		// A WorldPanel faces its GO +X with up +Z; rotate it to lie in the board plane facing out
		// of the face (board +Z) with text up along the ranks (board +Y).
		var inPlane = boardRot * Rotation.LookAt( new Vector3( 0f, 0f, 1f ), new Vector3( 0f, 1f, 0f ) );

		Vector3 WhiteOffset = new( outX, -seatY, 0f );  // right of board, biased toward the bottom
		Vector3 BlackOffset = new( -outX, seatY, 0f );  // left of board, biased toward the top

		AddSeatTag( "SpectatorWhiteTag", boardCentre + boardRot * WhiteOffset, inPlane, fitScale, white: true );
		AddSeatTag( "SpectatorBlackTag", boardCentre + boardRot * BlackOffset, inPlane, fitScale, white: false );

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

	/// <summary>A single player-tag strip (SpectatorSeatPanel) lying coplanar with the board at its
	/// edge — fixed intrinsic size (SeatPxWidth × SeatPxHeight), sized to a max-length name, at the
	/// given uniform scale.</summary>
	void AddSeatTag( string name, Vector3 localPos, Rotation rotation, float scale, bool white )
	{
		var tag = new GameObject( true, name );
		tag.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		tag.Parent = _root;
		tag.LocalPosition = localPos;
		tag.LocalRotation = rotation;
		tag.LocalScale = new Vector3( 1f, 1f, 1f ) * scale;
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
