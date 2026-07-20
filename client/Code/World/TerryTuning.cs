using Gambit.Chess;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// The inspector surface for the half-rise/hand knobs (M14) — a SCENE-authored component
/// (lobby.scene, "TerryTuning" GO), because the components that consume these values are
/// all runtime-built and have no inspector of their own. Drag a slider during play and the
/// pose changes that frame; save the scene and the tuning ships.
///
/// <para><b>It pushes into the <see cref="SeatedHandSpikes"/>/<see cref="TerryPose"/>
/// statics ON CHANGE, not every frame</b> — every consumer keeps reading the statics it
/// already reads, the console levers keep working between inspector touches, and (the real
/// reason) the one-shot diagnostics (<c>gambit_terry_doctor</c>/sweep/probe) force those
/// same statics through phases mid-run: a component re-asserting its values every frame
/// would silently fight every phase of every diagnostic.</para>
///
/// <para>Tunings are per-machine at runtime (a joiner gets the host's SAVED scene values
/// via the snapshot at join, not live drags). That is fine: tuning is an editor activity,
/// and what ships is the saved scene.</para>
/// </summary>
public sealed class TerryTuning : Component
{
	// ── Reach ──
	[Property, Group( "Reach" ), Range( 0f, 20f )] public float MaxLean { get; set; } = 8f;
	[Property, Group( "Reach" ), Range( 0f, 12f )] public float RiseGrace { get; set; } = 4f;
	[Property, Group( "Reach" ), Range( 0f, 10f )] public float ReachMargin { get; set; } = 2.5f;
	[Property, Group( "Reach" ), Range( 0f, 60f )] public float WristDrop { get; set; } = 25f;
	[Property, Group( "Reach" ), Range( -90f, 90f )] public float HandRoll { get; set; } = 45f;

	// ── Rise ──
	[Property, Group( "Rise" ), Range( 0f, 50f )] public float MaxRise { get; set; } = 46f;
	[Property, Group( "Rise" ), Range( 0f, 20f )] public float MaxStep { get; set; } = 16f;
	[Property, Group( "Rise" ), Range( 0f, 1f )] public float RiseLift { get; set; } = 0.3f;
	[Property, Group( "Rise" ), Range( 1f, 20f )] public float RiseChaseRate { get; set; } = 6f;
	[Property, Group( "Rise" ), Range( 0f, 60f )] public float TorsoYawMax { get; set; } = 30f;
	[Property, Group( "Rise" ), Range( 0f, 70f )] public float TorsoPitchMax { get; set; } = 0f;

	// ── Tempo ──
	[Property, Group( "Tempo" ), Range( 0.25f, 4f )] public float GestureSpeed { get; set; } = 1f;
	[Property, Group( "Tempo" ), Range( 2f, 30f )] public float HandChaseRate { get; set; } = 8f;
	[Property, Group( "Tempo" ), Range( 0.5f, 12f )] public float HoverChaseRate { get; set; } = 2.5f;

	// ── Hand heights (above the board surface) ──
	[Property, Group( "Hand" ), Range( 6f, 20f )] public float HoverHeight { get; set; } = 12f;
	[Property, Group( "Hand" ), Range( 4f, 16f )] public float GraspHeight { get; set; } = 10f;
	[Property, Group( "Hand" ), Range( 8f, 24f )] public float LiftHeight { get; set; } = 14f;

	// Grasp clearance is measured off the MOVED PIECE'S OWN TOP, not the board surface — the one
	// knob for "where the hand ends up relative to the piece it is moving" (watch it with
	// gambit_terry_scholars). Unlike the three above (inert on the piece-child path today), this
	// one is live for every real move.
	[Property, Group( "Hand" ), Range( -4f, 10f )] public float GraspClearance { get; set; } = 0f;

	// ── Carry ──
	[Property, Group( "Carry" ), Range( 0f, 16f )] public float CarryHang { get; set; } = 8f;
	[Property, Group( "Carry" ), Range( 2f, 16f )] public float GrabRadius { get; set; } = 9f;
	[Property, Group( "Carry" ), Range( 0f, 5f )] public float HandHoldSeconds { get; set; } = 1.2f;

	// ── Switches ──
	[Property, Group( "Switches" )] public bool HandsOn { get; set; } = true;
	[Property, Group( "Switches" )] public bool HalfRiseOn { get; set; } = true;
	[Property, Group( "Switches" )] public bool BraceOn { get; set; } = true;
	[Property, Group( "Switches" )] public bool ServoOn { get; set; } = true;

	// Last-pushed mirrors: a knob only pushes when the INSPECTOR moved it, so the console
	// levers and the diagnostics' save/force/restore cycles stay authoritative in between.
	float _maxLean, _riseGrace, _reachMargin, _wristDrop, _handRoll, _maxRise, _maxStep, _riseLift,
		_riseChase, _yaw, _pitch, _hover, _grasp, _lift, _hang, _grab, _hold, _speed, _handChase,
		_hoverChase, _graspClearance;
	bool _hands, _rise, _brace, _servo;

	protected override void OnEnabled() => Push( all: true );

	protected override void OnUpdate() => Push( all: false );

	void Push( bool all )
	{
		if ( all || MaxLean != _maxLean ) SeatedHandSpikes.MaxLean = _maxLean = MaxLean;
		if ( all || RiseGrace != _riseGrace ) SeatedHandSpikes.RiseGrace = _riseGrace = RiseGrace;
		if ( all || ReachMargin != _reachMargin ) SeatedHandSpikes.ReachMargin = _reachMargin = ReachMargin;
		if ( all || WristDrop != _wristDrop ) SeatedHandSpikes.WristDrop = _wristDrop = WristDrop;
		if ( all || HandRoll != _handRoll ) SeatedHandSpikes.HandRoll = _handRoll = HandRoll;
		if ( all || MaxRise != _maxRise ) SeatedHandSpikes.MaxRise = _maxRise = MaxRise;
		if ( all || MaxStep != _maxStep ) SeatedHandSpikes.MaxStep = _maxStep = MaxStep;
		if ( all || RiseLift != _riseLift ) SeatedHandSpikes.RiseLift = _riseLift = RiseLift;
		if ( all || RiseChaseRate != _riseChase ) SeatedHandSpikes.RiseChaseRate = _riseChase = RiseChaseRate;
		if ( all || TorsoYawMax != _yaw ) SeatedHandSpikes.TorsoYawMax = _yaw = TorsoYawMax;
		if ( all || TorsoPitchMax != _pitch ) SeatedHandSpikes.TorsoPitchMax = _pitch = TorsoPitchMax;
		if ( all || GestureSpeed != _speed ) TerryPose.SpeedScale = _speed = GestureSpeed;
		if ( all || HandChaseRate != _handChase ) SeatedHandSpikes.HandChaseRate = _handChase = HandChaseRate;
		if ( all || HoverChaseRate != _hoverChase ) SeatedHandSpikes.HoverChaseRate = _hoverChase = HoverChaseRate;
		if ( all || HoverHeight != _hover ) TerryPose.HoverHeight = _hover = HoverHeight;
		if ( all || GraspHeight != _grasp ) TerryPose.GraspHeight = _grasp = GraspHeight;
		if ( all || LiftHeight != _lift ) TerryPose.LiftHeight = _lift = LiftHeight;
		if ( all || GraspClearance != _graspClearance ) SeatedHandSpikes.GraspClearance = _graspClearance = GraspClearance;
		if ( all || CarryHang != _hang ) SeatedHandSpikes.CarryHang = _hang = CarryHang;
		if ( all || GrabRadius != _grab ) SeatedHandSpikes.GrabRadius = _grab = GrabRadius;
		if ( all || HandHoldSeconds != _hold ) SeatedHandSpikes.HandHoldSeconds = _hold = HandHoldSeconds;
		if ( all || HandsOn != _hands ) SeatedHandSpikes.HandsOn = _hands = HandsOn;
		if ( all || HalfRiseOn != _rise ) SeatedHandSpikes.HalfRiseOn = _rise = HalfRiseOn;
		if ( all || BraceOn != _brace ) SeatedHandSpikes.BraceOn = _brace = BraceOn;
		if ( all || ServoOn != _servo ) SeatedHandSpikes.ServoOn = _servo = ServoOn;
	}
}
