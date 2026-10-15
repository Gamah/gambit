using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.VR;

namespace Gambit.World;

/// <summary>
/// The controllers, bridged onto the controls the lobby already has. Nothing downstream learns a
/// VR-specific input path: the board still reads <c>Input.Pressed("Select")</c>, a station still
/// reads <c>"use"</c>, standing up still reads <c>Input.EscapePressed</c>, and walking still comes
/// off <c>Input.AnalogMove</c>.
///
/// <para><b>Why this is possible at all.</b> VR input is a parallel API — nothing in the engine's
/// VR code writes the action system — but <c>Input.SetAction</c> and <c>Input.EscapePressed</c> are
/// both public and settable. A <c>GameObjectSystem</c> at <c>Stage.StartUpdate</c> is the one place
/// that can write them for a frame every component then reads: <c>Input.Process</c> has already
/// computed the frame's real keyboard and mouse state by then, and no component has run.</para>
///
/// <para><b>Actions are pulsed on the rising edge and never written false.</b> An action bit set
/// every frame while a trigger is held would fire <c>Input.Pressed</c> every frame, because
/// <c>Pressed</c> compares against a previous-frame set that was captured before this write
/// (<c>Input.Context.Flip</c>) — a held trigger would pick a square per frame. Writing false is
/// just as wrong, since the same bit carries the keyboard's state. So the only write is a single
/// true on the press, and the engine's own flip clears it.</para>
///
/// <para><b>The sticks only write while they are off centre</b>, so mouse look and WASD still work
/// exactly as they do on the desktop — the engine computes those into the same two properties
/// earlier in the frame, and an unconditional write would zero them.</para>
///
/// <para>Grip is deliberately unused: it is what grabbing a piece will want.</para>
/// </summary>
public sealed class VrInput : GameObjectSystem<VrInput>
{
	public enum Hand { Left, Right }

	/// <summary>The hand the beam belongs to — whichever last pulled its trigger, so there is no
	/// handedness setting to get wrong and either hand can take over mid-game.</summary>
	public static Hand Pointing { get; private set; } = Hand.Right;

	/// <summary>Where the pointing hand aims, in world space, or null when no VR rig is driving.
	/// <see cref="ChessBoardView"/> picks a square with this and <see cref="VrHands"/> draws the
	/// beam along it, so the square you pick and the line you see cannot disagree — the same rule
	/// <see cref="SeatAim.PickPixel"/> keeps for the desktop.</summary>
	public static Ray? AimRay { get; private set; }

	/// <summary>Degrees per second of smooth yaw from the right stick.</summary>
	public static float TurnRate { get; set; } = 110f;

	/// <summary>How far a trigger travels before it counts as a press.</summary>
	const float TriggerPress = 0.5f;

	/// <summary>Stick travel below this is centre: it writes nothing, which is what leaves the
	/// desktop's own movement and look alone.</summary>
	const float Deadzone = 0.15f;

	bool _leftTrigger;
	bool _rightTrigger;

	static RealTimeUntil _logUntil;
	static RealTimeSince _logLast;

	public VrInput( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Tick, nameof( Tick ) );
	}

	void Tick()
	{
		if ( Scene is null || Scene.IsEditor ) return;

		if ( !Sandbox.Game.IsRunningInVR || !VrRig.Active )
		{
			AimRay = null;
			return;
		}

		var left = Input.VR.LeftHand;
		var right = Input.VR.RightHand;

		bool leftTrigger = left.Trigger.Value >= TriggerPress;
		bool rightTrigger = right.Trigger.Value >= TriggerPress;
		bool leftPulled = leftTrigger && !_leftTrigger;
		bool rightPulled = rightTrigger && !_rightTrigger;
		_leftTrigger = leftTrigger;
		_rightTrigger = rightTrigger;

		if ( leftPulled ) Pointing = Hand.Left;
		if ( rightPulled ) Pointing = Hand.Right;

		var aim = Pointing == Hand.Left ? left.AimTransform : right.AimTransform;
		AimRay = new Ray( aim.Position, aim.Rotation.Forward );

		// Either trigger does both jobs a click does: pick a square, and sit down at whatever you
		// are standing next to. A panel click is not here — WorldInput reads the trigger itself in
		// VR, which is why the pointer lives on a hand (see VrHands).
		if ( leftPulled || rightPulled )
		{
			Input.SetAction( "Select", true );

			// "use" is also what CLOSES an engaged info board (LobbyPlayer reads it there), so
			// pulsing it while engaged would shut the board on the same press that clicked a
			// button on it. Engaged, the trigger is the pointer and nothing else; B leaves.
			if ( LobbyPlayer.Local is not { Engaged: true } ) Input.SetAction( "use", true );
		}

		// Escape is not an action — it is its own settable global, and LobbyPlayer clears it when
		// it consumes one (the cursor toggle while seated, standing up otherwise).
		//
		// B is the button for it; A answers too, because B is the one binding in the engine's
		// Index profile written with the wrong key. `game/core/cfg/fpxr/valve_knuckles.json` gives
		// button_b a `paths` (plural) holding a single string, where every other single binding in
		// both shipped profiles uses `path` and every `paths` holds a per-hand object. Whether the
		// native loader is lenient about that is not something the managed source can say, so the
		// mapping does not depend on it.
		if ( Pressed( left.ButtonB ) || Pressed( right.ButtonB )
			|| Pressed( left.ButtonA ) || Pressed( right.ButtonA ) )
			Input.EscapePressed = true;

		Walk( left.Joystick.Value );
		Turn( right.Joystick.Value );

		// A trail for things that only happen with the headset ON. The log file outlives the
		// session, so this is readable after stopping even though the state it describes is gone.
		if ( _logUntil > 0 && _logLast >= 1f )
		{
			_logLast = 0;
			Status();
		}
	}

	static bool Pressed( DigitalInput d ) => d.IsPressed && !d.WasPressed;

	/// <summary>Dump <see cref="Status"/> once a second for this many seconds, so a controller that
	/// only misbehaves while worn leaves a trail in the log to read afterwards. Set a controller
	/// down to read the console and it goes to sleep, which reads as "no input" — the one answer
	/// this is trying to rule out.</summary>
	[ConCmd( "gambit_vr_log" )]
	public static void LogFor( float seconds )
	{
		_logUntil = seconds;
		_logLast = 1f;
		Log.Info( $"[Gambit] vr: logging status for {seconds}s" );
	}

	/// <summary>Everything the rig believes about itself, one fact per line. The console command
	/// and the in-world board both render this, so they cannot disagree.
	///
	/// <para><b>Static on purpose.</b> It is readable whether or not this system was ever
	/// instantiated — which is itself one of the facts it reports, and the one that a dead stick,
	/// a missing beam and an unclickable panel all reduce to.</para></summary>
	public static List<string> StatusLines()
	{
		var lines = new List<string>
		{
			$"vr={Sandbox.Game.IsRunningInVR}  rig={VrRig.Active}  system={Current != null}",
			$"hands={VrHands.Instance != null}  hud={VrHud.Instance != null}",
		};

		if ( !Sandbox.Game.IsRunningInVR )
		{
			lines.Add( "not in VR - nothing else means anything" );
			return lines;
		}

		var left = Input.VR.LeftHand;
		var right = Input.VR.RightHand;

		lines.Add( $"head={left.Transform.Position.z:0}  anchor={Input.VR.Anchor.Position}" );
		lines.Add( $"L pos={Fmt( left.Transform.Position )} stick={Fmt( left.Joystick.Value )}"
			+ $" trig={left.Trigger.Value:0.00} a={Bit( left.ButtonA )} b={Bit( left.ButtonB )}" );
		lines.Add( $"R pos={Fmt( right.Transform.Position )} stick={Fmt( right.Joystick.Value )}"
			+ $" trig={right.Trigger.Value:0.00} a={Bit( right.ButtonA )} b={Bit( right.ButtonB )}" );
		lines.Add( $"active L stick={left.Joystick.Active} trig={left.Trigger.Active}"
			+ $"   R stick={right.Joystick.Active} trig={right.Trigger.Active}" );
		lines.Add( $"model L={Name( left.GetModel() )}  R={Name( right.GetModel() )}" );
		lines.Add( $"point={Pointing}  aim={(AimRay is { } r ? Fmt( r.Position ) : "none")}" );
		lines.Add( $"move={Fmt( Input.AnalogMove )}  look={Input.AnalogLook.yaw:0.00}"
			+ $"  suppressed={Input.Suppressed}" );
		lines.Add( VrHands.Instance?.StatusLine() ?? "hands: no component" );

		return lines;
	}

	static string Fmt( Vector3 v ) => $"{v.x:0},{v.y:0},{v.z:0}";
	static string Fmt( Vector2 v ) => $"{v.x:0.00},{v.y:0.00}";
	static string Bit( DigitalInput d ) => d.IsPressed ? "1" : "0";
	static string Name( Model m ) => m == null ? "none" : m.ResourceName;

	/// <summary>Everything the rig believes about itself, to the console. Prefer the in-world
	/// board (<c>gambit_vr_debug</c>) while wearing the headset; a console you cannot see is the
	/// reason the hands went unexplained for three sessions.</summary>
	[ConCmd( "gambit_vr_status" )]
	public static void Status()
	{
		foreach ( var line in StatusLines() )
			Log.Info( $"[Gambit] vr: {line}" );
	}

	/// <summary>Left stick walks, by both routes the engine offers.
	///
	/// <para><c>AnalogMove</c> is what <c>PlayerController</c> reads, and in the engine's axes —
	/// forward is +x and LEFT is +y (<c>Input.ComputeAnalogMove</c>) — so the stick's x is negated
	/// rather than copied. That write is enough on paper: <c>Input.Process</c> recomputes the
	/// analogs once per frame, before the scene ticks, so nothing overwrites it afterwards.</para>
	///
	/// <para>The movement ACTIONS are pressed as well, which is belt and braces rather than
	/// duplication: anything that recomputes <c>AnalogMove</c> derives it from exactly these four
	/// action bits, so the stick still walks even if a recompute lands somewhere this does not
	/// expect. It costs the analog magnitude in that case and nothing otherwise.</para></summary>
	static void Walk( Vector2 stick )
	{
		if ( stick.Length < Deadzone ) return;

		Input.AnalogMove = new Vector3( stick.y, -stick.x, 0f );

		if ( stick.y > Deadzone ) Input.SetAction( "Forward", true );
		if ( stick.y < -Deadzone ) Input.SetAction( "Backward", true );
		if ( stick.x < -Deadzone ) Input.SetAction( "Left", true );
		if ( stick.x > Deadzone ) Input.SetAction( "Right", true );
	}

	/// <summary>Right stick turns, smoothly. <c>AnalogLook</c> is added to the controller's eye
	/// angles as degrees for this frame, so the rate is scaled by frame time here; yaw runs
	/// anticlockwise, which is why pushing right is negative.</summary>
	static void Turn( Vector2 stick )
	{
		if ( MathF.Abs( stick.x ) < Deadzone ) return;

		Input.AnalogLook = new Angles( 0f, -stick.x * TurnRate * Time.Delta, 0f );
	}
}
