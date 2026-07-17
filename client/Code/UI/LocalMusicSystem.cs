using System.Linq;
using Sandbox;

namespace Gambit.UI;

/// <summary>
/// Builds the one client-local music HUD — the Skafinity player, its drop-in board
/// (<see cref="Skafinity.SkafinityMusicPanel"/>), and the south-wall engage driver
/// (<see cref="MusicBoardScreen"/>) — on a runtime <see cref="NetworkMode"/>.Never
/// GameObject, exactly once per client. Its own <see cref="ScreenPanel"/> keeps it
/// isolated from the scene UI ScreenPanel.
///
/// Why a system and not scene components (issue #12): a joining client does NOT load the
/// scene from disk. It DESTROYS its scene and rebuilds from the host's network snapshot —
/// which REBUILDS every NetworkMode.Snapshot object from the host's LIVE state and EXCLUDES
/// every NetworkMode.Never object (verified in engine: GameObject.Serialize.ShouldSave drops
/// Never under SceneForNetwork; SceneNetworkSystem.OnLoadSceneMsg destroys the scene then
/// applies the snapshot). Authoring the music UI in the scene therefore had no good mode:
/// on Snapshot (the old topology) the host's live panel Enabled/IsOpen rode the snapshot
/// onto the joiner and the board rendered open + unstyled; on Never the object never reached
/// the joiner at all (nothing happened). A GameObjectSystem is instantiated locally for every
/// scene on every machine, independent of the snapshot, so it can spawn a strictly-local
/// music HUD that each client constructs, styles and opens for itself. Mirrors terryball's
/// LocalHudSystem / LocalHud.
///
/// The player is client-local audio and the panel is per-client presentation — neither is
/// game state, so nothing built here is ever networked.
/// </summary>
public sealed class LocalMusicSystem : GameObjectSystem
{
	bool _built;

	public LocalMusicSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Ensure, nameof( Ensure ) );
	}

	void Ensure()
	{
		if ( _built ) return;
		// Play scenes only (not the editor's edit scene), and never a headless server: it has
		// no audio device (the player's SoundStream would spam) and no screen to draw on. This
		// is why the old dedicated-server disable in LobbyNetworkManager is gone — the player is
		// simply never built there now.
		if ( Scene is null || Scene.IsEditor || Application.IsDedicatedServer ) return;

		_built = true;

		// Idempotent against a hotload / re-entry that already built one.
		if ( Scene.GetAllComponents<MusicBoardScreen>().Any() ) return;

		var go = new GameObject( true, "LocalMusic" ) { Flags = GameObjectFlags.NotSaved };
		go.NetworkMode = NetworkMode.Never; // strictly client-local — never replicated

		// Its own screen panel, isolated from the scene UI ScreenPanel (which is Snapshot and so
		// leaks host state to joiners — the whole reason this HUD is off the scene).
		go.Components.Create<ScreenPanel>();

		// Client-local soundtrack. MixerName "Music" is load-bearing: the engine only scales a
		// mixer of that name by the music-volume slider, so without it the slider does nothing.
		var player = go.Components.Create<Skafinity.SkafinityPlayer>();
		player.MixerName = "Music";

		// The drop-in board starts DISABLED — its floating ♪ button is a pointer-events element
		// that, left on over the roaming lobby, holds the cursor released and kills mouselook.
		// MusicBoardScreen enables + force-opens it only while engaged at the south-wall board.
		go.Components.Create<Skafinity.SkafinityMusicPanel>( false );
		go.Components.Create<MusicBoardScreen>();
	}
}
