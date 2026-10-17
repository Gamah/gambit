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

	/// <summary>Seconds since either trigger was pulled, and how many pulls this session: with no
	/// other feedback in the headset, "did that register at all" is the first question.</summary>
	public static RealTimeSince LastTrigger { get; private set; }
	static int _pulls;

	static RealTimeUntil _logUntil;
	static RealTimeSince _logLast;

	public VrInput( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Tick, nameof( Tick ) );

		// Movement has to be written in the FIXED stage, because that is where it is read. See
		// FixedTick.
		Listen( Stage.StartFixedUpdate, 0, FixedTick, nameof( FixedTick ) );
	}

	/// <summary>Write the walk axis immediately before the controller consumes it.
	///
	/// <para><b>The look and move analogs are read at opposite ends of the tick, and that is the
	/// whole reason turning worked while walking did nothing.</b> <c>UpdateEyeAngles</c> reads
	/// <c>Input.AnalogLook</c> from <c>OnUpdate</c>, after <c>Stage.StartUpdate</c>; but
	/// <c>InputMove</c> reads <c>Input.AnalogMove</c> from <c>OnFixedUpdate</c>, and
	/// <c>Scene.SharedTick</c> runs the whole fixed update BEFORE it signals
	/// <c>StartUpdate</c>. Since <c>Input.Process</c> recomputes both analogs from real keyboard
	/// state once per frame ahead of the tick, a move written in <c>StartUpdate</c> was always
	/// consumed as zero and overwritten before it was ever read.</para>
	///
	/// <para><c>Stage.StartFixedUpdate</c> is signalled inside the fixed update, ahead of the
	/// components in it, which is the one place this write survives to be read.</para></summary>
	void FixedTick()
	{
		if ( Scene is null || Scene.IsEditor ) return;
		if ( !Sandbox.Game.IsRunningInVR || !VrRig.Active ) return;

		Walk( Input.VR.LeftHand.Joystick.Value );
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

		if ( leftPulled || rightPulled )
		{
			LastTrigger = 0;
			_pulls++;
		}

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
	/// <summary>The one thing worth shouting: which step of getting hands into the room failed.
	/// Each answer is a different fault with the same symptom — nothing in the room — and they are
	/// ordered so the first failure is the one named.</summary>
	public static string HandsVerdict()
	{
		if ( VrHands.Instance == null ) return "HANDS: NONE";
		if ( VrHands.LastError != null ) return "HANDS: THREW";
		if ( !VrHands.Built ) return "HANDS: NO OBJS";
		if ( VrHands.Placed == 0 ) return "HANDS: UNPLACED";
		if ( !VrHands.Drawn ) return "HANDS: NO MODEL";
		if ( !VrHands.BeamOn ) return "HANDS: NO BEAM";

		return "HANDS: DRAWN";
	}

	/// <summary>Whatever went wrong, as a sentence, or null. Either component's throw will do:
	/// in a headset they are the same fault — something is missing and nothing said why.</summary>
	public static string Error => VrHands.LastError ?? VrRig.LastError;

	public static List<string> StatusLines()
	{
		var lines = new List<string>
		{
			HandsVerdict(),
			$"vr={Sandbox.Game.IsRunningInVR} rig={VrRig.Active} system={Current != null}"
				+ $" hud={VrHud.Instance != null}",
			VrHands.Status(),
		};

		if ( Error != null ) lines.Add( $"err: {Error}" );

		if ( !Sandbox.Game.IsRunningInVR )
		{
			lines.Add( "not in VR - nothing else means anything" );
			return lines;
		}

		lines.Add( $"triggers={PullCount} last={LastTrigger:0.0}s square={HoverName()}" );
		lines.AddRange( DetailLines() );

		return lines;
	}

	/// <summary>The numbers, in the order they stop being needed. The board renders these smallest
	/// and last: by the time a reading here matters, the verdict above has already said which of
	/// them to look at.</summary>
	public static List<string> DetailLines()
	{
		var left = Input.VR.LeftHand;
		var right = Input.VR.RightHand;

		return new List<string>
		{
			$"stick L {Fmt( left.Joystick.Value )}  R {Fmt( right.Joystick.Value )}",
			$"btn L a{Bit( left.ButtonA )} b{Bit( left.ButtonB )}"
				+ $"  R a{Bit( right.ButtonA )} b{Bit( right.ButtonB )}",
			$"yaw head {Input.VR.Head.Rotation.Angles().yaw:0}  body {VrRig.BodyYaw:0}",
			$"move {Fmt( Input.AnalogMove )}  look {Input.AnalogLook.yaw:0.00}",
			$"head {Fmt( Input.VR.Head.Position )}  anchor {Fmt( Input.VR.Anchor.Position )}",
			$"point {Pointing}  aim {(AimRay is { } r ? Fmt( r.Position ) : "none")}",
			$"model L {Name( left.GetModel() )}  R {Name( right.GetModel() )}",
			$"placed {VrHands.Placed}  beam {VrHands.BeamOn}  suppressed {Input.Suppressed}",
		};
	}

	/// <summary>How many trigger pulls this session. With nothing else acknowledging a pull, "did
	/// that register at all" is the first question a player asks.</summary>
	public static int PullCount => _pulls;

	/// <summary>The square the pointing hand is over at the local player's own table, as the board
	/// itself sees it — so a trigger that did nothing can be told apart from one that was aimed
	/// nowhere.</summary>
	public static string HoverName()
	{
		var view = ChessStation.Active?.Components.Get<ChessBoardView>();
		if ( view == null ) return "NOT SEATED";

		return ChessBoardView.NameOf( view.HoverSquare )?.ToUpperInvariant() ?? "OFF BOARD";
	}

	static string Fmt( Vector3 v ) => $"{v.x:0},{v.y:0},{v.z:0}";
	static string Fmt( Vector2 v ) => $"{v.x:0.00},{v.y:0.00}";
	static string Bit( DigitalInput d ) => d.IsPressed ? "1" : "0";
	/// <summary>A model's file name. The resource path is longer than a row of this board, and the
	/// directory it sits in has never been the question.</summary>
	static string Name( Model m )
	{
		if ( m == null ) return "none";

		var path = m.ResourceName ?? "?";
		int slash = path.LastIndexOf( '/' );
		return slash < 0 ? path : path[(slash + 1)..];
	}

	/// <summary>Everything the rig believes about itself, to the console. Prefer the in-world
	/// board (<c>gambit_vr_debug</c>) while wearing the headset; a console you cannot see is the
	/// reason the hands went unexplained for three sessions.</summary>
	[ConCmd( "gambit_vr_status" )]
	public static void Status()
	{
		foreach ( var line in StatusLines() )
			Log.Info( $"[Gambit] vr: {line}" );
	}

	/// <summary>Left stick walks, where the player is LOOKING.
	///
	/// <para><b>The body and the view face different ways in VR, and only the body reaches the
	/// controller.</b> A headset never writes <c>EyeAngles</c> — head rotation moves the playspace
	/// anchor and nothing else — while <c>PlayerController.InputMove</c> builds its wish direction
	/// out of <c>EyeAngles</c>. So "stick forward" means "forward along the body", which after any
	/// head turn is not the direction the player is facing. Nothing about the stick's own axes is
	/// ambiguous: up is forward.</para>
	///
	/// <para>So the stick is read in the head's frame and handed over in the body's: push the
	/// composition through <c>Rotation</c> rather than trigonometry, because the sign errors in
	/// the trigonometric version are invisible until someone turns around.</para></summary>
	static void Walk( Vector2 stick )
	{
		if ( stick.Length < Deadzone ) return;

		// Engine axes: forward is +x, LEFT is +y.
		var wish = new Vector3( stick.y, -stick.x, 0f );

		float headYaw = Input.VR.Head.Rotation.Angles().yaw;
		var world = Rotation.FromYaw( headYaw ) * wish;

		Input.AnalogMove = Rotation.FromYaw( VrRig.BodyYaw ).Inverse * world;
	}

	/// <summary>Total yaw the right stick has asked for, in degrees, accumulated for as long as
	/// this system has been running.
	///
	/// <para><b>Seated, the stick has nothing else to turn.</b> <c>AnalogLook</c> reaches the view
	/// through <c>PlayerController.UpdateEyeAngles</c>, and the controller is disabled while
	/// seated — so without this the right stick is dead at a table, where a player still wants to
	/// look around the room. <see cref="VrRig"/> takes the difference since it captured the seat
	/// and yaws the playspace by it.</para>
	///
	/// <para>Accumulated HERE because this system's update runs exactly once a frame, where
	/// <c>VrRig.Drive</c> runs in both <c>OnUpdate</c> and <c>OnPreRender</c> and would integrate
	/// the same frame twice. Only the difference is ever read, so it is free to run while roaming
	/// and be ignored.</para></summary>
	public static float TurnYaw { get; private set; }

	/// <summary>Right stick turns, smoothly. <c>AnalogLook</c> is added to the controller's eye
	/// angles as degrees for this frame, so the rate is scaled by frame time here; yaw runs
	/// anticlockwise, which is why pushing right is negative.</summary>
	static void Turn( Vector2 stick )
	{
		if ( MathF.Abs( stick.x ) < Deadzone ) return;

		float yaw = -stick.x * TurnRate * Time.Delta;
		TurnYaw += yaw;
		Input.AnalogLook = new Angles( 0f, yaw, 0f );
	}
}
