using System;
using System.Collections.Generic;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Builds N chess tables arranged as a regular N-gon in the middle of the room.
/// Each station is a table of box primitives with a chess board and a full set of
/// procedural pieces (ChessSetBuilder) at the start position, plus two seats —
/// White faces the ring center from outside, Black sits opposite — each with its
/// own locked-camera anchor pitched down over the board.
///
/// Build paths (inherited from the arcade ring):
/// - Editor: OnEnabled builds a NotSaved preview (same pattern as LobbyRoom).
/// - Play: only the HOST builds, from LobbyNetworkManager.OnHostInitialize, which then
///   NetworkSpawns each station so its [Sync] seat occupancy replicates. Clients must
///   NOT build their own copies — they receive the host's via network spawn.
/// </summary>
public sealed class ChessRing : Component, Component.ExecuteInEditor
{
	/// <summary>How many stations / sides of the N-gon.</summary>
	[Property] public int StationCount { get; set; } = 8;

	/// <summary>Distance from ring center to each table, as tuned for an 8-station
	/// ring. The actual radius scales with StationCount to keep the spacing between
	/// neighboring tables constant (see RingRadius), clamped so the seats stay
	/// inside the room. 180 matches the old arcade ring's scene tuning.</summary>
	[Property] public float Radius { get; set; } = 180f;

	/// <summary>Yaw added to the whole ring. Station 0 faces outward along
	/// (baseAngle + this); the base angle aims station 0 at the player spawn.</summary>
	[Property] public float RingYawOffset { get; set; } = 90f;

	/// <summary>Build the box-primitive table under each station.</summary>
	[Property] public bool BuildTables { get; set; } = true;

	/// <summary>Uniform multiplier on all table box positions and sizes.</summary>
	[Property] public float TableScale { get; set; } = 1.5f;

	/// <summary>Wood tint for the table body; the board squares/frame derive their
	/// own fixed tints.</summary>
	[Property] public Color TableColor { get; set; } = new Color( 0.16f, 0.11f, 0.07f );

	/// <summary>Chess board span (all 8 files) in base units (× TableScale). The
	/// tabletop margin around it is not slack — see TopSizeX/TopSizeY.</summary>
	[Property] public float BoardSize { get; set; } = 26f;

	/// <summary>Straight-line distance (world units) from the board center to each
	/// seat's locked-camera anchor. The camera orbits the board center at this
	/// radius — SeatPitch rotates it up/down WITHOUT changing how close the board
	/// looks. Needs enough range that the NEAR back rank fits below screen center:
	/// perspective makes the near half of a tilted board subtend far more vertical
	/// FOV than the far half, so it's the first thing to clip when too close.</summary>
	[Property] public float SeatOrbitRadius { get; set; } = 56f;

	/// <summary>Camera elevation in degrees on that orbit: 0 = level with the
	/// board, 90 = straight overhead. Rotates around the board center, so range
	/// (and apparent board size) stays fixed while the view tilts.</summary>
	[Property, Range( 15f, 85f )] public float SeatPitch { get; set; } = 55f;

	/// <summary>Extra downward tilt of the seat camera's AIM, in degrees, applied
	/// as a pure rotation — the camera keeps its orbit position but pitches its
	/// look direction below the board center. This lowers the view by rotating
	/// the camera down rather than translating it, so use it (not SeatPitch/
	/// height) to angle the board down in frame. 0 = aim straight at the board
	/// center.</summary>
	[Property, Range( -20f, 20f )] public float SeatLookDownAngle { get; set; } = 8f;

	/// <summary>Height of the top-down (nadir) camera above the board centre for 2D play mode
	/// (M16), in the same units as <see cref="SeatOrbitRadius"/>. A separate anchor entirely —
	/// never derived from <see cref="SeatPitch"/>, which also feeds the chair/walk-up placement
	/// (<see cref="SeatSpotX"/>). Tune so the 8×8 board fills the frame from straight overhead.</summary>
	[Property] public float TopDownHeight { get; set; } = 56f;

	/// <summary>Calibration multiplier on the computed UI rect (see ScreenFractionRect) —
	/// nudge until engaged UI lines up with the board on screen.</summary>
	[Property] public float UiFit { get; set; } = 1f;

	// ══ Seated terries (M13) ══
	//
	// EVERY knob in this block is a code default and can only be retuned by editing and
	// hotloading — ChessRing is NOT in lobby.scene (LobbyRoom self-provisions it), exactly
	// as CLAUDE.md warns for SpectatorWall. Not a bug; a real ergonomic cost. gambit_terry
	// prints the whole chain so the loop is at least legible.

	/// <summary>
	/// The kill switch, and it is not optional. False restores <c>HideLocalAvatar</c>'s
	/// wholesale behaviour: your own avatar simply isn't drawn while seated, as it hasn't
	/// been since the fork.
	///
	/// <para><b>Git history demands this.</b> Commit <c>0f68c91</c> — "Don't draw the local
	/// avatar while seated (fixes Terry filling the camera after a seat switch)". The
	/// arithmetic explains that bug rather than excusing it: a STANDING avatar planted at
	/// the walk-up spot puts its head's front face around x = −27, well inside the frame's
	/// bottom edge at −30.1. A broken rig must not be able to re-ship it.</para></summary>
	[Property] public bool TerrySeated { get; set; } = true;

	/// <summary>
	/// How far back of the board a seated avatar's origin is planted, as |x|.
	///
	/// <para>Pinned from BOTH ends, which is why it is a knob and not a constant. Forward
	/// of ~34 the citizen's belly is inside the tabletop slab (the walk-up spot at 32.12
	/// puts it ~3.4 units in). Back of ~38 the elbows-on-table idle stops being physically
	/// possible — at 42 the elbow reach is 32.0 against a table edge of 30. 36 is the
	/// middle of the only band that works.</para>
	///
	/// <para>The belly figure rests on an ESTIMATED torso half-depth of ~5.5 scaled to a
	/// 72-unit citizen, which could easily be ±2. That is the tightest guess in M13 and the
	/// reason this must stay tunable.</para>
	///
	/// <para><b>BACK to 36 — the M13 scoot-in (36→26) is undone by the half-rise.</b> The
	/// scoot bought one rank of seated reach at a real cosmetic price nobody could see from
	/// this host: at 26 the seated chest sits a third of the way INTO the tabletop, and the
	/// first joined client to look at a seated terry from outside reported exactly that.
	/// The half-rise makes the trade unnecessary — the planner reads the live skeleton every
	/// frame, so a further seat just means a longer rise, and the harness at Back=36 reads
	/// BETTER than at 26 (54/64 vs 51/64, worst corner 6.8 vs 7.8): the longer horizontal
	/// run lines the rise up with the far squares. The measurement lore stands: shoulder
	/// (arm_upper_R) at 36 sits at x −44.6, 8.6 behind the plant origin, arm 19.9u — a
	/// SEATED arm reaches nothing from here, which is the half-rise's whole reason.</para></summary>
	[Property] public float SeatSitBack { get; set; } = 36f;

	/// <summary>
	/// Height a seated avatar's ORIGIN is planted at, station-local. The FLOOR, by default.
	///
	/// <para><b>The citizen's origin is its feet, not its hips</b> — the sit pose carries
	/// its own seat height above the origin and <see cref="SitOffsetHeight"/> trims it.
	/// citizen.vanmgrph's comment on that parameter ("30 units at the source, 12 after
	/// scaling to inches. Feet IK disables through tag on +12 node") only makes sense if
	/// the feet reach the floor at offset 0 — dangling feet on a high stool is what the tag
	/// turns IK off for. Planting the origin on the chair's pad would float the terry a
	/// whole seat-height into the air.</para></summary>
	[Property] public float SeatSitZ { get; set; } = 0f;

	/// <summary>
	/// Trim on the sit pose's own seat height, in INCHES — the animgraph's
	/// <c>sit_offset_height</c>, domain ±12 (a hard clamp in the graph, not a suggestion).
	///
	/// <para><b>This is the number the whole eye-height chain rests on, and it cannot be
	/// known on this host.</b> Dial it (or <see cref="ChairSeatTopZ"/>; they meet in the
	/// middle) until the hips land on the pad. Everything downstream — what is in frame,
	/// where the elbows reach — is measured from wherever this puts them.</para></summary>
	[Property, Range( -12f, 12f )] public float SitOffsetHeight { get; set; } = 0f;

	/// <summary>
	/// Blends the seat camera from the seated player's EYES (0) to today's orbit anchor (1).
	///
	/// <para><b>The default is 1 and reproduces today's anchor bit-for-bit</b>, not "near
	/// enough": <c>Vector3.Lerp(eye, orbit, 1)</c> delegates to System.Numerics, which is
	/// <c>a·(1−t) + b·t</c>, so at t = 1 it is <c>a·0 + b·1</c> = exactly <c>b</c> — checked
	/// against the real implementation, and true for any eye position at all. The rotation
	/// line below is then character-for-character the one that was already there. So
	/// shipping this changes nothing until someone turns it.</para>
	///
	/// <para><b>Turning it down costs the board, and there is a floor.</b> An eye-level
	/// camera is only 13.6° to the far rank, where a king hides EIGHT squares behind it and
	/// near/far foreshortening goes from today's 1.9× to 5.1×. The current anchor is
	/// playable precisely because it is 44° to the far rank. Picking does not break at any
	/// value (SquareUnderCursor is exact ray-plane math) — readability does.</para>
	///
	/// <para><b>And it cannot reach 0 while keeping arms-only.</b> As it descends, more of
	/// your own torso enters frame and there is NO clean mechanism to remove it: the citizen
	/// has no arms mesh (they live in Torso_LOD*, i.e. the Chest bodygroup) and bone-zeroing
	/// takes the arms with the spine. Usable band is roughly 0.6–1.0. Stated as a limit
	/// rather than discovered as a disappointment.</para>
	///
	/// <para>It blends the POSITION only, never the rotation — which is what buys all of the
	/// above: the camera keeps aiming at the board at every value, and there is one formula
	/// rather than two. It also leaves SeatOrbitRadius/SeatPitch alone, so the walk-up spot,
	/// the ring's radius clamp and the (dead) UI rect are untouched at every value. That is
	/// the point of the knob.</para></summary>
	[Property, Range( 0f, 1f )] public float SeatEyeBlend { get; set; } = 1f;

	/// <summary>Where the seated eye IS, as |x| back of the board and height — the blend's
	/// far end. <b>[GUESS]</b>: both rest on "seated eye ≈ 31 above the pad", an estimate
	/// from human proportion scaled to a 72-unit citizen, not a measurement. The skeleton
	/// lives in the compiled model, which is in neither repo.
	///
	/// <para><b>Deliberately NOT read from the eye bone.</b> Two reasons, the second
	/// decisive: bone transforms resolve during the animation update so an OnUpdate read can
	/// be a frame stale — and a camera welded to a breathing, blinking head bone makes the
	/// board swim. Static constants have no ordering hazard and no jitter. If true
	/// head-tracking is ever wanted, the path is TryGetBoneTransformAnimation in a late
	/// update.</para></summary>
	[Property] public float SeatEyeBack { get; set; } = 36f;
	[Property] public float SeatEyeHeight { get; set; } = 49f;

	/// <summary>
	/// Where a seated player's hand rests with nothing to do: elbows on the table.
	///
	/// <para><b>X spends a margin this file says is spent, and that is deliberate.</b> The
	/// tabletop-margin comment above states both X margins are "kept clear — they are the
	/// seat cameras' sightlines". The hands go there anyway, because the hands ARE the
	/// sightline's subject: that margin is the only part of the table a seat camera looks
	/// down over, which is exactly where you would put your elbows. The board frame's
	/// half-extent is 21.75 and the tabletop's is 30, so 26 lands squarely in the 8.25-wide
	/// margin — on the table, clear of the board.</para>
	///
	/// <para>Y is the offset toward the player's own outside; Z is the tabletop's surface
	/// plus a little, so a wrist rests ON it rather than in it.</para></summary>
	[Property] public float HandIdleX { get; set; } = 26f;
	[Property] public float HandIdleY { get; set; } = 10f;
	[Property] public float HandIdleZ { get; set; } = 31f;

	/// <summary>
	/// Extra height on every hand target over the board — the trim for "the hand is too
	/// close to the pieces".
	///
	/// <para><b>It lives here and not in TerryPose because TerryPose cannot read a
	/// [Property].</b> That file is deliberately Sandbox-free so it can be driven through
	/// real games in a harness on a host with no engine, which is exactly where its carry
	/// bug was caught — so its heights are constants and this is the knob that tunes them
	/// without giving that up. Turn TerryPose's constants for the SHAPE of the motion; turn
	/// this for how high the whole thing rides.</para></summary>
	[Property] public float HandLift { get; set; } = 0f;

	/// <summary>
	/// Offset from the square to where the WRIST goes, in the hand's own rotated frame —
	/// so the thumb and index end up over the square rather than the wrist.
	///
	/// <para><b>IK aims a BONE, and the bone is the wrist.</b> Put hand_R on a square and
	/// the fingers hang past it; the piece the player is thinking about is under the palm
	/// at best. This pulls the wrist back and up along the hand's own axes so the grip lands
	/// on the target instead. Read the real hand_R off a seated citizen with
	/// <c>gambit_terry</c> and tune against it — the ruler prints exactly this bone.</para></summary>
	[Property] public Vector3 HandGripOffset { get; set; } = new( -3f, 0f, 3f );

	/// <summary>Pitch of the hand over the board — nose-down, so the fingers point at the
	/// piece rather than the palm facing it.</summary>
	[Property, Range( 0f, 90f )] public float HandPitch { get; set; } = 60f;

	/// <summary>
	/// How far toward the tray a hand actually carries a piece it has taken, 0..1.
	///
	/// <para><b>It is not 1, and the arithmetic is why.</b> Each player's losses sit in
	/// their OWN tray, so taking a black knight means reaching to |y| = 28 — about 29 units
	/// across from a sitter whose pelvis is at y ≈ −0.9. That is a long way past where an
	/// arm plausibly goes, and an IK target out of reach doesn't fail politely: it
	/// straightens the arm and drags the shoulder after it.</para>
	///
	/// <para>So the hand lifts the piece, carries it most of the way, and lets go — and
	/// <see cref="ChessBoardView"/>'s own capture slide (which has been walking pieces to
	/// their trays since M11) finishes the trip. That is also just what happens when you
	/// take a piece: you lift it clear and set it down, you don't post it.</para></summary>
	[Property, Range( 0f, 1f )] public float HandDiscardReach { get; set; } = 0.6f;

	/// <summary>
	/// The seated arm's usable reach, station units — the radius of the sphere around the
	/// shoulder (arm_upper_R) that <see cref="LobbyPlayer.ApplyHandPose"/> clamps every hand
	/// target into.
	///
	/// <para><b>This is the load-bearing number, and it is measured, not guessed.</b>
	/// gambit_terry reads the arm off the real skeleton at 19.9u, and the shoulder sits far
	/// enough back that most of a 34-deep board is beyond it. An IK target past the arm doesn't
	/// fail politely — it straightens and drags the shoulder after it (the front-edge fist in
	/// the bug report). Clamping the target onto this sphere instead keeps the hand POINTING at
	/// the true square from as far as the arm honestly goes. The hand never touches the FEN
	/// piece anyway, so reaching toward a far piece reads better than straining short.</para>
	///
	/// <para>Default 18 = ~0.9 of the measured 19.9, backed off so the arm isn't at full lock.
	/// Raise it toward 20 for a longer reach and a straighter arm; drop it for a softer,
	/// more-bent one. Tune against gambit_terry_probe.</para></summary>
	[Property] public float HandReach { get; set; } = 18f;


	/// <summary>Overhead table spot brightness. Strictly neutral white so the piece
	/// and square tints read true (MarqueeGlow applies the user multiplier in play).</summary>
	[Property] public float MarqueeBrightness { get; set; } = 3.3f;

	public static ChessRing Instance { get; private set; }

	readonly List<GameObject> _spawned = new();
	bool _runtimeBuilt;

	// Board vertical stack in base units (× TableScale): table top surface, board
	// frame on it, squares on the frame, pieces on the squares.
	const float TableTopZ = 20f;
	const float FrameThickness = 1f;
	const float CellThickness = 0.5f;

	// The table's own body, in base units. Named because M13's chair has to fit UNDER
	// this thing and AROUND its foot, and every one of those clearances is the
	// difference between two of these numbers. They were typed inline in
	// BuildChessTable, which is fine for a builder nothing else reads and no use at all
	// to something that has to keep 1.5 units off the underside of the slab.
	const float TopThickness = 2f;
	const float FootSizeXY = 20f;
	const float FootHeight = 1f;
	const float PedestalSizeXY = 10f;
	const float PedestalHeight = 17f;

	/// <summary>The tabletop's TOP surface — what everything on the table stands on.</summary>
	public float TableTopSurfaceZ => TableTopZ * TableScale;                        // 30.0

	/// <summary>The tabletop slab's UNDERSIDE. This is the number a chair's armrest is
	/// derived FROM: it is the only thing that decides how tall an arm can be before it
	/// fouls the table, and typing the armrest height instead is how you get a 0.25
	/// clearance and find out in the room. See ChairArmrestZ.</summary>
	public float TableTopSlabBottomZ => ( TableTopZ - TopThickness ) * TableScale;  // 27.0

	/// <summary>|x| of the tabletop's edge.</summary>
	public float TableEdgeX => TopSizeX * 0.5f * TableScale;                        // 30.0

	/// <summary>|x| of the foot plate's edge — the widest part of the table at floor
	/// level, and therefore what bounds how far a chair can tuck in.</summary>
	public float FootEdgeX => FootSizeXY * 0.5f * TableScale;                       // 15.0

	// Tabletop footprint in base units. The board frame is BoardSize + 3 = 29
	// square, so these two numbers ARE the margins, and each margin has a job:
	//
	//   X (40 → 5.5 per side): BOTH are kept clear — they are the seat cameras'
	//     sightlines. −X is where White's camera looks down the board from and +X is
	//     Black's, so anything mid-edge there is in a player's foreground. The clock
	//     stood at +X once and read as a wall in Black's face.
	//   Y (44 → 7.5 per side): −Y is the clock strip (BuildStationClock) and then
	//     White's tray; +Y is Black's tray, with the number plaque hanging below its
	//     edge, clear of the tray that sits on it.
	//
	// Both were 34 (a 2.5 margin) until M11. ChessRing's own comment had promised
	// "a healthy margin for clocks/captures later" since M1 — it wasn't; two
	// columns of pieces need 7.5 and the plaque was sitting in one of the trays.
	// Widening was the cheap half of that debt.
	const float TopSizeX = 40f;
	const float TopSizeY = 44f;

	// A tray is a shallow felt-dark slab on the tabletop, outboard of the frame.
	const float TrayThickness = 0.4f;
	const int TrayRows = 8;  // along X, one per board file's worth of length
	const int TrayCols = 2;  // outward from the board

	// ── The Y margin's budget ──
	//
	// 7.5 base units per side, between the board frame's edge and the tabletop's, and
	// three things want it: the clock (a thin strip inboard, −Y only), the tray, and a
	// strip of bare tabletop at the edge so the tray reads as a tray sitting ON the
	// table rather than as the table's own border.
	//
	// It had NO gaps at all: the slab was `TrayCols * cell + 1` = exactly 7.5, so it ran
	// flush from the board frame to the table edge on both sides. Nobody chose that — it
	// is what "2 columns plus a lip" happens to equal at these numbers, which is the same
	// accident as the "healthy margin" that wasn't.
	//
	// Everything below DERIVES from these four, so the tray, its slots and the clock
	// cannot drift apart: change one and the rest move.
	// This 0.2 is now SHARED with the material bar, which lives on the clock's front face and
	// therefore projects into it (see BuildClockBar). A 0.2-thick bar spends all of it and puts
	// its fill at −14.44 against a board frame edge of −14.5 — a real intersection, not a near
	// miss, since the frame is a slab at clock-local z 0..1 and the bar sits at z 0.4..1.8.
	// The bar is thin BECAUSE of this number; widening the gap instead would shove the whole
	// assembly away from the board and take the tray's slot pitch with it.
	const float ClockBoardGap = 0.2f;   // board frame → clock, and the bar's headroom
	const float ClockTrayGap = 0.2f;    // clock → tray
	const float TrayEdgeGap = 1.0f;     // tray → tabletop edge

	/// <summary>Inboard end of the Y margin: the board frame's edge.</summary>
	float MarginInnerY => ( BoardSize + 3f ) * 0.5f;

	/// <summary>Outboard end of the Y margin: the tabletop's edge.</summary>
	float MarginOuterY => TopSizeY * 0.5f;

	/// <summary>Centre of the clock's thin strip. −Y only — see BuildStationClock.</summary>
	float ClockCenterY => MarginInnerY + ClockBoardGap + ClockDepth * 0.5f;

	/// <summary>The tray sits outboard of the clock's strip, on BOTH sides. Symmetric on
	/// purpose even though only −Y has a clock: two trays at different distances from
	/// their own board edge reads as a mistake, and the bare strip the clock leaves at +Y
	/// balances it.</summary>
	float TrayInnerY => MarginInnerY + ClockBoardGap + ClockDepth + ClockTrayGap;
	float TrayOuterY => MarginOuterY - TrayEdgeGap;
	float TrayWidth => TrayOuterY - TrayInnerY;

	/// <summary>Base-unit centre of a tray strip.</summary>
	float TrayCenterY => ( TrayInnerY + TrayOuterY ) * 0.5f;

	/// <summary>Slot spacing ACROSS a tray. Narrower than the board's cell — the tray is
	/// narrower than two cells now — so captured pieces sit closer together than they did
	/// on the board. That reads fine for a pile of dead pieces and is the price of the
	/// clock sharing this margin; if it looks crowded, TrayEdgeGap is the knob.</summary>
	float TraySlotPitchY => TrayWidth / TrayCols;

	/// <summary>World height of the playing surface above the station floor.</summary>
	public float BoardSurfaceZ => ( TableTopZ + FrameThickness + CellThickness ) * TableScale;

	/// <summary>Station-local position of one slot in a player's captured-piece
	/// tray, at piece-base height. <paramref name="white"/> selects WHOSE tray:
	/// each player's losses sit on their OWN right (White faces +X, so White's
	/// right is −Y — s&amp;box is Y-left).
	/// <para>Slots fill along X first (8 per column), then outward. Ordering is
	/// ChessBoardView's business, not the ring's.</para>
	/// <para>The two axes have DIFFERENT pitches, and that is not an oversight: along X
	/// the tray is as long as the board, so a cell's pitch fits; across Y it is narrower
	/// than two cells, so it uses its own. Both derive from the margin budget.</para></summary>
	public Vector3 TraySlotLocalPosition( bool white, int slot )
	{
		float cell = BoardSize / 8f;
		int row = slot % TrayRows;
		int col = slot / TrayRows;
		float x = ( row - ( TrayRows - 1 ) * 0.5f ) * cell;
		float y = TrayCenterY + ( col - ( TrayCols - 1 ) * 0.5f ) * TraySlotPitchY;
		return new Vector3( x, white ? -y : y, TableTopZ + TrayThickness ) * TableScale;
	}

	/// <summary>
	/// Normalized (0..1) viewport rect the board occupies while the camera is locked
	/// at a seat anchor. Same plain-trig approach as the old cabinet screen rect: the
	/// camera sits a known distance from a target of known half-extent, so the rect
	/// follows from the FOV. The camera is pitched down rather than square-on, so this
	/// is approximate — UiFit calibrates it.
	/// </summary>
	public static Rect ScreenFractionRect()
	{
		var ring = Instance;
		var cam = ring?.Scene?.Camera;
		if ( ring == null || cam == null )
			return new Rect( 0f, 0f, 1f, 1f );

		float halfWorld = ring.BoardSize * 0.5f * ring.TableScale * ring.UiFit;
		float dist = ring.SeatOrbitRadius; // camera orbits the board center at this range
		if ( dist <= 1f ) return new Rect( 0f, 0f, 1f, 1f );

		float tanHalf = MathF.Tan( cam.FieldOfView * 0.5f * (MathF.PI / 180f) );
		float widthFrac = halfWorld / (dist * tanHalf);
		float heightFrac = widthFrac * (Screen.Width / Screen.Height);

		widthFrac = Math.Clamp( widthFrac, 0.05f, 1f );
		heightFrac = Math.Clamp( heightFrac, 0.05f, 1f );

		return new Rect( (1f - widthFrac) * 0.5f, (1f - heightFrac) * 0.5f, widthFrac, heightFrac );
	}

	/// <summary>Inline style placing a panel over the board area (percent units, so
	/// it works regardless of ScreenPanel auto-scaling).</summary>
	public static string UiRectStyle()
	{
		var r = ScreenFractionRect();
		return $"left: {r.Left * 100f:0.##}%; top: {r.Top * 100f:0.##}%; width: {r.Width * 100f:0.##}%; height: {r.Height * 100f:0.##}%;";
	}

	protected override void OnEnabled()
	{
		Instance = this;
		BuildInternal();
	}

	// Fires on editor property changes and after deserialization, so the ring preview
	// regenerates in the editor without entering play mode. Guarded so it never clobbers
	// the host's authoritative networked build during play.
	protected override void OnValidate()
	{
		if ( !Active || _runtimeBuilt ) return;
		BuildInternal();
	}

	/// <summary>Re-run the build after a code hotload (Editor/HotloadRebuild.cs).
	/// Same guard as OnValidate: never clobbers the host's networked play-mode build.</summary>
	public void RebuildPreview()
	{
		if ( !Active || _runtimeBuilt ) return;
		BuildInternal();
	}

	protected override void OnStart()
	{
		// OnStart is NOT in ExecuteInEditor's method set, so it only runs in play mode:
		// drop the local preview built by OnEnabled — the host's Build() + NetworkSpawn
		// (LobbyNetworkManager.OnHostInitialize) is the authoritative copy, and joining
		// clients receive the stations over the network instead of building their own.
		if ( !_runtimeBuilt )
			Clear();
	}

	protected override void OnDisabled()
	{
		if ( Instance == this ) Instance = null;
		Clear();
	}

	/// <summary>Host-side runtime build; returns the station roots (for NetworkSpawn).</summary>
	public IReadOnlyList<GameObject> Build()
	{
		_runtimeBuilt = true;
		var stations = BuildInternal();
		Log.Info( $"[Gambit] ChessRing built {stations.Count} chess tables (radius {RingRadius( StationCount ):0})" );
		return stations;
	}

	/// <summary>Network-spawn every station in the scene so its [Sync] seat occupancy
	/// replicates — used by LobbyNetworkManager on host init and by the animated
	/// station-count rebuild.</summary>
	public void NetworkSpawnStations()
	{
		foreach ( var station in Scene.GetAllComponents<ChessStation>() )
		{
			station.GameObject.NetworkSpawn();
			// Default NetworkOrphaned.Destroy kills host-owned objects for everyone when
			// the host disconnects — hand them to the migrated host instead.
			station.GameObject.Network.SetOrphanedMode( NetworkOrphaned.Host );
		}
	}

	// ── Host station-count rebuild (inherited from the arcade ring, issue #49) ──
	// On change: hold while the host is still on the settings board, then 0.5s after
	// the panel closes slide every station down through the floor, rebuild the ring
	// with the new count (network-spawning the new stations), and slide back up.
	// Host-only; station transforms are networked, so clients see the slide.

	/// <summary>How far the tables sink below their rest height (table + signs are
	/// ~100 units tall at the default TableScale).</summary>
	[Property] public float SlideDepth { get; set; } = 110f;

	/// <summary>Duration of each table's slide leg (down, and up again).</summary>
	[Property] public float SlideSeconds { get; set; } = 0.9f;

	/// <summary>Stagger between successive tables starting their slide, so they
	/// drop/rise in sequence rather than all at once.</summary>
	[Property, Range( 0.05f, 0.75f )] public float SlideStaggerSeconds { get; set; } = 0.25f;

	/// <summary>Which servo slide SFX each table emits while descending /
	/// ascending (issue #54). Forward on descend, same sound reversed on ascend.</summary>
	[Property] public SlideSfx SlideSound { get; set; } = SlideSfx.Classic;

	public enum SlideSfx { Classic, Heavy, Quick, Ratchet }

	string SlideVariant => SlideSound switch
	{
		SlideSfx.Heavy => "slide_servo_heavy",
		SlideSfx.Quick => "slide_servo_quick",
		SlideSfx.Ratchet => "slide_servo_ratchet",
		_ => "slide_servo_classic",
	};

	/// <summary>True while the slide animation itself is running.</summary>
	public bool Rebuilding => _slidePhase is SlidePhase.Down or SlidePhase.Up;

	/// <summary>The count the ring is heading toward — StationCount once no change
	/// is pending. The settings UI highlights this one.</summary>
	public int PendingStationCount => _slidePhase == SlidePhase.None ? StationCount : _pendingCount;

	enum SlidePhase { None, Pending, Down, Up }

	SlidePhase _slidePhase = SlidePhase.None;
	TimeSince _slideTime;
	int _pendingCount;
	readonly HashSet<int> _slidePlayed = new(); // station indices that emitted their SFX this leg

	/// <summary>Change the station count with the slide-through-the-floor rebuild.
	/// Ignored unless this client is the host, and blocked while any seat is taken
	/// (re-checked before the slide starts — occupancy can change while pending).
	/// The count can be re-picked freely while pending; picking the current count
	/// cancels. The slide itself waits until the host closes the settings panel.</summary>
	public void HostSetStationCount( int count )
	{
		if ( !Networking.IsHost || Rebuilding ) return;
		count = Math.Clamp( count, 2, 16 );
		if ( AnyStationOccupied() ) return;

		_pendingCount = count;
		_slideTime = 0;
		_slidePhase = count == StationCount ? SlidePhase.None : SlidePhase.Pending;
	}

	bool AnyStationOccupied()
	{
		foreach ( var station in Scene.GetAllComponents<ChessStation>() )
			if ( station.AnySeatTaken ) return true;
		return false;
	}

	// Last SettingsModel version this client applied the PLAY MODE setting at. −1 so the first
	// frame applies it once (after TerryTuning.OnEnabled has done its one-shot push).
	int _handsSettingVersion = -1;

	/// <summary>Push the player's PLAY MODE (M16) into the three client-local gates it drives.
	///
	/// <para>Version-keyed — writes the statics only when the setting actually changes, exactly like
	/// <see cref="TerryTuning"/>'s own on-change push and the <c>gambit_terry_*</c> console levers.
	/// That is what keeps them from fighting: between player edits nobody re-asserts the statics, so
	/// a diagnostic (or the tuning inspector) that forces one mid-run stays authoritative. ChessRing
	/// is one per client, so this is one write per change rather than one per station.</para>
	///
	/// <list type="bullet">
	/// <item><see cref="SeatedHandSpikes.HandsOn"/> — only <c>3d-arms</c> animates hands; <c>2d</c>
	/// and <c>3d-clean</c> leave them off (bodies still sit, pieces still slide).</item>
	/// <item><see cref="ChessSetBuilder.FlatMode"/> — the render dispatch gate: <c>2d</c> makes every
	/// board build flat glyph quads. The two board views watch this and respawn their pieces.</item>
	/// <item><see cref="SeatedTerry.ForceHidden"/> — <c>2d</c> suppresses the seated bodies, which are
	/// noise under the top-down camera.</item>
	/// </list></summary>
	void ApplyPlayModeSetting()
	{
		if ( _handsSettingVersion == Gambit.UI.SettingsModel.SettingsVersion ) return;
		_handsSettingVersion = Gambit.UI.SettingsModel.SettingsVersion;

		string mode = Gambit.Game.PlayerData.ClampPlayMode( Gambit.Game.PlayerData.Load()?.PlayMode );
		SeatedHandSpikes.HandsOn = mode == "3d-arms";
		ChessSetBuilder.FlatMode = mode == "2d";
		SeatedTerry.ForceHidden = mode == "2d";
	}

	protected override void OnUpdate()
	{
		ApplyPlayModeSetting();

		// Never set in the editor (HostSetStationCount is play-mode, host-only)
		if ( _slidePhase == SlidePhase.None ) return;

		switch ( _slidePhase )
		{
			case SlidePhase.Pending:
				// Hold (and keep re-arming the delay) until the host has closed the
				// settings panel, then give the camera blend-out 1.5s to finish
				if ( SettingsStation.Active != null )
				{
					_slideTime = 0;
					return;
				}
				if ( _slideTime < 1.5f ) return;
				if ( AnyStationOccupied() )
				{
					_slidePhase = SlidePhase.None;
					return;
				}
				_slideTime = 0;
				_slidePlayed.Clear();
				_slidePhase = SlidePhase.Down;
				return;

			case SlidePhase.Down:
				ApplyStaggeredDrop( down: true );
				if ( _slideTime < LegDuration ) return;
				StationCount = _pendingCount;
				BuildInternal();
				SetStationDrop( SlideDepth ); // new stations start below the floor
				NetworkSpawnStations();
				_slideTime = 0;
				_slidePlayed.Clear();
				_slidePhase = SlidePhase.Up;
				return;

			case SlidePhase.Up:
				ApplyStaggeredDrop( down: false );
				if ( _slideTime < LegDuration ) return;
				SetStationDrop( 0f );
				_slidePhase = SlidePhase.None;
				return;
		}
	}

	/// <summary>Total length of a leg once the stagger fans the tables out: the
	/// last table starts at (count-1)*stagger and still needs a full SlideSeconds.</summary>
	float LegDuration => SlideSeconds + Math.Max( 0, _spawned.Count - 1 ) * SlideStaggerSeconds;

	/// <summary>Drive each station's drop on its own staggered timeline and fire its
	/// slide SFX the frame it starts moving (ascend = reversed WAV on the way up).</summary>
	void ApplyStaggeredDrop( bool down )
	{
		for ( int i = 0; i < _spawned.Count; i++ )
		{
			var go = _spawned[i];
			if ( !go.IsValid() ) continue;

			float lt = Math.Clamp( (_slideTime - i * SlideStaggerSeconds) / SlideSeconds, 0f, 1f );
			if ( lt > 0f && _slidePlayed.Add( i ) )
				go.GetComponent<ChessStation>()?.NetSlideSfx( SlideVariant, ascend: !down );

			float drop = down ? SlideDepth * lt * lt : SlideDepth * MathF.Pow( 1f - lt, 3f );
			SetDrop( go, drop );
		}
	}

	/// <summary>Sink every station root the given distance below its rest height
	/// (rest Z is the ring GO's own Z — stations are placed at WorldPosition + radial
	/// offset, which has no vertical component).</summary>
	void SetStationDrop( float drop )
	{
		foreach ( var go in _spawned )
			if ( go.IsValid() )
				SetDrop( go, drop );
	}

	void SetDrop( GameObject go, float drop )
	{
		var pos = go.WorldPosition;
		pos.z = WorldPosition.z - drop;
		go.WorldPosition = pos;
	}

	IReadOnlyList<GameObject> BuildInternal()
	{
		Clear();

		int count = StationCount < 1 ? 1 : StationCount;

		// Aim station 0 at the player spawn (the LobbyNetworkManager GO) so it's the
		// table players walk up to first. Fallback yaw 0 if there's no spawn in the
		// scene. RingYawOffset then turns the whole ring.
		float baseAngle = RingYawOffset;
		foreach ( var nm in Scene.GetAllComponents<LobbyNetworkManager>() )
		{
			var toSpawn = nm.WorldPosition - WorldPosition;
			if ( new Vector2( toSpawn.x, toSpawn.y ).Length > 1f )
				baseAngle = MathF.Atan2( toSpawn.y, toSpawn.x ) * (180f / MathF.PI) + RingYawOffset;
			break;
		}

		float radius = RingRadius( count );
		for ( int i = 0; i < count; i++ )
		{
			float angle = baseAngle + 360f / count * i;
			var outward = Rotation.FromYaw( angle );

			var station = new GameObject( true, $"ChessStation{i}" );
			station.Flags |= GameObjectFlags.NotSaved;
			station.WorldPosition = WorldPosition + outward.Forward * radius;
			// Station +X points radially inward: White sits on the outward (-X) side —
			// where players walk up — and Black on the inward (+X) side.
			station.WorldRotation = Rotation.FromYaw( angle + 180f );
			_spawned.Add( station );

			var component = station.AddComponent<ChessStation>();
			component.WhiteAnchor = BuildSeatAnchor( station, "WhiteAnchor", -1f );
			component.BlackAnchor = BuildSeatAnchor( station, "BlackAnchor", +1f );
			// Top-down (nadir) anchors for 2D play mode (M16) — a second per-seat camera target,
			// used instead of the orbit anchor when PlayMode is "2d". Separate GO, separate maths.
			component.WhiteTopAnchor = BuildTopAnchor( station, "WhiteTopAnchor", -1f );
			component.BlackTopAnchor = BuildTopAnchor( station, "BlackTopAnchor", +1f );

			// Game flow + board rendering (M2). Both replicate with the station GO
			// like ChessStation does — the controller for its [Sync] game state, the
			// view because every client renders its own board (dormant in editor
			// preview: neither is ExecuteInEditor).
			var controller = station.AddComponent<Gambit.Game.LocalGameController>();
			controller.Station = component;

			// The lichess relay client (M8), beside the local controller rather than
			// replacing it: a table plays locally until both seats opt in, and falls
			// back to local the moment lichess isn't available. Purely local state —
			// each client polls gamchess for itself, so nothing here is networked.
			var lichess = station.AddComponent<Gambit.Game.LichessGameController>();
			lichess.Station = component;
			lichess.Local = controller;

			var view = station.AddComponent<ChessBoardView>();
			view.Station = component;
			view.Controller = controller;
			view.Lichess = lichess;

			// Sound (M11). Beside the view and wired identically, because it resolves
			// the same seam the same way: what you hear and what you see must be the
			// same game. Local-only like the view — every client makes its own noise.
			var sounds = station.AddComponent<Gambit.Audio.TableSounds>();
			sounds.Station = component;
			sounds.Controller = controller;
			sounds.Lichess = lichess;

			// The seated players' hands (M13). Beside the sounds and wired identically, for
			// the identical reason: it resolves the same seam the same way, so what you see,
			// what you hear and what the hands do are the same game — and a third kind of
			// game gets hands by existing.
			var terry = station.AddComponent<SeatedTerry>();
			terry.Station = component;
			terry.Controller = controller;
			terry.Lichess = lichess;


			// Floating occupancy sign over the table (blank while the table is
			// empty). Billboarded per-viewer, so it reads from anywhere in the room.
			var sign = new GameObject( true, "Sign" );
			sign.Parent = station;
			sign.LocalPosition = new Vector3( 0, 0, 78f );
			sign.LocalScale = 1.2f * TableScale;
			sign.AddComponent<WorldPanel>().LookAtCamera = true;
			sign.AddComponent<Gambit.UI.StationScreenPanel>();

			if ( BuildTables )
			{
				BuildChessTable( station );
				// Board number on a little angled plaque hanging off the table's left edge,
				// instead of a number floating overhead (needs the table to sit against).
				BuildStationPlaque( station, i );
				// The clock, standing on the +X margin this table was widened to hold (M11).
				BuildStationClock( station, controller, lichess );
				// A chair at each seat (M13) — always both, occupied or not: a table with no
				// chairs reads as a table you can't sit at.
				BuildStationChair( station, component, ChessSeat.White );
				BuildStationChair( station, component, ChessSeat.Black );
			}
		}

		return _spawned;
	}

	// The number plate, in base units: thin along its facing normal (plaque-local X),
	// PlaqueLength along its width, PlaqueHeight down its face. The drop below derives
	// from PlaqueHeight rather than restating it — two hand-tuned numbers that are
	// supposed to mean "flush with the tabletop" is exactly how they stop meaning it.
	const float PlaqueThickness = 0.6f;
	const float PlaqueLength = 8f;
	const float PlaqueHeight = 5.5f;

	/// <summary>Tilt of the plate's face, degrees. Negative pitches the face upward, so a
	/// plaque hanging off the table edge angles up toward someone standing at it rather
	/// than presenting its edge.</summary>
	const float PlaqueTilt = -45f;

	/// <summary>Nudge along the plate's own facing normal, to lift it clear of the
	/// tabletop's edge rather than z-fighting it.</summary>
	const float PlaqueOutset = 0.3f;

	/// <summary>Small angled name-plate carrying the table number, hanging off the table's
	/// LEFT edge. Local +X is Black's side (radially inward), −X is White's (the walk-up
	/// side); +Y is White's left (the a-file edge) — s&amp;box is Y-left. All offsets are in
	/// base units × TableScale — tune in-editor.
	///
	/// <para><b>Where it has been, and why it moved twice.</b> It sat mid-edge at +Y until
	/// M11, which the trays now own — it would have stood in Black's tray. It then moved to
	/// the walk-up CORNER (−X,+Y) facing White. That worked and read wrong: a plate facing
	/// the White seat is furniture aimed at one of the two players, when the number exists
	/// for the room. It could never go mid-edge at −X either — that is exactly where White's
	/// seat camera looks down the board from, so a plaque there is a lump in White's
	/// foreground.</para>
	///
	/// <para><b>Now: the left edge, hanging down.</b> Yaw 90° instead of 180° — the same
	/// plate turned a quarter clockwise, so its length runs along the table's X rather than
	/// its Y, and its face looks outward (+Y) at the room instead of inward at a seat.</para>
	///
	/// <para><b>Its top edge sits ON the tabletop's edge, and that takes TWO offsets, not
	/// one.</b> The first version dropped the plate by h·cos(tilt) and stopped — so the top
	/// edge was at the right HEIGHT but tucked h·sin(tilt) back UNDER the tabletop, and the
	/// plaque read as inset beneath an overhang. A tilted plate's top corner moves in both
	/// axes at once: the tilt swings it inward exactly as far as it lowers it. So the
	/// centre is pushed out by the same amount it is dropped, and the corner lands on the
	/// table's edge. (At 45° the two are equal — which is precisely why one missing term
	/// was invisible in the arithmetic and obvious in the room.)</para>
	///
	/// <para>Nothing needs to move for the rotation: the plate is a child of the plaque GO,
	/// so its dimensions are plaque-local and the yaw carries them round with it.</para></summary>
	void BuildStationPlaque( GameObject station, int number )
	{
		float s = TableScale;

		// Where the plate's top edge sits relative to its centre, once tilted. Both terms
		// matter: cos lowers it, sin swings it inward. Derived from PlaqueHeight/PlaqueTilt
		// so changing either keeps the top edge on the tabletop's corner.
		float tilt = -PlaqueTilt * (MathF.PI / 180f);
		float halfH = PlaqueHeight * 0.5f;
		float drop = halfH * MathF.Cos( tilt );   // centre → top edge, down the world Z
		float inset = halfH * MathF.Sin( tilt );  // centre → top edge, inward along Y

		var plaque = new GameObject( true, $"BoardPlaque {number}" );
		plaque.Parent = station;
		// Centred along the table's length, hanging from the LEFT (+Y) tabletop corner:
		// out by `inset` so the tilt brings the top edge back to the edge, down by `drop`
		// so it lands on the surface. PlaqueOutset then lifts it clear along its normal.
		plaque.LocalPosition = new Vector3(
			0f,
			MarginOuterY + inset + PlaqueOutset,
			TableTopZ - drop ) * s;
		// Face the room off the left edge (+Y → yaw 90°) and tilt the face up 45°.
		plaque.LocalRotation = Rotation.From( PlaqueTilt, 90f, 0f );

		// The physical plate (thin along the plaque's facing normal = local +X).
		AddBox( plaque, "Plate", Vector3.Zero,
			new Vector3( PlaqueThickness, PlaqueLength, PlaqueHeight ) * s, null, FrameColor );

		// The number, flush on the plate's front face (+X), not billboarded — it rides the
		// plaque's tilt like a real plate.
		var num = new GameObject( true, "Number" );
		num.Parent = plaque;
		num.LocalPosition = new Vector3( 0.4f * s, 0f, 0f );
		num.LocalScale = 0.16f * s;
		num.AddComponent<WorldPanel>();
		num.AddComponent<Gambit.UI.MarqueeNumberPanel>().Number = number.ToString();
	}

	// The clock, in base units. A thin strip in the −Y margin, inboard of White's tray —
	// the side OPPOSITE the plaque, facing in across the board.
	const float ClockLength = 24f;    // along X, about the board's own width
	const float ClockDepth = 1.6f;    // across Y — thin, it shares this margin with a tray
	const float ClockHeight = 2.2f;   // a low plinth, not a tower: it must not fence the board

	/// <summary>
	/// Tilt of everything standing on the clock. Negative pitches it up, so it reads from the
	/// seats rather than presenting its edge — the same way you read a real chess clock beside
	/// a board: nobody is square to it, everybody is looking down at it.
	///
	/// <para><b>This is coupled to <see cref="ClockPlateHeight"/> and <see cref="ClockDepth"/>
	/// and cannot be tuned alone.</b> The plates lean BACK out of a 1.6-deep strip, so a
	/// plate's height projects sin(tilt) of itself straight back into Y — a tall plate at a
	/// steep tilt leans out over the board and clips the a-file. At 2.9 high and 30° that is
	/// ±0.725 of Y, inside the strip's own ±0.8. Raise one and lower the other, or the clock
	/// grows into the board.</para></summary>
	const float ClockFaceTilt = -30f;

	// ── The text span, and why it is NOT the plate ──
	//
	// This is the world box the 512px panel maps onto, and it is what fixes the TEXT's size:
	// a glyph's world height is `fontPx × ClockTextSpanLength / ClockPxWidth`, so these two
	// numbers alone decide how big the time reads. THE PLATE IS SEPARATE AND LARGER.
	//
	// They were one number, and that is exactly the bug: the plate's length derived the
	// panel's scale, so growing the plate to give the text a margin grew the text by the same
	// factor and bought nothing. The margin has to come from the difference between two
	// numbers, so there have to be two.
	const float ClockTextSpanLength = 6f;
	const float ClockTextSpanHeight = 2.4f;

	/// <summary>Bare plate around the text span, per side. <b>This is the margin knob — turn
	/// this, not the plate or the span.</b> The text ran edge-to-edge at 0 and read as
	/// cramped; the plate grows around a fixed span, so turning this up cannot change the
	/// text's size.
	///
	/// <para>X and Z differ because the text does not fill its span squarely: it is set in a
	/// monospace face (see WallTheme's $wall-font) that runs nearly the full width, while a
	/// digit's cap height leaves slack above and below on its own. Equal margins here would
	/// look unequal on the plate.</para></summary>
	const float ClockPlateMarginX = 0.8f;
	const float ClockPlateMarginZ = 0.25f;

	// The plate: the text's span plus its margin. Plate-local X is the facing normal, Y the
	// length, Z up the face.
	const float ClockPlateLength = ClockTextSpanLength + 2f * ClockPlateMarginX;   // 7.6
	const float ClockPlateHeight = ClockTextSpanHeight + 2f * ClockPlateMarginZ;   // 2.9
	const float ClockPlateThickness = 0.4f;

	/// <summary>Plate centre out from the strip's middle — DERIVED so each plate sits flush
	/// to its own end of the strip, which is the job `justify-content: space-between` did when
	/// this was CSS: each player's clock at their own end, the middle left for the bar.
	///
	/// <para><b>White is −X and Black is +X</b>, per BuildStationPlaque: +X is Black's side
	/// (radially inward), −X is White's (the walk-up side). The old panel had to reason about
	/// a WorldPanel's content-space handedness for this and got it backwards first try,
	/// rendering each player their OPPONENT's clock. Here it is a sign in table space.</para>
	///
	/// <para>Derived rather than typed because it was typed (7) and then the plate grew: a
	/// hand-set offset silently stops meaning "flush" the moment the thing it positions
	/// changes size, and what it becomes instead is "overhanging" or "clipping the bar".</para></summary>
	static float ClockPlateOffsetX => ( ClockLength - ClockPlateLength ) * 0.5f;

	/// <summary>Lift of a panel off its plate's face, along the plate's own normal — clear of
	/// it rather than z-fighting it. BuildStationPlaque's PlaqueOutset, same job.</summary>
	const float ClockTextOutset = 0.05f;

	/// <summary>The longest string a clock face can EVER be asked to show, in characters —
	/// derived from TimeControl.All, never typed.
	///
	/// <para><b>This is the number that sizes the text, and getting it from the wrong string is
	/// exactly how the face came to be too big.</b> It was sized against "3:00" — the default
	/// Blitz table, the one on screen while tuning — which is 4 characters. "10:00" and "30:00"
	/// are 5, so every Rapid and Classical table rendered a quarter wider than the one it was
	/// tuned on. Tuning a shared face against whichever table you happen to be standing at is
	/// the bug; asking the menu for its worst case is the fix.</para>
	///
	/// <para>Every preset has ZERO increment (checked, not assumed), so a clock can never climb
	/// above the bank it started with and the initial values really are the worst case. If an
	/// incrementing or longer control is ever added, this re-derives and the text shrinks to
	/// fit on its own — which is the point of computing it rather than writing "5".</para></summary>
	static int ClockMaxChars
	{
		get
		{
			int max = 1;
			foreach ( var tc in Gambit.Game.TimeControl.All )
			{
				// "∞" for an untimed table, exactly as TableClock renders it.
				var face = tc.IsUnlimited ? "∞" : Gambit.Game.TimeControl.Format( tc.InitialSeconds );
				if ( face.Length > max ) max = face.Length;
			}
			return max;
		}
	}

	/// <summary>Character advance of $wall-font, as a fraction of the font size.
	///
	/// <para><b>Measured off a real render, not read from the font.</b> This host has no s&amp;box
	/// toolchain and cannot load the face, so its metrics are not knowable here. "3:00" at 130px
	/// measured ~90% of a 512px panel (2026-07-16) — about 0.9em per character.</para>
	///
	/// <para><b>It does NOT match the nominal metric, and the error is deliberately one-way.</b>
	/// $wall-font is monospace (Consolas/Roboto Mono), whose digits advance ~0.6em, so the face
	/// s&amp;box actually falls back to is much wider than the stack asks for. Over-stating the
	/// advance only ever makes the text SMALLER than it strictly needs to be; under-stating it
	/// overflows the plate. Round this UP when unsure.</para></summary>
	const float ClockCharAdvanceEm = 0.9f;

	/// <summary>How much of the text span the LONGEST string is allowed to fill. The rest is
	/// slack inside the span, on top of the bare plate ClockPlateMarginX adds outside it.</summary>
	const float ClockTextFitFraction = 0.84f;

	/// <summary>The face's font size. <b>MUST match TableClockTextPanel's `font-size`</b> — it is
	/// the one number that lives in two files, and the stylesheet is the one that actually
	/// renders.
	///
	/// <para>It reads as a text-size knob and is NOT one: it cancels out of the world size
	/// entirely (text world height = span × fit ÷ (maxChars × advance), with no font term). All
	/// it decides is the panel's pixel RESOLUTION. <b>To resize the text, turn
	/// ClockTextSpanLength.</b></para></summary>
	const float ClockFontPx = 130f;

	// The plate face's pixel space, DERIVED so the longest string the menu can produce lands on
	// ClockTextFitFraction of the span. Width was a typed 512 and the font was tuned against it
	// by eye — which is the same mistake as a typed plate offset: it silently stops meaning
	// "fits" the moment the string it was tuned on isn't the longest one.
	//
	// HEIGHT keeps deriving from the TEXT SPAN's aspect — the box the panel maps onto — so the
	// pixel space and the world span cannot drift out of proportion.
	static float ClockPxWidth => ClockMaxChars * ClockCharAdvanceEm * ClockFontPx / ClockTextFitFraction;
	static float ClockPxHeight => ClockPxWidth * ClockTextSpanHeight / ClockTextSpanLength;

	/// <summary>Z at which everything in the tilted plane is CENTRED — the plates and the bar
	/// alike, which is what keeps their bottom edges level with each other for free.
	///
	/// <para><b>It is the plinth's top surface PLUS half a plate's height projected onto Z</b>,
	/// because a box is centred on its origin: put the origin on the surface and half the
	/// plate is inside the plinth. That is exactly what happened — the plates were buried to
	/// their waists and the bar, being shorter, was <b>entirely</b> inside the plinth and could
	/// never have been visible at all.</para>
	///
	/// <para><b>This is BuildStationPlaque's lesson arriving a second time</b>, and it is worth
	/// naming: <i>a tilted plate's edge is not half its height away from its centre.</i> The
	/// plaque's first version dropped by <c>h·cos(tilt)</c> and forgot the <c>h·sin(tilt)</c>
	/// the tilt swings sideways; this forgot the projection entirely and used the raw surface
	/// height. Both times the arithmetic looked obviously right and the room disagreed. The
	/// rule: <b>derive an edge from the centre through the rotation — never place a tilted
	/// object by the number that would be correct if it were flat.</b></para></summary>
	static float ClockPlaneOriginZ =>
		ClockHeight + ClockPlateHeight * 0.5f * MathF.Cos( ClockFaceTilt * ( MathF.PI / 180f ) );

	// ── The material bar ──
	//
	// Mesh, not a div. A track that is ALWAYS there, and a fill growing from dead centre toward
	// whoever is ahead — so at level the track is all you see, which is what a material bar at
	// zero should look like: a centred bar. Centre-out is also what lets it say WHO as well as
	// by how much.
	//
	// It lives on the BASE'S FRONT FACE — upright, full width, its own surface. It was in the
	// plates' tilted plane, squeezed into the gap they left, and that was two compromises at
	// once: a gauge reading its length at 30° off-axis, and a length rationed by whatever the
	// plates didn't want. The base's face is already vertical, already square to the room, and
	// already the full width of the assembly.
	//
	// It also retires the whole "don't let a plate clip the bar" problem rather than solving it
	// again: the plates are ABOVE the base and the bar is ON it, so no plate size can reach it.
	// ClockBarGap and ClockPlateInnerX existed only to referee that fight and are gone.
	const float ClockBarHeight = 1.4f;      // a band on a 2.2 face: 0.4 of bare base above and below

	/// <summary>Thin, and not by taste — this is an applique on a face, and its whole depth is
	/// spent out of ClockBoardGap, which is only 0.2. At the plates' 0.2 the fill reached into
	/// the board frame. Thickness costs nothing here anyway: the bar is read head-on as a
	/// coloured LENGTH, and depth is not part of the signal.</summary>
	const float ClockBarThickness = 0.06f;

	/// <summary>How far the fill stands proud of its track — only enough to beat z-fighting.
	/// The fill is told from the track by COLOUR, not by depth.</summary>
	const float ClockBarProud = 0.04f;

	// ── The lead badge ──
	//
	// The real material difference, ALWAYS drawn, on its own small plate between the two clock
	// plates. It is a second string, so by this file's own rule it is a second plate — never a
	// second div on an existing panel.
	//
	// It sat proud in front of the bar until the bar moved down to the base's face; its own
	// transform is UNCHANGED, because the position was confirmed right in the room. It keeps
	// its plate — the plate is why the number was legible in the first place, and a small black
	// plate standing between two big ones reads as part of the row.
	//
	// It is what keeps the bar honest past saturation: the bar pins at BarFullAt and stops
	// telling the truth, and the number never does.
	const float ClockLeadLength = 3f;
	const float ClockLeadHeight = 1.3f;      // inside ClockBarHeight — it sits ON the bar
	const float ClockLeadThickness = 0.2f;

	/// <summary>Proud of the tilted plane's origin. It was sized to clear the fill that used to
	/// pass behind it; the fill has gone to the base's face, and this is kept at its exact value
	/// anyway — it is what puts the badge where the room says it belongs. Turning it now moves
	/// the badge along the plane's normal, which is not a free tidy-up.</summary>
	const float ClockLeadProud = 0.34f;

	/// <summary>Badge centre below the tilted plane's origin. Its ONE job is to keep the badge
	/// exactly where it was when the bar left this plane: it used to be `(ClockPlateHeight −
	/// ClockBarHeight) / 2`, sharing the bar's bottoms-level drop, and the moment the bar moved
	/// that expression became a phantom dependency on a number that is no longer here.
	/// Confirmed in the room — do not "derive" it back into something tidier and 0.05 lower.</summary>
	const float ClockLeadDropZ = 0.75f;

	static float ClockLeadPxHeight => ClockPxWidth * ClockLeadHeight / ClockLeadLength;

	// Plate tints. BLACK, and that is not the same as "a very dark number": the table spot
	// runs at MarqueeBrightness 3.3, and tint is albedo — it gets MULTIPLIED by the light. The
	// old 0.047 was chosen to read as near-black and measured (99, 97, 114) on screen, a mid
	// grey, because 0.047 × 3.3 encodes to almost exactly that. Zero is the only albedo a
	// bright light cannot lift. Anything you want to stay black under this spot must be 0.
	internal static readonly Color ClockPlateColor = new( 0f, 0f, 0f );

	/// <summary>The RUNNING side's plate, barely lifted off black — its text going bright is
	/// the real signal, and this is the whisper behind it. Kept tiny for the reason above: at
	/// 3.3× even 0.012 reads as a visible dark grey.</summary>
	internal static readonly Color ClockPlateOnColor = new( 0.012f, 0.012f, 0.014f );

	// Bar tints. ONE fill colour, always — the fill does not change colour with who is ahead.
	//
	// It used to: near-white for White, near-black for Black. Black's was hard to see, and it
	// could not really be fixed, only traded — the fill sits on a track on a dark base, so the
	// colour that MEANS Black is the colour that disappears there. The direction was always
	// carrying "who" anyway (the fill grows from dead centre toward the leader), so the colour
	// was saying the same thing a second time and doing it badly. One legible fill beats two
	// where one of them is a guess at the light.
	//
	// The track stays a mid grey. It was mid specifically so a near-black fill could still be
	// told from it; with only a pale fill left it now just has to be darker than that, and it
	// is — turn it down if the bar wants more punch.
	static readonly Color ClockBarTrackColor = new( 0.05f, 0.05f, 0.055f );
	static readonly Color ClockBarFillColor = new( 0.90f, 0.89f, 0.86f );

	/// <summary>The engine's WorldPanel pixel→world constant
	/// (<c>ScenePanelObject.ScreenToWorldScale</c>, read from the shipped engine, not
	/// recalled): a WorldPanel's world size is <c>PanelSize × this × transform scale</c>.
	///
	/// <para><b>Deriving the face's scale from it is the whole point.</b> The first version
	/// guessed <c>0.022</c>, which put a 0.85-world-unit panel on a 30-unit body — the
	/// clock rendered as an invisible speck and read as "the panel isn't working". A
	/// WorldPanel's scale is not a world size and cannot be eyeballed; it is
	/// <c>wanted_world_size / (PanelSize × 0.05)</c>. SpectatorSeatPanel keeps its own copy
	/// of this constant for the same reason.</para></summary>
	const float PxToWorld = 0.05f;

	/// <summary>
	/// The table's clock: a thin, low strip lying in the −Y margin between the board and
	/// White's tray, carrying two plates and a material bar angled up and inward across the
	/// board.
	///
	/// <para><b>Why −Y and one facing, not +X and one per seat.</b> It stood on the +X margin
	/// with a face aimed at each seat, which put a tower in Black's near foreground — the
	/// exact objection that moved the plaque off −X, and it looked like a wall. Beside the
	/// board is where a real chess clock goes: the seats are at ±X, so a face at −Y pointing
	/// +Y is square to neither of them and readable to both, because both are looking DOWN at
	/// the table anyway. Everything here shares that one facing, which is how the real object
	/// works — two dials on one body, both read at an angle by two people.</para>
	///
	/// <para>The margin is shared: ClockDepth comes out of the same budget the tray does
	/// (see ClockBoardGap and friends), so the tray moves outboard by exactly the strip
	/// this takes. Nothing here is free space.</para>
	///
	/// <para><b>Local, never networked.</b> Same rule as the board view: every client
	/// renders its own, reading state that is already replicated. Nothing here decides
	/// anything — the host owns a local game's clock and lichess owns a lichess game's,
	/// and this shows whichever the seam resolves to.</para>
	///
	/// <para><b>Three objects, not one panel.</b> Two plates and a bar, each a mesh, sharing
	/// one tilted plane. See ClockPlateThickness for why this is not composed in CSS; the
	/// short version is that the five bugs it cost were all layout and none were data, and
	/// this repo's one working WorldPanel shape holds one string. <see cref="TableClock"/>
	/// drives them.</para>
	/// </summary>
	void BuildStationClock( GameObject station, Gambit.Game.LocalGameController controller,
		Gambit.Game.LichessGameController lichess )
	{
		float s = TableScale;

		var clock = new GameObject( true, "TableClock" );
		clock.Parent = station;
		// −Y: White's side, opposite the plaque at +Y.
		clock.LocalPosition = new Vector3( 0f, -ClockCenterY, TableTopZ ) * s;

		// The body: a low strip sitting on the tabletop, centred on its own height. The
		// plates stand on it and the bar is inlaid between them.
		AddBox( clock, "Body", new Vector3( 0f, 0f, ClockHeight * 0.5f ) * s,
			new Vector3( ClockLength, ClockDepth, ClockHeight ) * s, null, FrameColor );

		var driver = clock.AddComponent<TableClock>();
		driver.Controller = controller;
		driver.Lichess = lichess;

		BuildClockPlate( clock, white: true, out var whiteText, out var whitePlate );
		BuildClockPlate( clock, white: false, out var blackText, out var blackPlate );
		driver.WhiteText = whiteText;
		driver.BlackText = blackText;
		driver.WhitePlate = whitePlate;
		driver.BlackPlate = blackPlate;

		BuildClockBar( clock, driver );
		BuildClockLead( clock, driver );
	}

	/// <summary>One seat's plate: a mesh standing on the strip with a one-string panel flush
	/// on its face. BuildStationPlaque's shape — plate box, panel lifted off its front along
	/// the plate's own normal, riding the tilt like a real plate rather than billboarded.
	///
	/// <para>The plate GO carries the rotation and the boxes are its children, so their
	/// dimensions are plate-local and the yaw carries them round with it. Same trick, same
	/// reason.</para></summary>
	void BuildClockPlate( GameObject clock, bool white,
		out Gambit.UI.TableClockTextPanel text, out ModelRenderer plateRenderer )
	{
		float s = TableScale;
		text = null;
		plateRenderer = null;

		var plate = new GameObject( true, white ? "Plate White" : "Plate Black" );
		plate.Parent = clock;
		// Standing ON the body — see ClockPlaneOriginZ, which is why this is not ClockHeight —
		// at its own player's end. See ClockPlateOffsetX: White is −X.
		plate.LocalPosition = new Vector3(
			( white ? -ClockPlateOffsetX : ClockPlateOffsetX ), 0f, ClockPlaneOriginZ ) * s;
		// Tipped up and facing +Y across the board. Both plates share this one facing rather
		// than each aiming at its own seat: neither player is square to it, both are looking
		// down at the table anyway, and that is how a real chess clock's two dials work.
		plate.LocalRotation = Rotation.From( ClockFaceTilt, 90f, 0f );

		var box = AddBoxGo( plate, "Face", Vector3.Zero,
			new Vector3( ClockPlateThickness, ClockPlateLength, ClockPlateHeight ) * s,
			null, ClockPlateColor );
		if ( box is null ) return;                 // no box model: AddBox has always drawn nothing
		plateRenderer = box.GetComponent<ModelRenderer>();

		var face = new GameObject( true, "Text" );
		face.Parent = plate;
		// Flush on the plate's front (+X), lifted clear of it.
		face.LocalPosition = new Vector3( ClockPlateThickness * 0.5f + ClockTextOutset, 0f, 0f ) * s;

		// Scale DERIVED so the panel's width spans the TEXT SPAN — never guessed, and never
		// the plate's length. That distinction IS the margin: the plate is bigger than the
		// span, the leftover is the bare border, and because the text's size is pinned to the
		// span it does not grow when the plate does. Scale off the plate instead and the
		// margin knob does nothing but zoom. See PxToWorld: a WorldPanel's scale is not a
		// world size and cannot be eyeballed.
		face.LocalScale = ( ClockTextSpanLength * s ) / ( ClockPxWidth * PxToWorld );

		var worldPanel = face.AddComponent<WorldPanel>();
		// Matches the SPAN's aspect, because ClockPxHeight is derived from it.
		worldPanel.PanelSize = new Vector2( ClockPxWidth, ClockPxHeight );

		text = face.AddComponent<Gambit.UI.TableClockTextPanel>();
	}

	/// <summary>The material bar: a track that is ALWAYS drawn, and a fill growing from dead
	/// centre toward whoever is ahead. <see cref="TableClock"/> moves the fill.
	///
	/// <para><b>Upright, on the base's front face, the full width of the assembly.</b> It was
	/// tilted 30° in the plates' plane and rationed to the gap they left — a gauge whose whole
	/// signal is a LENGTH, read off-axis and cut short. This face is already vertical, already
	/// square to the room, and already as wide as the clock; the bar just uses all of it.</para>
	///
	/// <para><b>Everything it projects comes out of ClockBoardGap</b>, because on this face
	/// "proud" points at the board. At the old 0.2 gap the fill landed inside the board frame —
	/// see ClockBoardGap, which is now the bar's clearance rather than spare tabletop.</para>
	///
	/// <para>The track's back face sits flush against the base rather than inside it: the base
	/// is opaque, so anything not proud of that face is simply not there. Same lesson as the
	/// plates being buried, arriving on a different axis.</para></summary>
	void BuildClockBar( GameObject clock, TableClock driver )
	{
		float s = TableScale;

		var bar = new GameObject( true, "Bar" );
		bar.Parent = clock;
		// On the front (+Y, board-facing) face, centred on the base's height. Pushed out by half
		// the track's thickness so the track's BACK lands flush on the face and all of its body
		// is outside the base.
		bar.LocalPosition = new Vector3(
			0f, ClockDepth * 0.5f + ClockBarThickness * 0.5f, ClockHeight * 0.5f ) * s;
		// Yaw only — UPRIGHT. The plates' pitch is deliberately absent; this is the one thing on
		// the clock that is not in their plane. Yaw 90 still maps local +Y onto table −X, so the
		// fill's sign in TableClock is unchanged by the move.
		bar.LocalRotation = Rotation.From( 0f, 90f, 0f );

		AddBox( bar, "Track", Vector3.Zero,
			new Vector3( ClockBarThickness, ClockLength, ClockBarHeight ) * s,
			null, ClockBarTrackColor );

		// The fill is built at FULL extension — dead centre to one end, half the track — and the
		// driver scales it down from there. Building it full is what lets TableClock hold the
		// full scale as a number instead of re-deriving it from Model.Bounds: AddBoxGo did that
		// arithmetic once already, and doing it twice is how the two come to disagree.
		float halfLength = ClockLength * 0.5f;
		var fill = AddBoxGo( bar, "Fill",
			new Vector3( ClockBarProud, 0f, 0f ) * s,
			new Vector3( ClockBarThickness, halfLength, ClockBarHeight ) * s,
			null, ClockBarFillColor );
		if ( fill is null ) return;

		// Tinted once, here, and never again — TableClock has no reference to this renderer
		// because there is nothing left for it to decide about the colour.
		driver.BarFill = fill;
		driver.BarFillFullScale = fill.LocalScale;
		driver.BarFillBasePosition = fill.LocalPosition;
		driver.BarFillHalfLength = halfLength * s;
	}

	/// <summary>The lead badge: the real material difference, on its own black plate standing in
	/// the plates' tilted plane, centred between them and above the base.
	///
	/// <para><b>Its transform is unchanged and that is the point.</b> It was built as a child of
	/// the bar; when the bar moved down to the base's face it got its own anchor carrying the
	/// identical plane transform, so the badge itself did not move by a thousandth. The position
	/// was called right in the room, and "it had to be reparented" is not a reason to
	/// re-tune it.</para>
	///
	/// <para>Its own plate, not text laid onto something else, because the plate is what makes
	/// the background a constant — which is why the number was legible at all.</para></summary>
	void BuildClockLead( GameObject clock, TableClock driver )
	{
		float s = TableScale;

		// The plates' plane, as an anchor of its own. This used to BE the bar's GO — the badge
		// rode it for free — so it is spelled out here rather than lost with it.
		var plane = new GameObject( true, "LeadPlane" );
		plane.Parent = clock;
		plane.LocalPosition = new Vector3( 0f, 0f, ClockPlaneOriginZ ) * s;
		plane.LocalRotation = Rotation.From( ClockFaceTilt, 90f, 0f );

		var badge = new GameObject( true, "Lead" );
		badge.Parent = plane;
		badge.LocalPosition = new Vector3( ClockLeadProud, 0f, -ClockLeadDropZ ) * s;

		AddBox( badge, "Plate", Vector3.Zero,
			new Vector3( ClockLeadThickness, ClockLeadLength, ClockLeadHeight ) * s,
			null, ClockPlateColor );

		var face = new GameObject( true, "Text" );
		face.Parent = badge;
		face.LocalPosition = new Vector3( ClockLeadThickness * 0.5f + ClockTextOutset, 0f, 0f ) * s;
		// Same derivation as a clock plate, off the badge's own length — so the number's size
		// follows the badge and nothing else.
		face.LocalScale = ( ClockLeadLength * s ) / ( ClockPxWidth * PxToWorld );

		var worldPanel = face.AddComponent<WorldPanel>();
		worldPanel.PanelSize = new Vector2( ClockPxWidth, ClockLeadPxHeight );

		driver.LeadText = face.AddComponent<Gambit.UI.TableClockTextPanel>();
	}

	// ══ The chair (M13) ══
	//
	// A cantilever tube frame — Breuer/Cesca. Legs a U opening backwards, arms a U opening
	// forwards, sharing the seat rail, so in side elevation they read as an S rotated 90°.
	// Square pad, L-shaped back, thin tube.
	//
	// ── Chair-local space, and why the mirror is a yaw ──
	//
	// The chair GO sits AT its seat's walk-up spot and is yawed to face the board, so
	// chair-local +X means "toward the board" for BOTH seats and the two chairs are one
	// code path with one set of numbers. M13's design table gave every coordinate twice,
	// signed per seat; that is the arithmetic the clock's plates got backwards first try.
	// Here the only thing carrying a side is the GO's own x, where a wrong seat is visible
	// in the diff.
	//
	// ── Nothing here is free space ──
	//
	// Two ends of this are DERIVED and are not style: the chair's centre is the seat spot
	// (SeatSpotX — move the camera and the chair follows it, because they are the same
	// place), and the armrest's height comes off the tabletop's underside. See
	// ChairArmrestZ for what typing that one instead costs.

	/// <summary>Tube radius. Thin — this is a bent-tube frame, not a plank.</summary>
	const float ChairTubeRadius = 0.75f;

	/// <summary>
	/// Radius of the frame's corners. A real cantilever chair is ONE bent tube, so its
	/// corners are bends, not mitres — 2.5 against a 0.75 tube is about 3.3× the tube's own
	/// radius, which is roughly what a tube bender will give you before it kinks.
	///
	/// <para>Bounded by the shortest leg it sits on, and BuildTubePath clamps rather than
	/// trusting: a corner may not eat more than half of either leg or two bends meet in the
	/// middle and the tube folds through itself. The chair's shortest leg is the back riser
	/// (ChairArmrestZ − ChairSeatRailZ = 7.35), so the clamp only bites past ~3.6.</para></summary>
	[Property] public float ChairBendRadius { get; set; } = 2.5f;

	/// <summary>Pad depth, along the board axis. Also the frame's span: the floor rail,
	/// the seat rail and the armrest all run this far.</summary>
	[Property] public float ChairSeatDepth { get; set; } = 20f;

	/// <summary>
	/// Pad width, across. The side frames sit at ±half of this, so the tubes stand
	/// ChairTubeRadius proud of the pad's edges on each side.
	///
	/// <para><b>This was 18 and read as a child's chair, and the reason is a number the
	/// engine will tell you.</b> A citizen's <c>BodyRadius</c> is 16 — it is <b>32 units
	/// wide</b>. An 18-wide seat is narrower than the person on it. 72 units tall is
	/// roughly human-proportioned (eye at 64, and s&amp;box units are inches), which is why
	/// an 18-inch seat HEIGHT is a real chair and the same number as a WIDTH is not: real
	/// chairs are about as wide as the sitter, and this citizen is chunkier than the human
	/// its height implies.</para></summary>
	[Property] public float ChairSeatWidth { get; set; } = 26f;

	/// <summary>Pad slab thickness. The seat rail runs through its middle, so the tube
	/// stands (ChairTubeRadius − half of this) proud above and below — the pad is set
	/// INTO the frame rather than laid on top of it.</summary>
	const float ChairPadThickness = 1.2f;

	/// <summary>The sitting surface's height.
	///
	/// <para><b>MEASURED, not guessed — and it isn't 18.</b> This started at 18 because a
	/// real chair seat is 17–18 inches and s&amp;box units are inches. But the citizen's sit
	/// pose is what actually decides it, and <c>gambit_terry</c>'s ruler read the real bones
	/// off a live seated one: <b>pelvis at z = 16.57</b> with the ankle at 3.81 (feet flat on
	/// the floor). A pelvis BONE at 16.57 sits a few units above the surface the body rests
	/// on — the sit bones are below the hip joint — so the pose wants a seat around 14, and
	/// a pad at 18 was ABOVE the hips it was meant to hold.</para>
	///
	/// <para>Lowering the pad rather than raising the pose with <c>SitOffsetHeight</c> is
	/// deliberate: the offset moves the whole pose relative to the origin, and the origin is
	/// at the FEET — so raising it to meet an 18 pad would lift the feet off the floor. The
	/// pose already has its feet planted; the chair is what should move.</para>
	///
	/// <para>Still a knob, and still the first thing to check: re-run <c>gambit_terry</c>
	/// seated and compare the pelvis against this number.</para></summary>
	[Property] public float ChairSeatTopZ { get; set; } = 14f;

	/// <summary>Clearance from the armrest's top to the tabletop's underside. See
	/// ChairArmrestZ — this is the input, the height is the output.</summary>
	const float ChairArmrestTableGap = 1.5f;

	/// <summary>Back panel: bottom and top. ChairBackTopZ is the chair's overall height and
	/// what a D5 chair model would be scaled to.
	///
	/// <para><b>One flat rect, and no L.</b> M13 specified an L-shaped back with a return
	/// curling forward at the top — but that was an interpretation, not a derivation (the
	/// file says so itself), and in the room it read as the back curving inward at you. The
	/// panel is the coloured surface of the chair and it wants to be one plane.</para></summary>
	[Property] public float ChairBackBottomZ { get; set; } = 22f;
	[Property] public float ChairBackTopZ { get; set; } = 34f;
	const float ChairBackThickness = 1.2f;

	/// <summary>How far an EMPTY chair tucks toward the table. The seated position is the
	/// geometry itself — that IS the pulled-out state, and pulling out any further would
	/// put the board out of the terry's reach (hips at 36 back, ~29 of arm, the near rank
	/// at 17.06). So tucking is the only direction there is.
	///
	/// <para>Clamped to <see cref="ChairMaxTuck"/>: a chair tucked past the foot plate is
	/// inside the table. That clamp is what the travel is really worth — see
	/// <see cref="ChairCenterX"/>, which is where the room to move comes from.</para></summary>
	[Property] public float ChairTuckInset { get; set; } = 9f;

	/// <summary>Seconds an empty chair takes to slide out as someone sits.
	///
	/// <para>Free of any networking, which is the whole reason it can exist: it derives
	/// from the <c>[Sync(FromHost)]</c> seat occupancy, so every client animates the same
	/// chair the same way off state that already replicates.</para></summary>
	[Property] public float ChairTuckSeconds { get; set; } = 0.5f;

	/// <summary>How far the frame's tint is pushed from mid-grey toward its seat's piece
	/// colour. An INTERPRETATION of "75% black vs white", not a derivation — hence a
	/// constant rather than a buried literal.
	///
	/// <para>A bare <c>pieceColor × 0.75</c> was rejected: it drives Black to
	/// (0.068, 0.053, 0.045), which is the table's own wood (0.16, 0.11, 0.07) under the
	/// 3.3× spot. Lerping from grey keeps Black at (0.1925, 0.1775, 0.17) — still clearly
	/// the dark chair, still clearly not the table.</para></summary>
	const float ChairFrameTintStrength = 0.75f;

	/// <summary>What the frame tint is pushed AWAY from. Mid-grey: a chair frame is metal,
	/// and metal is what both seats' frames have in common.</summary>
	static readonly Color ChairFrameBase = new( 0.5f, 0.5f, 0.5f );

	// ── Derived. Change a constant above and every one of these moves. ──

	/// <summary>
	/// How far BEHIND the plant the chair's centre sits, and how far to the side.
	///
	/// <para><b>Both measured, not chosen.</b> <c>gambit_terry</c>'s ruler read a seated
	/// citizen's pelvis at <b>3.4 back of its own origin and 0.89 to its left</b> — the sit
	/// pose leans. The plant puts the ORIGIN at SeatSitBack, so a chair centred there is
	/// centred 3.4 in front of the person in it, which is why the terry read as sitting too
	/// far back and slightly to one side of its own seat. Offsetting the chair by the pose's
	/// own lean puts the sitter in the middle of it.</para>
	///
	/// <para>Y is in CHAIR-local terms (positive = the sitter's left), so one number covers
	/// both seats: the chair is yawed to face the board, exactly as the citizen is.</para></summary>
	[Property] public float ChairSeatOffsetX { get; set; } = 3.4f;
	[Property] public float ChairSeatOffsetY { get; set; } = 0.89f;

	/// <summary>
	/// Chair centre, as a magnitude: <b>where the person actually sits</b>
	/// (<see cref="SeatSitBack"/> plus the pose's own backward lean), not the walk-up spot.
	///
	/// <para><b>It was SeatSpotX (32.12) and that was wrong twice over.</b> The seat spot is
	/// where you STAND to press E; the seated avatar is planted 3.9 further back, so the
	/// chair sat forward of its own occupant. And it cost the tuck its travel: the binding
	/// constraint is that the front riser must stay outboard of the table's foot plate
	/// (±15), so a chair centred at 32.12 could only ever move 7.4 — which is why the slide
	/// was there but barely read. Centred on the sitter it has 10.2 to give.</para></summary>
	public float ChairCenterX => SeatSitBack + ChairSeatOffsetX;                // 39.4

	/// <summary>How far the chair is shifted sideways so the seat is under the sitter rather
	/// than under the plant — the pose's own lateral lean, as a station-space y.
	///
	/// <para>White faces +X so its left is +Y; Black faces −X so its left is −Y. One
	/// constant covers both because the chair is yawed to face the board exactly as the
	/// citizen is, so "the sitter's left" is the same side of each.</para></summary>
	public float ChairOffsetY( ChessSeat seat ) =>
		( seat == ChessSeat.White ? +1f : -1f ) * ChairSeatOffsetY;

	/// <summary>The frame's front, as a chair-local x (+ is toward the board).</summary>
	float ChairFrontX => ChairSeatDepth * 0.5f;                                 // +10.0

	/// <summary>The frame's back, as a chair-local x.</summary>
	float ChairBackX => -ChairSeatDepth * 0.5f;                                 // −10.0

	/// <summary>Side frames sit at ±this in chair-local y.</summary>
	float ChairSideY => ChairSeatWidth * 0.5f;                                  // ±13.0

	/// <summary>The floor rail's centreline — one radius up, so the tube rests ON the floor.</summary>
	float ChairFloorRailZ => ChairTubeRadius;                                   // 0.75

	/// <summary>The seat rail's centreline: through the pad's middle, so the pad is inset
	/// into the frame rather than balanced on it.</summary>
	float ChairSeatRailZ => ChairSeatTopZ - ChairPadThickness * 0.5f;           // 17.40

	/// <summary>
	/// The armrest's centreline. <b>Derived from the tabletop's underside, and that is the
	/// point of the whole number.</b>
	///
	/// <para>Typed instead as "8 above the seat" it lands at z 26, tops out at 26.75
	/// against an underside of 27.00, and every arm in the room has a quarter of a unit of
	/// air over it — which nobody would notice until the first person sat down. Coming at
	/// it from the table gives 24.75, an armrest top of 25.50, and a real 1.50 of clearance
	/// that stays 1.50 if the table ever changes height. Same lesson as ClockPlaneOriginZ
	/// and the plaque's corner: derive the edge, don't place it by the number that would be
	/// right if nothing else existed.</para>
	/// </summary>
	float ChairArmrestZ => TableTopSlabBottomZ - ChairArmrestTableGap - ChairTubeRadius;  // 24.75

	/// <summary>The most a chair may tuck before its front riser is inside the table.
	///
	/// <para>Measured at the tube's INNER SURFACE, not its centreline — the chair is a
	/// cylinder and its skin is what would meet the foot plate. (M13's design table gave
	/// 8.12 from the centreline; the edge gives 7.37. Same conclusion at the shipped 6.0,
	/// which is the only reason the miss was harmless.)</para></summary>
	public float ChairMaxTuck => ChairCenterX - ChairFrontX - ChairTubeRadius - FootEdgeX; // 13.65

	/// <summary>
	/// One seat's chair, parented to the station beside WhiteAnchor / Table / BoardPlaque /
	/// TableClock — never under Table, where ChessBoardView.ResolveCells prefix-scans for
	/// "Cell " and would try to parse a chair leg as a square.
	///
	/// <para><b>Always present at both seats.</b> A table with no chairs reads as a table
	/// you can't sit at, and spawning one on sit would pop it in under the player.</para>
	///
	/// <para><b>No collider.</b> The consequence is real and accepted: you walk through
	/// every chair in the room. In exchange SeatWorldPosition, InteractRange and the ring's
	/// floor-slide are all untouched — and the seat spot IS the chair's centre, so a solid
	/// chair is a solid box exactly where a player has to stand to sit down.</para>
	///
	/// <para>Built here rather than in a builder component of its own, which buys hotload
	/// for free: Editor/HotloadRebuild.cs already calls RebuildPreview().</para>
	/// </summary>
	void BuildStationChair( GameObject station, ChessStation component, ChessSeat seat )
	{
		// The driver lives on the STATION, not on the chair — because the chair is
		// client-local and the station is what every client actually receives. See
		// BuildChairView.
		var driver = station.AddComponent<StationChair>();
		driver.Station = component;
		driver.Seat = seat;

		// The EDITOR's preview chair. At runtime nothing is built here: StationChair builds
		// a client-local copy on every machine instead, because a lathed tube has no asset
		// path and cannot cross the wire (BuildChairView says why at length). _runtimeBuilt
		// is false in the editor and on a joining client — whose preview OnStart throws away
		// anyway — and true only on the host's authoritative build.
		if ( !_runtimeBuilt )
			BuildChairView( station, seat, null );
	}

	/// <summary>Name of a seat's chair GameObject. One place, because StationChair has to
	/// find and replace whatever the ring's preview left behind.</summary>
	public static string ChairName( ChessSeat seat ) =>
		seat == ChessSeat.White ? "Chair White" : "Chair Black";

	/// <summary>
	/// Build one chair's geometry under <paramref name="station"/>, and hand its panels to
	/// <paramref name="driver"/> (null for the editor preview, which has no driver).
	///
	/// <para><b>NotSaved | NotNetworked, and that is load-bearing rather than tidy.</b> The
	/// tube is a runtime <c>Model.Builder</c> mesh, and a runtime model HAS NO ASSET PATH —
	/// a ModelRenderer serialises its Model as a path, so the moment a chair rides the
	/// host's snapshot to a joining client, every tube on it resolves to the engine's ERROR
	/// model, stretched by that tube's own (radius, radius, length) scale. Which is exactly
	/// what shipping it networked looked like: a joiner's whole room full of stretched error
	/// models, while the host — holding the real Model object in memory — saw nothing
	/// wrong.</para>
	///
	/// <para><b>This is why ChessBoardView destroys the ring's "Pieces" and rebuilds
	/// "PiecesView" as NotSaved|NotNetworked</b>, and it is the same constraint: the lathed
	/// PIECES cannot cross the wire either. They get away with it only because EnsureBoard
	/// destroys the broken set within a frame of the station arriving — so the bug has
	/// always been there and has never been visible. A chair with nothing to destroy it
	/// just sits there. <b>Anything built from Model.Builder must be built per client, on a
	/// NotNetworked object.</b></para>
	/// </summary>
	public GameObject BuildChairView( GameObject station, ChessSeat seat, StationChair driver )
	{
		bool white = seat == ChessSeat.White;
		float side = white ? -1f : +1f;

		var chair = new GameObject( true, ChairName( seat ) );
		chair.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		chair.Parent = station;
		// AT the seat spot, facing the board — so chair-local +X is "toward the board" on
		// both sides and everything below is written once, unsigned. StationChair slides
		// this x for the tuck.
		chair.LocalPosition = new Vector3( side * ChairCenterX, ChairOffsetY( seat ), 0f );
		chair.LocalRotation = Rotation.FromYaw( white ? 0f : 180f );

		// ── D5 fallback hook, mirroring BuildPiece/PieceHeight's convention: a real model
		// scaled to the same overall height, so an import keeps the room's proportions.
		var model = Model.Load( "models/chess/chair.vmdl" );
		if ( model != null && !model.IsError )
		{
			var go = new GameObject( true, "Model" );
			go.Parent = chair;
			var mr = go.AddComponent<ModelRenderer>();
			mr.Model = model;
			mr.Tint = ChairFrameTint( white );
			float h = model.Bounds.Size.z;
			if ( h > 0.01f ) go.LocalScale = ChairBackTopZ / h;
			return chair;
		}

		BuildChairFrame( chair, ChairFrameTint( white ), driver );
		return chair;
	}

	/// <summary>75% of the way from a metal grey toward this seat's piece colour.
	/// Component-wise rather than Color.Lerp, matching WallTheme's house style (it avoids
	/// Color operators throughout).</summary>
	static Color ChairFrameTint( bool white )
	{
		var piece = white ? ChessSetBuilder.WhiteColor : ChessSetBuilder.BlackColor;
		float t = ChairFrameTintStrength;
		return new Color(
			ChairFrameBase.r + ( piece.r - ChairFrameBase.r ) * t,
			ChairFrameBase.g + ( piece.g - ChairFrameBase.g ) * t,
			ChairFrameBase.b + ( piece.b - ChairFrameBase.b ) * t );
	}

	/// <summary>
	/// The procedural frame, in chair-local space.
	///
	/// <para><b>The two descriptions cross-check each other, and that is the design's own
	/// proof.</b> In SIDE ELEVATION each frame is one 5-segment polyline — floor rail →
	/// front riser → seat rail → back riser → armrest — whose front end is free, so the
	/// arms open forwards while the legs open backwards. In PLAN the sled closes at the
	/// front and the back rail closes at the back. Both readings land on the same
	/// object.</para>
	///
	/// <para>Every segment is a pair of endpoints, so every edge in here is checkable by
	/// arithmetic on a host that cannot render — see BuildTube.</para>
	/// </summary>
	void BuildChairFrame( GameObject chair, Color frame, StationChair driver )
	{
		float r = ChairTubeRadius;
		float b = ChairBendRadius;

		// ── The side frames, as ONE continuous bent tube each ──
		//
		// Read it as the polyline it is: along the floor, up at the front, back along under
		// the seat, up at the back, and forward again as the arm. The arm's front end is
		// free — that IS the cantilever, and it is what makes the legs' U open backwards
		// while the arms' opens forwards.
		//
		// One tube, not five with spheres over the mitres: a bent-tube chair is its bends.
		for ( int i = 0; i < 2; i++ )
		{
			float y = i == 0 ? -ChairSideY : +ChairSideY;
			ChessSetBuilder.BuildTubePath( chair, i == 0 ? "Frame R" : "Frame L", new[]
			{
				new Vector3( ChairBackX, y, ChairFloorRailZ ),   // sled, heel
				new Vector3( ChairFrontX, y, ChairFloorRailZ ),  // sled, toe
				new Vector3( ChairFrontX, y, ChairSeatRailZ ),   // front riser, top
				new Vector3( ChairBackX, y, ChairSeatRailZ ),    // seat rail, back
				new Vector3( ChairBackX, y, ChairArmrestZ ),     // back riser, top
				new Vector3( ChairFrontX, y, ChairArmrestZ ),    // armrest, free end
			}, r, b, frame );
		}

		// The two cross-tubes that make the side frames one object rather than two: the
		// sled closes at the FRONT, the seat rails at the BACK. Opposite ends on purpose —
		// same reason the polyline above doubles back on itself.
		ChessSetBuilder.BuildTube( chair, "FloorCross",
			new Vector3( ChairFrontX, -ChairSideY, ChairFloorRailZ ),
			new Vector3( ChairFrontX, +ChairSideY, ChairFloorRailZ ), r, frame );
		ChessSetBuilder.BuildTube( chair, "BackCross",
			new Vector3( ChairBackX, -ChairSideY, ChairSeatRailZ ),
			new Vector3( ChairBackX, +ChairSideY, ChairSeatRailZ ), r, frame );

		// ── The panels. Tinted by StationChair from the room theme, so they are handed
		// over rather than coloured here.
		var pad = AddBoxGo( chair, "SeatPad",
			new Vector3( 0f, 0f, ChairSeatRailZ ),
			new Vector3( ChairSeatDepth, ChairSeatWidth, ChairPadThickness ),
			null, ChairFrameBase );

		// One flat upright at the back riser's own x. No return curling forward — see
		// ChairBackTopZ.
		var back = AddBoxGo( chair, "BackPanel",
			new Vector3( ChairBackX, 0f, ( ChairBackBottomZ + ChairBackTopZ ) * 0.5f ),
			new Vector3( ChairBackThickness, ChairSeatWidth, ChairBackTopZ - ChairBackBottomZ ),
			null, ChairFrameBase );

		// Null for the editor preview, which has no driver and keeps the static tint —
		// MarqueeGlow's precedent exactly.
		driver?.SetPanels( pad, back );
	}

	/// <summary>Locked-camera anchor for one seat. side −1 = White (outward),
	/// +1 = Black (inward). Positioned above and behind the board edge, pre-aimed
	/// down at the board center so LobbyPlayer can lerp straight to it.
	///
	/// <para>Since M13 the position is a blend between the seated player's eyes and that
	/// orbit point — see <see cref="SeatEyeBlend"/>, whose default of 1 makes this method
	/// return exactly what it always did.</para></summary>
	GameObject BuildSeatAnchor( GameObject station, string name, float side )
	{
		// Spherical orbit around the board center: SeatOrbitRadius sets the range,
		// SeatPitch the elevation on that sphere. The camera sits dead on the
		// seat's own axis (no sideways slew) facing straight down the board.
		var center = new Vector3( 0, 0, BoardSurfaceZ + 2f );
		float pitch = SeatPitch * (MathF.PI / 180f);
		var orbit = center + new Vector3(
			side * SeatOrbitRadius * MathF.Cos( pitch ),
			0,
			SeatOrbitRadius * MathF.Sin( pitch ) );

		// Where the player's own eyes are, roughly (see SeatEyeBack/SeatEyeHeight — both
		// [GUESS], both knobs). At SeatEyeBlend 1 this value is multiplied by zero.
		var eye = new Vector3( side * SeatEyeBack, 0, SeatEyeHeight );
		var pos = Vector3.Lerp( eye, orbit, SeatEyeBlend );

		var anchor = new GameObject( true, name );
		anchor.Parent = station;
		anchor.LocalPosition = pos;
		// Aim at the board center, then pitch the aim down by SeatLookDownAngle —
		// a pure rotation of the view, position unchanged (positive = look lower).
		//
		// Derived from the BLENDED position and never itself blended: that is what keeps
		// the camera aimed at the board at every value of SeatEyeBlend with one formula
		// instead of two, and it is why this line is unchanged from before M13 — which is
		// half of the bit-for-bit proof (the other half being Lerp(eye, orbit, 1) === orbit).
		anchor.LocalRotation = Rotation.LookAt( center - anchor.LocalPosition, Vector3.Up )
			* Rotation.FromPitch( SeatLookDownAngle );
		return anchor;
	}

	/// <summary>Top-down (nadir) camera anchor for one seat — the 2D play-mode view (M16). side −1 =
	/// White (outward), +1 = Black (inward). Directly above the board centre looking straight down,
	/// with the seat's FAR rank at the top of the screen so each player reads the board from their
	/// own side. A separate anchor from <see cref="BuildSeatAnchor"/> — it never touches SeatPitch
	/// (which also drives chair/walk-up placement), and LobbyPlayer eases between the two for free
	/// when the mode changes while seated.</summary>
	GameObject BuildTopAnchor( GameObject station, string name, float side )
	{
		var center = new Vector3( 0, 0, BoardSurfaceZ + 2f );

		var anchor = new GameObject( true, name );
		anchor.Parent = station;
		anchor.LocalPosition = center + Vector3.Up * TopDownHeight;
		// Look straight DOWN. The up-hint is this seat's board-forward (the far rank), never
		// Vector3.Up — that is parallel to the look direction and degenerate. So the camera's
		// screen-up points down the board away from the player, as at a real board.
		var farDir = new Vector3( -side, 0, 0 );
		anchor.LocalRotation = Rotation.LookAt( Vector3.Down, farDir );
		return anchor;
	}

	/// <summary>Ring radius for a given station count: keeps the chord between
	/// neighboring tables at the scene-tuned 8-station spacing (Radius), then
	/// clamps so the outer seat spots (the orbit's ground footprint, plus a margin
	/// for the player) stay inside the room walls.</summary>
	float RingRadius( int count )
	{
		int n = Math.Max( count, 2 );
		float r = Radius * ( MathF.Sin( MathF.PI / 8f ) / MathF.Sin( MathF.PI / n ) );
		float roomHalf = ( Components.Get<LobbyRoom>()?.RoomSize ?? 800f ) * 0.5f;
		// SeatSpotX IS this seat footprint — the orbit's ground radius. It was spelled out
		// here and again in BuildSeatAnchor's offset; M13 needs it a third time (the chair
		// is centred on it), and three copies of one trig expression is how they stop
		// agreeing. The 30 stays a literal: it is a margin for the PLAYER, not a footprint.
		return MathF.Min( r, roomHalf - SeatSpotX - 30f );
	}

	/// <summary>
	/// |x| of a seat's walk-up spot: the camera orbit's ground footprint, and so also the
	/// centre of that seat's chair (M13). White is at −x, Black at +x.
	///
	/// <para>Everything about a seat is measured from here — the anchor sits directly above
	/// it (see <see cref="BuildSeatAnchor"/>), <see cref="ChessStation.SeatWorldPosition"/>
	/// is it at floor height, and <see cref="RingRadius"/> reserves room for it. At the
	/// shipped SeatOrbitRadius 56 / SeatPitch 55° it is 32.12.</para>
	/// </summary>
	public float SeatSpotX => SeatOrbitRadius * MathF.Cos( SeatPitch * ( MathF.PI / 180f ) );

	/// <summary>
	/// Chess table: pedestal + top slab of box primitives, a board frame with 64
	/// tinted square cells, and a full piece set at the start position. Layout in
	/// base units, all multiplied by TableScale. Local +X is Black's side (radially
	/// inward), −X is White's. One collider for the whole table, on its own
	/// uniformly-scaled GO — see LobbyRoom for why visuals and colliders split.
	/// </summary>
	void BuildChessTable( GameObject station )
	{
		var table = new GameObject( true, "Table" );
		table.Parent = station;
		table.LocalPosition = Vector3.Zero;

		float s = TableScale;

		var colliderGo = new GameObject( true, "Collider" );
		colliderGo.Parent = table;
		colliderGo.LocalPosition = new Vector3( 0, 0, 10.5f ) * s;
		// Tag kept from the cabinet era — Collision.config already knows it.
		colliderGo.Tags.Add( "cabinet" );
		// Kept a unit proud of the tabletop on each side, as it always was.
		colliderGo.AddComponent<BoxCollider>().Scale = new Vector3( TopSizeX + 2f, TopSizeY + 2f, 21 ) * s;

		// Body: foot plate, pedestal column, tabletop slab (top surface at TableTopZ).
		// Every box is placed from its own named size rather than a literal, so the
		// chair's clearances (which are differences between these) can't drift from the
		// geometry they describe. Same numbers as before, derived.
		AddBox( table, "Foot", new Vector3( 0, 0, FootHeight * 0.5f ) * s,
			new Vector3( FootSizeXY, FootSizeXY, FootHeight ) * s );
		AddBox( table, "Pedestal", new Vector3( 0, 0, FootHeight + PedestalHeight * 0.5f ) * s,
			new Vector3( PedestalSizeXY, PedestalSizeXY, PedestalHeight ) * s );
		AddBox( table, "Top", new Vector3( 0, 0, TableTopZ - TopThickness * 0.5f ) * s,
			new Vector3( TopSizeX, TopSizeY, TopThickness ) * s );

		BuildBoard( table );
		BuildTrays( table );
		BuildPieces( table );

		// Overhead spot pooling neutral white light on the board — the "powered on"
		// cue the marquee light used to provide. Only base-light properties are set
		// (spot cone names are undocumented); MarqueeGlow applies the user brightness
		// multiplier in play.
		var lightGo = new GameObject( true, "TableLight" );
		lightGo.Parent = table;
		lightGo.LocalPosition = new Vector3( 0, 0, 90f * s );
		lightGo.LocalRotation = Rotation.FromPitch( 90f ); // forward straight down

		var light = lightGo.AddComponent<SpotLight>();
		light.LightColor = new Color( MarqueeBrightness, MarqueeBrightness, MarqueeBrightness );
		light.Radius = 220f * s;
		lightGo.AddComponent<MarqueeGlow>();
	}

	// Square tints: neutral black and white (matching the room's checkerboard
	// floor). The pieces stay warm ivory/walnut, so they still read against
	// same-color squares. Frame keeps a dark wood tone. Public because
	// ChessBoardView restores cell tints to these after clearing highlights.
	public static readonly Color LightSquare = new( 0.85f, 0.85f, 0.85f );
	public static readonly Color DarkSquare = new( 0.09f, 0.09f, 0.09f );

	// 2D play mode (M16): the classic cream/brown board. The board views retint their cells to
	// these while FlatMode is on and back to the neutral pair above when it's off. Public so both
	// ChessBoardView (tables) and SpectatorBoard3D (the wall) share one palette.
	public static readonly Color Light2D = new( 0.93f, 0.85f, 0.67f );   // cream
	public static readonly Color Dark2D = new( 0.71f, 0.53f, 0.39f );    // brown
	static readonly Color FrameColor = new( 0.12f, 0.08f, 0.05f );
	// Darker than the frame and flatter than the wood: a tray should read as a
	// recess the pieces are set INTO, not another ledge on the table. It also has
	// to sit under the same ~3.3 overhead spot as the board without competing
	// with the light squares for the eye.
	static readonly Color TrayColor = new( 0.05f, 0.035f, 0.02f );

	/// <summary>Station-local position of a square's center at piece-base height
	/// (the cell top surface). file 0=a … 7=h, rank 0 = rank 1. The Table GO sits
	/// at the station origin, so this works parented to either.</summary>
	public Vector3 SquareLocalPosition( int file, int rank ) =>
		CellCenter( rank, file, TableTopZ + FrameThickness + CellThickness ) * TableScale;

	/// <summary>
	/// M14 Approach A domain gate: is this square inside the seated arm's honest reach band?
	/// <b>Scaffolding — folded into a constant or deleted in the cleanup pass with
	/// <see cref="SeatedHandSpikes"/>.</b>
	///
	/// <para>The board is centred at x=0; White sits at −X and reaches the most-negative ranks,
	/// Black mirrors it. A square counts as reachable when its near-edge distance
	/// (<c>towardSeat</c>, positive toward this seat) clears <see cref="SeatedHandSpikes.ReachBandX"/>.
	/// At the shipped geometry rank 1 is 17.06, rank 2 is 12.19 — so the default band (12) is
	/// "ranks 1–2, your own side", the doc's measured envelope. Everything nearer centre idles the
	/// hand rather than straining the arm short.</para>
	/// </summary>
	public bool SquareReachable( ChessSeat seat, int square )
	{
		if ( square < 0 ) return false;
		var p = SquareLocalPosition( square & 7, square >> 3 );
		float towardSeat = seat == ChessSeat.White ? -p.x : p.x;
		return towardSeat >= SeatedHandSpikes.ReachBandX;
	}

	/// <summary>
	/// Where a hand goes to drop a piece it has just taken: the middle of that piece's
	/// OWNER's tray — because that is where ChessBoardView actually puts it, and the hand
	/// following the piece somewhere else would be two answers to one question.
	///
	/// <para><paramref name="white"/> is the VICTIM's colour, not the captor's: each
	/// player's losses sit in their own tray (see TraySlotLocalPosition), so taking a black
	/// knight means reaching across to Black's side. That is a long reach, and it is also
	/// what a real player does.</para></summary>
	public Vector3 TrayHandLocalPosition( bool white ) =>
		TraySlotLocalPosition( white, TrayRows / 2 );

	/// <summary>Uniform scale ChessBoardView passes to ChessSetBuilder so runtime
	/// pieces match the ring-built preview set (see BuildPieces).</summary>
	public float PieceScale => TableScale * ( BoardSize / 26f );

	/// <summary>World edge length of one board square.</summary>
	public float CellWorldSize => BoardSize / 8f * TableScale;

	/// <summary>Board frame slab + 64 tinted cells on the tabletop.</summary>
	void BuildBoard( GameObject table )
	{
		float s = TableScale;
		float cell = BoardSize / 8f;

		AddBox( table, "BoardFrame",
			new Vector3( 0, 0, TableTopZ + FrameThickness * 0.5f ) * s,
			new Vector3( BoardSize + 3f, BoardSize + 3f, FrameThickness ) * s, null, FrameColor );

		float cellCenterZ = TableTopZ + FrameThickness + CellThickness * 0.5f;
		for ( int rank = 0; rank < 8; rank++ )
		{
			for ( int file = 0; file < 8; file++ )
			{
				bool light = ( rank + file ) % 2 != 0; // a1 (rank 0, file 0) is dark
				AddBox( table, $"Cell {(char)('a' + file)}{rank + 1}",
					CellCenter( rank, file, cellCenterZ ) * s,
					new Vector3( cell, cell, CellThickness ) * s, null,
					light ? LightSquare : DarkSquare );
			}
		}
	}

	/// <summary>The two captured-piece trays: a shallow slab in each Y margin,
	/// running the board's length so a full tray lines up with the board's ranks.
	/// <para>The names must NOT start with "Cell " — ChessBoardView.ResolveCells
	/// prefix-scans the Table's children for exactly that and would try to parse a
	/// tray as a square.</para></summary>
	void BuildTrays( GameObject table )
	{
		float s = TableScale;
		float z = TableTopZ + TrayThickness * 0.5f;       // resting ON the tabletop
		// Width comes from the margin budget, not from "2 columns + a lip" — which is
		// what made the slab exactly fill the margin and touch both edges.
		var size = new Vector3( BoardSize, TrayWidth, TrayThickness ) * s;

		AddBox( table, "Tray White", new Vector3( 0, -TrayCenterY, z ) * s, size, null, TrayColor );
		AddBox( table, "Tray Black", new Vector3( 0, TrayCenterY, z ) * s, size, null, TrayColor );
	}

	/// <summary>Base-unit center of a square: ranks along local X (rank 1 nearest
	/// White at −X), files along −Y so a1 sits at White's left — which puts a light
	/// square on each player's right, per convention.</summary>
	Vector3 CellCenter( int rank, int file, float z )
	{
		float cell = BoardSize / 8f;
		float x = ( rank - 3.5f ) * cell;
		float y = ( 3.5f - file ) * cell;
		return new Vector3( x, y, z );
	}

	/// <summary>Full set at the start position. Piece scale rides BoardSize so
	/// resizing the board keeps pieces proportional to their squares.</summary>
	void BuildPieces( GameObject table )
	{
		float pieceScale = TableScale * ( BoardSize / 26f );
		float z = TableTopZ + FrameThickness + CellThickness;

		var pieces = new GameObject( true, "Pieces" );
		pieces.Parent = table;
		pieces.LocalPosition = Vector3.Zero;

		for ( int file = 0; file < 8; file++ )
		{
			PlacePiece( pieces, ChessSetBuilder.BackRank[file], white: true, rank: 0, file, z, pieceScale );
			PlacePiece( pieces, ChessPieceType.Pawn, white: true, rank: 1, file, z, pieceScale );
			PlacePiece( pieces, ChessPieceType.Pawn, white: false, rank: 6, file, z, pieceScale );
			PlacePiece( pieces, ChessSetBuilder.BackRank[file], white: false, rank: 7, file, z, pieceScale );
		}
	}

	void PlacePiece( GameObject parent, ChessPieceType type, bool white, int rank, int file, float z, float pieceScale )
	{
		// White faces +X (toward Black), Black faces −X — knights look at the enemy.
		var piece = ChessSetBuilder.BuildPiece( parent, type, white, pieceScale, yaw: white ? 0f : 180f );
		piece.LocalPosition = CellCenter( rank, file, z ) * TableScale;
	}

	void AddBox( GameObject parent, string name, Vector3 localPos, Vector3 size,
		Rotation? localRot = null, Color? tint = null )
		=> AddBoxGo( parent, name, localPos, size, localRot, tint );

	/// <summary>As <see cref="AddBox"/>, but hands back the GameObject.
	///
	/// <para>Only for boxes something has to find again — the clock's plates (tinted as a
	/// seat's turn starts) and its material fill (rescaled as the balance moves). Prefer
	/// <see cref="AddBox"/> everywhere else: a builder that returns nothing cannot be
	/// quietly turned into a per-frame handle.</para>
	///
	/// <para>Returns null if the box model is missing — every caller must cope, because
	/// AddBox has always been allowed to draw nothing.</para></summary>
	GameObject AddBoxGo( GameObject parent, string name, Vector3 localPos, Vector3 size,
		Rotation? localRot = null, Color? tint = null )
	{
		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null )
		{
			Log.Warning( "[Gambit] models/dev/box.vmdl failed to load — chess tables have colliders but no visuals" );
			return null;
		}

		var visual = new GameObject( true, name );
		visual.Parent = parent;
		visual.LocalPosition = localPos;
		visual.LocalRotation = localRot ?? Rotation.Identity;

		var modelSize = model.Bounds.Size;
		visual.LocalScale = new Vector3(
			size.x / modelSize.x,
			size.y / modelSize.y,
			size.z / modelSize.z );

		var renderer = visual.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = tint ?? TableColor;
		return visual;
	}

	void Clear()
	{
		foreach ( var go in _spawned )
		{
			if ( go.IsValid() )
				go.Destroy();
		}
		_spawned.Clear();
	}
}
