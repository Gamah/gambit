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

		// ── Escape (and the plate) hand the cursor back, and it STICKS. ──
		SeatAim.Suspend();
		Check( !SeatAim.Aiming && Mouse.Visibility == MouseVisibility.Auto,
			"Escape: cursor comes straight back" );
		Frame( playing: true, modal: false );
		Check( !SeatAim.Aiming, "suspend survives the next frame of a live game" );
		Check( SeatAim.PickPixel().x == 640, "suspended: the pick follows the pointer again" );

		SeatAim.Toggle();
		Frame( playing: true, modal: false );
		Check( SeatAim.Aiming, "the plate: back into aim" );

		SeatAim.Toggle();
		Frame( playing: true, modal: false );
		Check( !SeatAim.Aiming, "the plate toggles the other way too" );

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

		// ── The aim banner's hit test (P99). ──
		//
		// The plate floats above the clock in the clock's TILTED plane, so the pick is no
		// longer the tabletop-plane test the board squares use — which is exactly why the
		// arithmetic is in SeatAim rather than in the Component, and why it is run here.
		//
		// Numbers mirror ChessRing at TableScale 1 (BoardSize 26): the clock strip's Y is
		// −ClockCenterY + ClockForwardSlide = −15.5 + 1.1, the banner's Z is TableTopZ +
		// ClockTopZ + AimFloatGap + its own rise ≈ 20 + 4.712 + 1.8 + 0.952, the tilt is
		// ClockFaceTilt, and the halves are AimPlateLength/Height ÷ 2 with FaceOutset at
		// half AimPlateThickness. If the room wants the plate somewhere else, these move —
		// the point of the section is the BASIS, which doesn't.
		const float cY = -14.4f, cZ = 27.464f, outset = 0.15f, tilt = -30f;
		const float halfLen = 6f, halfHgt = 1.1f;

		// The plane's basis, written out independently of the code under test.
		double t = tilt * Math.PI / 180.0;
		float nY = (float)Math.Cos( t ), nZ = -(float)Math.Sin( t );   // face normal
		float uY = (float)Math.Sin( t ), uZ = (float)Math.Cos( t );    // up the plane

		// Fire straight AT a point on the face: origin out along the normal, aimed back.
		bool HitFace( float x, float up )
		{
			float fy = cY + nY * outset + uY * up, fz = cZ + nZ * outset + uZ * up;
			return SeatAim.PlateHit(
				x, fy + nY * 20f, fz + nZ * 20f, 0f, -nY, -nZ,
				cY, cZ, outset, tilt, halfLen, halfHgt );
		}

		Check( HitFace( 0f, 0f ), "banner: dead centre is a hit" );
		Check( HitFace( 5.9f, 0f ) && HitFace( -5.9f, 0f ),
			"banner: just inside each END is a hit" );
		Check( !HitFace( 6.1f, 0f ) && !HitFace( -6.1f, 0f ),
			"banner: just past an end MISSES (the hit area is exactly the plate)" );
		Check( HitFace( 0f, 1.05f ) && HitFace( 0f, -1.05f ),
			"banner: just inside top and bottom is a hit" );
		Check( !HitFace( 0f, 1.15f ) && !HitFace( 0f, -1.15f ),
			"banner: just past top or bottom MISSES" );

		// The tilt is load-bearing, not decoration: a face angled up catches a ray coming
		// straight DOWN, and an upright one (tilt 0) is parallel to it and cannot.
		Check( SeatAim.PlateHit( 0f, cY + nY * outset, cZ + nZ * outset + 30f, 0f, 0f, -1f,
				cY, cZ, outset, tilt, halfLen, halfHgt ),
			"banner: a ray from straight above hits the tilted face" );
		Check( !SeatAim.PlateHit( 0f, cY, cZ + 30f, 0f, 0f, -1f,
				cY, cZ, outset, 0f, halfLen, halfHgt ),
			"banner: the same ray misses an UPRIGHT plate — the tilt is really applied" );

		// Sanity on the degenerate case: tilt 0 is a plate facing +Y, hit by a level ray.
		Check( SeatAim.PlateHit( 0f, cY - 30f, cZ, 0f, 1f, 0f,
				cY, cZ, outset, 0f, halfLen, halfHgt ),
			"banner: tilt 0 degenerates to an upright plate facing the board" );

		// Behind the camera is not in front of it, and neither is a grazing ray.
		Check( !SeatAim.PlateHit(
				0f, cY + nY * 20f, cZ + nZ * 20f, 0f, nY, nZ,
				cY, cZ, outset, tilt, halfLen, halfHgt ),
			"banner: a ray pointing AWAY from the plate misses" );
		Check( !SeatAim.PlateHit( 0f, cY + nY * 20f, cZ + nZ * 20f, 1f, 0f, 0f,
				cY, cZ, outset, tilt, halfLen, halfHgt ),
			"banner: a ray parallel to the face misses rather than dividing by zero" );

		// The FACE is picked, not the mid-plane: it sits outset along +normal, so a ray
		// starting at the centre and heading out the front still reaches it, and one
		// heading out the back never does.
		Check( SeatAim.PlateHit( 0f, cY, cZ, 0f, nY, nZ,
				cY, cZ, outset, tilt, halfLen, halfHgt ),
			"banner: the picked plane is the FRONT face, not the plate's middle" );
		Check( !SeatAim.PlateHit( 0f, cY, cZ, 0f, -nY, -nZ,
				cY, cZ, outset, tilt, halfLen, halfHgt ),
			"banner: nothing is pickable through its back" );

		Console.WriteLine( fails == 0 ? "\nALL PASS" : $"\n{fails} FAILED" );
		return fails == 0 ? 0 : 1;
	}
}
