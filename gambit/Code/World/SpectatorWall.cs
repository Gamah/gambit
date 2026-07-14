using Sandbox;

namespace Gambit.World;

/// <summary>
/// Hangs the giant spectator board on the lobby's west wall (PLAN.md M5 —
/// SpectatorBoard rewrite) and fronts it with a SpectatorStation engage target
/// ("Press E to spectate") that opens the interactive channel picker. The board is a
/// display-only WorldPanel (SpectatorBoardPanel) driven by a single
/// <see cref="Gambit.Game.SpectatorController"/> living on this wall — it mirrors a live
/// sbox game, streams lichess TV (polled), or watches a game by id.
///
/// Same editor-preview + wall-dimension-watch pattern as InfoWall/SettingsWall:
/// OnEnabled/OnValidate rebuild NotSaved GOs; OnUpdate rebuilds on a room resize.
/// Client-side only — nothing is networked (each client reads its own synced copy of
/// the relayed FEN, or polls lichess itself).
/// </summary>
public sealed class SpectatorWall : Component, Component.ExecuteInEditor
{
	/// <summary>World units between the wall's inner face (RoomSize / 2) and the panel plane.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	/// <summary>Scale multiplier on the board WorldPanel — it's meant to read across the
	/// whole room, so it's large.</summary>
	[Property] public float BoardScale { get; set; } = 3.4f;

	/// <summary>Board center along the wall, as a fraction of wall width.</summary>
	[Property] public float BoardYFrac { get; set; } = 0f;

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

		_root = new GameObject( true, "SpectatorWall" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;

		// The one controller that feeds the board — lives on the wall root so both the
		// display panel and the engaged screen find it via SpectatorController.Instance.
		_root.AddComponent<Gambit.Game.SpectatorController>();

		// Display board — panel plane is local Y (width) / Z (height). Roughly square so
		// the 8×8 grid reads true; centred at mid-wall height.
		var board = new GameObject( true, "SpectatorBoard" );
		board.Parent = _root;
		board.LocalPosition = new Vector3( wallX, BoardYFrac * WallWidth, WallHeight * 0.55f );
		board.LocalRotation = facing;
		board.LocalScale = new Vector3( 1f, 1f, 1f ) * BoardScale;
		board.AddComponent<WorldPanel>();
		board.AddComponent<Gambit.UI.SpectatorBoardPanel>();

		// Engage target at the board foot: "Press E to spectate" opens the channel picker.
		var station = new GameObject( true, "SpectatorStation" );
		station.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		station.Parent = _root;
		station.LocalPosition = new Vector3( wallX, BoardYFrac * WallWidth, WallHeight * 0.5f );
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
