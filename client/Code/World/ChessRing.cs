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

	/// <summary>Calibration multiplier on the computed UI rect (see ScreenFractionRect) —
	/// nudge until engaged UI lines up with the board on screen.</summary>
	[Property] public float UiFit { get; set; } = 1f;

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
	const float ClockBoardGap = 0.2f;   // board frame → clock
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

	protected override void OnUpdate()
	{
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

	/// <summary>X of a plate's INNER edge — where the middle, and so the bar, has to stop.</summary>
	static float ClockPlateInnerX => ClockPlateOffsetX - ClockPlateLength * 0.5f;

	/// <summary>Lift of a panel off its plate's face, along the plate's own normal — clear of
	/// it rather than z-fighting it. BuildStationPlaque's PlaqueOutset, same job.</summary>
	const float ClockTextOutset = 0.05f;

	// The plate face's pixel space. Width is arbitrary (it only fixes how many px a base unit
	// is worth); HEIGHT IS DERIVED from the TEXT SPAN's aspect — the box the panel actually
	// maps onto — so the panel's pixel space and its world span cannot drift out of
	// proportion. Typing both by hand is how a stylesheet's px stop meaning what the geometry
	// says they mean.
	const float ClockPxWidth = 512f;
	const float ClockPxHeight = ClockPxWidth * ClockTextSpanHeight / ClockTextSpanLength;

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
	// Mesh, not a div. A track that is ALWAYS there, and a fill growing from dead centre
	// toward whoever is ahead — so at level the track is all you see, which is what a material
	// bar at zero should look like: a centred bar. Centre-out is also what lets the bar say
	// WHO as well as by how much. Lives in the plates' plane so the three read as one
	// instrument; its bottom edge is level with theirs (see BuildClockBar).
	const float ClockBarHeight = 1.4f;
	const float ClockBarThickness = 0.2f;

	/// <summary>Clear space between a plate's inner edge and the bar's end.</summary>
	const float ClockBarGap = 0.7f;

	/// <summary>The track's length — DERIVED from whatever the plates leave, so a plate can
	/// never grow into the bar. This is the "don't clip the bar" constraint expressed as
	/// arithmetic rather than as two hand-set numbers that have to be remembered together.</summary>
	static float ClockBarLength => 2f * ( ClockPlateInnerX - ClockBarGap );

	/// <summary>How far the fill stands proud of its track, along the plate normal.</summary>
	const float ClockBarProud = 0.12f;

	// ── The lead badge ──
	//
	// The real material difference, ALWAYS drawn, on its own small plate standing proud IN
	// FRONT OF the bar. It is a second string, so by this file's own rule it is a second
	// plate — never a second div on an existing panel.
	//
	// It carries its own black plate rather than floating text straight onto the bar because
	// the thing behind it CHANGES COLOUR: White's fill is near-white and Black's is near-black,
	// so no single text colour is legible against both. A plate makes the background a
	// constant, which is the only way "always draws in front of the bar" can also mean
	// "always readable".
	//
	// It is what keeps the bar honest past saturation: the bar pins at BarFullAt and stops
	// telling the truth, and the number never does.
	const float ClockLeadLength = 3f;
	const float ClockLeadHeight = 1.3f;      // inside ClockBarHeight — it sits ON the bar
	const float ClockLeadThickness = 0.2f;

	/// <summary>Proud of the FILL, not just the track — the fill passes behind this. Must
	/// clear ClockBarProud + half the fill's thickness or the badge and the fill intersect.</summary>
	const float ClockLeadProud = 0.34f;

	const float ClockLeadPxHeight = ClockPxWidth * ClockLeadHeight / ClockLeadLength;

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

	// Bar tints. The track is a MID grey so that both fills contrast with it — White's is
	// near-white and Black's near-black, and a dark track would swallow Black's fill whole.
	static readonly Color ClockBarTrackColor = new( 0.05f, 0.05f, 0.055f );
	internal static readonly Color ClockBarWhiteColor = new( 0.90f, 0.89f, 0.86f );
	internal static readonly Color ClockBarBlackColor = new( 0.002f, 0.002f, 0.003f );

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

	/// <summary>The material bar: a track that is ALWAYS drawn, a fill growing from dead
	/// centre toward whoever is ahead, and the lead badge standing proud in front of both.
	/// <see cref="TableClock"/> moves the fill and writes the number.
	///
	/// <para><b>The track never hides, and that is the design.</b> Level is where every game
	/// starts, and at level the track — centred, empty — is exactly what a material bar should
	/// look like. It used to hide itself outright, which meant the normal state of the object
	/// was "not there".</para>
	///
	/// <para><b>Bottom edge level with the plates', by arithmetic.</b> The bar is shorter than
	/// a plate, so centring it in the plane would float it around the plates' middles and read
	/// as three unrelated things; bottoms level is what makes them one instrument. That was
	/// `align-items: flex-end` when this was CSS — here it is the plate's half-height minus
	/// the bar's, which is a number that can be checked.</para></summary>
	void BuildClockBar( GameObject clock, TableClock driver )
	{
		float s = TableScale;

		var bar = new GameObject( true, "Bar" );
		bar.Parent = clock;
		// The plates' origin AND their rotation — so all three lie in one plane, and the bar
		// rests on the plinth exactly as they do rather than sinking into it.
		bar.LocalPosition = new Vector3( 0f, 0f, ClockPlaneOriginZ ) * s;
		bar.LocalRotation = Rotation.From( ClockFaceTilt, 90f, 0f );

		// Bottoms level: the plate spans ±ClockPlateHeight/2 about its centre and both are
		// centred at the same height, so dropping the bar by the difference of the half-heights
		// puts its lower edge exactly on the plates'. Sharing ClockPlaneOriginZ is what makes
		// that true for free — level with each other, and both level with the plinth's top.
		float dropZ = ( ClockPlateHeight - ClockBarHeight ) * 0.5f;

		AddBox( bar, "Track", new Vector3( 0f, 0f, -dropZ ) * s,
			new Vector3( ClockBarThickness, ClockBarLength, ClockBarHeight ) * s,
			null, ClockBarTrackColor );

		// The fill is built at FULL extension — dead centre to one end, half the track — and
		// the driver scales it down from there. Building it full is what lets TableClock hold
		// the full scale as a number instead of re-deriving it from Model.Bounds: AddBoxGo did
		// that arithmetic once already, and doing it twice is how the two come to disagree.
		float halfLength = ClockBarLength * 0.5f;
		var fill = AddBoxGo( bar, "Fill",
			new Vector3( ClockBarProud, 0f, -dropZ ) * s,
			new Vector3( ClockBarThickness, halfLength, ClockBarHeight ) * s,
			null, ClockBarWhiteColor );
		if ( fill is null ) return;

		driver.BarFill = fill;
		driver.BarFillRenderer = fill.GetComponent<ModelRenderer>();
		driver.BarFillFullScale = fill.LocalScale;
		driver.BarFillBasePosition = fill.LocalPosition;
		driver.BarFillHalfLength = halfLength * s;

		BuildClockLead( bar, driver, -dropZ );
	}

	/// <summary>The lead badge: the real material difference, on its own black plate standing
	/// proud in front of the bar, centred on it.
	///
	/// <para>Its own plate, and not text laid straight onto the bar, because the thing behind
	/// it changes colour — White's fill is near-white, Black's near-black, and no single text
	/// colour survives both. The plate makes the background a constant.</para>
	///
	/// <para>It covers the middle of the fill, which is fine and deliberate: the fill grows
	/// from the centre, so its TIP is the part that carries the length, and the tip is always
	/// outboard of this. For a lead too small to clear the badge, the number is the readout
	/// anyway.</para></summary>
	void BuildClockLead( GameObject bar, TableClock driver, float localZ )
	{
		float s = TableScale;

		var badge = new GameObject( true, "Lead" );
		badge.Parent = bar;
		// In the bar's own plane and rotation (it is a child), just proud of the FILL — see
		// ClockLeadProud, which must clear the fill or the two intersect.
		badge.LocalPosition = new Vector3( ClockLeadProud, 0f, localZ ) * s;

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

	/// <summary>Locked-camera anchor for one seat. side −1 = White (outward),
	/// +1 = Black (inward). Positioned above and behind the board edge, pre-aimed
	/// down at the board center so LobbyPlayer can lerp straight to it.</summary>
	GameObject BuildSeatAnchor( GameObject station, string name, float side )
	{
		// Spherical orbit around the board center: SeatOrbitRadius sets the range,
		// SeatPitch the elevation on that sphere. The camera sits dead on the
		// seat's own axis (no sideways slew) facing straight down the board.
		var center = new Vector3( 0, 0, BoardSurfaceZ + 2f );
		float pitch = SeatPitch * (MathF.PI / 180f);
		var offset = new Vector3(
			side * SeatOrbitRadius * MathF.Cos( pitch ),
			0,
			SeatOrbitRadius * MathF.Sin( pitch ) );

		var anchor = new GameObject( true, name );
		anchor.Parent = station;
		anchor.LocalPosition = center + offset;
		// Aim at the board center, then pitch the aim down by SeatLookDownAngle —
		// a pure rotation of the view, position unchanged (positive = look lower).
		anchor.LocalRotation = Rotation.LookAt( center - anchor.LocalPosition, Vector3.Up )
			* Rotation.FromPitch( SeatLookDownAngle );
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
		float seatFootprint = SeatOrbitRadius * MathF.Cos( SeatPitch * (MathF.PI / 180f) );
		return MathF.Min( r, roomHalf - seatFootprint - 30f );
	}

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
		AddBox( table, "Foot", new Vector3( 0, 0, 0.5f ) * s, new Vector3( 20, 20, 1 ) * s );
		AddBox( table, "Pedestal", new Vector3( 0, 0, 9.5f ) * s, new Vector3( 10, 10, 17 ) * s );
		AddBox( table, "Top", new Vector3( 0, 0, TableTopZ - 1f ) * s, new Vector3( TopSizeX, TopSizeY, 2 ) * s );

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
