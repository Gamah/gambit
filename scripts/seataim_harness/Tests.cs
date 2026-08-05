using System;
using Sandbox;
using Gambit.World;
using Gambit.Game;

static class T
{
	static int fails;

	static void Check( bool ok, string what )
	{
		Console.WriteLine( ( ok ? "  ok   " : "  FAIL " ) + what );
		if ( !ok ) fails++;
	}

	/// <summary>One frame of the seated update, with the mouse delta the player made.</summary>
	static void Frame( bool playing, bool modal, float pitch = 0, float yaw = 0 )
	{
		Input.RawLook = new Angles( pitch, yaw, 0 );
		SeatAim.Update( playing, modal );
		Input.RawLook = default;
	}

	static void Reset( bool enabled )
	{
		SeatAim.Clear();
		PlayerData.Current.LookAimAtBoard = enabled;
	}

	static int Main()
	{
		Console.WriteLine( "SeatAim — cursor vs look aim (P99)" );

		// ── The setting is off: nothing ever takes the mouse. ──
		Reset( false );
		Frame( playing: true, modal: false );
		Check( !SeatAim.Aiming, "setting off: a live game does NOT hide the cursor" );
		Check( Mouse.Visibility == MouseVisibility.Auto, "setting off: cursor left on Auto" );

		// ── The setting is on: cursor until the game is PLAYING. ──
		Reset( true );
		Frame( playing: false, modal: false );
		Check( !SeatAim.Aiming, "idle seat: cursor stays active (setup panel is clickable)" );
		Check( Mouse.Visibility == MouseVisibility.Auto, "idle seat: cursor visible" );

		Frame( playing: true, modal: false );
		Check( SeatAim.Aiming, "game starts: look aim engages" );
		Check( Mouse.Visibility == MouseVisibility.Hidden, "aiming: pointer is hidden" );
		Check( SeatAim.PickPixel().x == 960 && SeatAim.PickPixel().y == 540,
			"aiming: the pick is centre-screen" );

		// ── Escape hands the cursor back, and it STICKS. ──
		SeatAim.Suspend();
		Check( !SeatAim.Aiming && Mouse.Visibility == MouseVisibility.Auto,
			"Escape: cursor comes straight back" );
		Frame( playing: true, modal: false );
		Check( !SeatAim.Aiming, "suspend survives the next frame of a live game" );
		Check( SeatAim.PickPixel().x == 640, "suspended: the pick follows the pointer again" );

		SeatAim.Toggle();
		Frame( playing: true, modal: false );
		Check( SeatAim.Aiming, "Escape again: back into aim" );

		SeatAim.Toggle();
		Frame( playing: true, modal: false );
		Check( !SeatAim.Aiming, "Escape cycles the other way too" );
		Check( SeatAim.Suspended, "cursor by choice mid-game reads as Suspended (the HUD's line)" );

		// ── Toggleable is what LobbyPlayer keys Escape on: while it is true Escape cycles
		//    the cursor, and everywhere else it is the plain stand-up it has always been.
		//    Getting this wrong in either direction traps the player in their seat or takes
		//    the cursor toggle away, so it is asserted rather than read. ──
		Check( SeatAim.Toggleable, "a live game with the setting on: Escape is the cursor toggle" );
		Frame( playing: true, modal: true );
		Check( !SeatAim.Toggleable, "a modal owns the mouse: Escape goes back to standing up" );
		Frame( playing: false, modal: false );
		Check( !SeatAim.Toggleable, "no live game: Escape is the plain stand-up" );
		Reset( false );
		Frame( playing: true, modal: false );
		Check( !SeatAim.Toggleable && !SeatAim.Suspended,
			"setting off: Escape is never the cursor toggle" );
		Reset( true );
		Frame( playing: true, modal: false );
		SeatAim.Clear();
		Check( !SeatAim.Toggleable, "standing up: Escape is plain old Escape again" );

		Reset( true );
		Frame( playing: true, modal: false );

		// ── A game ending forgets the suspend, so the NEXT game starts in aim. ──
		Frame( playing: false, modal: false );
		Check( !SeatAim.Aiming, "game over: cursor" );
		Frame( playing: true, modal: false );
		Check( SeatAim.Aiming, "next game: aim engages again — the suspend did not persist" );

		// ── A modal releases the cursor by itself and gives aim back after. ──
		Frame( playing: true, modal: true );
		Check( !SeatAim.Aiming && Mouse.Visibility == MouseVisibility.Auto,
			"promotion picker: cursor released automatically" );
		Frame( playing: true, modal: false );
		Check( SeatAim.Aiming, "picker answered: aim resumes on its own" );

		// ...but a modal must NOT resurrect an aim the player switched off themselves.
		SeatAim.Suspend();
		Frame( playing: true, modal: true );
		Frame( playing: true, modal: false );
		Check( !SeatAim.Aiming, "a modal opening and closing does not undo the player's Escape" );

		// ── The look offset. ──
		Reset( true );
		Frame( playing: true, modal: false );          // engage
		Frame( playing: true, modal: false, pitch: 5, yaw: -7 );
		Check( Math.Abs( SeatAim.LookOffset.pitch - 5 ) < 0.001f
			&& Math.Abs( SeatAim.LookOffset.yaw + 7 ) < 0.001f, "look accumulates while aiming" );

		for ( int i = 0; i < 100; i++ )
			Frame( playing: true, modal: false, pitch: 10, yaw: 10 );
		Check( SeatAim.LookOffset.pitch <= SeatAim.MaxPitch + 0.001f
			&& SeatAim.LookOffset.yaw <= SeatAim.MaxYaw + 0.001f,
			"look is clamped: you cannot turn away from your own board" );
		Check( SeatAim.LookOffset.roll == 0f, "no roll is ever accumulated" );

		var held = SeatAim.LookOffset;
		SeatAim.Suspend();
		Frame( playing: true, modal: false, pitch: 20, yaw: 20 );
		Check( SeatAim.LookOffset.pitch == held.pitch && SeatAim.LookOffset.yaw == held.yaw,
			"cursor mode: mouse movement does NOT swing the view, and the view is kept where it was" );

		SeatAim.Clear();
		Check( SeatAim.LookOffset.pitch == 0 && SeatAim.LookOffset.yaw == 0,
			"standing up re-centres the seat view" );

		// ── Losing AVAILABILITY re-centres; being suspended or modal does not. ──
		//
		// The bug this covers: the offset used to survive everything but standing up, so
		// switching MOVE MODE to CURSOR — or simply finishing the game — left the view turned up
		// to 45° off the board with NOTHING left that could turn it back (Escape is a plain
		// stand-up again, the mouse only moves a pointer). Part of your own board sat off screen
		// until you stood up. Each way out is asserted separately because the difference between
		// them IS the rule.
		Reset( true );
		Frame( playing: true, modal: false );
		Frame( playing: true, modal: false, pitch: 9, yaw: 12 );
		Check( SeatAim.TakeRecentred() == false, "aiming normally never asks for a re-centre" );

		// A MODAL is a loan of the cursor, not the end of aiming: the view must be exactly where
		// the player left it when the picker closes.
		Frame( playing: true, modal: true );
		Check( !SeatAim.TakeRecentred() && SeatAim.LookOffset.yaw == 12,
			"a modal does NOT re-centre the view — it hands aim back untouched" );
		Frame( playing: true, modal: false );

		// Escape, likewise: aim is one keypress away, so the offset is still the player's to move.
		SeatAim.Suspend();
		Frame( playing: true, modal: false );
		Check( !SeatAim.TakeRecentred() && SeatAim.LookOffset.yaw == 12,
			"Escape does NOT re-centre the view — the suspend keeps it" );
		SeatAim.Resume();
		Frame( playing: true, modal: false );

		// The game ending: aim is gone, so the view comes back to the board.
		Frame( playing: false, modal: false );
		Check( SeatAim.LookOffset.pitch == 0 && SeatAim.LookOffset.yaw == 0,
			"the game ending re-centres the seat view" );
		Check( SeatAim.TakeRecentred(), "the game ending asks the camera to EASE back" );
		Check( !SeatAim.TakeRecentred(), "the re-centre request is one-shot" );

		// Switching MOVE MODE to CURSOR mid-game: the same, and the one the owner hit.
		Reset( true );
		Frame( playing: true, modal: false );
		Frame( playing: true, modal: false, pitch: 9, yaw: 12 );
		PlayerData.Current.LookAimAtBoard = false;      // the picker, mid-game
		Frame( playing: true, modal: false );
		Check( SeatAim.LookOffset.pitch == 0 && SeatAim.LookOffset.yaw == 0,
			"switching MOVE MODE to CURSOR re-centres the seat view" );
		Check( SeatAim.TakeRecentred(), "switching to CURSOR asks the camera to EASE back" );

		// And it must not fire again every frame afterwards — that would pin the camera in a
		// permanent re-blend and CameraSettled would never come back true.
		Frame( playing: true, modal: false );
		Check( !SeatAim.TakeRecentred(), "CURSOR mode does not keep re-requesting a re-centre" );
		Check( Mouse.Visibility == MouseVisibility.Auto,
			"standing up ALWAYS hands the mouse back (a stuck cursor is unrecoverable)" );

		// ── The engine's own gate: AnalogLook is dead while a cursor is visible, so a
		//    frame that releases the mouse cannot also swing the view. ──
		Reset( true );
		Frame( playing: true, modal: false );
		Input.RawLook = new Angles( 9, 9, 0 );
		SeatAim.Suspend();                       // cursor back mid-frame
		var after = SeatAim.LookOffset;
		SeatAim.Update( true, false );
		Check( SeatAim.LookOffset.pitch == after.pitch,
			"releasing the cursor does not smear the view on the way out" );

		Console.WriteLine( fails == 0 ? "\nALL PASS" : $"\n{fails} FAILED" );
		return fails == 0 ? 0 : 1;
	}
}
