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

	/// <summary><b>Master gate for the hands (A &amp; B).</b> false = bodies only, no hand IK —
	/// the shipped M13 world. <c>gambit_terry_hands</c>. Turn on to watch the hands play through a
	/// real or local game at your table.</summary>
	public static bool HandsOn;

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

	/// <summary>The most the terry leans in, world units of shoulder-forward travel. ~15 is a real
	/// lean over the board without lunging flat. Higher reaches farther but starts to look like a
	/// dive; lower keeps it subtle and leans more on the piece-slide. <c>gambit_terry_maxlean</c>.</summary>
	public static float MaxLean = 15f;

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
		Log.Info( "── M14 seated hands — levers & playbook ──" );
		Log.Info( $"   HandsOn={HandsOn}  SitPose={SitPoseClamped}  "
			+ $"NaturalLean={( NaturalLean ? $"ON (max {MaxLean}u on '{NaturalLeanBone}')" : "off" )}" );
		Log.Info( $"   Comparison levers — OutOfReach(nat off)={( UseSphereClamp ? "sphere clamp" : $"idle band {ReachBandX}" )}  "
			+ $"ManualLean(B)={( LeanOn ? $"{LeanForward}u/{LeanBone}" : "off" )}  ArmScale(C)={ArmScale}" );
		Log.Info( "   The DEFAULT is the natural graded lean: the terry leans in as far as needed to reach a piece, "
			+ "and the piece-slide finishes the farthest squares. No arm-stretch, no dead idle." );
		Log.Info( "── run it ──" );
		Log.Info( "   1. sit down, gambit_terry_hands, start a game → watch your hand reach & lean over the board." );
		Log.Info( "   2. gambit_terry_sweep → one table: how far baseline / natural lean / sit=2 / both reach (~5s)." );
		Log.Info( "   Tune: gambit_terry_maxlean <u> (how far it leans), gambit_terry_natlbone <bone> (waist vs shoulders), "
			+ "gambit_terry_sit 2 (add the free pose lean)." );
		Log.Info( "── the question that decides it ──" );
		Log.Info( "   Does the lean-and-reach read as a PERSON playing chess? If yes → ship natural lean (+maybe sit=2)." );
		Log.Info( "   Comparison-only levers (off by default): gambit_terry_natural off, then gambit_terry_clamp / "
			+ "gambit_terry_lean / gambit_terry_armscale to see the isolated hacks. Full plan: SEATED-HANDS-REACH.md." );
	}
}
