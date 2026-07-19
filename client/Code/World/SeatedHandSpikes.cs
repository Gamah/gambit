using Gambit.Chess;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// <b>M14 scaffolding — the levers, and the whole file is meant to be deleted in one cleanup
/// pass.</b> M13 shipped the seated bodies and CUT the reaching-hand path as geometrically
/// impossible for a fixed-size citizen (proof + numbers in <c>SEATED-HANDS-REACH.md</c>). M14
/// restored that path from tag <c>terry-hands-final</c> and puts every one of the doc's three
/// gated approaches behind a runtime lever here, so ONE editor session can test and log all of
/// them and ONE cleanup pass keeps the winner and deletes the rest.
///
/// <para><b>Nothing here is on by default.</b> The bodies (<see cref="ChessRing.TerrySeated"/>)
/// stay shipped and untouched; the hands and every reach spike start OFF. Flip them with the
/// <c>gambit_terry_*</c> console commands below, read the result off <c>gambit_terry</c> (the
/// ruler + reach grid) and <c>gambit_terry_probe</c> (achieved-vs-asked per square), then decide.
/// <c>gambit_terry_spikes</c> prints this whole playbook and the live lever state.</para>
///
/// <para><b>Why statics, not <c>[Property]</c> knobs:</b> <see cref="ChessRing"/> and
/// <see cref="SeatedTerry"/> are runtime-built (code defaults, no scene inspector), so a knob
/// there can only be moved by editing-and-hotloading. These are the levers you want to pull
/// LIVE while sitting at the board, so they are console-settable statics — the same shape as
/// <see cref="SeatedTerry.Probe"/>. Gameplay reads them each frame
/// (<see cref="LobbyPlayer.ApplyHandPose"/>, <see cref="LobbyPlayer.ApplySitPose"/>,
/// <see cref="ChessRing.SquareReachable"/>).</para>
///
/// <para><b>The INSPECTOR surface is <see cref="TerryTuning"/></b> — a scene-authored
/// component (lobby.scene) whose [Property] sliders push into these statics ON CHANGE.
/// Tune there and save the scene; the SCENE's values are the shipping truth, and the code
/// defaults here are only what a scene-less world falls back to. The console levers keep
/// working between inspector touches (TerryTuning deliberately never re-asserts, so the
/// doctor/sweep/probe can still force phases through these statics mid-run).</para>
///
/// <para><b>Cleanup pass, when a verdict is in:</b> keep the winning approach's real behaviour
/// (fold the chosen lever's value back into <see cref="ChessRing"/>/<see cref="TerryPose"/> as a
/// fixed constant, or delete the whole hand path again per the doc's "shipping nothing is a
/// legitimate close"), then delete THIS file, <see cref="TerryCommands"/>, the
/// <see cref="SeatedTerry"/> probe block, and the <c>SeatedHandSpikes.*</c> reads in
/// <see cref="LobbyPlayer"/>. That is the whole rip-out surface, on purpose.</para>
/// </summary>
public static class SeatedHandSpikes
{
	// ───────────────────────────────── The levers ─────────────────────────────────
	// Each field is a lever; the comment says what result it is FOR and what the cleanup
	// decision is once you have looked at it. Defaults are the shipped-M13 world with hands OFF.

	/// <summary><b>Master gate for the hands.</b> false = bodies only, no hand IK — the shipped
	/// M13 world. <c>gambit_terry_hands</c>. <b>Default ON since the M14 half-rise</b>: the half-rise makes the
	/// reach real, so the hands are the deliverable rather than a spike. The kill chain is
	/// three-deep on purpose: <see cref="ChessRing.TerrySeated"/> (no bodies at all) →
	/// this (bodies, no hands) → <see cref="HalfRiseOn"/> (hands, no rise).</summary>
	public static bool HandsOn = true;

	// ───────────────────────────── M14: the half-rise ─────────────────────────────

	/// <summary><b>The M14 half-rise deliverable: the half-rise reach (default ON).</b> When a square is past
	/// even the leaned arm, the terry rises off the chair and carries the PELVIS toward it — the
	/// same bone-override mechanism M14 proved on spine_2, applied one bone higher, bounded by the
	/// LEGS (planted feet, which may step) instead of by the chair. Feet stay planted via the
	/// engine's own foot IK targets, pre-compensated for the override (the animgraph solves before
	/// overrides apply). The whole geometry is <c>Code/Chess/HalfRise.cs</c>, harness-proven:
	/// 51/64 squares honestly reachable, worst corner ~8u (the piece-slide finishes those).
	/// <c>gambit_terry_rise</c>. Off = the M14 natural-lean world, seated ceiling ~rank 2.</summary>
	public static bool HalfRiseOn = true;

	/// <summary>Hard cap on how far the pelvis may travel, world units. The harness plans ~24u for
	/// the far rank; this only exists so a bad input can't fly the terry across the room.
	/// <c>gambit_terry_maxrise</c>.</summary>
	public static float MaxRise = 34f;

	/// <summary>How far a planted foot may slide toward the hips when the legs run out (a person
	/// shifts a foot to lean across a table). 0 = feet welded, rise shrinks instead.
	/// <c>gambit_terry_step</c>.</summary>
	public static float MaxStep = 12f;

	/// <summary>Chase rate for the pelvis/lean easing, 1/s — the rise is a motion, not a teleport,
	/// and the planner's output may step when the leg constraint engages. Lower = statelier.
	/// <c>gambit_terry_risechase</c>.</summary>
	public static float RiseChaseRate = 6f;

	/// <summary>Plant the OFF hand on the tabletop while risen (it tracks the body through the +Y
	/// side margin, and is skipped whenever the left arm couldn't honestly reach it).
	/// <c>gambit_terry_brace</c>.</summary>
	public static bool BraceOn = true;

	/// <summary>Override the MEASURED leg reach (pelvis→ankle budget), world units; 0 = use the
	/// live chain measurement. Exists because the first editor run showed the planner asking for
	/// far less rise than the harness plans — and a mis-measured leg (a twist/helper bone
	/// resolving where the real one should) collapses the leg triangle silently.
	/// <c>gambit_terry_leg</c>.</summary>
	public static float LegReachOverride;

	/// <summary>The closed-loop hand correction (default ON): measure the final hand's error
	/// against the true ask each frame and steer it out — the post-override native warp
	/// (~5u, procedural bones by elimination) can be measured but not modelled.
	/// <c>gambit_terry_servo</c>.</summary>
	public static bool ServoOn = true;

	/// <summary>How far the torso may TURN toward the piece, degrees (0 = off). Two-bone IK
	/// can never rotate the chest, so the turn is authored on the spine override — capped,
	/// eased in with the rise, and trued up by the servo. <c>gambit_terry_yaw</c>.</summary>
	public static float TorsoYawMax = 30f;

	/// <summary>How far the torso may PITCH over the table, degrees. <b>Default 0 — the
	/// editor measured the answer</b>: a bone-override ROTATION does not carry child bones
	/// (the pitch budgeted 15.8u of shoulder travel; ~3 materialised), so pitch buys no
	/// reach and the doctor's miss grew by exactly the phantom budget. Translations DO
	/// carry — every rise/lean lands to the decimal. The lever stays for cosmetic
	/// experiments only; reach never depends on it. <c>gambit_terry_pitch</c>.</summary>
	public static float TorsoPitchMax = 0f;

	/// <summary>How fast the hand chases its target while REPLAYING A COMMITTED MOVE, 1/s —
	/// the fast tempo. Was ChessRing.HandChaseRate, a code default on a runtime-built
	/// component. A committed move is a hard, snappy gesture: the hand catches up from wherever
	/// the lazy hover left it and completes the whole pickup-carry-drop quickly. Tuned via
	/// TerryTuning. See <see cref="HoverChaseRate"/> for the other tempo.</summary>
	public static float HandChaseRate = 8f;

	/// <summary>How fast the hand chases the CURSOR while hovering or with a piece merely
	/// selected, 1/s — the lazy tempo (banner jank #4: "two tempos, not one"). Deciding is not
	/// moving: the hand may drift, a-file to h-file taking a lazy couple of seconds. Much
	/// slower than <see cref="HandChaseRate"/> on purpose — a hover that snaps to the cursor
	/// reads as twitchy, and selecting a piece is NOT a committed move. Tuned via
	/// TerryTuning.</summary>
	public static float HoverChaseRate = 2.5f;

	// ── The carry (read by ChessBoardView; tuned via TerryTuning) ──

	/// <summary>How far below the wrist bone a held piece's base hangs.</summary>
	public static float CarryHang = 8f;

	/// <summary>How close the hand must get before a piece leaves its slide for the hand.</summary>
	public static float GrabRadius = 9f;

	/// <summary>How long a move-slide waits at its origin for the hand to come and grab it.</summary>
	public static float HandHoldSeconds = 1.2f;

	/// <summary>One-shot: log the ENTIRE half-rise pipeline for the next planned reach frame —
	/// planner inputs (live bones, measured chains), plan outputs, eased applied values, and
	/// each key bone's animation-vs-final position. This is how "the hand stops at rank 2" gets
	/// split into planner-under-asking vs bones-under-moving vs solver-missing, from one paste.
	/// <c>gambit_terry_rise_dbg</c>.</summary>
	public static bool RiseDebug;

	/// <summary><b>Approach A vs the old M13 hack — the A/B comparison lever.</b>
	/// false (default) = Approach A: a square outside <see cref="ReachBandX"/> idles the hand, no
	/// reach animated (the doc's "don't move the hand at all"). true = the cut M13 sphere clamp
	/// that pulled far targets onto a reach sphere — kept ONLY so you can flip between the two
	/// failure modes live and judge which reads better. <c>gambit_terry_clamp</c>.
	/// <para>Cleanup: if A is kept, delete the sphere-clamp branch in
	/// <see cref="LobbyPlayer.ApplyHandPose"/> and <see cref="ChessRing.HandReach"/> with it.</para></summary>
	public static bool UseSphereClamp;

	/// <summary><b>Approach A reach band.</b> Station-local |x| threshold: a square whose near-edge
	/// distance is at least this is "reachable" and gets an animated reach; nearer to centre idles.
	/// Rank geometry at BoardSize 26 / TableScale 1.5: rank1 = 17.06, rank2 = 12.19, rank3 = 7.31.
	/// Default 12 ≈ "ranks 1–2 (your own side)", the doc's honest envelope. Lower it to reach
	/// farther in (and watch the arm strain in <c>gambit_terry_probe</c>). <c>gambit_terry_band</c>.
	/// <para>Cleanup: if A is kept, fold the chosen value into <see cref="ChessRing.SquareReachable"/>
	/// as a constant.</para></summary>
	public static float ReachBandX = 12f;

	/// <summary><b>The real motion: a graded natural lean (default ON).</b> A seated player reaches
	/// a far piece by leaning in from the waist, not by growing the arm. When on, the hand leans
	/// the torso toward the target only as far as NEEDED (capped at <see cref="MaxLean"/>), reaches
	/// from the leaned position clamped to that envelope so the arm never straightens or drags, and
	/// lets ChessBoardView's piece-slide finish the last bit for the farthest squares. No
	/// distortion, no dead idle — this is what a person does. <c>gambit_terry_natural</c>.
	/// <para>Supersedes the isolated Approach-A idle and the M13 sphere clamp; those remain only as
	/// comparison levers below.</para></summary>
	public static bool NaturalLean = true;

	/// <summary>The most the terry leans in, world units of shoulder-forward travel. The
	/// LEAN-vs-RISE split is the "pelvis should go back" knob: every unit the spine shear
	/// carries is a unit the pelvis doesn't travel. 12 read as aggressive in the editor;
	/// 8 is the current taste. <c>gambit_terry_maxlean</c>.</summary>
	public static float MaxLean = 8f;

	/// <summary>Horizontal shortfall the hand may leave to the piece-slide before the body
	/// rises at all — the dead-band that keeps bottom-rank moves seated. "Getting out of
	/// the chair should only be for far ranks." <c>gambit_terry_grace</c>.</summary>
	public static float RiseGrace = 4f;

	/// <summary>Z the hips gain per unit of forward rise (0 = the old horizontal glide).
	/// Pushing UP off the chair while driving forward is what makes a deep reach read as
	/// standing into a lean. <c>gambit_terry_lift</c>.</summary>
	public static float RiseLift = 0.3f;

	/// <summary>Bone the natural lean translates forward. <c>spine_2</c> tips the shoulders over the
	/// board (its subtree carries the arm). If it reads as sliding rather than leaning, this is the
	/// knob to move (a lower spine bone hinges more from the waist).</summary>
	public static string NaturalLeanBone = "spine_2";

	/// <summary><b>Approach B — the SetBoneOverride fake-lean.</b> false = no lean. true = each
	/// frame, override <see cref="LeanBone"/> forward toward the board by <see cref="LeanForward"/>
	/// units before the hand IK solves. <c>gambit_terry_lean &lt;units&gt;</c> (0 turns it off).
	/// <para><b>This is the spike the doc says to run FIRST:</b> does the two-bone hand IK re-solve
	/// against the LEANED shoulder, or the animator's original one? Source can't say — the editor
	/// can. Lean, then run <c>gambit_terry</c>: if the shoulder line and the reach grid move
	/// forward, the lean composed and B is alive; if <c>hand_R</c> doesn't reach any farther, the
	/// override is a post-solve overwrite the IK never saw and B is dead — fall back to A.</para>
	/// <para>Cleanup: if B is kept, this becomes real per-frame code in
	/// <see cref="LobbyPlayer.ApplyReachSpikes"/> with the chosen bone/offset as constants.</para></summary>
	public static bool LeanOn;

	/// <summary>Which bone Approach B overrides. Default <c>spine_2</c> — a torso lean, the doc's
	/// cosmetic intent, but it depends on the arm subtree INHERITING a physics-bone override (the
	/// second unknown). <b>To test the compose question directly, set this to <c>arm_upper_R</c></b>
	/// (<c>gambit_terry_leanbone arm_upper_R</c>): that moves the IK root itself, so if reach still
	/// doesn't extend, the IK is definitively ignoring the override.</summary>
	public static string LeanBone = "spine_2";

	/// <summary>How far, in world units, Approach B shoves <see cref="LeanBone"/> toward the board
	/// each frame. Doc's estimate is ~10u of plausible lean → best-case reach to board centre
	/// (ranks 1–4ish); ranks 5–8 stay unreachable regardless. <c>gambit_terry_lean</c>.</summary>
	public static float LeanForward = 10f;

	/// <summary><b>Approach C — per-instance arm scale (best-effort probe).</b> 1 = neutral.
	/// Anything else overrides the arm bones' transform with a scaled one and re-measures.
	/// <c>gambit_terry_armscale &lt;k&gt;</c>.
	/// <para><b>Read the result with a very cold eye.</b> The engine has NO runtime bone-scale API;
	/// the <c>Transform.Scale</c> we set physically reaches the native <c>SetPhysicsBone</c> call,
	/// but whether the two-bone IK reads a scaled bone as a longer segment or renormalises it away
	/// is a native unknown (SEATED-HANDS-REACH.md, Approach C). So: set a scale, run
	/// <c>gambit_terry</c>, and check whether <c>hand_R</c> ACTUALLY reaches farther, not just
	/// whether the measured arm length grew. If it does nothing, C is declined WITH EVIDENCE — the
	/// realistic outcome the doc predicts, now proven rather than assumed.</para></summary>
	public static float ArmScale = 1f;

	/// <summary><b>Cross-cutting spike — the sit pose.</b> 1 = <c>sitting_01</c> (M13 shipped),
	/// 2 = <c>sitting_02</c>. The doc's open question M13 never answered: does <c>sitting_02</c>
	/// lean the shoulders over the table for free? It's in the binary clip, unreadable on the dev
	/// host — an editor look settles it. <c>gambit_terry_sit &lt;1|2&gt;</c>. If 2 leans meaningfully,
	/// it changes the reach math for A and B before either even runs; measure the shoulder x with
	/// <c>gambit_terry</c> under each.</summary>
	public static int SitPose = 1;

	/// <summary>Clamp <see cref="SitPose"/> into citizen.vanmgrph's real <c>sit</c> enum
	/// (0 not_sitting, 1 sitting_01, 2 sitting_02, 3 sitting_03) so a fat-fingered lever can't
	/// write a value the animgraph will silently ignore.</summary>
	public static int SitPoseClamped => SitPose < 1 ? 1 : SitPose > 3 ? 3 : SitPose;

	// ─────────────────────────────── The lever commands ───────────────────────────────

	[ConCmd( "gambit_terry_hands" )]
	public static void ToggleHands()
	{
		HandsOn = !HandsOn;
		if ( !HandsOn ) LobbyPlayer.Local?.ClearHandPose(); // drop any IK + bone override we left on
		Log.Info( $"[Gambit] seated hands {( HandsOn ? "ON" : "OFF (bodies only — the shipped world)" )}. "
			+ "Sit down; run gambit_terry to read the reach, gambit_terry_probe to sweep it." );
	}

	[ConCmd( "gambit_terry_rise" )]
	public static void ToggleRise()
	{
		HalfRiseOn = !HalfRiseOn;
		if ( !HalfRiseOn ) LobbyPlayer.Local?.ClearHandPose(); // drop pelvis override + foot/left IK
		Log.Info( HalfRiseOn
			? $"[Gambit] half-rise ON (default) — the terry rises off the chair to reach far squares; feet "
				+ $"planted (step up to {MaxStep}u), pelvis capped {MaxRise}u, brace {( BraceOn ? "on" : "off" )}."
			: "[Gambit] half-rise OFF — seated lean only (the M14 world, reach ceiling ~rank 2)." );
	}

	[ConCmd( "gambit_terry_maxrise" )]
	public static void SetMaxRise( float u )
	{
		MaxRise = u < 0f ? 0f : u;
		Log.Info( $"[Gambit] max pelvis rise = {MaxRise}u (the harness plans ~24 for the far rank)." );
	}

	[ConCmd( "gambit_terry_step" )]
	public static void SetMaxStep( float u )
	{
		MaxStep = u < 0f ? 0f : u;
		Log.Info( $"[Gambit] max foot step = {MaxStep}u (0 = feet welded; the rise shrinks instead)." );
	}

	[ConCmd( "gambit_terry_risechase" )]
	public static void SetRiseChase( float k )
	{
		RiseChaseRate = k <= 0f ? 6f : k;
		Log.Info( $"[Gambit] rise chase rate = {RiseChaseRate}/s (lower = statelier rise)." );
	}

	[ConCmd( "gambit_terry_brace" )]
	public static void ToggleBrace()
	{
		BraceOn = !BraceOn;
		if ( !BraceOn ) LobbyPlayer.Local?.ClearHandPose();
		Log.Info( $"[Gambit] table brace (off hand) {( BraceOn ? "ON" : "OFF" )}." );
	}

	/// <summary>Slack subtracted from the measured arm before planning (how far inside the reach
	/// sphere every ask sits). Bigger bends the elbow into every reach AND drags the body
	/// further over the table (the planner rises until the ask fits the shortened arm) — the
	/// margin-8 doctor verdict is what planked the terry over the board in the first
	/// screenshot. With the servo killing the solver's constant warp, small margins land
	/// clean; the doctor now prefers the smallest that does. <c>gambit_terry_margin</c>.
	///
	/// <para><b>Dropped 4 → 2.5 in the LOOK pass (M14).</b> The banner's #1 jank was a
	/// permanently bent elbow: every ask sat deep inside the reach sphere, so the two-bone IK
	/// never extended the arm. The servo trues the final hand regardless, so pushing the ask
	/// out toward the sphere edge (a longer honest arm) costs no accuracy and straightens the
	/// elbow. Slide it back up toward the old doctor verdict if a reach starts straining.</para></summary>
	public static float ReachMargin = 2.5f;

	[ConCmd( "gambit_terry_margin" )]
	public static void SetReachMargin( float u )
	{
		ReachMargin = u < 0f ? 0f : u;
		Log.Info( $"[Gambit] reach margin = {ReachMargin}u inside the measured arm. Bigger = bent-elbow asks the "
			+ "solver actually lands; smaller = longer reach the solver may undercut. Re-run gambit_terry_probe." );
	}

	/// <summary>Extra nose-down the HAND is pitched, on top of the forearm's own declination
	/// (banner jank #2, the "super kinked wrist"). The wrist rotation used to be a FIXED 60°
	/// nose-down + bearing yaw — but a far reach flattens the forearm toward horizontal, and a
	/// hand forced to keep pointing 60° down off a near-horizontal forearm hyper-flexes at the
	/// wrist. So the pitch now TRACKS the shoulder→hand bearing (≈ the forearm) and this knob
	/// is only the grasp curl added past it, capped at <c>ChessRing.HandPitch</c>. Setting it
	/// to the cap reproduces the old fixed-pitch behaviour exactly. <c>gambit_terry_wristdrop</c>.</summary>
	public static float WristDrop = 25f;

	[ConCmd( "gambit_terry_wristdrop" )]
	public static void SetWristDrop( float deg )
	{
		WristDrop = deg < 0f ? 0f : deg;
		Log.Info( $"[Gambit] wrist drop = {WristDrop}° of grasp curl past the forearm bearing "
			+ "(capped at ring.HandPitch). Lower = flatter hand on far reaches; = the cap restores the old fixed pitch." );
	}

	/// <summary>Roll (barrel-twist about the pointing axis) applied to the hand IK target —
	/// the lever that fixes the "t-rex arm" (banner jank, reported live). The citizen's
	/// two-bone hand IK is fed a full hand ROTATION, so the elbow pole follows it: with roll
	/// 0 the arm is trapped in a vertical plane and the elbow just drops (tucked, low). Rolling
	/// the hand swings the shoulder/elbow OUT away from the torso and tilts the wrist
	/// left/right instead of up/down — a person reaching across a table pronates like this.
	///
	/// <para><b>Sign is UNVERIFIED</b> (same footing as the finger-polarity constants): if the
	/// elbow swings INTO the body instead of out, negate this. Applied identically to both
	/// seats — the two hands point 180° apart, so one roll about each one's own forward axis
	/// abducts both symmetrically; if Black turns out mirrored, that's the next thing to try.
	/// <c>gambit_terry_roll</c>.</para></summary>
	public static float HandRoll = 45f;

	[ConCmd( "gambit_terry_roll" )]
	public static void SetHandRoll( float deg )
	{
		HandRoll = deg;
		Log.Info( $"[Gambit] hand roll = {HandRoll}° — swings the elbow out of the torso (t-rex fix). "
			+ "Negate if the elbow goes the WRONG way (into the body); 0 = the old vertical-plane arm." );
	}

	[ConCmd( "gambit_terry_leg" )]
	public static void SetLegReach( float u )
	{
		LegReachOverride = u < 0f ? 0f : u;
		Log.Info( LegReachOverride > 0f
			? $"[Gambit] leg reach OVERRIDDEN to {LegReachOverride}u (the planner's pelvis→ankle budget)."
			: "[Gambit] leg reach back to the live chain measurement (gambit_terry_rise_dbg prints it)." );
	}

	[ConCmd( "gambit_terry_pitch" )]
	public static void SetPitch( float degrees )
	{
		TorsoPitchMax = degrees < 0f ? 0f : degrees;
		if ( TorsoPitchMax <= 0f ) LobbyPlayer.Local?.ClearHandPose();
		Log.Info( TorsoPitchMax > 0f
			? $"[Gambit] torso pitch ON, capped {TorsoPitchMax}° over the table; hips capped at the table edge."
			: "[Gambit] torso pitch OFF — hips uncapped (the pre-pitch glide, for comparison)." );
	}

	[ConCmd( "gambit_terry_grace" )]
	public static void SetGrace( float u )
	{
		RiseGrace = u < 0f ? 0f : u;
		Log.Info( $"[Gambit] rise grace = {RiseGrace}u left to the slide before the body rises (bigger = lazier terry)." );
	}

	[ConCmd( "gambit_terry_lift" )]
	public static void SetLift( float perUnit )
	{
		RiseLift = perUnit < 0f ? 0f : perUnit;
		Log.Info( $"[Gambit] rise lift = {RiseLift:0.00} Z per unit forward (0 = the horizontal glide; ~0.3 pushes up into the lean)." );
	}

	[ConCmd( "gambit_terry_servo" )]
	public static void ToggleServo()
	{
		ServoOn = !ServoOn;
		if ( !ServoOn ) LobbyPlayer.Local?.ClearHandPose();
		Log.Info( $"[Gambit] hand servo {( ServoOn ? "ON" : "OFF" )} — the closed-loop correction for the ~5u post-override warp." );
	}

	[ConCmd( "gambit_terry_yaw" )]
	public static void SetYaw( float degrees )
	{
		TorsoYawMax = degrees < 0f ? 0f : degrees;
		Log.Info( TorsoYawMax > 0f
			? $"[Gambit] torso yaw ON, capped {TorsoYawMax}° toward the piece (eased in with the rise)."
			: "[Gambit] torso yaw OFF." );
	}

	[ConCmd( "gambit_terry_rise_dbg" )]
	public static void RiseDbg()
	{
		RiseDebug = true;
		Log.Info( "[Gambit] rise debug armed — reach at a FAR square (hover/select it, or run the probe) "
			+ "and the next planned frame dumps the whole pipeline: inputs → plan → applied → bones." );
	}

	[ConCmd( "gambit_terry_natural" )]
	public static void ToggleNatural()
	{
		NaturalLean = !NaturalLean;
		if ( !NaturalLean ) LobbyPlayer.Local?.ClearHandPose();
		Log.Info( NaturalLean
			? $"[Gambit] natural lean ON (default) — the terry leans in up to {MaxLean}u to reach, then the "
				+ "piece-slide covers the rest. gambit_terry_maxlean tunes it; gambit_terry_natlbone the bone."
			: "[Gambit] natural lean OFF — falls back to the isolated Approach-A idle / sphere-clamp levers." );
	}

	[ConCmd( "gambit_terry_maxlean" )]
	public static void SetMaxLean( float u )
	{
		MaxLean = u < 0f ? 0f : u;
		Log.Info( $"[Gambit] max natural lean = {MaxLean}u of shoulder-forward travel. "
			+ "~15 is a real lean over the board; higher reaches farther but starts to look like a dive." );
	}

	[ConCmd( "gambit_terry_natlbone" )]
	public static void SetNaturalBone( string bone )
	{
		NaturalLeanBone = string.IsNullOrWhiteSpace( bone ) ? "spine_2" : bone;
		LobbyPlayer.Local?.ClearHandPose();
		Log.Info( $"[Gambit] natural lean bone = '{NaturalLeanBone}'. spine_2 tips the shoulders; a lower spine "
			+ "bone (spine_1/spine_0) hinges more from the waist if it reads as sliding rather than leaning." );
	}

	[ConCmd( "gambit_terry_clamp" )]
	public static void ToggleClamp()
	{
		UseSphereClamp = !UseSphereClamp;
		Log.Info( UseSphereClamp
			? "[Gambit] out-of-reach mode = OLD M13 SPHERE CLAMP (far targets pulled onto a reach "
				+ "sphere — collapses the far ranks onto ~rank 2). This is the cut hack, here only to compare."
			: "[Gambit] out-of-reach mode = APPROACH A (a square past the reach band idles the hand; "
				+ $"ChessBoardView's piece-slide finishes far moves). Band |x| >= {ReachBandX}." );
	}

	[ConCmd( "gambit_terry_band" )]
	public static void SetBand( float x )
	{
		ReachBandX = x;
		Log.Info( $"[Gambit] Approach A reach band |x| >= {x} station-local. "
			+ "At BoardSize 26 / TableScale 1.5: rank1=17.06, rank2=12.19, rank3=7.31, rank4=2.44. "
			+ "Run gambit_terry_probe to see which squares the arm actually lands." );
	}

	[ConCmd( "gambit_terry_lean" )]
	public static void SetLean( float units )
	{
		LeanForward = units;
		LeanOn = units != 0f;
		if ( !LeanOn ) LobbyPlayer.Local?.ClearHandPose(); // wipe the bone override immediately
		if ( LeanOn && !HandsOn ) HandsOn = true;          // B needs the hand active to mean anything
		Log.Info( LeanOn
			? $"[Gambit] Approach B lean ON: {units}u forward on '{LeanBone}' each frame (hands forced ON). "
				+ "Run gambit_terry — did the shoulder line and reach grid move forward? That is whether the "
				+ "IK re-solved against the lean. If hand_R didn't reach farther, B is dead; try "
				+ "'gambit_terry_leanbone arm_upper_R' to test the IK root directly, else fall back to A."
			: "[Gambit] Approach B lean OFF." );
	}

	[ConCmd( "gambit_terry_leanbone" )]
	public static void SetLeanBone( string bone )
	{
		LeanBone = string.IsNullOrWhiteSpace( bone ) ? "spine_2" : bone;
		LobbyPlayer.Local?.ClearHandPose(); // drop the override on the old bone
		Log.Info( $"[Gambit] Approach B lean bone = '{LeanBone}'. "
			+ "spine_2 = torso lean (depends on the arm subtree inheriting the override); "
			+ "arm_upper_R = move the IK root itself (the direct compose test). "
			+ "Re-run gambit_terry_lean to re-apply." );
	}

	[ConCmd( "gambit_terry_armscale" )]
	public static void SetArmScale( float k )
	{
		ArmScale = k <= 0f ? 1f : k;
		if ( ArmScale == 1f ) LobbyPlayer.Local?.ClearHandPose();
		else if ( !HandsOn ) HandsOn = true;
		Log.Info( ArmScale == 1f
			? "[Gambit] Approach C arm scale = 1 (neutral)."
			: $"[Gambit] Approach C arm scale = {ArmScale} on arm_upper_R + arm_lower_R1 (hands forced ON). "
				+ "BEST-EFFORT — there is no runtime bone-scale API; this sets Transform.Scale on a bone "
				+ "override and hopes the native IK reads it. Run gambit_terry: did hand_R ACTUALLY reach "
				+ "farther, or did only the measured arm length grow? If nothing moved, C is declined with evidence." );
	}

	[ConCmd( "gambit_terry_sit" )]
	public static void SetSit( int pose )
	{
		SitPose = pose;
		Log.Info( $"[Gambit] sit pose = {SitPoseClamped} "
			+ $"({( SitPoseClamped == 1 ? "sitting_01, M13 shipped" : SitPoseClamped == 2 ? "sitting_02 — DOES IT LEAN?" : "sitting_0" + SitPoseClamped )}). "
			+ "Re-applied every frame on local + proxies. Run gambit_terry under 1 then 2 and compare the "
			+ "arm_upper_R / spine_2 x: if 2 sits the shoulders forward, that reach is free before A or B." );
	}

	/// <summary><b>The one-command knob turner.</b> Strains the hand at the far-rank centre under
	/// each candidate reach margin, measures planned/applied rise, shoulder travel and the miss,
	/// dumps the full pipeline once, prints ONE verdict table and APPLIES the winner. Run this
	/// instead of turning gambit_terry_margin/_leg by hand.</summary>
	[ConCmd( "gambit_terry_doctor" )]
	public static void DoctorCmd()
	{
		if ( ChessStation.Active == null )
		{
			Log.Warning( "[Gambit] doctor: sit down first — it drives YOUR seated hand, and nobody is seated." );
			return;
		}
		SeatedTerry.Doctor = !SeatedTerry.Doctor;
		Log.Info( SeatedTerry.Doctor
			? "[Gambit] doctor ON — ~6s of automated reach trials; one verdict table lands at the end. Paste it back."
			: "[Gambit] doctor cancelled." );
	}

	/// <summary><b>The one-paste command.</b> Runs every QUANTITATIVE spike in turn — baseline,
	/// sit=2, lean(spine_2), lean(arm_upper_R), armscale — settling the skeleton between each and
	/// dumping ONE table with the verdicts. Sit down and run it; ~7s, hold still. The only spike it
	/// can't score is Approach A's read-as-playing taste call, which is inherently a look
	/// (<c>gambit_terry_hands</c> then watch). Restores every lever afterwards.</summary>
	[ConCmd( "gambit_terry_sweep" )]
	public static void Sweep()
	{
		if ( ChessStation.Active == null )
		{
			Log.Warning( "[Gambit] sweep: sit down first — it drives YOUR seated hand, and nobody is seated." );
			return;
		}
		SeatedTerry.Sweep = !SeatedTerry.Sweep;
		Log.Info( SeatedTerry.Sweep
			? "[Gambit] sweep ON — measuring reach under every spike config (~7s). One table lands at the end; paste it back."
			: "[Gambit] sweep cancelled." );
	}

	/// <summary>The playbook: the whole M14 spike plan, the live lever state, and which lever to
	/// pull for which reading — in one place so a session in the editor never has to reconstruct
	/// it from the doc.</summary>
	[ConCmd( "gambit_terry_spikes" )]
	public static void Playbook()
	{
		Log.Info( "── M14 half-rise hands — levers & playbook ──" );
		Log.Info( $"   HandsOn={HandsOn}  HalfRise={( HalfRiseOn ? $"ON (maxrise {MaxRise}, step {MaxStep}, chase {RiseChaseRate}, brace {( BraceOn ? "on" : "off" )})" : "OFF" )}  "
			+ $"SitPose={SitPoseClamped}  Lean={( NaturalLean ? $"{MaxLean}u/'{NaturalLeanBone}'" : "off" )}" );
		Log.Info( $"   Comparison levers — OutOfReach(rise+nat off)={( UseSphereClamp ? "sphere clamp" : $"idle band {ReachBandX}" )}  "
			+ $"ManualLean(B)={( LeanOn ? $"{LeanForward}u/{LeanBone}" : "off" )}  ArmScale(C)={ArmScale}" );
		// LIVE tempo + posture knobs — if these lines are here at all, the look-pass build is loaded.
		float spd = TerryPose.SpeedScale <= 0f ? 1f : TerryPose.SpeedScale;
		Log.Info( $"   Posture — reachMargin {ReachMargin}u  wristDrop {WristDrop}°  handRoll {HandRoll}° "
			+ "(gambit_terry_margin / _wristdrop / _roll)" );
		Log.Info( $"   Tempo — gestures run on PHASE DEADLINES (reach {TerryPose.ReachTime}s + lift {TerryPose.LiftTime}s"
			+ $" + carry {TerryPose.TravelTime}s + drop {TerryPose.DropTime}s; hand arrives per stage or snaps)  "
			+ $"idle-return chase {HoverChaseRate}/s  gestureSpeed {TerryPose.SpeedScale}× → move≈{TerryPose.MoveTime / spd:0.00}s"
			+ $" (capture = same gesture; victim slides to its tray on its own)" );
		Log.Info( "   The DEFAULT is the half-rise: past the leaned arm the terry rises off the chair toward the piece, "
			+ "feet planted (they may step), off hand braced on the table, and PICKS THE PIECE UP (it rides the hand)." );
		Log.Info( "── run it ──" );
		Log.Info( "   1. sit down, start a game → far moves should lift the terry off the chair; the piece rides the hand." );
		Log.Info( "   2. gambit_terry_sweep → the verdict table: baseline / lean / half-rise / +sit2 at the far rank (~6s)." );
		Log.Info( "   3. gambit_terry_probe → all-64 grid; the harness predicts ok everywhere but ~rank 7-8 edges (≤8u)." );
		Log.Info( "── the engine unknowns this session must answer ──" );
		Log.Info( "   a. Does the pelvis override carry the LEG chains as spine_2 carried the arm's? (sweep verdict)" );
		Log.Info( "   b. Do the pre-compensated foot pins keep the feet still through a rise? (look at the feet)" );
		Log.Info( "   c. Does the rise READ as a person leaning over the table? (the taste call, as ever)" );
		Log.Info( "   Kill chain: ChessRing.TerrySeated → gambit_terry_hands → gambit_terry_rise. Doc: TERRY-HALFRISE.md." );
	}
}
