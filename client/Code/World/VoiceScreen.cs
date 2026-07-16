using Sandbox;
using System.Collections.Generic;
using System.Linq;
using Gambit.Game;
using Gambit.UI.Screens;

namespace Gambit.World;

/// <summary>
/// Client-local keyboard driver for proximity voice (M12), copied from terryball's VoiceScreen and
/// trimmed to Gambit. Self-attached to the scene ScreenPanel by <see cref="LobbyPlayer"/> (never
/// networked). Each frame it pushes the local player's transmit config onto their own
/// <see cref="GambitVoice"/>, applies this client's chosen HEARING RANGE to every avatar's voice, and
/// reads two keys:
///
///   G — toggle the master voice switch (<see cref="VoicePrefs.VoiceEnabled"/>). OFF = neither send
///       nor hear; ON = transmit (subject to the player's own s&amp;box voice mode) + hear un-muted
///       players. (V is the engine's default push-to-talk key, so it can't be the toggle — that's why
///       terryball landed on G, and G is free in Gambit's Input.config too.)
///   B — open/close a keyboard-navigable mute roster (only while roaming, so its ↑/↓/Enter never
///       fight the seated chess move-selector, which is on the arrow keys): ↑/↓ move the selection,
///       Enter/Space toggles mute on the highlighted player, Esc/B closes. No mouse — the cursor stays
///       captured so mouselook is never interrupted.
///
/// Transmit is gated owner-locally by driving <see cref="Voice.Mode"/>: we never touch a networked
/// Enabled flag. When ON we set AlwaysOn + the real "Voice" push-to-talk binding (so a player whose
/// s&amp;box mode is Push-To-Talk still has to hold their key); when OFF we set Manual + IsListening
/// false + an unbound sentinel input so nothing can ever leak through.
///
/// The engine ultimately decides whether the mic actually transmits (<see cref="Voice.IsListening"/>
/// honours the user's s&amp;box voip_mode, which game code can't change) — this just surfaces + gates
/// our side of it.
/// </summary>
public sealed class VoiceScreen : Component
{
	/// <summary>An input action name that is deliberately never bound, so Input.Down( it ) is always
	/// false. Used as the push-to-talk input while voice is OFF to hard-gate transmit closed.</summary>
	public const string NoVoiceInput = "__gambit_novoice__";

	/// <summary>The real push-to-talk binding — the engine's voice action, bound to V in Input.config.</summary>
	public const string VoiceInput = "Voice";

	/// <summary>True while the mute roster is open. The panel reads this to show/hide the roster.</summary>
	public bool IsOpen { get; private set; }

	/// <summary>Index into <see cref="Roster"/> of the highlighted row.</summary>
	public int SelectedIndex { get; private set; }

	/// <summary>Everyone on the server except us, in a stable order — the roster the panel renders and B navigates.</summary>
	public IReadOnlyList<Connection> Roster { get; private set; } = System.Array.Empty<Connection>();

	// True while we've forced the local walker's movement input off for an open roster, so we know to
	// hand it back. Kept separate from IsOpen so a disable/destroy mid-roster still restores control.
	private bool _controlsFrozen;

	// A linear, non-front-loaded falloff, cached so we're not allocating a Curve per voice per frame.
	// The engine default is savagely front-loaded (~4% volume by 20% of range), which reads as broken
	// voice rather than distant voice — terryball hit this and switched to a plain linear curve.
	private static readonly Curve LinearFalloff =
		new Curve( new Curve.Frame( 0f, 1f ), new Curve.Frame( 1f, 0f ) );

	protected override void OnDisabled() { IsOpen = false; RestoreControls(); }
	protected override void OnDestroy() { IsOpen = false; RestoreControls(); }

	protected override void OnUpdate()
	{
		ApplyTransmitConfig();
		ApplyHearingRange();

		// While the roster's open its Enter/Space (mute) and Esc/B (close) keys overlap gameplay: Enter
		// opens the built-in chat overlay, Space jumps. Swallow both so navigating the menu never leaks
		// into the world. Runs every frame the roster's up (and restores on close).
		ApplyRosterInputGate();

		// Don't eat keys while the built-in chat box has focus (its stub gate is always false now, but
		// keep the guard — it's the right shape if the engine overlay ever exposes focus state here).
		if ( ChatPanel.IsOpen )
			return;

		if ( IsOpen )
		{
			ServiceRoster();
			return;
		}

		// G — master voice toggle. Harmless while seated (G is bound to nothing else), so no engage gate.
		if ( Input.Keyboard.Pressed( "G" ) )
			VoicePrefs.VoiceEnabled = !VoicePrefs.VoiceEnabled;

		// B — open the mute roster, but only while roaming: seated, the arrow keys drive the chess move
		// selector and Enter has its own meaning, so a roster there would fight the game.
		if ( Input.Keyboard.Pressed( "B" ) && !LocalEngaged() )
			OpenRoster();
	}

	// Push transmit config onto our own (non-proxy) voice every frame so it survives anything else
	// poking the component. This runs regardless of engage state — you should be able to talk to the
	// opponent across the board while seated.
	private void ApplyTransmitConfig()
	{
		var voice = LocalVoice();
		if ( !voice.IsValid() ) return;

		if ( VoicePrefs.VoiceEnabled )
		{
			voice.Mode = Voice.ActivateMode.AlwaysOn;
			voice.PushToTalkInput = VoiceInput; // real binding: PTT-mode users still hold their key
		}
		else
		{
			voice.Mode = Voice.ActivateMode.Manual;
			voice.IsListening = false;
			voice.PushToTalkInput = NoVoiceInput; // unbound → never transmits
		}
	}

	// Apply this client's chosen hearing range to every avatar's voice. The 3D falloff is computed on
	// the RECEIVER off the (proxy) Voice component here, so setting Distance locally is how "how far I
	// hear" is controlled — the sliders on the world-settings board write PlayerData, we read it. Keyed
	// on OUR OWN engage state: tighter at a table, wider while roaming (both tunable). Falloff/Volume
	// are re-asserted too, cheaply, so a late-joined proxy that missed the spawn values still sounds
	// right (the loop is at most a lobby's worth of avatars).
	private void ApplyHearingRange()
	{
		var data = PlayerData.Current;
		float range = PlayerData.ClampVoiceRange(
			LocalEngaged() ? data.VoiceRangeAtTable : data.VoiceRangeRoaming );

		foreach ( var v in Scene.GetAllComponents<GambitVoice>() )
		{
			if ( !v.IsValid() ) continue;
			v.Distance = range;
			v.Falloff = LinearFalloff;
			v.Volume = 2f; // headroom — a normal speaking voice shouldn't need a shout
		}
	}

	private void OpenRoster()
	{
		RebuildRoster();
		if ( Roster.Count == 0 ) return; // nobody to mute — don't bother opening
		IsOpen = true;
		SelectedIndex = 0;
	}

	private void ServiceRoster()
	{
		// Sitting down closes the roster — its keys belong to the game once you're at a board.
		if ( LocalEngaged() ) { IsOpen = false; return; }

		RebuildRoster();
		if ( Roster.Count == 0 ) { IsOpen = false; return; }

		SelectedIndex = System.Math.Clamp( SelectedIndex, 0, Roster.Count - 1 );

		// Esc or B closes.
		if ( Input.EscapePressed )
		{
			Input.EscapePressed = false;
			IsOpen = false;
			return;
		}
		if ( Input.Keyboard.Pressed( "B" ) )
		{
			IsOpen = false;
			return;
		}

		if ( Input.Keyboard.Pressed( "UP" ) )
			SelectedIndex = (SelectedIndex - 1 + Roster.Count) % Roster.Count;
		if ( Input.Keyboard.Pressed( "DOWN" ) )
			SelectedIndex = (SelectedIndex + 1) % Roster.Count;

		if ( Input.Keyboard.Pressed( "ENTER" ) || Input.Keyboard.Pressed( "SPACE" ) )
		{
			var target = Roster[SelectedIndex];
			if ( target is not null )
				VoicePrefs.ToggleMute( target.SteamId );
		}
	}

	// Everyone but us, ordered by name for stability.
	private void RebuildRoster()
	{
		var local = Connection.Local;
		Roster = Connection.All
			.Where( c => c is not null && c != local )
			.OrderBy( c => c.DisplayName )
			.ToList();
	}

	/// <summary>Our own (non-proxy) voice transmitter, if the avatar's spawned yet.</summary>
	public GambitVoice LocalVoice()
		=> Scene?.GetAllComponents<GambitVoice>().FirstOrDefault( v => v.IsValid() && !v.IsProxy );

	/// <summary>A given connection's voice transmitter (their avatar's), so the roster can show a
	/// speaking dot. Matches the avatar by network owner.</summary>
	public GambitVoice VoiceFor( Connection c )
		=> c is null ? null : Scene?.GetAllComponents<GambitVoice>()
			.FirstOrDefault( v => v.IsValid() && v.GameObject.Network.Owner?.Id == c.Id );

	private static bool LocalEngaged() => LobbyPlayer.Local?.Engaged ?? false;

	// The local walker's movement controller, used to freeze WASD/jump while the roster's open.
	private PlayerController LocalPlayer()
		=> LobbyPlayer.Local?.GameObject.Components.Get<PlayerController>();

	// Keep the open roster from bleeding into the world: clear the chat action (Enter would otherwise
	// open the built-in chat overlay, which reads it in its later UI tick) and freeze the walker's
	// movement input (Space would otherwise jump). UseInputControls only gates movement/jump/duck —
	// look + camera stay live, so mouselook still works while you pick someone to mute. Restored the
	// frame the roster closes.
	private void ApplyRosterInputGate()
	{
		if ( IsOpen )
		{
			// The engine chat action's internal name is lowercase; our config action is "Chat". Clear
			// both so an Enter never opens chat regardless of which one the overlay listens to.
			Input.Clear( "chat" );
			Input.Clear( "Chat" );

			var pc = LocalPlayer();
			if ( pc.IsValid() )
			{
				pc.UseInputControls = false;
				_controlsFrozen = true;
			}
		}
		else
		{
			RestoreControls();
		}
	}

	private void RestoreControls()
	{
		if ( !_controlsFrozen ) return;
		_controlsFrozen = false;

		var pc = LocalPlayer();
		if ( pc.IsValid() )
			pc.UseInputControls = true;
	}
}
