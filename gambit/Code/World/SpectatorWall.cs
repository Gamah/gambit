using Sandbox;

namespace Gambit.World;

/// <summary>
/// Hangs the giant spectator board over the lobby's north wall (PLAN.md M5 —
/// SpectatorBoard rewrite; mirrors rotaliate-client's floating SpectatorBoard) and
/// fronts it with a walk-up control board (SpectatorInfoPanel) at eye height that carries
/// the SpectatorStation engage target ("Press E to spectate") and opens the interactive
/// channel picker. The big board is a display-only WorldPanel (SpectatorBoardPanel) driven
/// by a single
/// <see cref="Gambit.Game.SpectatorController"/> living on this wall — it mirrors a live
/// sbox game, streams lichess TV (polled), or watches a game by id.
///
/// The board <b>floats above the wall top</b>, centred on the wall's width and facing
/// back into the room, sized to read from across the lobby — not flat against the wall.
/// Its bottom edge always clears the wall top by <see cref="ClearAboveWall"/> regardless
/// of <see cref="BoardScale"/> (the vertical centre is wall-top + clearance + half the
/// board's world height, and the half-height tracks the scale).
///
/// Same editor-preview + wall-dimension-watch pattern as InfoWall/SettingsWall:
/// OnEnabled/OnValidate rebuild NotSaved GOs; OnUpdate rebuilds on a room resize.
/// Client-side only — nothing is networked (each client reads its own synced copy of
/// the relayed FEN, or polls lichess itself).
/// </summary>
public sealed class SpectatorWall : Component, Component.ExecuteInEditor
{
	/// <summary>Intrinsic pixel size of SpectatorBoardPanel's &lt;root&gt; — the WorldPanel's
	/// PanelSize is pinned to this so the 720×820 board isn't clipped to the 512-px default
	/// (which cropped the board's lower ranks + status line and read as "squished").</summary>
	const float PanelPxWidth = 720f;
	const float PanelPxHeight = 820f;

	/// <summary>Intrinsic pixel size of SpectatorInfoPanel's &lt;root&gt; — the walk-up
	/// control board at eye height. Landscape so it reads as a sign, not a big square.</summary>
	const float InfoPxWidth = 560f;
	const float InfoPxHeight = 300f;

	/// <summary>WorldPanel px→world-unit factor (Sandbox ScreenToWorldScale) — a panel of P
	/// px renders P × scale × this many world units wide.</summary>
	const float PxToWorld = 0.05f;

	/// <summary>World units the board plane sits in front of the wall's inner face
	/// (RoomSize / 2), toward the room.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	/// <summary>Uniform scale multiplier on the floating board WorldPanel — it floats high
	/// above the wall and must read from across the whole room, so it's large.</summary>
	[Property] public float BoardScale { get; set; } = 7.5f;

	/// <summary>Uniform scale multiplier on the walk-up control board.</summary>
	[Property] public float InfoBoardScale { get; set; } = 4.5f;

	/// <summary>Gap between the wall top and the bottom edge of the floating board. The
	/// board's clearance tracks the scale automatically (centre = wall-top + this +
	/// half the board's world height).</summary>
	[Property] public float ClearAboveWall { get; set; } = 18f;

	/// <summary>Board centre along the wall, as a fraction of wall width (0 = centred).</summary>
	[Property] public float BoardYFrac { get; set; } = 0f;

	/// <summary>Height up the wall of the walk-up control board + "Press E" engage target, as
	/// a fraction of wall height — kept at eye level (the big board floats out of reach).</summary>
	[Property] public float StationHeightFrac { get; set; } = 0.4f;

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

		// Floating display board: panel plane is local width/height, centred on the wall
		// width and hovering just above the wall top so the whole lobby can watch one game
		// from across the room. Its bottom edge clears the wall top by ClearAboveWall — the
		// world half-height scales with BoardScale, so that gap holds at any size.
		float halfHeight = PanelPxHeight * BoardScale * PxToWorld * 0.5f;
		float boardZ = WallHeight + ClearAboveWall + halfHeight;

		var board = new GameObject( true, "SpectatorBoard" );
		board.Parent = _root;
		board.LocalPosition = new Vector3( centreX, wallY, boardZ );
		board.LocalRotation = facing;
		board.LocalScale = new Vector3( 1f, 1f, 1f ) * BoardScale;
		// Pin PanelSize to the panel's intrinsic px so the full board (not just its top-left
		// 512 px) renders — the default 512 clipped the lower ranks and the status line.
		var panel = board.AddComponent<WorldPanel>();
		panel.PanelSize = new Vector2( PanelPxWidth, PanelPxHeight );
		board.AddComponent<Gambit.UI.SpectatorBoardPanel>();

		// Walk-up control board at eye height, directly under the floating board: shows the
		// current source and invites "Press E to spectate". It carries the engage station so
		// the visible sign and the walk-up target line up (the player-distance check is 3D,
		// so it must stay near the floor, not up at the floating board).
		var info = new GameObject( true, "SpectatorInfoBoard" );
		info.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		info.Parent = _root;
		info.LocalPosition = new Vector3( centreX, wallY, WallHeight * StationHeightFrac );
		info.LocalRotation = facing;
		info.LocalScale = new Vector3( 1f, 1f, 1f ) * InfoBoardScale;
		var infoPanel = info.AddComponent<WorldPanel>();
		infoPanel.PanelSize = new Vector2( InfoPxWidth, InfoPxHeight );
		info.AddComponent<Gambit.UI.SpectatorInfoPanel>();
		info.AddComponent<SpectatorStation>();
	}

	void Clear()
	{
		if ( _root.IsValid() )
			_root.Destroy();
		_root = null;
	}
}
