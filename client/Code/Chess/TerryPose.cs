namespace Gambit.Chess;

/// <summary>What a seated player's working hand is doing right now.</summary>
public enum HandPhase
{
	/// <summary>Not on the board — elbows on the table.</summary>
	Idle,

	/// <summary>Floating over a piece, never touching it. NOT produced by
	/// <see cref="TerryPose.Advance"/> any more (the thinking hand was cut — hands rest
	/// unless a move is confirmed); kept because the reach probe/sweep drive it directly.</summary>
	Hover,

	/// <summary>Hand down at a piece, fingers nearly closed. Same status as
	/// <see cref="Hover"/>: probe-only since the thinking-hand cut.</summary>
	Selected,

	/// <summary>The budgeted approach: hand travelling from its rest pose to the
	/// from-square. A REAL stage of the move (owner, 2026-07-19): the whole gesture has a
	/// fixed time budget, and the approach spends its slice of it — however far the reach,
	/// the hand is OVER the piece when this phase ends, because the driver converges on a
	/// deadline, not at a rate. Worst case that is a snap, which is the accepted trade.</summary>
	Reaching,

	/// <summary>A capture, step one: reaching to the TO square and closing on the piece
	/// that is about to be taken. Only ever happens for a capture.</summary>
	Clearing,

	/// <summary>A capture, step two: carrying the taken piece off the board to its owner's
	/// tray, and letting go of it there.</summary>
	Discarding,

	/// <summary>Closing on the from-square and lifting.</summary>
	Lifting,

	/// <summary>Travelling from-square → to-square at lift height.</summary>
	Carrying,

	/// <summary>Setting the piece down on the to-square and opening the fingers.</summary>
	Dropping,
}

/// <summary>
/// Everything the driver needs to place one hand this frame, as plain scalars.
///
/// <para><b>No Vector3, deliberately.</b> That type is Sandbox, and the moment it appears
/// here none of this can be run on the dev host — which is the whole reason the file
/// exists. Squares travel as indices and heights as floats; <c>SeatedTerry</c> maps them
/// through <c>ChessRing.SquareLocalPosition</c>, which is the only part that needs the
/// engine. Same shape as <see cref="CapturedMaterial"/> (plain <c>char[64]</c>) and
/// <see cref="BoardDiff"/> (plain FEN + ply).</para>
///
/// <para>It carries its own <see cref="Ply"/>, <see cref="Since"/> and <see cref="Rush"/>
/// rather than leaving them to the caller: that is what makes <see cref="TerryPose.Advance"/>
/// a closed state machine, and what lets the abandon rule be PROVEN here instead of
/// reviewed.</para>
/// </summary>
/// <param name="Phase">What the hand is doing.</param>
/// <param name="FromSquare">Square index (rank*8+file) the hand is at, or −1 when idle.</param>
/// <param name="ToSquare">Where it is heading; equal to <paramref name="FromSquare"/> unless travelling.</param>
/// <param name="ToTray">The destination is the taken piece's TRAY, not <paramref name="ToSquare"/>.
/// Only ever true while <see cref="HandPhase.Discarding"/>. The driver owns where a tray IS.</param>
/// <param name="Travel">0..1 along From → To. Only ever non-zero while travelling.</param>
/// <param name="Height">World units above the board surface.</param>
/// <param name="FingerClose">0..1 → the animgraph's <c>holdtype_pose_hand</c>.</param>
/// <param name="Weight">0..1 blend from the elbows-on-table idle target to the board target.</param>
/// <param name="Ply">The move count this pose was resolved against — see the abandon rule.</param>
/// <param name="Capture">This move takes a piece, so the timeline has the victim's trip to
/// the tray in front of it. <b>Stored, not inferred.</b> The first version worked it back out
/// of Phase and Since — which silently fails during a capture's own Lifting phase (the
/// prologue is over, so the phase looks ordinary, but the clock is only 1.2s into a 2.8s
/// timeline and reads as a plain move nearly finished). It would have replayed as the wrong
/// gesture.</param>
/// <param name="Since">Seconds since this move's animation began, already scaled by Rush.</param>
/// <param name="Rush">How far behind the game this hand is running — see <see cref="TerryPose.Advance"/>.</param>
/// <param name="PhaseRemaining">Timeline-seconds left in the current phase (0 outside a
/// move). The driver's DEADLINE: it converges the hand on the phase target so it arrives
/// exactly when this runs out — a rate would lag on far reaches, a deadline degrades to a
/// snap. Timeline units: divide by (1+Rush)·SpeedScale for wall seconds.</param>
public readonly record struct HandPose(
	HandPhase Phase,
	int FromSquare,
	int ToSquare,
	bool ToTray,
	float Travel,
	float Height,
	float FingerClose,
	float Weight,
	int Ply,
	bool Capture,
	float Since,
	float Rush,
	float PhaseRemaining = 0f )
{
	/// <summary>Nothing has been observed yet: idle, no square, no weight, ply 0.</summary>
	public static readonly HandPose None =
		new( HandPhase.Idle, -1, -1, false, 0f, 0f, 0f, 0f, 0, false, 0f, 0f );

	/// <summary>The hand is on the board rather than resting on the table.</summary>
	public bool OnBoard => FromSquare >= 0;

	/// <summary>Playing out a move, rather than reacting to a cursor.</summary>
	public bool Animating => Phase is HandPhase.Reaching or HandPhase.Clearing
		or HandPhase.Discarding or HandPhase.Lifting or HandPhase.Carrying
		or HandPhase.Dropping;
}

/// <summary>What the driver observed about this seat this frame.
///
/// <para><b>No hover, no selection — by owner decision (2026-07-19).</b> The hand rests on
/// the table unless a move has been CONFIRMED (the ply advanced). The old thinking-hand —
/// drifting after the cursor, parking on a selected piece — was cut wholesale; the wire
/// state that carried it (<c>LobbyPlayer.HandState</c>) went with it. A move is already
/// relayed, so every client drives the same gesture off LastMoveUci without being told
/// anything extra.</para></summary>
/// <param name="Ply">The game's move count.</param>
/// <param name="LastMoveUci">UCI of the move that produced <paramref name="Ply"/>, or null.</param>
/// <param name="SeatMoved">That move was played by the seat being animated.</param>
/// <param name="Capture">That move took a piece — so the hand has to clear the victim off
/// the board before it can put anything on the square. <see cref="BoardDiff"/> answers this
/// from the FEN alone, en passant included.</param>
/// <param name="GameLive">There is a game to have hands about.</param>
public readonly record struct HandInput(
	int Ply,
	string LastMoveUci,
	bool SeatMoved,
	bool Capture,
	bool GameLive );

/// <summary>
/// The seated hand's state machine (M13), pure and Sandbox-free so it can be driven
/// through real games in a dotnet harness on the dev host.
///
/// <para><b>Worth extracting because the edges read as obviously correct and aren't.</b> A
/// turn flipping mid-lift, a selection cleared under a carry, a move landing while the
/// player hovers somewhere else entirely, a takeback rewinding the ply out from under an
/// animation — every one of those is a case where the naive machine keeps playing an
/// animation of something that didn't happen. Left as a private method on a Component,
/// none of it could have been executed here at all; that is exactly the
/// <see cref="CapturedMaterial"/> lesson.</para>
///
/// <para><b>The abandon rule: the game is authority, the animation is decoration.</b> No
/// phase survives a ply change. <see cref="Advance"/> compares <see cref="HandInput.Ply"/>
/// against the ply the previous pose was resolved at, and on any difference it drops what
/// was in flight and re-derives from scratch — either a fresh pickup for the move that just
/// landed, or nothing. It cannot return a carry holding the old move's squares.</para>
/// </summary>
public static class TerryPose
{
	// ── The timeline ──
	//
	// QUICK. This shipped as "SLOW, on purpose" (1.6s move / 2.8s capture) on the theory
	// that a fast hand reads as a glitch — and the first watchable two-client builds proved
	// that theory backwards: once the piece actually rides the hand, slow reads as broken
	// and snappy reads as a person playing. The standing direction: a move is never more
	// than a second, and Rush (below) compresses it further the moment the game outpaces
	// even that. GestureSpeed (TerryTuning) scales the whole clock live.
	//
	// The FRONT half of a move — the reach from the table to the from-square — is the
	// Reaching stage: a budgeted slice of the move like every other, not a rate. The
	// driver converges the hand on each stage's DEADLINE (PhaseRemaining), so it is over
	// the piece when Reaching ends however far the reach was — snapping in the worst
	// case. The piece waits on its square for the hand (ChessBoardView's slide hold), so
	// the stages and the board can't desync.

	/// <summary>The approach: rest pose → over the from-square. A budgeted stage of the
	/// move, NOT a rate — the driver spends exactly this long however far the reach is,
	/// so a cross-board approach is fast and a near one is gentle, and the hand is
	/// guaranteed over the piece when it ends. 0.12 by owner request ("accelerate to the
	/// piece much faster") — a dart, not a stroll.</summary>
	public const float ReachTime = 0.12f;

	/// <summary>Close on the from-square and lift.</summary>
	public const float LiftTime = 0.18f;

	/// <summary>Carry from-square → to-square.</summary>
	public const float TravelTime = 0.35f;

	/// <summary>Set down and open the fingers.</summary>
	public const float DropTime = 0.2f;

	/// <summary>A plain move, end to end — the whole budget, approach included.</summary>
	public const float MoveTime = ReachTime + LiftTime + TravelTime + DropTime;  // 0.98

	/// <summary>A capture is the SAME hand gesture as a move now (owner decision,
	/// 2026-07-19): the hand follows only the TAKING piece, and the victim lerps to its
	/// tray on its own, simultaneously, the moment the move starts. The old prologue —
	/// hand flies to the victim, carries it to the tray, comes back for the attacker
	/// (Clearing/Discarding, +0.48s) — read as the hand doing a weird shuttle between the
	/// two pieces, and is cut. The banner's fuller capture choreography (attacker-first +
	/// the DropAndSwap exchange beat) is superseded by this simpler rule.</summary>
	public const float CaptureTime = MoveTime;

	/// <summary>
	/// How much faster than 1× the hand may play to catch up, per move it has fallen behind.
	/// Capped, because past a point a hand moving fast enough is a hand teleporting, and the
	/// abandon rule is a better answer than a blur.
	/// </summary>
	public const float MaxRush = 4f;

	/// <summary>
	/// Height a hovering hand floats at, above the board surface.
	///
	/// <para><b>This was 6, on the reasoning "above a pawn (4.8) and below a king (9.6)" —
	/// which is exactly backwards.</b> Clearing the SHORTEST piece is not clearance at all:
	/// a hand at 6 is buried to the wrist in every king, queen and bishop it passes, and
	/// reads as pawing at the set. The tallest piece is the king at 9.6, so hovering starts
	/// there and adds room to see under. A hand you can see daylight beneath reads as
	/// considering a piece; one at piece height reads as touching it.</para></summary>
	public static float HoverHeight = 12f;   // static, not const: TerryTuning drives it from the inspector

	/// <summary>Height the fingers close at — reaching DOWN toward the piece from the hover,
	/// brushing the king's 9.6. (The "never touching" rule this constant was born under is
	/// gone: since the hand-carry, the grasped piece really does ride the hand, so closing
	/// AT the piece is honest now — and a whole extra unit of daylight read as a wrist
	/// hovering awkwardly high.)</summary>
	public static float GraspHeight = 10f;   // static, not const: TerryTuning drives it from the inspector

	/// <summary>Height a carried piece travels at: clear of everything on the board, and
	/// above the hover so a pickup visibly lifts. (17 carried the wrist a half-piece too
	/// high through every move — the first thing the eye catches on a carry.)</summary>
	public static float LiftHeight = 14f;    // static, not const: TerryTuning drives it from the inspector

	/// <summary>
	/// Seconds for the hand to go back to rest after a move's gesture finishes.
	///
	/// <para>Was 1.2s ("a hand relaxing"); owner verdict on seeing it: much faster — the
	/// lingering hand read as loitering over the board, not relaxing.</para></summary>
	public const float FadeOutTime = 0.45f;

	// Finger poses, as holdtype_pose_hand. POLARITY IS UNVERIFIED — the parameter, its
	// 0..1 range and its finger role are all read off citizen.vanmgrph, but which end is
	// open and which is closed is not knowable on this host. If it reads inverted in the
	// editor, flip these four constants and nothing else.
	public const float FingersHovering = 0.25f;
	public const float FingersGrasping = 0.85f;
	public const float FingersHolding = 1f;
	public const float FingersReleased = 0.2f;

	/// <summary>
	/// Advance one hand by <paramref name="dt"/> seconds.
	///
	/// <para>Order matters and is the whole design: the ply check comes FIRST, so a move
	/// landing always wins over whatever was being animated. Everything below it is
	/// reasoning about a game that has not changed since the last frame.</para>
	/// </summary>
	/// <summary>Multiplier over the whole gesture clock — fades, timeline, everything.
	/// 1 = the authored M13 tempo, which the first watchable builds read as "painfully
	/// slow". Static (not const) so TerryTuning drives it from the inspector; the tempo
	/// question is a slider now, not a recompile.</summary>
	public static float SpeedScale = 1f;

	public static HandPose Advance( in HandPose prev, in HandInput input, float dt )
	{
		dt *= SpeedScale <= 0f ? 1f : SpeedScale;
		if ( !input.GameLive )
			return Idle( prev, input.Ply, dt );

		// ── The abandon rule ──
		//
		// The ply moved: everything in `prev` describes a position that no longer exists.
		// Re-derive from scratch rather than continuing — a carry whose from-square has
		// since been captured onto is an animation of a move nobody played.
		//
		// A ply that went DOWN (a takeback, a reset, a resync onto a FEN with no history —
		// see BoardDiff) lands here too and goes idle, which is right: none of those is a
		// move and none of them should have a hand play one.
		if ( input.Ply != prev.Ply )
		{
			if ( input.Ply > prev.Ply && input.SeatMoved
				&& TryParseUci( input.LastMoveUci, out int from, out int to ) )
			{
				// ── Rush ──
				//
				// We were still animating the LAST move when this one landed, so the hand is
				// behind the game and the slow timeline is why. Play the new one faster, and
				// keep stacking while the game keeps outrunning us: a hand that finishes
				// cleanly resets to 1× on its own (Idle clears it below).
				//
				// This is what buys the slow timeline. The animation is allowed to take 1.6s
				// only for as long as the game lets it — the moment it can't, it compresses
				// rather than falling behind or being dropped mid-gesture.
				float rush = prev.Animating ? Min( prev.Rush + 1f, MaxRush ) : 0f;
				return Timeline( from, to, input.Capture, since: 0f, ply: input.Ply, rush );
			}

			return Idle( prev with { Ply = input.Ply }, input.Ply, dt );
		}

		// A move already in flight, and the game hasn't moved under it: run it out.
		if ( prev.Animating )
		{
			// Rush scales the CLOCK, not the shape — so a rushed animation is the same
			// gesture played faster and lands in exactly the same place.
			float since = prev.Since + dt * ( 1f + prev.Rush );

			if ( since < ( prev.Capture ? CaptureTime : MoveTime ) )
				return Timeline( prev.FromSquare, prev.ToSquare, prev.Capture, since,
					input.Ply, prev.Rush );
			// The hand has set the piece down; fall through to the fade back to rest.
		}

		// Nothing playing: the hand rests on the table. No hover, no selection tracking —
		// a gesture exists only for a CONFIRMED move (the ply branch above), by owner
		// decision. The thinking hand was cut wholesale, not gated.
		return Idle( prev, input.Ply, dt );
	}

	/// <summary>Off the board, fading out SLOWLY (see FadeOutTime). Keeps no square once the
	/// weight is spent: the driver reads Weight 0 as "use the elbows-on-table target" and a
	/// square would be a lie.</summary>
	static HandPose Idle( in HandPose prev, int ply, float dt )
	{
		float w = Approach( prev.Weight, 0f, dt / FadeOutTime );
		// Fade out from where the hand actually IS, so it doesn't snap to the table the
		// frame a hover ends. Once the weight is spent, drop the square entirely.
		if ( w <= 0f )
			return HandPose.None with { Ply = ply };

		return prev with
		{
			Phase = HandPhase.Idle,
			Weight = w,
			Ply = ply,
			Capture = false,
			Since = 0f,
			Rush = 0f,
			PhaseRemaining = 0f,
		};
	}

	/// <summary>
	/// The move's timeline, resolved from <paramref name="since"/> alone — so a pose
	/// re-derived from its clock lands in exactly the same place as one that ran frame by
	/// frame, and there is no accumulated state to get out of step with it.
	///
	/// <para><b>Every phase keeps BOTH squares, and Travel is the only thing that moves.</b>
	/// That is not tidiness — the first version wrote the from-square into <c>ToSquare</c>
	/// while lifting, on the reasoning that a lift doesn't travel so the destination was
	/// redundant. It isn't: <see cref="Advance"/> feeds <c>prev.FromSquare</c> and
	/// <c>prev.ToSquare</c> straight back in on the next frame, so the destination was
	/// erased before the carry could ever read it and <b>every move was carried from its
	/// origin to its origin</b> — a hand that lifts a piece, travels nowhere, and sets it
	/// back down while the piece itself is already across the board. It read as obviously
	/// correct and the harness caught it on the first run. Keep the pair intact.</para>
	///
	/// <para><b>A capture is a different gesture, not a faster one.</b> You cannot put a
	/// piece on an occupied square: the victim comes off FIRST, goes to its own tray, and
	/// only then does the attacker move. That is two trips, and pretending otherwise is what
	/// makes a capture read as one piece eating another.</para>
	/// </summary>
	static HandPose Timeline( int from, int to, bool capture, float since, int ply, float rush )
	{
		const float w = 1f; // a hand holding a piece is never partly on the board

		// ── ONE height for the whole gesture (owner decision, 2026-07-19). ──
		// The hand floats in to piece height and is then COMPLETELY LOCKED in Z:
		// every phase below runs at GraspHeight — no Grasp→Lift→Grasp profile, no
		// height lerps. Pick-up / put-down height variance is an explicit LATER
		// (add it back per-phase when the flat version reads right in the editor).
		// LiftHeight/HoverHeight no longer drive the timeline; their TerryTuning
		// sliders are inert until that variance returns.

		// `since` stays ABSOLUTE — time since this move began — everywhere, including in
		// what gets stored. `t` is the offset into the plain-move part. Keeping the two
		// apart is what lets a pose be re-derived from its own clock.
		//
		// No capture prologue: the hand plays the attacker's Reach/Lift/Carry/Drop and
		// nothing else (see CaptureTime). The `capture` flag still rides the pose — the
		// abandon/replay rule needs the true timeline length, and finger styling may want
		// it later.
		//
		// Every return carries PhaseRemaining — the driver's arrival deadline. The stages
		// exist "if we have time"; the deadline is what makes them true when we don't:
		// however far the hand is from a stage's target, it converges by the stage's end,
		// degrading to a snap. A new ply mid-gesture re-derives everything (the abandon
		// rule) and Rush compresses the clock — reality always wins.
		float t = since;

		// The approach: rest → over the from-square, Weight ramping the hand onto the
		// board. Fingers come from open toward the grasp as the hand closes in.
		if ( t < ReachTime )
		{
			float u = Div( t, ReachTime );
			return new HandPose( HandPhase.Reaching, from, to, false, 0f,
				GraspHeight, Lerp( FingersHovering, FingersGrasping, u ),
				Smoothstep( u ), ply, capture, since, rush, ReachTime - t );
		}
		t -= ReachTime;

		if ( t < LiftTime )
		{
			return new HandPose( HandPhase.Lifting, from, to, false, 0f,
				GraspHeight, FingersHolding, w, ply, capture, since, rush, LiftTime - t );
		}

		if ( t < LiftTime + TravelTime )
		{
			float u = Div( t - LiftTime, TravelTime );
			return new HandPose( HandPhase.Carrying, from, to, false, Smoothstep( u ),
				GraspHeight, FingersHolding, w, ply, capture, since, rush,
				LiftTime + TravelTime - t );
		}

		float d = Clamp01( Div( t - LiftTime - TravelTime, DropTime ) );
		// Travel 1: the hand is over the DESTINATION. Same pair of squares, arrived.
		return new HandPose( HandPhase.Dropping, from, to, false, 1f,
			GraspHeight, Lerp( FingersHolding, FingersReleased, d ),
			w, ply, capture, since, rush,
			Max( LiftTime + TravelTime + DropTime - t, 0f ) );
	}

	/// <summary>UCI ("e2e4", "e7e8q") → two square indices, matching
	/// <c>ChessBoardView.SquareUnderCursor</c>'s rank*8+file encoding.</summary>
	public static bool TryParseUci( string uci, out int from, out int to )
	{
		from = -1;
		to = -1;
		if ( uci is not { Length: >= 4 } ) return false;

		from = SquareIndex( uci[0], uci[1] );
		to = SquareIndex( uci[2], uci[3] );
		if ( from >= 0 && to >= 0 ) return true;

		from = -1;
		to = -1;
		return false;
	}

	static int SquareIndex( char fileChar, char rankChar )
	{
		int file = fileChar - 'a';
		int rank = rankChar - '1';
		if ( file is < 0 or > 7 || rank is < 0 or > 7 ) return -1;
		return rank * 8 + file;
	}

	// ── Scalar helpers ──
	//
	// Hand-rolled rather than MathX/MathF: this file must compile with no Sandbox
	// reference so the harness on the dev host can run it.

	static float Approach( float value, float target, float step )
	{
		if ( value < target ) return value + step >= target ? target : value + step;
		if ( value > target ) return value - step <= target ? target : value - step;
		return target;
	}

	static float Div( float a, float b ) => b <= 0f ? 1f : a / b;

	static float Min( float a, float b ) => a < b ? a : b;

	static float Max( float a, float b ) => a > b ? a : b;

	static float Clamp01( float v ) => v < 0f ? 0f : v > 1f ? 1f : v;

	static float Lerp( float a, float b, float t )
	{
		t = Clamp01( t );
		return a + ( b - a ) * t;
	}

	static float Smoothstep( float t )
	{
		t = Clamp01( t );
		return t * t * ( 3f - 2f * t );
	}
}
