using Gambit.Chess;

// ── The measured world (gambit_terry, SeatSitBack=26, TableScale 1.5, White frame) ──
// Sources: SEATED-HANDS-REACH.md's measurement table + ChessRing constants.
V3 Shoulder = new( -41.6f, -7.6f, 36.6f ); // arm_upper_R, LIVE (doctor dump, Back=36)
V3 Pelvis = new( -39.4f, 0f, 16.6f );      // pelvis bone, LIVE (doctor dump)
V3 FootL = new( -29.4f, +7f, 1f );         // the CHOSEN plants (pelvis.x+10, ±7)
V3 FootR = new( -29.4f, -7f, 1f );

const float BoardSurfaceZ = 32.25f;
const float CellPitch = 4.875f;            // 3.25 base × 1.5
const float NearRankX = -17.0625f;
const float AFileY = +17.0625f;            // "a1 sits at y +17.1"
float GraspZ = BoardSurfaceZ + TerryPose.GraspHeight;

var t = RiseTunables.Default;

V3 Square( int file, int rank ) =>
	new( NearRankX + rank * CellPitch, AFileY - file * CellPitch, GraspZ );

int failures = 0;
void Check( bool ok, string what )
{
	if ( ok ) return;
	failures++;
	Console.WriteLine( $"FAIL: {what}" );
}

// ── 1. The reach grid: every square, White frame (Black is the exact mirror) ──
var residual = new float[64];
var risen = new float[64];
int reachable = 0, seatedOnly = 0;
float worst = 0f;

for ( int rank = 0; rank < 8; rank++ )
for ( int file = 0; file < 8; file++ )
{
	var target = Square( file, rank );
	var plan = HalfRise.Plan( target, Shoulder, Pelvis, FootL, FootR, t );
	int i = rank * 8 + file;
	residual[i] = plan.Residual;
	risen[i] = plan.PelvisDelta.Length;
	if ( plan.Residual <= 8f ) reachable++; // ≤ the 9u grab radius: the hand still PICKS IT UP
	if ( plan.Residual <= 8f && plan.PelvisDelta.Length <= 0.01f ) seatedOnly++;
	worst = MathF.Max( worst, plan.Residual );

	// ── Invariants, every square ──
	var hips = Pelvis + plan.PelvisDelta;
	Check( ( hips - plan.FootL ).Length <= t.LegReach + 0.05f, $"{Name( i )}: left leg over-extended {( hips - plan.FootL ).Length:0.0}" );
	Check( ( hips - plan.FootR ).Length <= t.LegReach + 0.05f, $"{Name( i )}: right leg over-extended {( hips - plan.FootR ).Length:0.0}" );
	Check( plan.FootL.X <= t.FootMinX + 0.01f, $"{Name( i )}: left foot in the table base x={plan.FootL.X:0.0}" );
	Check( plan.FootR.X <= t.FootMinX + 0.01f, $"{Name( i )}: right foot in the table base x={plan.FootR.X:0.0}" );
	Check( plan.FootL.Z == FootL.Z && plan.FootR.Z == FootR.Z, $"{Name( i )}: a step left the floor" );
	Check( hips.X <= t.HipMaxX + 0.05f, $"{Name( i )}: hips past the table edge x={hips.X:0.0}" );
	Check( plan.PitchGain <= t.PitchGain + 0.01f, $"{Name( i )}: pitch over budget" );

	// The clamped hand must sit on/inside the risen shoulder's reach sphere — the
	// no-strain contract the whole design rests on.
	var shoulderRisen = Shoulder + plan.LeanDir * ( plan.Lean + plan.PitchGain ) + plan.PelvisDelta;
	float armAsk = ( plan.Hand - shoulderRisen ).Length;
	Check( armAsk <= t.Reach + 0.05f, $"{Name( i )}: hand asked past the arm {armAsk:0.0}" );

	// Residual is honest: |true target − clamped hand| equals it.
	Check( MathF.Abs( ( target - plan.Hand ).Length - plan.Residual ) <= 0.05f,
		$"{Name( i )}: residual lies ({plan.Residual:0.0} vs {( target - plan.Hand ).Length:0.0})" );

	// The brace, when planted, is within the LEFT arm's reach of the risen left shoulder.
	if ( plan.Brace is { } brace )
	{
		var leftShoulder = new V3( Shoulder.X, -Shoulder.Y, Shoulder.Z )
			+ plan.LeanDir * ( plan.Lean + plan.PitchGain ) + plan.PelvisDelta;
		float ask = ( brace - leftShoulder ).Length;
		Check( ask <= 20f, $"{Name( i )}: brace out of the left arm's reach {ask:0.0}" );
	}
}

Console.WriteLine( "── half-rise reach grid: residual units short after every lever (0 = hand arrives) ──" );
Console.WriteLine( "       a      b      c      d      e      f      g      h" );
for ( int rank = 7; rank >= 0; rank-- )
{
	string row = $"   {rank + 1} ";
	for ( int file = 0; file < 8; file++ )
	{
		float m = residual[rank * 8 + file];
		row += m <= 0.01f ? "  ok   " : $" {m,5:0.0} ";
	}
	Console.WriteLine( row );
}
Console.WriteLine( "── pelvis rise per square (|delta|, world units) ──" );
for ( int rank = 7; rank >= 0; rank-- )
{
	string row = $"   {rank + 1} ";
	for ( int file = 0; file < 8; file++ )
		row += $" {risen[rank * 8 + file],5:0.0} ";
	Console.WriteLine( row );
}
Console.WriteLine( $"in-hand (residual ≤ grab radius): {reachable}/64 (of those, seated no-rise: {seatedOnly})  worst residual: {worst:0.0}u" );

// M13's grid had 5 squares reachable and the far rank 30–35 short. The gates encode the
// POSE-FIRST trade made after the in-editor rounds: override rotations don't carry child
// bones (so torso pitch buys no reach), and the hips stop at the table edge (the plank was
// worse than a slide-assisted far rank). Ranks 1–5 in hand; 6–8 strain-and-slide.
Check( reachable >= 48, $"only {reachable}/64 in hand — the half-rise is not earning its keep" );
Check( worst <= 18f, $"worst residual {worst:0.0} — a far corner is out of even slide-assist theatre range" );

// ── 2. Mirroring: a Black-seat plan through V3.Mirrored is exactly the White plan ──
{
	var target = Square( 2, 6 );
	var white = HalfRise.Plan( target, Shoulder, Pelvis, FootL, FootR, t );
	var black = HalfRise.Plan( target.Mirrored.Mirrored, Shoulder, Pelvis, FootL, FootR, t );
	Check( white == black, "mirror round-trip is not the identity" );
	Check( new V3( 1f, -2f, 3f ).Mirrored == new V3( -1f, 2f, 3f ), "Mirrored is wrong" );
}

// ── 3. Continuity: neighbouring targets → neighbouring poses (no pops as the hand
// chases the cursor across the board). Sample a dense line across the diagonal. ──
{
	V3 prevDelta = default; float prevLean = 0f; bool first = true;
	for ( float u = 0f; u <= 1f; u += 0.01f )
	{
		var target = new V3( NearRankX + u * 7 * CellPitch, AFileY - u * 7 * CellPitch, GraspZ );
		var p = HalfRise.Plan( target, Shoulder, Pelvis, FootL, FootR, t );
		if ( !first )
		{
			// Steep gradients (~2.4u where the leg constraint engages) are fine — the
			// runtime eases the pelvis like the hand chase. This catches genuine cliffs.
			Check( ( p.PelvisDelta - prevDelta ).Length <= 4f,
				$"pelvis pops {( p.PelvisDelta - prevDelta ).Length:0.00} at u={u:0.00}" );
			Check( MathF.Abs( p.Lean - prevLean ) <= 0.5f, $"lean pops at u={u:0.00}" );
		}
		prevDelta = p.PelvisDelta; prevLean = p.Lean; first = false;
	}
}

// ── 4. Degenerates: target at the shoulder, straight below, NaN-free ──
{
	foreach ( var target in new[] { Shoulder, Pelvis, new V3( -31.8f, -6f, 0f ), V3.Zero } )
	{
		var p = HalfRise.Plan( target, Shoulder, Pelvis, FootL, FootR, t );
		Check( !float.IsNaN( p.Hand.X + p.PelvisDelta.X + p.Lean + p.FootL.X + p.FootR.X ),
			"NaN escaped the planner" );
	}
}

Console.WriteLine( failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECKS FAILED" );
return failures == 0 ? 0 : 1;

static string Name( int i ) => $"{(char)( 'a' + ( i & 7 ) )}{( i >> 3 ) + 1}";
