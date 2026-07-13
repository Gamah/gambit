using Gambit.World;

// Not "Gambit.Editor" — that would shadow the engine's Editor namespace
// (same trap as Gambit.Game vs Sandbox.Game).
namespace Gambit;

/// <summary>
/// Issue #36: many code changes appeared to require an editor restart. The actual
/// mechanism is that the procedural builders (LobbyRoom, ChessRing, the wall
/// builders) only build in OnEnabled/OnValidate — a hotload patches their code but
/// re-runs neither, so the NotSaved preview geometry in the scene keeps reflecting
/// the old code. This hook re-runs each builder's preview rebuild after every
/// hotload, so geometry/code changes show up immediately like any other hotloaded
/// change.
///
/// Play-mode safety lives in the components themselves: ChessRing.RebuildPreview
/// keeps the _runtimeBuilt guard so the host's networked station build is never
/// clobbered, and all builds are NotSaved so nothing leaks into the scene file.
/// </summary>
public static class HotloadRebuild
{
	[EditorEvent.Hotload]
	public static void OnHotload()
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( !scene.IsValid() )
			return;

		foreach ( var room in scene.GetAllComponents<LobbyRoom>() )
			room.RebuildPreview();

		foreach ( var ring in scene.GetAllComponents<ChessRing>() )
			ring.RebuildPreview();

		foreach ( var wall in scene.GetAllComponents<InfoWall>() )
			wall.RebuildPreview();

		foreach ( var wall in scene.GetAllComponents<SettingsWall>() )
			wall.RebuildPreview();

		foreach ( var floor in scene.GetAllComponents<FloorCheckerboard>() )
			floor.RebuildPreview();

		foreach ( var lights in scene.GetAllComponents<Streetlights>() )
			lights.RebuildPreview();
	}
}
