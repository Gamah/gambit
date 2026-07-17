using System.Linq;
using Gambit.World;
using Sandbox;

namespace Gambit.UI;

/// <summary>
/// Drives the Skafinity library's drop-in <see cref="Skafinity.SkafinityMusicPanel"/> from
/// the south-wall music board's engage flow instead of its built-in floating ♪ button.
///
/// The panel's button is an interactive (pointer-events) element; left always-on over the
/// roaming lobby it keeps the mouse cursor released, which kills third-person mouselook
/// ("camera doesn't turn"). So the panel is enabled <em>only</em> while the local player is
/// engaged at the music board (<see cref="SettingsStation"/> Kind = Music) — where the cursor
/// is already freed — and forced open, so engaging shows the board, not the button.
///
/// Lives on the always-enabled UI ScreenPanel GO alongside the panel; the panel itself starts
/// disabled in the scene.
/// </summary>
public sealed class MusicBoardScreen : Component
{
	Skafinity.SkafinityMusicPanel _panel;
	bool _wasEngaged;

	protected override void OnUpdate()
	{
		// The panel starts disabled, so the enabled-only Scene.GetAllComponents won't see it —
		// search self including disabled (it lives on this same LocalMusic GO, built by
		// LocalMusicSystem).
		_panel ??= Components.Get<Skafinity.SkafinityMusicPanel>( FindMode.EverythingInSelf );
		if ( _panel == null ) return;

		// Keep trying to give the panel its player until one exists.
		//
		// The library resolves this ITSELF in its own OnStart — but only once, and via the
		// enabled-only Scene.GetAllComponents. If the SkafinityPlayer isn't up at that exact
		// moment (a construction-order race, even though both now live on the same LocalMusic
		// GO), the panel's Player stays null for the rest of the session and every field
		// renders `Player?.X ?? default`: seed "—", N 0, empty queue, dead buttons — a whole
		// board of nothing. Retrying costs a scene scan on the frames before it succeeds and
		// nothing after — the same shape as the _panel lookup directly above.
		//
		// This used to fail far worse on a JOINING client, and issue #12 is the record of why:
		// the panel and player lived on the scene's /UI and /GameController GameObjects, which
		// were NetworkMode.Snapshot. A joiner rebuilds Snapshot objects from the host's snapshot
		// (see LocalMusicSystem for the mechanism), so the host's live panel state (Enabled/
		// IsOpen) rode the snapshot onto the joiner and the board rendered open + unstyled. Both
		// are now built client-local by LocalMusicSystem, so no host state can reach a joiner.
		//
		// Fixed here rather than in the library: Libraries/gamah.skafinity is
		// source-committed but it is a drop-in, and its one-shot resolve is only wrong
		// for hosts whose panel outlives an absent player. Ours is one of those.
		_panel.Player ??= Scene.GetAllComponents<Skafinity.SkafinityPlayer>().FirstOrDefault();

		bool engaged = SettingsStation.Active?.Music ?? false;
		_panel.Enabled = engaged;

		// Show the board (not the ♪ button) once, the moment we engage. Re-opening every frame
		// would make the panel's own ✕ inert (it closes, we instantly re-open). Instead, treat a
		// close while engaged as leaving the board, mirroring Escape.
		if ( engaged && !_wasEngaged )
		{
			if ( !_panel.IsOpen )
				_panel.Toggle();
		}
		else if ( engaged && !_panel.IsOpen )
		{
			Gambit.World.LobbyPlayer.Local?.Disengage();
		}

		_wasEngaged = engaged;
	}
}
