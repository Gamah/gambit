namespace Gambit.Chess;

/// <summary>What a seated player's working hand is doing right now.</summary>
public enum HandPhase
{
	/// <summary>Not on the board — elbows on the table.</summary>
	Idle,

	/// <summary>Floating over a piece the player could move, never touching it.</summary>
	Hover,

	/// <summary>A piece is picked out: hand down at it, fingers nearly closed.</summary>
	Selected,

	/// <summary>A move landed: closing on the from-square and lifting.</summary>
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
/// <para>It carries its own <see cref="Ply"/> and <see cref="Since"/> rather than leaving
/// them to the caller: that is what makes <see cref="TerryPose.Advance"/> a closed state
/// machine, and what lets the abandon rule below be PROVEN here instead of reviewed.</para>
/// </summary>
/// <param name="Phase">What the hand is doing.</param>
/// <param name="FromSquare">Square index (rank*8+file) the hand is at, or −1 when idle.</param>
/// <param name="ToSquare">Where it is heading; equal to <paramref name="FromSquare"/> unless carrying.</param>
/// <param name="Travel">0..1 along From → To. Only ever non-zero while carrying.</param>
/// <param name="Height">World units above the board surface.</param>
/// <param name="FingerClose">0..1 → the animgraph's <c>holdtype_pose_hand</c>.</param>
/// <param name="Weight">0..1 blend from the elbows-on-table idle target to the square target.</param>
/// <param name="Ply">The move count this pose was resolved against — see the abandon rule.</param>
/// <param name="Since">Seconds into the pickup timeline (Lifting → Carrying → Dropping).</param>
public readonly record struct HandPose(
	HandPhase Phase,
	int FromSquare,
	int ToSquare,
	float Travel,
	float Height,
	float FingerClose,
	float Weight,
	int Ply,
	float Since )
{
	/// <summary>Nothing has been observed yet: idle, no square, no weight, ply 0.</summary>
	public static readonly HandPose None =
		new( HandPhase.Idle, -1, -1, 0f, 0f, 0f, 0f, 0, 0f );

	/// <summary>The hand is on the board rather than resting on the table.</summary>
	public bool OnBoard => FromSquare >= 0;
}

/// <summary>What the driver observed about this seat this frame.</summary>
/// <param name="Hover">Square the seated player is hovering, or −1. Already means "a square
/// this player can act on" — <c>ChessBoardView</c> only assigns it past its own input gate
/// (their turn or a premove, no promotion picker up, camera settled), so there is no extra
/// predicate to apply here.</param>
/// <param name="Selected">Square they have picked out, or −1.</param>
/// <param name="Ply">The game's move count.</param>
/// <param name="LastMoveUci">UCI of the move that produced <paramref name="Ply"/>, or null.</param>
/// <param name="SeatMoved">That move was played by the seat being animated.</param>
/// <param name="GameLive">There is a game to have hands about.</param>
public readonly record struct HandInput(
	int Hover,
	int Selected,
	int Ply,
	string LastMoveUci,
	bool SeatMoved,
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
	// ── The pickup timeline ──
	//
	// Post-hoc by construction: a move is only observable once it has already been played,
	// so the front half (reach, close) is driven live from hover/selection and this covers
	// what is left — lift, travel, set down. Total 0.56s, which is about as long as a
	// pickup can take before the piece has visibly been on its new square for a while.

	public const float LiftTime = 0.14f;
	public const float TravelTime = 0.26f;
	public const float DropTime = 0.16f;
	public const float PickupTime = LiftTime + TravelTime + DropTime;

	/// <summary>
	/// Height a hovering hand floats at, above the board surface.
	///
	/// <para><b>This was 6, on the reasoning "above a pawn (4.8) and below a king (9.6)" —
	/// which is exactly backwards.</b> Clearing the SHORTEST piece is not clearance at all:
	/// a hand at 6 is buried to the wrist in every king, queen and bishop it passes, and
	/// reads as pawing at the set. The tallest piece is the king at 9.6, so hovering starts
	/// there and adds room to see under. A hand you can see daylight beneath reads as
	/// considering a piece; one at piece height reads as touching it.</para></summary>
	public const float HoverHeight = 14f;

	/// <summary>Height the fingers close at — reaching DOWN toward the piece from the hover,
	/// but still clear of the king's 9.6. Never touching: the pieces are rendered from the
	/// FEN and the hand is decoration, so a hand that appeared to hold one would be lying
	/// about which of them is real.</summary>
	public const float GraspHeight = 11f;

	/// <summary>Height a carried piece travels at: clear of everything on the board, and
	/// above the hover so a pickup visibly lifts.</summary>
	public const float LiftHeight = 17f;

	/// <summary>Seconds for <see cref="HandPose.Weight"/> to cross its full range. The
	/// hand fades between the idle target and the board rather than teleporting when the
	/// cursor crosses onto a square.</summary>
	public const float FadeTime = 0.25f;

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
	public static HandPose Advance( in HandPose prev, in HandInput input, float dt )
	{
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
				return Timeline( from, to, since: 0f, ply: input.Ply, weight: 1f );

			return Idle( prev with { Ply = input.Ply }, input.Ply, dt );
		}

		// A pickup already in flight, and the game hasn't moved under it: run it out.
		if ( prev.Phase is HandPhase.Lifting or HandPhase.Carrying or HandPhase.Dropping )
		{
			float since = prev.Since + dt;
			if ( since < PickupTime )
				return Timeline( prev.FromSquare, prev.ToSquare, since, input.Ply, prev.Weight );
			// The hand has set the piece down; fall through to whatever it is doing NOW,
			// which is usually hovering the square it is still over.
		}

		// Selected beats hover: the hand is on the piece it has picked out, not chasing
		// the cursor round the board looking for somewhere to put it.
		if ( input.Selected >= 0 )
			return Square( HandPhase.Selected, input.Selected, GraspHeight, FingersGrasping,
				prev, input.Ply, dt );

		if ( input.Hover >= 0 )
			return Square( HandPhase.Hover, input.Hover, HoverHeight, FingersHovering,
				prev, input.Ply, dt );

		return Idle( prev, input.Ply, dt );
	}

	/// <summary>The hand on a square, fading in from wherever it was.</summary>
	static HandPose Square( HandPhase phase, int square, float height, float fingers,
		in HandPose prev, int ply, float dt ) =>
		new( phase, square, square, 0f, height, fingers,
			Approach( prev.Weight, 1f, dt ), ply, 0f );

	/// <summary>Off the board, fading out. Keeps no square: the driver reads Weight 0 as
	/// "use the elbows-on-table target" and the square would be a lie.</summary>
	static HandPose Idle( in HandPose prev, int ply, float dt )
	{
		float w = Approach( prev.Weight, 0f, dt );
		// Fade out from where the hand actually IS, so it doesn't snap to the table the
		// frame a hover ends. Once the weight is spent, drop the square entirely.
		if ( w <= 0f )
			return HandPose.None with { Ply = ply };

		return prev with
		{
			Phase = HandPhase.Idle,
			Travel = prev.Travel,
			Weight = w,
			Ply = ply,
			Since = 0f,
		};
	}

	/// <summary>
	/// The pickup timeline, resolved from <paramref name="since"/> alone — so a pose
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
	/// </summary>
	static HandPose Timeline( int from, int to, float since, int ply, float weight )
	{
		weight = 1f; // a pickup is never partial — the hand is holding a piece

		if ( since < LiftTime )
		{
			float u = LiftTime <= 0f ? 1f : since / LiftTime;
			return new HandPose( HandPhase.Lifting, from, to, 0f,
				Lerp( GraspHeight, LiftHeight, u ), FingersHolding, weight, ply, since );
		}

		if ( since < LiftTime + TravelTime )
		{
			float u = TravelTime <= 0f ? 1f : ( since - LiftTime ) / TravelTime;
			return new HandPose( HandPhase.Carrying, from, to, Smoothstep( u ),
				LiftHeight, FingersHolding, weight, ply, since );
		}

		float d = Clamp01( DropTime <= 0f ? 1f : ( since - LiftTime - TravelTime ) / DropTime );
		// Travel 1: the hand is over the DESTINATION. Same pair of squares, arrived.
		return new HandPose( HandPhase.Dropping, from, to, 1f,
			Lerp( LiftHeight, GraspHeight, d ), Lerp( FingersHolding, FingersReleased, d ),
			weight, ply, since );
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

	static float Approach( float value, float target, float dt )
	{
		float step = dt / FadeTime;
		if ( value < target ) return value + step >= target ? target : value + step;
		if ( value > target ) return value - step <= target ? target : value - step;
		return target;
	}

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
