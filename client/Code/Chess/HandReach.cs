namespace Gambit.Chess;

/// <summary>
/// A tiny 3-vector for the reach planner. <b>Not <c>Sandbox.Vector3</c>, deliberately</b> —
/// the moment that type appears here none of this can run in the dotnet harness on the dev
/// host, and the harness is the only place the reach geometry can be PROVEN (nothing on that
/// host renders). Same doctrine as <see cref="MoveGesture"/>'s "no engine type" rule; this
/// file needs real vector math, so it carries its own.
/// </summary>
public readonly record struct R3( float X, float Y, float Z )
{
	public static readonly R3 Zero = new( 0f, 0f, 0f );

	public static R3 operator +( R3 a, R3 b ) => new( a.X + b.X, a.Y + b.Y, a.Z + b.Z );
	public static R3 operator -( R3 a, R3 b ) => new( a.X - b.X, a.Y - b.Y, a.Z - b.Z );
	public static R3 operator *( R3 a, float s ) => new( a.X * s, a.Y * s, a.Z * s );

	public float Length => Sqrt( X * X + Y * Y + Z * Z );
	public float LengthXY => Sqrt( X * X + Y * Y );

	/// <summary>Unit vector, or Zero when degenerate — a planner must never divide by a
	/// zero-length reach and NaN a bone override (a NaN override doesn't crash; it teleports
	/// the skeleton to nowhere and reads as the model vanishing).</summary>
	public R3 Normal
	{
		get { float l = Length; return l < 1e-4f ? Zero : new R3( X / l, Y / l, Z / l ); }
	}

	/// <summary>The horizontal (XY) part as a unit vector — how lean and rise directions are
	/// derived, so a target above the shoulder never tilts the lean upward.</summary>
	public R3 HorizontalNormal
	{
		get { float l = LengthXY; return l < 1e-4f ? Zero : new R3( X / l, Y / l, 0f ); }
	}

	/// <summary>Mirror through the station centre (negate X and Y — a 180° rotation about Z,
	/// NOT a reflection, so chirality survives and Black's right hand stays its right hand).
	/// The planner thinks only in White's frame; the caller rotates Black's inputs in and its
	/// outputs back out through this.</summary>
	public R3 Mirrored => new( -X, -Y, Z );

	// Hand-rolled Sqrt — no System.MathF dependence questions in the harness, and Sqrt is the
	// only transcendental this file needs.
	static float Sqrt( float v )
	{
		if ( v <= 0f ) return 0f;
		double x = v, r = x;
		for ( int i = 0; i < 24; i++ ) r = 0.5 * ( r + x / r );
		return (float)r;
	}
}

/// <summary>The measured seated skeleton and the reach knobs, world units, White's frame.
/// Defaults are order-of-magnitude placeholders; the runtime feeds LIVE bone reads
/// (shoulder, pelvis, feet, measured arm length) over them wherever it can — fact #9: the
/// only thing this host can prove is the MATH, not the numbers, so the numbers come from the
/// editor's <c>gambit_terry</c> dump and these are just what a scene-less harness falls back
/// to.</summary>
/// <param name="Reach">Honest arm reach: measured arm length minus a grip margin.</param>
/// <param name="MaxLean">Spine-lean cap (the seated torso pivot before the pelvis moves).</param>
/// <param name="RiseGrace">Horizontal shortfall the hand may LEAVE to the piece rather than
/// rise for — the dead-band that keeps near/bottom-rank moves fully seated.</param>
/// <param name="LegReach">Pelvis → ankle minus a margin — how far the hips may stray from a
/// planted foot before a foot must step.</param>
/// <param name="MaxStep">How far a planted foot may slide toward the hips when the legs run out.</param>
/// <param name="MaxRise">Hard cap on |pelvis delta| — past this a reach reads as flight.</param>
/// <param name="RiseLift">Z gained per unit of forward rise: the hips push UP off the chair
/// as they drive forward, so a deep reach reads as standing into a lean, not gliding.</param>
/// <param name="HipMaxX">The hips may never pass this X (the near board edge stops a real
/// body). White-frame: the board is at +X of the seat, so this is a MAX on the pelvis X.</param>
/// <param name="FootMaxX">Feet may never step past this X toward the board (the table's foot
/// plate/pedestal starts there). White-frame: a MAX, same reason as <paramref name="HipMaxX"/>.</param>
public readonly record struct ReachTunables(
	float Reach, float MaxLean, float RiseGrace, float LegReach, float MaxStep,
	float MaxRise, float RiseLift, float HipMaxX, float FootMaxX )
{
	public static readonly ReachTunables Default = new(
		Reach: 18f, MaxLean: 8f, RiseGrace: 3f, LegReach: 30f, MaxStep: 16f,
		MaxRise: 40f, RiseLift: 0.3f, HipMaxX: -19.75f, FootMaxX: -16f );
}

/// <summary>What one frame of reaching should do to the seated skeleton — all White-frame,
/// station-local; the caller mirrors for Black and converts to world.</summary>
/// <param name="Lean">Horizontal distance the spine leans toward the target.</param>
/// <param name="LeanDir">Unit horizontal direction of the lean (Zero when Lean is 0).</param>
/// <param name="PelvisDelta">Translation to override onto the pelvis — the half-rise itself.</param>
/// <param name="FootL">Where the left foot should be planted (may have stepped).</param>
/// <param name="FootR">Where the right foot should be planted (may have stepped).</param>
/// <param name="Stepped">A foot had to slide — the legs ran out before the reach did.</param>
/// <param name="Hand">The clamped hand target: on the reach sphere of the RISEN shoulder, so
/// the arm never strains or drags. Equals the true target whenever it is honestly reachable.</param>
/// <param name="Residual">Units the clamped hand still falls short of the true target — 0 for
/// a genuine reach; the piece's own slide covers whatever this reports (the hand trails).</param>
/// <param name="Rise01">0..1 how deep into the half-rise this frame is (drives blends/sound).</param>
public readonly record struct ReachPlan(
	float Lean, R3 LeanDir, R3 PelvisDelta, R3 FootL, R3 FootR, bool Stepped,
	R3 Hand, float Residual, float Rise01 );

/// <summary>
/// The move-only half-rise reach planner (M14 Phase 2). Pure geometry, Sandbox-free, driven
/// over all 64 squares from both seats in the dotnet harness — which is the difference
/// between this and the two attempts it replaces.
///
/// <para><b>The doctrine, kept from the attempts (re-derived, not restored):</b> the seated
/// arm is ~20u against a board whose far corner is ~35u away, and the ONLY thing that extends
/// reach is moving the SHOULDER. A seated lean buys ~8u; past that the PELVIS carries the
/// shoulder forward (the half-rise), bounded by the LEGS (planted feet that may step) instead
/// of by the chair. The rise is mostly HORIZONTAL — the leg triangle is the scarce resource
/// and every unit of altitude is bought from it at full price, while the arm covers grasp
/// height above the shoulder for free inside its own sphere. A person leaning across a table
/// does exactly this: hips low and forward, the reach goes up at the end.</para>
///
/// <para><b>MOVE-ONLY.</b> This planner is called only while a move is being executed
/// (<see cref="MoveGesture.Phase.Approach"/>/<see cref="MoveGesture.Phase.Carry"/>), never on
/// hover — the runtime gates it. Attempt 2's fatal tic was the body heaving up on every
/// HOVERED far square; the gate is not this file's job, but this file exists to be called only
/// then, and the <see cref="ReachPlan.Rise01"/> it returns is what the runtime eases in and
/// out so the rise tracks the gesture, not the cursor.</para>
///
/// <para><b>The contract with the runtime:</b> everything returned is station-local
/// White-frame. The runtime mirrors for Black (<see cref="R3.Mirrored"/>), applies
/// <see cref="ReachPlan.PelvisDelta"/> and the lean as bone-override TRANSLATIONS (which carry
/// the whole subtree exactly — measured), and aims the animgraph IK at each target MINUS the
/// override translation that will carry that limb's chain (the animgraph solves IK BEFORE
/// overrides apply, so a pre-compensated target lands on the true one after the translate). A
/// residual ~5u native warp survives everything; the runtime steers it out with a servo.</para>
/// </summary>
public static class HandReach
{
	public static ReachPlan Plan( R3 target, R3 shoulder, R3 pelvis, R3 footL, R3 footR,
		in ReachTunables t )
	{
		// ── 1. Seated reach: no deficit, no theatre. The arm already reaches — hand on the
		// true target, nothing moves. Most near moves land here.
		float deficit = ( target - shoulder ).Length - t.Reach;
		if ( deficit <= 0f )
			return new ReachPlan( 0f, R3.Zero, R3.Zero, footL, footR, false, target, 0f, 0f );

		// ── 2. The seated lean: spine toward the target, horizontal only (a lean is a torso
		// pivot, not a levitation), along the true bearing so a corner reach leans INTO it.
		var leanDir = ( target - shoulder ).HorizontalNormal;
		float lean = Min( deficit, t.MaxLean );
		var shoulderLeaned = shoulder + leanDir * lean;

		float deficit2 = ( target - shoulderLeaned ).Length - t.Reach;
		if ( deficit2 <= 0f )
			return new ReachPlan( lean, leanDir, R3.Zero, footL, footR, false, target, 0f, 0f );

		// ── 3. The half-rise: carry the pelvis (and the shoulder on it) toward the target
		// until the arm honestly reaches. HORIZONTAL need, computed at the un-lifted shoulder
		// (lifting toward grasp height only ever helps, so this is conservative).
		float dz = target.Z - shoulderLeaned.Z;
		float horizReachSq = t.Reach * t.Reach - dz * dz;
		if ( horizReachSq <= 1f ) horizReachSq = 1f;   // target absurdly high/low: get under it
		float horizReach = Sqrt( horizReachSq );

		var toTarget = target - shoulderLeaned;
		float horizDist = toTarget.LengthXY;
		var dir = toTarget.HorizontalNormal;
		// The grace: the piece's own slide may carry the last few units, so the body rises
		// only for what the hand GENUINELY can't have — bottom ranks stay seated.
		float horizNeed = Max( horizDist - horizReach - t.RiseGrace, 0f );

		// Capped by MaxRise and by the table edge the hips can't cross.
		float rise = Min( horizNeed, t.MaxRise );
		if ( dir.X > 0.01f )
		{
			float hipRoom = Max( ( t.HipMaxX - pelvis.X ) / dir.X, 0f );
			rise = Min( rise, hipRoom );
		}

		// The lift: hips gain a little Z as they drive forward — reads as pushing up off the
		// chair into a lean, not a body gliding over the table.
		var delta = dir * rise + new R3( 0f, 0f, rise * t.RiseLift );

		// ── 4. The legs are the boundary. Feet stay planted; when a hip strays past LegReach
		// of its foot, the foot STEPS toward the hips; when even a full step can't cover it,
		// the RISE shrinks (never the leg constraint). A DESCENDING scan finds the largest
		// workable rise — the foot-plate clamp makes feasibility non-monotone near the corner,
		// so bisection would land on an arbitrary branch and pop the pelvis.
		var hips = pelvis + delta;
		var fl = StepFoot( footL, hips, t );
		var fr = StepFoot( footR, hips, t );
		bool stepped = ( fl - footL ).Length > 0.01f || ( fr - footR ).Length > 0.01f;

		if ( ( hips - fl ).Length > t.LegReach + 0.01f || ( hips - fr ).Length > t.LegReach + 0.01f )
		{
			float best = 0f;
			for ( int i = 24; i >= 0; i-- )
			{
				float mid = rise * i / 24f;
				var h = pelvis + dir * mid + new R3( 0f, 0f, mid * t.RiseLift );
				var sl = StepFoot( footL, h, t );
				var sr = StepFoot( footR, h, t );
				if ( ( h - sl ).Length <= t.LegReach + 0.01f && ( h - sr ).Length <= t.LegReach + 0.01f )
				{ best = mid; break; }
			}
			rise = best;
			delta = dir * rise + new R3( 0f, 0f, rise * t.RiseLift );
			hips = pelvis + delta;
			fl = StepFoot( footL, hips, t );
			fr = StepFoot( footR, hips, t );
			stepped = ( fl - footL ).Length > 0.01f || ( fr - footR ).Length > 0.01f;
		}

		// ── 5. Clamp the hand onto the reach sphere of the ACTUAL risen+leaned shoulder, and
		// report the residual. Equal to the true target when the reach is honest; short (and
		// the piece slide finishes the trip) when it isn't. This is design point 6: a hand
		// reaching AFTER a piece reads fine; only a failed GRAB ever read as broken, and the
		// clock+piece model means there is no grab to fail.
		var shoulderFinal = shoulder + leanDir * lean + delta;
		var reachVec = target - shoulderFinal;
		float reachLen = reachVec.Length;
		R3 hand; float residual;
		if ( reachLen <= t.Reach )
		{
			hand = target;
			residual = 0f;
		}
		else
		{
			hand = shoulderFinal + reachVec.Normal * t.Reach;
			residual = reachLen - t.Reach;
		}

		float rise01 = t.MaxRise > 1e-4f ? Clamp01( rise / t.MaxRise ) : 0f;
		return new ReachPlan( lean, leanDir, delta, fl, fr, stepped, hand, residual, rise01 );
	}

	/// <summary>Slide a planted foot toward the hips when they stray past the leg's reach —
	/// but never past the table's foot plate (<see cref="ReachTunables.FootMaxX"/>) and never
	/// more than one <see cref="ReachTunables.MaxStep"/>. The foot only ever moves toward the
	/// hips, so a reach that pulls back releases the foot's need but not its position (the
	/// runtime eases it home separately). White-frame: the hips are at +X of the seated feet,
	/// so the step is toward +X and the plate is an UPPER bound on the stepped X.</summary>
	static R3 StepFoot( R3 foot, R3 hips, in ReachTunables t )
	{
		var toFoot = foot - hips;
		float d = toFoot.Length;
		if ( d <= t.LegReach ) return foot;                 // still in reach: don't move

		// Bring the foot to the edge of the leg's reach along the hips→foot line, clamped to
		// a single step and to the foot plate.
		float pull = Min( d - t.LegReach, t.MaxStep );
		var stepped = foot - toFoot.Normal * pull;
		if ( stepped.X > t.FootMaxX )
			stepped = new R3( t.FootMaxX, stepped.Y, stepped.Z );
		return stepped;
	}

	static float Min( float a, float b ) => a < b ? a : b;
	static float Max( float a, float b ) => a > b ? a : b;
	static float Clamp01( float v ) => v < 0f ? 0f : v > 1f ? 1f : v;
	static float Sqrt( float v )
	{
		if ( v <= 0f ) return 0f;
		double x = v, r = x;
		for ( int i = 0; i < 24; i++ ) r = 0.5 * ( r + x / r );
		return (float)r;
	}
}
