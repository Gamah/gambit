using System.Linq;
using Gambit.World;
using Sandbox;

namespace Gambit.UI;

/// <summary>
/// Drives the Skafinity library's drop-in <see cref="Skafinity.SkafinityMusicPanel"/> from the
/// KEYBOARD, from anywhere in the lobby — the same shape terryball uses, and the reason the
/// south-wall music board is gone (it was a place you had to walk to for a setting that has
/// nothing to do with where you are standing).
///
///   N — open/close the music board.
///   M — mute/unmute the soundtrack, board open or not.
///
/// The panel's own floating ♪ button is an interactive (pointer-events) element; left always-on
/// over the roaming lobby it holds the cursor released and kills mouselook. So the panel stays
/// <em>disabled</em> while closed (its button never shows, never steals input); opening enables
/// and force-opens it, closing disables it again. Same trick the old MusicBoardScreen played off
/// the engage flow — the trigger is a key now, not a wall.
///
/// Lives on the client-local LocalMusic GameObject built by <see cref="LocalMusicSystem"/>,
/// beside the panel and the <see cref="Skafinity.SkafinityPlayer"/>. The bottom-left keycap
/// hints for both keys are drawn by <see cref="Gambit.UI.Screens.HudHints"/>.
/// </summary>
public sealed class MusicScreen : Component
{
	/// <summary>True while the music board is showing. Read by <see cref="Gambit.UI.Screens.HudHints"/>
	/// (which hides the hints under it) and by the key gate below.</summary>
	public static bool IsOpen { get; private set; }

	/// <summary>The soundtrack's master switch, for the M hint. SkafinityPlayer.Enabled is a
	/// `new` bool distinct from the component's own Enabled; false = muted, matching the
	/// panel's own MUTE button.</summary>
	public static bool Muted { get; private set; }

	Skafinity.SkafinityPlayer _player;
	Skafinity.SkafinityMusicPanel _panel;

	protected override void OnDisabled() => Close();
	protected override void OnDestroy() => Close();

	protected override void OnUpdate()
	{
		// The panel starts disabled, so the enabled-only Scene scan won't see it — find it on
		// our own GO including disabled. The player plays on its own and is found scene-wide.
		_panel ??= Components.Get<Skafinity.SkafinityMusicPanel>( FindMode.EverythingInSelf );
		_player ??= Scene.GetAllComponents<Skafinity.SkafinityPlayer>().FirstOrDefault();
		if ( _panel == null ) return;

		// Keep handing the panel its player until one exists. The library resolves this ITSELF
		// in its own OnStart — but only once, and via the enabled-only Scene.GetAllComponents.
		// If the player isn't up at that exact moment, the panel's Player stays null for the
		// session and every field renders `Player?.X ?? default`: seed "—", N 0, empty queue,
		// dead buttons. Retrying costs a scene scan on the frames before it succeeds and nothing
		// after. (Carried over from MusicBoardScreen, which learned it the hard way.)
		if ( _player != null )
		{
			_panel.Player ??= _player;
			Muted = !_player.Enabled;
		}

		// M mutes from anywhere; N opens the board only while ROAMING. Engaged (a chess seat, a
		// wall board, the spectator wall) already has its own screen up and its own Escape
		// meaning — dropping a second full-screen panel on top of that is how Escape stops being
		// predictable. Muting is safe everywhere because it draws nothing.
		bool roaming = !( LobbyPlayer.Local?.Engaged ?? false );

		if ( _player != null && Input.Keyboard.Pressed( "M" ) )
			_player.Enabled = !_player.Enabled;

		if ( Input.Keyboard.Pressed( "N" ) && ( roaming || IsOpen ) )
		{
			if ( IsOpen ) Close(); else Open();
			return;
		}

		if ( !IsOpen ) return;

		// Engaging at anything while the board is up (walking up to a board is a click-free
		// action — E) closes it rather than leaving two panels fighting for the cursor.
		if ( !roaming )
		{
			Close();
			return;
		}

		// The panel's own ✕ closes itself — mirror that back into our cursor/open state.
		if ( !_panel.IsOpen )
		{
			Close();
			return;
		}

		// Escape also closes it. Safe to consume here: the board only opens while roaming, and
		// LobbyPlayer only reads EscapePressed while Engaged (stand up / SeatAim), so the two
		// can never both want this key on the same frame.
		if ( Input.EscapePressed )
		{
			Input.EscapePressed = false;
			Close();
			return;
		}

		// Keep the cursor freed for the panel's buttons. Visible rather than SeatAim's Auto is
		// deliberate and bounded to exactly this window: Visible zeroes Input.AnalogLook, so the
		// player doesn't spin the camera while clicking around the board — and Close() below
		// always puts Auto back, including from OnDisabled/OnDestroy.
		Mouse.Visibility = MouseVisibility.Visible;
	}

	void Open()
	{
		IsOpen = true;
		_panel.Enabled = true;
		if ( !_panel.IsOpen ) _panel.Toggle();
		Mouse.Visibility = MouseVisibility.Visible;
	}

	void Close()
	{
		IsOpen = false;
		if ( _panel != null )
		{
			_panel.Enabled = false;               // hide it (and its ♪ button) so it stops stealing pointer input
			if ( _panel.IsOpen ) _panel.Toggle(); // normalise back to closed for the next open
		}
		Mouse.Visibility = MouseVisibility.Auto;  // hand the cursor back to look control
	}
}
