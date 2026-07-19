using Gambit.Chess;

// M14 harness: proves MoveGesture (the one-clock tempo model) and HandReach (the move-only
// half-rise reach planner) on the dev host. Both are Sandbox-free, so this is the only place
// they can actually be RUN rather than reviewed. Exit non-zero on any failed assertion.

int failures = 0;
void Check( bool ok, string what )
{
	if ( ok ) return;
	Console.WriteLine( $"  FAIL: {what}" );
	failures++;
}
bool Finite( float f ) => !float.IsNaN( f ) && !float.IsInfinity( f );
bool Finite3( R3 v ) => Finite( v.X ) && Finite( v.Y ) && Finite( v.Z );

// ─────────────────────────────── MoveGesture ───────────────────────────────
Console.WriteLine( "MoveGesture — the one clock:" );
{
	float budget = 1.0f, a = 0.3f, c = 0.5f, r = 0.2f;

	var s0 = MoveGesture.SampleAt( 0f, budget, a, c, r );
	Check( s0.Phase == MoveGesture.Phase.Approach, "t=0 is Approach" );
	Check( s0.PieceProgress == 0f, "t=0 piece frozen at origin" );
	Check( s0.HandWeight < 0.01f, "t=0 hand at rest anchor" );
	Check( s0.GripClose < 0.01f, "t=0 grip open" );

	// End of approach: hand has arrived (weight ~1) BEFORE the piece has moved (progress 0).
	// This is the whole point — the piece does not move until the hand is on it.
	var sApp = MoveGesture.SampleAt( a * 0.999f, budget, a, c, r );
	Check( sApp.PieceProgress == 0f, "piece still frozen at end of Approach" );
	Check( sApp.HandWeight > 0.98f, "hand arrived by end of Approach" );
	Check( sApp.GripClose > 0.9f, "grip closed by end of Approach" );

	// Mid carry: piece moving, hand holding it, grip shut.
	var sMid = MoveGesture.SampleAt( a + c * 0.5f, budget, a, c, r );
	Check( sMid.Phase == MoveGesture.Phase.Carry, "mid is Carry" );
	Check( sMid.PieceProgress is > 0f and < 1f, "piece mid-slide" );
	Check( sMid.HandWeight > 0.99f, "hand holds piece through Carry" );
	Check( sMid.GripClose > 0.99f, "grip shut through Carry" );

	// Release: piece landed, hand returning, grip opening.
	var sRel = MoveGesture.SampleAt( a + c + r * 0.5f, budget, a, c, r );
	Check( sRel.Phase == MoveGesture.Phase.Release, "late is Release" );
	Check( sRel.PieceProgress == 1f, "piece landed by Release" );
	Check( sRel.HandWeight is > 0f and < 1f, "hand easing home in Release" );

	var sDone = MoveGesture.SampleAt( budget * 1.01f, budget, a, c, r );
	Check( sDone.Done && sDone.Phase == MoveGesture.Phase.Done, "past budget is Done" );
	Check( sDone.PieceProgress == 1f, "Done piece on destination" );
	Check( sDone.HandWeight == 0f && sDone.GripClose == 0f, "Done hand home, grip open" );

	// Continuity + monotonicity over a fine sweep: piece progress never goes backward; hand
	// weight rises then falls with no discontinuity (a jump reads as a teleporting arm).
	float prevPiece = -1f, prevHand = -1f, peakHand = 0f; bool fell = false;
	for ( int i = 0; i <= 1000; i++ )
	{
		float since = budget * i / 1000f;
		var s = MoveGesture.SampleAt( since, budget, a, c, r );
		Check( Finite( s.PieceProgress ) && Finite( s.HandWeight ) && Finite( s.GripClose ),
			$"finite sample at {since:0.000}" );
		Check( s.PieceProgress >= prevPiece - 1e-4f, $"piece progress monotone at {since:0.000}" );
		if ( prevHand >= 0f )
			Check( MathF.Abs( s.HandWeight - prevHand ) < 0.05f, $"hand weight continuous at {since:0.000}" );
		if ( s.HandWeight >= peakHand ) peakHand = s.HandWeight; else fell = true;
		prevPiece = s.PieceProgress; prevHand = s.HandWeight;
	}
	Check( peakHand > 0.99f, "hand weight reaches full extension" );
	Check( fell, "hand weight comes back down" );

	// A capture stretches the whole gesture proportionally.
	Check( MathF.Abs( MoveGesture.Duration( 1.0f, true, 1.5f ) - 1.5f ) < 1e-4f, "capture is 1.5x" );
	Check( MathF.Abs( MoveGesture.Duration( 1.0f, false, 1.5f ) - 1.0f ) < 1e-4f, "plain move is 1x" );

	// Degenerate splits must not divide by zero or overrun.
	var sBad = MoveGesture.SampleAt( 0.5f, 1f, 0f, 0f, 0f );
	Check( Finite( sBad.PieceProgress ), "zero-sum split falls back cleanly" );
}

// ─────────────────────────────── HandReach ───────────────────────────────
Console.WriteLine( "HandReach — move-only half-rise over all 64 squares, both seats:" );
{
	var t = ReachTunables.Default;

	// A plausible White-frame seated skeleton (station-local). Real numbers come from the
	// editor's gambit_terry bone dump at runtime — this proves the MATH is sound for a
	// reasonable skeleton, not a specific reachable set. White sits at −X; the board is +X of
	// the seat; +Y is the a-file.
	var shoulder = new R3( -34.6f, -3f, 43f );   // seated shoulder: up and back over the chair
	var pelvis   = new R3( -24f, 0f, 16.6f );     // measured pelvis height
	var footL    = new R3( -22f, 6f, 0f );
	var footR    = new R3( -22f, -6f, 0f );

	// Board squares: ranks along X (rank1 nearest at −X), files along −Y (a-file at +Y).
	// Grasp point ~ a little above the board surface.
	const float surfaceZ = 32f, cell = 3.6f;
	R3 Square( int file, int rank )   // file 0=a..7=h, rank 0=1..7=8
	{
		float x = -12.6f + rank * cell;   // rank1 near, rank8 far
		float y = 12.6f - file * cell;    // a-file +Y, h-file −Y
		return new R3( x, y, surfaceZ );
	}

	int reachable = 0, trailing = 0;
	float maxResidual = 0f;
	for ( int rank = 0; rank < 8; rank++ )
	for ( int file = 0; file < 8; file++ )
	{
		var target = Square( file, rank );
		var p = HandReach.Plan( target, shoulder, pelvis, footL, footR, t );

		Check( Finite3( p.Hand ) && Finite3( p.PelvisDelta ) && Finite3( p.FootL ) && Finite3( p.FootR )
			&& Finite( p.Residual ) && Finite( p.Rise01 ), $"finite plan {file},{rank}" );

		// The clamped hand is always on or inside the final shoulder's reach sphere.
		var shoulderFinal = shoulder + p.LeanDir * p.Lean + p.PelvisDelta;
		float reachLen = ( p.Hand - shoulderFinal ).Length;
		Check( reachLen <= t.Reach + 0.05f, $"hand within arm at {file},{rank} (|{reachLen:0.0}| <= {t.Reach})" );

		Check( p.Residual >= -1e-3f, $"residual non-negative at {file},{rank}" );
		if ( p.Residual < 1e-3f )
			Check( ( p.Hand - target ).Length < 0.05f, $"honest reach lands on target at {file},{rank}" );

		// The rise is forward and bounded; it never pushes the pelvis backward for a forward
		// target, and never past MaxRise or the table edge.
		Check( p.PelvisDelta.X >= -1e-3f, $"pelvis never rises backward at {file},{rank}" );
		float riseMag = p.PelvisDelta.LengthXY;
		Check( riseMag <= t.MaxRise + 0.05f, $"rise bounded by MaxRise at {file},{rank}" );
		Check( pelvis.X + p.PelvisDelta.X <= t.HipMaxX + 0.05f, $"hips respect table edge at {file},{rank}" );

		// Feet never step past the foot plate (a MAX in White frame), and the legs are
		// honoured after the scan.
		Check( p.FootL.X <= t.FootMaxX + 0.05f && p.FootR.X <= t.FootMaxX + 0.05f, $"feet clear plate at {file},{rank}" );
		var hips = pelvis + p.PelvisDelta;
		Check( ( hips - p.FootL ).Length <= t.LegReach + 0.5f, $"left leg honoured at {file},{rank}" );
		Check( ( hips - p.FootR ).Length <= t.LegReach + 0.5f, $"right leg honoured at {file},{rank}" );

		if ( p.Residual < 1e-3f ) reachable++; else { trailing++; maxResidual = MathF.Max( maxResidual, p.Residual ); }
	}

	// R3.Mirrored is the whole Black-seat mechanism: the runtime mirrors Black's world inputs
	// INTO White frame, plans there (the caps stay White-frame — they are NOT frame-agnostic,
	// which is why the planner is only ever called in White frame), then mirrors the plan back
	// out. So the one property that must hold is that Mirrored is a clean involution.
	var probe = new R3( 7.3f, -4.1f, 31.9f );
	Check( ( probe.Mirrored.Mirrored - probe ).Length < 1e-5f, "R3.Mirrored is an involution" );
	Check( MathF.Abs( probe.Mirrored.Z - probe.Z ) < 1e-5f, "mirror keeps Z (a Z-rotation, not a reflection)" );

	Console.WriteLine( $"  {reachable}/64 honestly reached, {trailing}/64 the hand trails "
		+ $"(max shortfall {maxResidual:0.0}u — the piece's own slide finishes those)." );
	// The reachable COUNT is informational, not pass/fail: at SeatSitBack=26 with placeholder
	// bones the split is whatever the guessed skeleton makes it, and the real numbers come from
	// the editor's gambit_terry dump. What we DO assert is that both code paths behave — proven
	// with two targets placed relative to the shoulder itself, independent of board guesses:

	// A target well inside the arm, at shoulder height, straight ahead → honest reach, no
	// theatre: hand ON it, no lean, no rise.
	var near = shoulder + new R3( t.Reach * 0.5f, 0f, 0f );
	var pNear = HandReach.Plan( near, shoulder, pelvis, footL, footR, t );
	Check( pNear.Residual < 1e-3f, "near target reached honestly" );
	Check( ( pNear.Hand - near ).Length < 0.05f, "near target: hand lands on it" );
	Check( pNear.Lean == 0f && pNear.PelvisDelta.Length < 1e-3f, "near target: no lean, no rise" );

	// A target far past even a full rise → trails, but with a BOUNDED, forward rise and a hand
	// that still sits on the reach sphere (never NaN, never past the arm).
	var far = shoulder + new R3( 80f, 0f, -8f );
	var pFar = HandReach.Plan( far, shoulder, pelvis, footL, footR, t );
	Check( pFar.Residual > 1f, "far target trails (design point 6)" );
	Check( pFar.PelvisDelta.X > 0f && pFar.PelvisDelta.LengthXY <= t.MaxRise + 0.05f, "far target: bounded forward rise" );
	Check( ( pFar.Hand - ( shoulder + pFar.LeanDir * pFar.Lean + pFar.PelvisDelta ) ).Length <= t.Reach + 0.05f,
		"far target: hand still on the reach sphere" );
}

Console.WriteLine();
if ( failures == 0 )
{
	Console.WriteLine( "ALL GREEN — MoveGesture + HandReach proven." );
	return 0;
}
Console.WriteLine( $"{failures} FAILURE(S)." );
return 1;
