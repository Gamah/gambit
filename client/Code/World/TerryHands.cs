using System;
using Sandbox;
using Gambit.Chess;

namespace Gambit.World;

/// <summary>
/// M14 tuning surface: every knob the seated hands read, as process <b>statics</b>, plus the
/// <c>gambit_terry*</c> console levers and the live-values dump.
///
/// <para><b>Why statics, not <c>[Property]</c> knobs.</b> <see cref="ChessRing"/> and the
/// per-avatar hand driver on <see cref="LobbyPlayer"/> are runtime-built (code defaults, no
/// scene inspector), so a knob there can only be moved by editing-and-hotloading. These are
/// the levers you want to pull LIVE while sitting at the board. And they are statics for a
/// second reason the owner requires: <b>a tuning slider must apply to EVERY terry in the
/// world at once</b>, not just the local one — every avatar's driver reads these same fields,
/// so one lever moves all of them. Each machine has its own statics; a joined client uses the
/// host's saved scene values, which is fine — tuning is an editor activity.</para>
///
/// <para><b>The kill chain is three-deep, on purpose</b> (a clean, total revert to the
/// shipped-M13 world at any level): <see cref="ChessRing.TerrySeated"/> (no seated bodies at
/// all) → <see cref="HandsOn"/> (bodies, no hands — Phase 1 off) → <see cref="HalfRiseOn"/>
/// (hands, no half-rise — Phase 2 off, near moves only, far moves trail).</para>
///
/// <para><b>Build the live-values dump on day one</b> — the M14 doctrine's hardest-won rule.
/// Two full rounds of attempt 2 went into tuning sliders that were confirmed live and changed
/// nothing visible, because the motion was dominated by a different subsystem. <c>gambit_terry</c>
/// prints the whole chain — which levers are set, whether there is a body to pose, the measured
/// arm, and what the active move gesture is doing — so "is it even loaded / is this the right
/// knob" is never a guess again.</para>
/// </summary>
public static class TerryHands
{
	// ───────────────────────────── Kill switches ─────────────────────────────

	/// <summary><b>Phase 1 master gate.</b> false = seated bodies with no hand IK (the shipped
	/// M13 world). <c>gambit_terry_hands</c>. Default ON: the hands are the M14 deliverable.</summary>
	public static bool HandsOn = true;

	/// <summary><b>Phase 2 gate: the move-only half-rise.</b> false = near moves only; a piece
	/// past the seated arm's reach has the hand trail and the piece finish its slide alone
	/// (design point 6). Default <b>OFF</b> so Phase 1 is what loads first and is judged on its
	/// own — flip <c>gambit_terry_rise 1</c> to test the rise. The half-rise is the riskier,
	/// heavier layer; the owner judges Phase 1 before turning it on.</summary>
	public static bool HalfRiseOn = false;

	/// <summary>Hold the working hand at a fixed rest anchor (forearm on the table edge) via a
	/// light IK anchor between moves, rather than releasing it to the raw sit animation
	/// (design point 3). <c>gambit_terry_rest</c>. If the anchored rest ever reads worse than a
	/// relaxed arm, turn it off and the hand simply isn't driven except during a move.</summary>
	public static bool RestAnchorOn = true;

	// ───────────────────────────── Tempo (design point 10) ─────────────────────────────

	/// <summary>Rest-to-rest seconds for one move — the single tempo slider, deliberate not
	/// hurried (~0.8–1s). <c>gambit_terry_budget</c>.</summary>
	public static float MoveBudget = 0.95f;

	/// <summary>A capture gets up to this multiple of the base budget (the attacker's trip is
	/// the same shape, just given more time to read).</summary>
	public static float CaptureBudgetScale = 1.35f;

	/// <summary>The one timeline's split: travel to the piece / carry it / ease home. Passed
	/// raw to <see cref="MoveGesture"/>, which normalises them, so these are weights not
	/// fractions — they need not sum to 1.</summary>
	public static float ApproachFrac = 0.30f;
	public static float CarryFrac = 0.50f;
	public static float ReleaseFrac = 0.20f;

	// ───────────────────────────── Grip / hand placement ─────────────────────────────

	/// <summary>World units the wrist rides above the piece it is moving. The IK aims the
	/// WRIST bone, so this plus <see cref="GripOffset"/> is what puts the fingers on the piece
	/// rather than the palm through it.</summary>
	public static float GraspHeight = 6f;

	/// <summary>Wrist pull-back in hand-rotation space — the IK aims the wrist, so a target
	/// dropped straight on the grasp point puts the fingers past it. Tune against
	/// <c>gambit_terry</c>'s hand_R readout.</summary>
	public static Vector3 GripOffset = new( -3f, 0f, 0f );

	/// <summary>Where the working hand rests between moves — station-local, WHITE frame (the
	/// driver mirrors it for Black). In White's near margin, forearm on the table edge.</summary>
	public static Vector3 RestAnchorLocal = new( -16f, -9f, 34f );

	// ───────────────────────────── Hand rotation (facts #2) ─────────────────────────────

	/// <summary>Cap on the wrist's nose-down pitch. The effective pitch is the forearm's own
	/// declination toward the target (so a flat far reach doesn't hyper-flex the wrist)
	/// plus <see cref="WristDrop"/>, clamped here. A fixed pitch was the "super kinked wrist".</summary>
	public static float HandPitchCap = 55f;

	/// <summary>A little extra nose-down curl added on top of the forearm declination, so the
	/// fingers point at the piece even on a shallow reach.</summary>
	public static float WristDrop = 12f;

	/// <summary>Rolls the hand target to swing the elbow OUT of the torso — the "t-rex arm"
	/// fix (fact #2). The IK consumes the target rotation as the elbow pole; 0 traps the arm
	/// in a vertical plane and the elbow just drops. Sign unverified — flip if the elbow tucks
	/// INTO the body.</summary>
	public static float HandRoll = 35f;

	// ───────────────────────────── Reach / half-rise (Phase 2) ─────────────────────────────

	/// <summary>Grip margin subtracted from the LIVE-measured arm length to get the honest
	/// reach fed to <see cref="HandReach"/>. The arm is measured off the bones each frame; this
	/// is the only reach number that isn't.</summary>
	public static float ReachMargin = 2f;

	// The rest map onto ReachTunables one-to-one; see HandReach.cs for what each means.
	public static float MaxLean = 8f;
	public static float RiseGrace = 3f;
	public static float LegReach = 30f;
	public static float MaxStep = 16f;
	public static float MaxRise = 40f;
	public static float RiseLift = 0.3f;
	public static float HipMaxX = -19.75f;
	public static float FootMaxX = -16f;

	/// <summary>The closed-loop hand servo (fact #4): a ~5u native warp survives every override
	/// we can model, so measure last frame's hand_R-vs-true-ask error, integrate, clamp, decay.
	/// Only runs while the half-rise is active.</summary>
	public static bool ServoOn = true;
	public static float ServoRate = 12f;
	public static float ServoClamp = 8f;

	/// <summary>Build the reach tunables from the live statics, using the measured arm.</summary>
	public static ReachTunables Reach( float measuredArm ) => new(
		Reach: MathF.Max( measuredArm - ReachMargin, 4f ),
		MaxLean: MaxLean, RiseGrace: RiseGrace, LegReach: LegReach, MaxStep: MaxStep,
		MaxRise: MaxRise, RiseLift: RiseLift, HipMaxX: HipMaxX, FootMaxX: FootMaxX );

	// ───────────────────────────── Console levers ─────────────────────────────

	[ConCmd( "gambit_terry_hands" )]
	public static void SetHands( int on ) { HandsOn = on != 0; Log.Info( $"[Gambit] terry hands {(HandsOn ? "ON" : "off")}" ); }

	[ConCmd( "gambit_terry_rise" )]
	public static void SetRise( int on ) { HalfRiseOn = on != 0; Log.Info( $"[Gambit] terry half-rise {(HalfRiseOn ? "ON" : "off")}" ); }

	[ConCmd( "gambit_terry_rest" )]
	public static void SetRest( int on ) { RestAnchorOn = on != 0; Log.Info( $"[Gambit] terry rest-anchor {(RestAnchorOn ? "ON" : "off")}" ); }

	[ConCmd( "gambit_terry_budget" )]
	public static void SetBudget( float seconds ) { MoveBudget = MathF.Max( seconds, 0.1f ); Log.Info( $"[Gambit] terry move budget {MoveBudget:0.00}s" ); }

	[ConCmd( "gambit_terry_grasp" )]
	public static void SetGrasp( float u ) { GraspHeight = u; Log.Info( $"[Gambit] terry grasp height {GraspHeight:0.0}u" ); }

	[ConCmd( "gambit_terry_roll" )]
	public static void SetRoll( float deg ) { HandRoll = deg; Log.Info( $"[Gambit] terry hand roll {HandRoll:0}°" ); }

	/// <summary>The live-values dump. Prints the whole chain so "is this even loaded / is this
	/// the right knob" is never a guess.</summary>
	[ConCmd( "gambit_terry" )]
	public static void Dump()
	{
		Log.Info( "── gambit_terry ──" );
		Log.Info( $"  levers: hands={(HandsOn ? "ON" : "off")} rise={(HalfRiseOn ? "ON" : "off")} "
			+ $"rest={(RestAnchorOn ? "ON" : "off")} servo={(ServoOn ? "ON" : "off")}" );
		Log.Info( $"  tempo: budget={MoveBudget:0.00}s ×{CaptureBudgetScale:0.00} on capture; "
			+ $"split {ApproachFrac:0.##}/{CarryFrac:0.##}/{ReleaseFrac:0.##} (approach/carry/release)" );
		Log.Info( $"  grip: grasp={GraspHeight:0.0}u offset={GripOffset} rest={RestAnchorLocal}" );
		Log.Info( $"  rot: pitchCap={HandPitchCap:0}° wristDrop={WristDrop:0}° roll={HandRoll:0}°" );
		Log.Info( $"  reach: margin={ReachMargin:0.0} lean={MaxLean:0} grace={RiseGrace:0} "
			+ $"legReach={LegReach:0} maxRise={MaxRise:0} lift={RiseLift:0.0#}" );

		var ring = ChessRing.Instance;
		Log.Info( $"  world: TerrySeated={(ring?.TerrySeated.ToString() ?? "(no ring)")}" );

		var lp = LobbyPlayer.Local;
		if ( lp == null ) { Log.Info( "  local player: (none)" ); return; }
		if ( lp.SeatedAt is not { } seatedAt )
		{
			Log.Info( "  local player: not seated — sit at a table to see the hand chain." );
			return;
		}
		Log.Info( $"  seated: station={seatedAt.Station.GameObject.Name} seat={seatedAt.Seat} "
			+ $"hasBody={lp.HasBody} measuredArm={lp.MeasuredArmDebug:0.0}u riseApplied={lp.RiseAppliedDebug:0.0}u" );

		var view = seatedAt.Station.GameObject.Components.Get<ChessBoardView>();
		if ( view?.ActiveHandMove is { } m )
			Log.Info( $"  MOVE: mover={m.MoverSeat} {SquareName( m.FromSquare )}→{SquareName( m.ToSquare )} "
				+ $"phase={m.Phase} handWeight={m.HandWeight:0.00} grip={m.GripClose:0.00} capture={m.Capture}" );
		else
			Log.Info( "  MOVE: none (hand at rest anchor)" );
	}

	static string SquareName( int sq ) =>
		sq < 0 ? "--" : $"{(char)('a' + ( sq & 7 ))}{(char)('1' + ( sq >> 3 ))}";
}
