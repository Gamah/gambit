namespace Gambit.Chess;

/// <summary>
/// The one clock that owns a single terry move (M14). Given how long ago the move landed
/// and the tempo budget, it says — for THIS frame — where the piece is along its slide,
/// how far the hand has travelled from its rest anchor toward the piece, and how closed
/// the grip is. Three phases on one timeline: Approach, Carry, Release.
///
/// <para><b>Sandbox-free on purpose.</b> No <c>Vector3</c>, no <c>Time</c>, no engine type —
/// just floats in and a struct out, so the whole tempo model runs in the dotnet harness on
/// the dev host (the only place it can be PROVEN — nothing here renders). Same doctrine as
/// <see cref="BoardDiff"/>, <see cref="CapturedMaterial"/> and <see cref="MoveSpeech"/>. The
/// engine glue (which piece, which hand, world transforms) stays in <c>World/</c>.</para>
///
/// <para><b>Why a clock and not a proximity test.</b> Attempt 2 tried to detect "the hand
/// arrived at the piece" by 3D distance and it NEVER fired — the hand hovers above the board
/// and the piece sits on it, so the distance was always past the grab radius, the piece never
/// rode the hand, and it waited out the hold and slid on its own. The fix is this file: the
/// clock GATES the piece. It does not start sliding until Approach ends, because the clock
/// KNOWS the hand has arrived — the clock is what sent it there. No grab radius exists.</para>
///
/// <para><b>The reconciliation of the two mental models.</b> "The hand moves the piece"
/// (owner) and "the hand IK target is derived from the piece" (robustness) are the same thing
/// here: the PIECE's slide (<see cref="Sample.PieceProgress"/>) is the authoritative path,
/// authored by <c>ChessBoardView</c> and landing exactly on the square; the HAND rides it
/// (<see cref="Sample.HandWeight"/> blends the rest anchor toward the piece, then holds at 1
/// through the carry). The flaky thing — the arm — can only ever be a little off the RIGHT
/// answer, never authoritative.</para>
/// </summary>
public static class MoveGesture
{
	/// <summary>Which leg of the one timeline a move is in.</summary>
	public enum Phase
	{
		/// <summary>Hand travels from the rest anchor to the origin piece; the piece is
		/// FROZEN on its origin square. The hand initiates.</summary>
		Approach,

		/// <summary>The piece slides origin → destination (flat, on the board surface). The
		/// hand tracks it — this is "the hand moves the piece."</summary>
		Carry,

		/// <summary>The piece is on the destination; the grip opens and the hand eases back
		/// to the rest anchor.</summary>
		Release,

		/// <summary>The budget is spent: piece landed, hand home, grip open. The gesture is
		/// over and the caller may drop it.</summary>
		Done,
	}

	/// <summary>Everything the caller needs for one frame, as plain scalars.</summary>
	/// <param name="Phase">Which leg of the timeline.</param>
	/// <param name="PieceProgress">0..1 along origin → destination. <b>0 for the whole of
	/// Approach</b> (the gate that fixes "the piece moves before/without the hand"), eased
	/// 0→1 across Carry, pinned at 1 through Release and Done. This drives the piece's slide.</param>
	/// <param name="HandWeight">0..1 blend from the rest anchor (0) to the piece's grip point
	/// (1). Eased 0→1 across Approach, held at 1 through Carry, eased 1→0 across Release. This
	/// drives where the hand IK target is.</param>
	/// <param name="GripClose">0..1 for the pinch (the animgraph <c>holdtype_pose_hand</c>).
	/// Closes as the hand arrives at the end of Approach, held closed through Carry, opens
	/// across Release.</param>
	/// <param name="Done">The gesture has finished — a convenience alias for
	/// <c>Phase == Phase.Done</c>.</param>
	public readonly record struct Sample(
		Phase Phase, float PieceProgress, float HandWeight, float GripClose, bool Done );

	/// <summary>
	/// Sample the gesture at <paramref name="since"/> seconds after the move landed.
	/// </summary>
	/// <param name="since">Seconds elapsed since the move was detected (already real time).</param>
	/// <param name="budget">Total rest-to-rest duration for this move, seconds. A capture
	/// passes a larger budget (the owner allows up to ~1.5× a plain move) — the SPLIT below is
	/// a fraction of whatever budget arrives, so a capture stretches proportionally.</param>
	/// <param name="approachFrac">Fraction of the budget spent travelling to the piece.</param>
	/// <param name="carryFrac">Fraction spent carrying the piece to its square.</param>
	/// <param name="releaseFrac">Fraction spent easing the hand back home.</param>
	public static Sample SampleAt( float since, float budget,
		float approachFrac, float carryFrac, float releaseFrac )
	{
		// Normalise the split so callers can pass raw weights (0.3/0.5/0.2 or 3/5/2) and a
		// zero-sum or lopsided set never divides by zero or overruns the timeline.
		float sum = approachFrac + carryFrac + releaseFrac;
		if ( sum <= 1e-4f ) { approachFrac = 0.3f; carryFrac = 0.5f; releaseFrac = 0.2f; sum = 1f; }
		float a = approachFrac / sum;
		float c = carryFrac / sum;
		// r is the remainder — never recomputed from releaseFrac, so a+c+r == 1 exactly.

		if ( budget <= 1e-4f ) budget = 1f;
		float p = since / budget;           // 0..1+ across the whole gesture

		if ( p >= 1f )
			return new Sample( Phase.Done, 1f, 0f, 0f, true );

		if ( p < a )
		{
			// ── Approach ── piece frozen at origin; hand travels rest → piece; grip closes
			// over the LAST part of the reach so the pinch snaps shut as the hand arrives,
			// not while it is still crossing the table.
			float ta = a <= 1e-4f ? 1f : p / a;
			return new Sample(
				Phase.Approach,
				PieceProgress: 0f,
				HandWeight: SmoothStep( ta ),
				GripClose: Grab( ta ),
				Done: false );
		}

		if ( p < a + c )
		{
			// ── Carry ── the piece slides; the hand holds on it (weight 1, grip shut). The
			// piece uses the same ease-out the board's own slides use (1-(1-t)^3), so a
			// hand-driven move and a plain slide decelerate onto the square identically.
			float tc = c <= 1e-4f ? 1f : ( p - a ) / c;
			return new Sample(
				Phase.Carry,
				PieceProgress: EaseOutCubic( tc ),
				HandWeight: 1f,
				GripClose: 1f,
				Done: false );
		}

		// ── Release ── piece landed; hand eases home; grip opens. r = 1-a-c.
		float r = 1f - a - c;
		float tr = r <= 1e-4f ? 1f : ( p - a - c ) / r;
		return new Sample(
			Phase.Release,
			PieceProgress: 1f,
			HandWeight: 1f - SmoothStep( tr ),
			GripClose: 1f - tr,
			Done: false );
	}

	/// <summary>Total wall-clock length of a move, given the tempo budget and capture flag.
	/// Kept here (not on the caller) so the harness proves the same number the runtime uses.</summary>
	public static float Duration( float budget, bool capture, float captureScale ) =>
		budget * ( capture ? Max( captureScale, 1f ) : 1f );

	// ── Easing (hand-rolled: no System.Math dependence questions in the harness) ──

	/// <summary>Smootherstep-ish ease-in-out on 0..1, for the hand's travel and return: it
	/// leaves the anchor and settles onto the piece without a hard start or stop.</summary>
	static float SmoothStep( float t )
	{
		t = Clamp01( t );
		return t * t * ( 3f - 2f * t );
	}

	/// <summary>Ease-out cubic on 0..1 — the board's own slide curve, so the carried piece
	/// decelerates onto its square the same way an un-handed slide does.</summary>
	static float EaseOutCubic( float t )
	{
		t = Clamp01( t );
		float u = 1f - t;
		return 1f - u * u * u;
	}

	/// <summary>The grip closes only over the final <see cref="GrabFrac"/> of the approach —
	/// open while crossing to the piece, shut as it lands on it.</summary>
	static float Grab( float ta )
	{
		const float GrabFrac = 0.4f;
		if ( ta <= 1f - GrabFrac ) return 0f;
		return SmoothStep( ( ta - ( 1f - GrabFrac ) ) / GrabFrac );
	}

	static float Clamp01( float t ) => t < 0f ? 0f : t > 1f ? 1f : t;
	static float Max( float a, float b ) => a > b ? a : b;
}
