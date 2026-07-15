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
