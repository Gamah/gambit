using System;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Cursor vs LOOK aim at the board a player is seated at (P99) — the whole state machine,
/// in one client-local place.
///
/// <para><b>What it decides.</b> Two ways to pick a square: the mouse CURSOR (the pre-P99
/// behaviour — the seat camera is locked and the pointer picks whatever it is over), or LOOK
/// aim (the cursor is hidden, the mouse turns the seated view, and the square under the
/// CENTRE of the screen is the one you pick). Which one is live is <see cref="Aiming"/>, and
/// exactly three things read it: <see cref="ChessBoardView"/> (which ray to pick with),
/// <see cref="LobbyPlayer"/> (the camera offset and the Escape key), and
/// <see cref="Gambit.UI.GameHud"/> (the crosshair, and which way Escape goes next).</para>
///
/// <para><b>The cursor is the default, and look aim is the exception.</b> Even with the
/// setting on, the cursor stays active until a game is actually PLAYING: an empty seat, a
/// table mid-setup and a finished game are all places a player needs to click a panel, and
/// taking the pointer away there would break the setup panel, the resign button and the
/// rematch prompt for the sake of a board nobody is playing on. So aim engages when the
/// game starts and leaves when it ends.</para>
///
/// <para><b>Three ways back to the cursor mid-game, and they are not the same.</b>
/// <list type="bullet">
/// <item><see cref="Suspend"/> — the player asked, by pressing Escape. It sticks until they
/// press Escape again, and it is the only one that survives a modal closing.</item>
/// <item>A MODAL is up (<see cref="ModalOpen"/>) — the promotion picker, which appears
/// mid-game with no warning and would otherwise be unanswerable. The cursor is released
/// automatically and aim resumes the moment it is answered, WITHOUT clearing a suspend the
/// player asked for themselves.</item>
/// <item>The game stopped being live — which also clears the suspend, so the next game at
/// this table starts in aim again rather than remembering that you once pressed Escape.</item>
/// </list></para>
///
/// <para><b>Static because it is strictly client-local</b>, like <see cref="Gambit.Game.VoicePrefs"/>:
/// nothing here is networked, nothing is per-station (you can only be seated at one table), and
/// the local player is the only one who has a cursor at all. <see cref="Clear"/> on standing up
/// is what keeps it from leaking into roaming — see the note on <see cref="ApplyCursor"/>.</para>
/// </summary>
public static class SeatAim
{
	/// <summary>Look aim is driving right now: the cursor is hidden and the pick is
	/// centre-screen. False means the cursor is live, which is every other case.</summary>
	public static bool Aiming { get; private set; }

	/// <summary>The player pressed Escape for the cursor mid-game. Cleared when they press it
	/// again, and when the game stops being live.</summary>
	static bool _suspended;

	/// <summary>The player has the cursor back BY CHOICE, with aim still available — the half
	/// of the cycle <see cref="Aiming"/> cannot describe, because "not aiming" is also every
	/// table where the feature is off. <see cref="Gambit.UI.GameHud"/> reads it to say which
	/// way Escape goes next; nothing decides anything on it.</summary>
	public static bool Suspended => _suspended && Toggleable;

	/// <summary>Escape means "switch the cursor on/off" right now, rather than "stand up":
	/// the setting is on, a game is live, and no modal has already taken the mouse. Set
	/// per-frame by <see cref="Update"/> and cleared by <see cref="Clear"/>, so a player who
	/// is not seated at a live game always has plain old Escape.
	///
	/// <para><b>The consequence is deliberate and worth stating.</b> While this is true,
	/// Escape does NOT stand you up — the HUD's Leave button does, and one Escape puts a
	/// cursor on the screen to click it with. Escape is the only key that works with the
	/// pointer hidden, so it belongs to the mode it can be used in; leaving is a thing you do
	/// with a cursor anyway.</para></summary>
	public static bool Toggleable { get; private set; }

	/// <summary>A modal that needs the pointer is up — set per-frame by
	/// <see cref="LobbyPlayer"/> from the board view's pending promotion. Deliberately NOT
	/// the same flag as <see cref="_suspended"/>: this one restores aim by itself.</summary>
	public static bool ModalOpen { get; private set; }

	/// <summary>Accumulated look offset from the seat anchor's own aim, in degrees.
	/// Applied by <see cref="LobbyPlayer.UpdateLockedCamera"/> on top of the anchor
	/// rotation, so the anchor stays the single source of where a seat looks.
	///
	/// <para>It PERSISTS when the cursor comes back, on purpose: releasing the cursor while
	/// looking at the corner of your own board should hand you a pointer, not snap the view
	/// back to centre. It is cleared only by <see cref="Clear"/> — standing up.</para></summary>
	public static Angles LookOffset { get; private set; }

	/// <summary>How far look aim may turn off the seat's own aim, in degrees. The board is
	/// the whole point of the view, so this is a nudge rather than free look: enough to put
	/// any corner of the board under the centre of the screen (the anchor already looks down
	/// the board at it), not enough to end up facing the room with a game running.</summary>
	public const float MaxYaw = 45f;
	public const float MaxPitch = 30f;

	/// <summary>Whether the local player has ASKED for look aim at all (the world-settings
	/// board). Read live so the picker takes effect the next frame, exactly as PLAY MODE does.</summary>
	public static bool Enabled =>
		Gambit.Game.PlayerData.Load()?.LookAimAtBoard ?? false;

	/// <summary>Per-frame, from <see cref="LobbyPlayer"/> while seated at a chess station.
	/// <paramref name="playing"/> is the <see cref="Gambit.Game.IBoardGame"/> seam's Playing —
	/// never a controller's own, which is stale by construction during a lichess game.</summary>
	public static void Update( bool playing, bool modalOpen )
	{
		ModalOpen = modalOpen;

		// A game that isn't live hands the cursor back AND forgets that the player once
		// asked for it: the next game starts in aim, which is what the setting means.
		if ( !playing )
			_suspended = false;

		// Whether Escape is the cursor toggle this frame. A modal is excluded on purpose: it
		// has already taken the mouse and gives it back by itself, so there is nothing for a
		// toggle to do — and pressing Escape at a picker must not leave a suspend behind that
		// the player never asked for.
		Toggleable = Enabled && playing && !modalOpen;

		bool aiming = Toggleable && !_suspended;

		if ( aiming )
		{
			// AnalogLook is already zero while a cursor is visible (the engine zeroes it in
			// ComputeAnalogLook), so this can only accumulate in the frames we own the mouse —
			// there is no window where a click that frees the cursor also swings the view.
			var look = LookOffset + Input.AnalogLook;
			LookOffset = new Angles(
				Math.Clamp( look.pitch, -MaxPitch, MaxPitch ),
				Math.Clamp( look.yaw, -MaxYaw, MaxYaw ),
				0f );
		}

		Aiming = aiming;
		ApplyCursor( aiming );
	}

	/// <summary>Give the cursor back on the player's own say-so (Escape). Sticks until
	/// <see cref="Resume"/> or the game ending.</summary>
	public static void Suspend()
	{
		_suspended = true;
		Aiming = false;
		ApplyCursor( false );
	}

	/// <summary>Back into aim on the player's say-so (Escape again). A no-op unless the
	/// setting is on, which is what <see cref="Toggleable"/> already gates.</summary>
	public static void Resume() => _suspended = false;

	/// <summary>Escape, while <see cref="Toggleable"/>: one key, both directions. There is no
	/// second control — a world-space button was built for this and thrown away, because a
	/// plate on the table is one more thing to find, aim at and click in the mode whose whole
	/// point is that you are not using a pointer.</summary>
	public static void Toggle()
	{
		if ( Aiming ) Suspend();
		else Resume();
	}

	/// <summary>Standing up (or a seat/mode change that ends seated play). Hands the cursor
	/// back, forgets the suspend, and re-centres the view on the seat anchor.</summary>
	public static void Clear()
	{
		_suspended = false;
		ModalOpen = false;
		Toggleable = false;    // Escape is plain old Escape again the moment you are up
		Aiming = false;
		LookOffset = default;
		ApplyCursor( false );
	}

	/// <summary>Whether the mouse is ours right now, so we only write the global on a
	/// transition rather than fighting whatever else may set it every frame.</summary>
	static bool _hidden;

	/// <summary>
	/// The one mechanism behind all of this, and it is the engine's own: <c>MouseVisibility.Hidden</c>
	/// locks the pointer to the game — no cursor, and <c>Input.AnalogLook</c> starts reporting mouse
	/// movement (the engine zeroes AnalogLook whenever a cursor is visible). So hiding the cursor and
	/// getting a look axis are the SAME switch, and there is no second path to keep in step.
	///
	/// <para><b>Never set Visible — set Auto.</b> Auto is the engine default and already shows a
	/// cursor while clickable UI is on screen, which is exactly what a seated player has (the HUD);
	/// it is also what a ROAMING player must have, and this is a global. Writing Visible here would
	/// leave a cursor stuck on the screen the moment a code path forgot to reset it, and roaming
	/// mouselook would die with it — the same failure the repo already documents for a free-floating
	/// interactive panel.</para></summary>
	static void ApplyCursor( bool hide )
	{
		if ( hide == _hidden ) return;
		_hidden = hide;
		Mouse.Visibility = hide ? MouseVisibility.Hidden : MouseVisibility.Auto;
	}

	/// <summary>Where a board pick comes from this frame: the mouse in cursor mode, the
	/// centre of the screen — where <see cref="Gambit.UI.GameHud"/> draws the crosshair — in
	/// aim mode. One function, so nothing that picks can disagree with what is drawn.</summary>
	public static Vector2 PickPixel() =>
		Aiming ? new Vector2( Screen.Width, Screen.Height ) * 0.5f : Mouse.Position;
}
