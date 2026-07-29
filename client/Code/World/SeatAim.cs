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
/// <see cref="AimToggleButton"/> (the banner's label).</para>
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
/// <item><see cref="Suspend"/> — the player asked (Escape, or the plate over the clock).
/// It sticks until they ask for aim back, and it is the only one that survives a modal
/// closing.</item>
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

	/// <summary>The player asked for the cursor back mid-game (Escape, or the plate over the
	/// clock). Cleared when they ask for aim back, and when the game stops being live.</summary>
	static bool _suspended;

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

		bool aiming = Enabled && playing && !_suspended && !modalOpen;

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

	/// <summary>Give the cursor back on the player's own say-so (Escape, or the plate over
	/// the clock). Sticks until <see cref="Resume"/> or the game ending.</summary>
	public static void Suspend()
	{
		_suspended = true;
		Aiming = false;
		ApplyCursor( false );
	}

	/// <summary>Back into aim on the player's say-so (the plate over the clock). A no-op
	/// unless the setting is on — the plate isn't shown otherwise, but nothing here relies
	/// on that.</summary>
	public static void Resume() => _suspended = false;

	/// <summary>The plate over the clock: one control, both directions.</summary>
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
	/// centre of the screen in aim mode.
	///
	/// <para>One function so the board view and the banner cannot disagree about what
	/// the player is pointing at — a button that could only be clicked with a cursor would be
	/// unreachable in precisely the mode it exists to escape.</para></summary>
	public static Vector2 PickPixel() =>
		Aiming ? new Vector2( Screen.Width, Screen.Height ) * 0.5f : Mouse.Position;

	/// <summary>
	/// Does a ray hit the aim banner? A rectangle on a plane TILTED about the station's X
	/// axis — the clock's plane — in station-local space.
	///
	/// <para><b>It is here, and it is scalar, so it can be RUN.</b> The corner plate this
	/// replaced lay in the tabletop surface, so it could borrow
	/// <see cref="ChessBoardView.SquareUnderCursor"/>'s arithmetic and inherit its
	/// correctness. A tilted plane cannot, and this host cannot render — so rather than a
	/// second geometry nobody can check, the whole test is plain floats with no engine type
	/// in it and <c>scripts/seataim_harness</c> runs it.</para>
	///
	/// <para>The plane's basis, derived once: the banner's normal is
	/// <c>(0, cos t, −sin t)</c>, its LENGTH axis is the station's X, and its UP-the-plane
	/// axis is the cross of those, <c>(0, sin t, cos t)</c>. At <c>t = 0</c> that degenerates
	/// to an upright plate facing +Y, which is the sanity check to hold it to.</para>
	/// </summary>
	/// <param name="tiltDegrees">The plate's pitch — <c>ChessRing.ClockFaceTilt</c>, negative
	/// for a face angled UP and toward the board.</param>
	/// <param name="centerY">Plate centre, station-local. X is always 0 (dead centre).</param>
	/// <param name="faceOutset">How far the FRONT face stands off that centre along the
	/// normal — half the plate's thickness. Pick the face, not the mid-plane.</param>
	public static bool PlateHit(
		float originX, float originY, float originZ,
		float dirX, float dirY, float dirZ,
		float centerY, float centerZ, float faceOutset, float tiltDegrees,
		float halfLength, float halfHeight )
	{
		float t = tiltDegrees * ( MathF.PI / 180f );
		float ct = MathF.Cos( t ), st = MathF.Sin( t );

		// Plane normal, and the plate's own up-the-plane axis (normal × length axis).
		float nY = ct, nZ = -st;
		float uY = st, uZ = ct;

		float denom = dirY * nY + dirZ * nZ;
		if ( MathF.Abs( denom ) < 0.0001f ) return false;   // ray parallel to the face

		// The face sits faceOutset along the normal from the centre.
		float cY = centerY + nY * faceOutset;
		float cZ = centerZ + nZ * faceOutset;

		float hit = ( ( cY - originY ) * nY + ( cZ - originZ ) * nZ ) / denom;
		if ( hit <= 0f ) return false;                      // behind the camera

		// Where it lands, relative to the plate centre, in the plate's own two axes.
		float pX = originX + dirX * hit;
		float pY = originY + dirY * hit - cY;
		float pZ = originZ + dirZ * hit - cZ;

		return MathF.Abs( pX ) <= halfLength
			&& MathF.Abs( pY * uY + pZ * uZ ) <= halfHeight;
	}
}
