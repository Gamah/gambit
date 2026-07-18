namespace Gambit.Chess;

/// <summary>
/// A tiny 3-vector for the reach planner. <b>Not Sandbox.Vector3, deliberately</b> — the
/// moment that type appears here, none of this can run in the dotnet harness on the dev
/// host, and the harness is the only place the reach geometry can be PROVEN (nothing on
/// that host renders). Same doctrine as <see cref="TerryPose"/>'s "no Vector3" rule; this
/// file needs real vector math, so it carries its own.
/// </summary>
public readonly record struct V3( float X, float Y, float Z )
{
	public static readonly V3 Zero = new( 0f, 0f, 0f );

	public static V3 operator +( V3 a, V3 b ) => new( a.X + b.X, a.Y + b.Y, a.Z + b.Z );
	public static V3 operator -( V3 a, V3 b ) => new( a.X - b.X, a.Y - b.Y, a.Z - b.Z );
	public static V3 operator *( V3 a, float s ) => new( a.X * s, a.Y * s, a.Z * s );

	public float Length => Sqrt( X * X + Y * Y + Z * Z );
	public float LengthXY => Sqrt( X * X + Y * Y );

	/// <summary>Unit vector, or Zero when degenerate — a planner must never divide by a
	/// zero-length reach and NaN a bone override (a NaN override doesn't crash, it
	/// teleports the skeleton to nowhere and reads as the model vanishing).</summary>
	public V3 Normal
	{
		get
		{
			float l = Length;
			return l < 1e-4f ? Zero : new V3( X / l, Y / l, Z / l );
		}
	}

	/// <summary>The horizontal (XY) part, as a unit vector — how lean and rise directions
	/// are derived, so a target above the shoulder never tilts the lean upward.</summary>
	public V3 HorizontalNormal
	{
		get
		{
			float l = LengthXY;
			return l < 1e-4f ? Zero : new V3( X / l, Y / l, 0f );
		}
	}

	/// <summary>Mirror through the station centre (negate X and Y — a 180° rotation about
	/// Z, NOT a reflection, so chirality survives and Black's right hand stays its right
	/// hand). This is the whole seat-mirroring story: the planner thinks only in White's
	/// frame, and callers rotate Black's inputs in and outputs back out through this.</summary>
	public V3 Mirrored => new( -X, -Y, Z );

	// Hand-rolled: no System.MathF dependence questions in the harness, and Sqrt is the
	// only transcendental this file needs.
	static float Sqrt( float v )
	{
		if ( v <= 0f ) return 0f;
		double x = v;
		double r = x;
		for ( int i = 0; i < 24; i++ ) r = 0.5 * ( r + x / r );
		return (float)r;
	}
}

/// <summary>The measured skeleton and the tuning knobs, in world units, White's frame.
/// Every default here is a MEASURED number (gambit_terry, SeatSitBack=26) or a knob the
/// editor retunes live — the runtime feeds live bone reads over these wherever it can.</summary>
public readonly record struct RiseTunables(
	float Reach,          // honest arm reach: measured arm length minus a grip margin
	float MaxLean,        // spine_2 translate cap (M14's proven natural lean)
	float LegReach,       // pelvis → ankle, minus a margin — how far the hips may stray from a planted foot
	float MaxStep,        // how far a planted foot may slide toward the hips when the legs run out
	float MaxRise,        // hard cap on |pelvis delta| — past this a hover reads as flight
	float FootMinX,       // feet may never step past this X: the table's foot plate starts there
	float BraceEngage,    // pelvis forward travel at which the off hand plants on the table
	float BraceMinX,      // brace X window: the brace tracks the risen pelvis, clamped to the
	float BraceMaxX,      //   tabletop's own extent so it never hangs off either edge
	float BraceY,         // brace lands in the player's LEFT side margin (White frame: +Y)
	float BraceZ )        // ...on the tabletop surface
{
	public static readonly RiseTunables Default = new(
		Reach: 18f, MaxLean: 6f, LegReach: 30f, MaxStep: 12f, MaxRise: 34f,
		FootMinX: -16f,
		BraceEngage: 6f, BraceMinX: -24f, BraceMaxX: 10f, BraceY: 24f, BraceZ: 30f );
}

/// <summary>What one frame of reaching should do to the skeleton — all White-frame,
/// station-local; the caller mirrors for Black and converts to world.</summary>
/// <param name="Lean">How far spine_2 translates toward the target (horizontal), world units.</param>
/// <param name="LeanDir">Unit horizontal direction of that lean (Zero when Lean is 0).</param>
/// <param name="PelvisDelta">Translation to override onto the pelvis — the half-rise itself.</param>
/// <param name="FootL">Where the left foot should be planted (may have stepped).</param>
/// <param name="FootR">Where the right foot should be planted (may have stepped).</param>
/// <param name="Stepped">A foot had to slide — the legs ran out before the reach did.</param>
/// <param name="Brace">Tabletop point for the off hand, or null while seated low.</param>
/// <param name="Hand">The clamped hand target: on the reach sphere of the RISEN shoulder, so
/// the arm never strains or drags. Equal to the true target whenever it is honestly reachable.</param>
/// <param name="Residual">Units the clamped hand still falls short of the true target —
/// 0 for a genuine reach; the piece-slide fallback covers whatever this reports.</param>
/// <param name="Rise01">0..1 how deep into the half-rise this frame is (drives blends).</param>
public readonly record struct RisePlan(
	float Lean, V3 LeanDir, V3 PelvisDelta, V3 FootL, V3 FootR, bool Stepped,
	V3? Brace, V3 Hand, float Residual, float Rise01 );

/// <summary>
/// The half-rise reach planner (M14, the IK half-rise attempt). Pure geometry, Sandbox-free, PROVEN in the dotnet
/// harness against all 64 squares from both seats — which is the difference between this
/// and the two attempts it replaces.
///
/// <para><b>Why this works where M13/M14 could not.</b> The seated arm is ~20u against a
/// board whose far corner is ~35u away, and M14's in-editor sweeps proved the only thing
/// that extends reach is MOVING THE SHOULDER — then capped the shoulder's travel at a
/// seated torso's lean (~6u), because the pelvis was pinned to the chair. The half-rise
/// unpins it: the same bone-override mechanism that leaned spine_2 lifts the PELVIS up and
/// over the board, bounded by the legs (which stay planted, and may step) instead of by the
/// chair. The shoulder rides the pelvis, and the far half of the board comes inside the arm.</para>
///
/// <para><b>The contract with the runtime</b>: everything returned is a station-local
/// White-frame quantity. The runtime mirrors for Black (<see cref="V3.Mirrored"/>), applies
/// <see cref="RisePlan.PelvisDelta"/> and the lean as bone overrides, and aims the animgraph
/// IK at each true target MINUS the override translation that will carry that limb's chain —
/// the animgraph solves before overrides apply, so a pre-compensated target lands exactly on
/// the true one after the translate. Feet ride the pelvis only; hands ride pelvis + lean.</para>
/// </summary>
public static class HalfRise
{
	public static RisePlan Plan( V3 target, V3 shoulder, V3 pelvis, V3 footL, V3 footR,
		in RiseTunables t )
	{
		// ── 1. Seated reach: no deficit, no theatre ──
		float deficit = ( target - shoulder ).Length - t.Reach;
		if ( deficit <= 0f )
			return new RisePlan( 0f, V3.Zero, V3.Zero, footL, footR, false, null,
				target, 0f, 0f );

		// ── 2. The proven M14 lean first: spine_2 toward the target, capped ──
		// Horizontal only — a lean is a torso pivot, not a levitation — and along the true
		// bearing rather than straight ahead, so a corner reach leans INTO the corner.
		var leanDir = ( target - shoulder ).HorizontalNormal;
		float lean = Min( deficit, t.MaxLean );
		var shoulderLeaned = shoulder + leanDir * lean;

		float deficit2 = ( target - shoulderLeaned ).Length - t.Reach;
		if ( deficit2 <= 0f )
			return new RisePlan( lean, leanDir, V3.Zero, footL, footR, false, null,
				target, 0f, 0f );

		// ── 3. The half-rise: carry the pelvis (and the shoulder with it) toward the
		// target until the arm honestly reaches.
		//
		// HORIZONTAL, not toward the target in 3D — and the difference is 20 squares. The
		// legs are the scarce resource (~30u of pelvis-to-ankle against feet that may not
		// pass the table's foot plate), and every unit of altitude the pelvis gains is
		// bought from that budget at full price — while the ARM can cover the grasp
		// height's ~7.5u above the shoulder for free inside its own sphere. The first cut
		// rose along the 3D bearing and the far rank died at ~13u short on leg length; kept
		// low it comes inside the arm. A person leaning across a table does exactly this:
		// hips stay low and drive forward, the reach goes up at the end. ──
		float dz = target.Z - shoulderLeaned.Z;
		float horizReachSq = t.Reach * t.Reach - dz * dz;
		if ( horizReachSq <= 1f )
			horizReachSq = 1f; // target absurdly high/low: degrade to "get almost under it"
		float horizReach = Sqrt( horizReachSq );

		var toTarget = target - shoulderLeaned;
		float horizDist = toTarget.LengthXY;
		var dir = toTarget.HorizontalNormal;
		float rise = Min( Max( horizDist - horizReach, 0f ), t.MaxRise );
		var delta = dir * rise;

		// ── 4. The legs are the new boundary. Feet stay planted; when a hip strays past
		// LegReach of its foot, the foot STEPS toward the hips (people shift a foot to
		// lean across a table); when even a full step can't cover it, the RISE shrinks —
		// never the leg constraint. The scale-back searches the largest workable rise so
		// the failure mode is "a little short, slide finishes it", not a snapped leg. ──
		var hips = pelvis + delta;
		var fl = StepFoot( footL, hips, t );
		var fr = StepFoot( footR, hips, t );
		bool stepped = ( fl - footL ).Length > 0.01f || ( fr - footR ).Length > 0.01f;

		if ( ( hips - fl ).Length > t.LegReach || ( hips - fr ).Length > t.LegReach )
		{
			float lo = 0f, hi = rise;
			for ( int i = 0; i < 20; i++ )
			{
				float mid = ( lo + hi ) * 0.5f;
				var h = pelvis + dir * mid;
				var sl = StepFoot( footL, h, t );
				var sr = StepFoot( footR, h, t );
				if ( ( h - sl ).Length <= t.LegReach && ( h - sr ).Length <= t.LegReach )
					lo = mid;
				else
					hi = mid;
			}
			rise = lo;
			delta = dir * rise;
			hips = pelvis + delta;
			fl = StepFoot( footL, hips, t );
			fr = StepFoot( footR, hips, t );
			stepped = true;
		}

		// ── 5. Clamp the hand to the RISEN envelope, exactly as the M14 lean clamped to
		// the leaned one: the arm must never strain (straighten + drag) at what it cannot
		// have. Whatever is left after the rise is the slide's job, reported honestly. ──
		var shoulderRisen = shoulderLeaned + delta;
		var reachOut = target - shoulderRisen;
		float over = reachOut.Length - t.Reach;
		var hand = over > 0f ? shoulderRisen + reachOut.Normal * t.Reach : target;
		float residual = Max( over, 0f );

		// ── 6. The off hand braces on the table once the body is genuinely over it —
		// both because that is what a person does and because it explains the pose. It
		// TRACKS the risen pelvis (clamped to the tabletop) rather than sitting at a fixed
		// edge point: a fixed brace ends up behind a fully-risen body, outside the left
		// arm's own ~20u — the exact strain-and-drag failure the right hand is clamped
		// against. It lands in the LEFT side margin (White frame +Y), never on the board —
		// and when even the tracked point is outside the left arm (a deep reach toward the
		// RIGHT edge shifts the whole body away from the +Y margin), the arm just hangs:
		// no brace at all beats a brace the arm visibly strains at.
		V3? brace = null;
		if ( delta.X >= t.BraceEngage )
		{
			var candidate = new V3(
				Clamp( pelvis.X + delta.X, t.BraceMinX, t.BraceMaxX ), t.BraceY, t.BraceZ );
			// Rest left shoulder = right mirrored across the body's own centreline.
			var leftShoulder = new V3( shoulder.X, -shoulder.Y, shoulder.Z )
				+ leanDir * lean + delta;
			if ( ( candidate - leftShoulder ).Length <= t.Reach )
				brace = candidate;
		}

		return new RisePlan( lean, leanDir, delta, fl, fr, stepped, brace, hand,
			residual, t.MaxRise <= 0f ? 1f : Min( rise / t.MaxRise, 1f ) );
	}

	/// <summary>Slide a planted foot horizontally toward the hips just far enough to bring
	/// the hip inside <see cref="RiseTunables.LegReach"/>, capped at MaxStep. The foot
	/// stays ON the floor — only X/Y move — and never steps when it doesn't need to.</summary>
	static V3 StepFoot( V3 foot, V3 hips, in RiseTunables t )
	{
		float need = ( hips - foot ).Length - t.LegReach;
		if ( need <= 0f ) return foot;

		var toward = ( hips - foot ).HorizontalNormal;
		// Horizontal travel buys less than straight-line distance when the hip is high
		// above the foot; overshoot a little (1.4×) and cap. Exactness is not needed —
		// the binary search in Plan() owns the hard guarantee.
		float step = Min( need * 1.4f, t.MaxStep );
		var stepped = foot + toward * step;

		// The table's foot plate starts at FootMinX: a foot may step toward the table but
		// never INTO its base. Clamping X (not rejecting the step) keeps whatever lateral
		// travel the step also bought.
		if ( stepped.X > t.FootMinX ) stepped = stepped with { X = t.FootMinX };
		return stepped;
	}

	static float Min( float a, float b ) => a < b ? a : b;
	static float Max( float a, float b ) => a > b ? a : b;
	static float Clamp( float v, float lo, float hi ) => v < lo ? lo : v > hi ? hi : v;

	// Same hand-rolled Newton sqrt as V3's — no MathF, so the harness story stays clean.
	static float Sqrt( float v )
	{
		if ( v <= 0f ) return 0f;
		double x = v, r = v;
		for ( int i = 0; i < 24; i++ ) r = 0.5 * ( r + x / r );
		return (float)r;
	}
}
