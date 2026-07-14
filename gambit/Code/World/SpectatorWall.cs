using Sandbox;

namespace Gambit.World;

/// <summary>
/// Hangs the giant spectator board over the lobby's west wall (PLAN.md M5 —
/// SpectatorBoard rewrite; mirrors rotaliate-client's floating SpectatorBoard) and
/// fronts it with a SpectatorStation engage target ("Press E to spectate") on the wall
/// at walk-up height that opens the interactive channel picker. The board is a
/// display-only WorldPanel (SpectatorBoardPanel) driven by a single
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

	/// <summary>WorldPanel px→world-unit factor (Sandbox ScreenToWorldScale) — a panel of P
	/// px renders P × scale × this many world units wide.</summary>
	const float PxToWorld = 0.05f;

	/// <summary>World units the board plane sits in front of the wall's inner face
	/// (RoomSize / 2), toward the room.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	/// <summary>Uniform scale multiplier on the board WorldPanel — it floats high above the
	/// wall and must read from across the whole room, so it's large.</summary>
	[Property] public float BoardScale { get; set; } = 9f;

	/// <summary>Gap between the wall top and the bottom edge of the floating board. The
	/// board's clearance tracks the scale automatically (centre = wall-top + this +
	/// half the board's world height).</summary>
	[Property] public float ClearAboveWall { get; set; } = 18f;

	/// <summary>Board centre along the wall, as a fraction of wall width (0 = centred).</summary>
	[Property] public float BoardYFrac { get; set; } = 0f;

	/// <summary>Height up the wall of the "Press E to spectate" engage target, as a fraction
	/// of wall height — kept at walk-up height (the board itself floats out of reach).</summary>
	[Property] public float StationHeightFrac { get; set; } = 0.35f;

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

		// West wall runs along Y; the panel faces +X, back into the room.
		var facing = Rotation.FromYaw( 0f );
		float wallX = -( WallWidth * 0.5f - WallInset );
		float centreY = BoardYFrac * WallWidth;

		_root = new GameObject( true, "SpectatorWall" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;

		// The one controller that feeds the board — lives on the wall root so both the
		// display panel and the engaged screen find it via SpectatorController.Instance.
		_root.AddComponent<Gambit.Game.SpectatorController>();

		// Floating display board: panel plane is local Y (width) / Z (height), centred on
		// the wall width and hovering just above the wall top so the whole lobby can watch
		// one game from across the room. Its bottom edge clears the wall top by
		// ClearAboveWall — the world half-height scales with BoardScale, so that gap holds
		// at any size.
		float halfHeight = PanelPxHeight * BoardScale * PxToWorld * 0.5f;
		float boardZ = WallHeight + ClearAboveWall + halfHeight;

		var board = new GameObject( true, "SpectatorBoard" );
		board.Parent = _root;
		board.LocalPosition = new Vector3( wallX, centreY, boardZ );
		board.LocalRotation = facing;
		board.LocalScale = new Vector3( 1f, 1f, 1f ) * BoardScale;
		// Pin PanelSize to the panel's intrinsic px so the full board (not just its top-left
		// 512 px) renders — the default 512 clipped the lower ranks and the status line.
		var panel = board.AddComponent<WorldPanel>();
		panel.PanelSize = new Vector2( PanelPxWidth, PanelPxHeight );
		board.AddComponent<Gambit.UI.SpectatorBoardPanel>();

		// Engage target on the wall at walk-up height (the board floats out of reach):
		// "Press E to spectate" opens the channel picker. The player-distance check is 3D,
		// so this must stay near the floor, not up at the floating board.
		var station = new GameObject( true, "SpectatorStation" );
		station.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		station.Parent = _root;
		station.LocalPosition = new Vector3( wallX, centreY, WallHeight * StationHeightFrac );
		station.LocalRotation = facing;
		station.AddComponent<SpectatorStation>();
	}

	void Clear()
	{
		if ( _root.IsValid() )
			_root.Destroy();
		_root = null;
	}
}
