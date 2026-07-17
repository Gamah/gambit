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
		// search self including disabled (it lives on this same UI ScreenPanel GO).
		_panel ??= Components.Get<Skafinity.SkafinityMusicPanel>( FindMode.EverythingInSelf );
		if ( _panel == null ) return;

		// Keep trying to give the panel its player until one exists.
		//
		// The library resolves this ITSELF — but only once, in its OnStart, via the
		// enabled-only Scene.GetAllComponents. If the SkafinityPlayer isn't up at that
		// exact moment, the panel's Player stays null for the rest of the session, and
		// every field in it renders `Player?.X ?? default`: seed "—", N 0, empty queue,
		// dead buttons. A whole board of nothing, which is what a JOINED instance was
		// showing while the host's was fine.
		//
		// The panel (on /UI) and the player (on /GameController) are separate GameObjects,
		// so their OnStart order isn't guaranteed even on the host: if the panel's one-shot
		// resolve runs first, its Player stays null forever. Retrying costs a scene scan on
		// the frames before it succeeds and nothing after — the same shape as the _panel
		// lookup directly above, which retries for exactly this kind of reason.
		//
		// This USED TO fail worse on a joining client, when /UI and /GameController were
		// NetworkMode 2 (Snapshot): the joiner rebuilt them from the host's snapshot rather
		// than constructing them locally, so a different construction order gave the one-shot
		// resolve a different answer — and the host's live panel state (Enabled/IsOpen) rode
		// the snapshot too, rendering the board open + unstyled (issue #12). Both GOs are now
		// NetworkMode.Never so each client builds its own UI/audio locally; this retry stays
		// as the cross-GameObject-ordering guard it always also was.
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
