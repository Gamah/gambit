using System;
using System.Collections.Generic;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Builds N arcade stations arranged as a regular N-gon in the middle of the room,
/// screens facing each other across the center (players stand inside the ring and
/// look outward at a screen). Each station is a cabinet of box primitives under a
/// single "Cabinet" parent GO so the primitives can later be swapped for a real model.
///
/// Build paths:
/// - Editor: OnEnabled builds a NotSaved preview (same pattern as LobbyRoom).
/// - Play: only the HOST builds, from LobbyNetworkManager.OnHostInitialize, which then
///   NetworkSpawns each station so its [Sync] occupancy replicates. Clients must NOT
///   build their own copies — they receive the host's via network spawn.
/// </summary>
public sealed class ArcadeRing : Component, Component.ExecuteInEditor
{
	/// <summary>How many stations / sides of the N-gon.</summary>
	[Property] public int StationCount { get; set; } = 8;

	/// <summary>Distance from ring center to each screen, as tuned for an 8-station
	/// ring. The actual radius scales with StationCount to keep the spacing between
	/// neighboring cabinets constant (see RingRadius), clamped so the locked-camera
	/// anchors stay inside the room.</summary>
	[Property] public float Radius { get; set; } = 160f;

	/// <summary>Height of the screen center above the floor. Keep in sync with the
	/// exposed back-face center (46 * CabinetScale) so the screen sits in the slot
	/// between the cabinet's Deck and Top boxes.</summary>
	[Property] public float ScreenHeight { get; set; } = 69f;

	/// <summary>Extra multiplier on the screen panel (on top of CabinetScale). The
	/// WorldPanel's intrinsic size is undocumented, so tune this until the panel
	/// fills the exposed back-face width (36 * CabinetScale units).</summary>
	[Property] public float ScreenScale { get; set; } = 1f;

	/// <summary>How far inward of the screen the locked camera sits.</summary>
	[Property] public float CameraDistance { get; set; } = 75f;

	/// <summary>Build the box-primitive cabinet under each screen.</summary>
	[Property] public bool BuildCabinets { get; set; } = true;

	/// <summary>Yaw added to the whole ring. Station 0's screen faces outward along
	/// (baseAngle + this); 90 turns the ring so the OG cabinet (station 0) points at
	/// the +Y leaderboard / spectator wall.</summary>
	[Property] public float RingYawOffset { get; set; } = 90f;

	/// <summary>Uniform multiplier on all cabinet box positions and sizes.</summary>
	[Property] public float CabinetScale { get; set; } = 1.5f;

	[Property] public Color CabinetColor { get; set; } = new Color( 0.03f, 0.03f, 0.03f );

	/// <summary>Calibration multiplier on the computed UI rect (see ScreenFractionRect) —
	/// nudge until the game UI lines up with the cabinet screen.</summary>
	[Property] public float UiFit { get; set; } = 1f;

	/// <summary>Cube board span in base units (× CabinetScale). The screen slot is
	/// 36 wide; 26 leaves a healthy margin.</summary>
	[Property] public float BoardSize { get; set; } = 28f;

	/// <summary>Fraction of a cell step a cube fills — the remainder is the gap
	/// between cubes at rest.</summary>
	[Property] public float CubeSize { get; set; } = 0.78f;

	/// <summary>How far the cube board protrudes in front of the screen face,
	/// expressed as a fraction of a cell step (so it scales with BoardSize).
	/// 0.08 ≈ 8% of a step — just a sliver proud of the screen.</summary>
	[Property] public float CubeProtrusion { get; set; } = 0.08f;

	/// <summary>Material override for the board cubes (and selectors) — the dev-box
	/// grid texture greys the tints out. A ".shader" path loads via
	/// Material.FromShader. Blank or missing path = no override. (An unlit cube
	/// shader was tried for #48 and removed — emissive cubes read far too bright;
	/// the marquee duck handles the board shadows instead.)</summary>
	[Property] public string CubeMaterial { get; set; } = "materials/default.vmat";

	/// <summary>Extra camera pull-back from the anchor while the cube board is out
	/// (play/replay), so the proud cubes don't crowd the view.</summary>
	[Property] public float PlayCameraBackoff { get; set; } = 10f;

	/// <summary>Extra camera lift (world Z) while the cube board is out; the camera
	/// tilts down to keep aiming at the board center.</summary>
	[Property] public float PlayCameraRise { get; set; } = 8f;

	/// <summary>Marquee spot brightness. Strictly neutral white — a tinted light
	/// would shift the cabinet/bezel hues around the board (#48).</summary>
	[Property] public float MarqueeBrightness { get; set; } = 3.3f;

	/// <summary>Fraction of MarqueeBrightness left while a cube board is out —
	/// CubeBoardView ducks the light so the spot cone doesn't paint a hotspot and
	/// cube self-shadows across the board (#48). 0 = off during play, 1 = no duck.</summary>
	[Property] public float MarqueeDuck { get; set; } = 0.3f;

	/// <summary>Multiplier on the board cube tints. Scales all palette colors
	/// equally, preserving the verified ΔE separations. Default 1: the palettes
	/// themselves are already dimmed to 2/3 intensity.</summary>
	[Property] public float CubeBrightness { get; set; } = 1f;

	public static ArcadeRing Instance { get; private set; }

	readonly List<GameObject> _spawned = new();
	bool _runtimeBuilt;

	/// <summary>
	/// World half-extent of the square screen opening (#33). Single source for the
	/// bezel/glass geometry AND the locked-camera UI rect, so the menus always fill
	/// the physical opening exactly and both track the ScreenScale slider together.
	/// Clamped so the opening keeps a bezel lip inside the cabinet's 36-wide slot.
	/// </summary>
	public float ScreenOpeningHalf => MathF.Min( 18f * ScreenScale * UiFit, 16f ) * CabinetScale;

	/// <summary>
	/// Normalized (0..1) viewport rect the cabinet screen occupies while the camera is
	/// locked at the anchor. No projection API is documented, but none is needed: the
	/// camera sits square-on at CameraDistance from a screen of half-extent
	/// ScreenOpeningHalf (shared with the bezel/glass geometry), so the rect is plain
	/// trigonometry from the FOV. Assumes FieldOfView is horizontal — UiFit calibrates
	/// the result either way.
	/// </summary>
	public static Rect ScreenFractionRect()
	{
		var ring = Instance;
		var cam = ring?.Scene?.Camera;
		if ( ring == null || cam == null || ring.CameraDistance <= 0f )
			return new Rect( 0f, 0f, 1f, 1f );

		float halfWorld = ring.ScreenOpeningHalf;
		float tanHalf = MathF.Tan( cam.FieldOfView * 0.5f * (MathF.PI / 180f) );
		// Include the play-mode pull-back so overlays stay on the screen rect
		float dist = ring.CameraDistance + LobbyPlayer.CameraBackoff;
		float widthFrac = halfWorld / (dist * tanHalf);
		float heightFrac = widthFrac * (Screen.Width / Screen.Height);

		widthFrac = Math.Clamp( widthFrac, 0.05f, 1f );
		heightFrac = Math.Clamp( heightFrac, 0.05f, 1f );

		return new Rect( (1f - widthFrac) * 0.5f, (1f - heightFrac) * 0.5f, widthFrac, heightFrac );
	}

	/// <summary>Inline style placing a panel over the cabinet screen (percent units, so
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
		// Order-safe either way: if the host already rebuilt, _runtimeBuilt guards the
		// authoritative set; if not, this clears the preview before the host rebuild.
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
		return BuildInternal();
	}

	/// <summary>Network-spawn every station in the scene so its [Sync] occupancy
	/// replicates — used by LobbyNetworkManager on host init and by the animated
	/// cabinet-count rebuild.</summary>
	public void NetworkSpawnStations()
	{
		foreach ( var station in Scene.GetAllComponents<ArcadeStation>() )
		{
			station.GameObject.NetworkSpawn();
			// Default NetworkOrphaned.Destroy kills host-owned objects for everyone when
			// the host disconnects — hand them to the migrated host instead.
			station.GameObject.Network.SetOrphanedMode( NetworkOrphaned.Host );
		}
	}

	// ── Host cabinet-count rebuild (issue #49) ──
	// On change: hold while the host is still on the settings board, then 0.5s after
	// the panel closes slide every station down through the floor, rebuild the ring
	// with the new count (network-spawning the new stations), and slide back up.
	// Host-only; station transforms are networked, so clients see the slide.

	/// <summary>How far the cabinets sink below their rest height (cabinets are
	/// ~120 units tall at the default CabinetScale).</summary>
	[Property] public float SlideDepth { get; set; } = 130f;

	/// <summary>Duration of each cabinet's slide leg (down, and up again).</summary>
	[Property] public float SlideSeconds { get; set; } = 0.9f;

	/// <summary>Stagger between successive cabinets starting their slide, so they
	/// drop/rise in sequence rather than all at once.</summary>
	[Property, Range( 0.05f, 0.75f )] public float SlideStaggerSeconds { get; set; } = 0.25f;

	/// <summary>Which servo slide SFX each cabinet emits while descending /
	/// ascending (issue #54). Forward on descend, same sound reversed on ascend.
	/// Swap in the inspector to A/B the variations.</summary>
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
	/// Ignored unless this client is the host, and blocked while any cabinet is
	/// occupied (the settings board greys the cells out, but occupancy can change
	/// while pending, so it is re-checked before the slide starts). The count can be
	/// re-picked freely while pending — picking the current count cancels. The slide
	/// itself waits until the host closes the settings panel.</summary>
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
		foreach ( var station in Scene.GetAllComponents<ArcadeStation>() )
			if ( station.Occupied ) return true;
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

	/// <summary>Total length of a leg once the stagger fans the cabinets out: the
	/// last cabinet starts at (count-1)*stagger and still needs a full SlideSeconds.</summary>
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
				go.GetComponent<ArcadeStation>()?.NetSlideSfx( SlideVariant, ascend: !down );

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

		// Station 0 is the "OG" cabinet (demo mode runs on it) — aim it at the player
		// spawn (the LobbyNetworkManager GO) so the demo plays on the cabinet players
		// walk up to first. Fallback yaw 0 if there's no spawn in the scene. RingYawOffset
		// then turns the whole ring (default 90°: station 0 points at the +Y leaderboard
		// / spectator wall).
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

			var station = new GameObject( true, $"ArcadeStation{i}" );
			station.Flags |= GameObjectFlags.NotSaved;
			station.WorldPosition = WorldPosition + outward.Forward * radius;
			// Station +X points radially inward, so the screen (and the player) face
			// outward and the cabinets sit back-to-back in the middle
			station.WorldRotation = Rotation.FromYaw( angle + 180f );
			_spawned.Add( station );

			// Screen faces away from the ring center (panel front is the GO's -X after
			// yaw 180, same as the old wall-mounted setup)
			var screen = new GameObject( true, "Screen" );
			screen.Parent = station;
			screen.LocalPosition = new Vector3( 0, 0, ScreenHeight );
			screen.LocalRotation = Rotation.FromYaw( 180f );
			// WorldPanel world size scales with the GO transform (PanelSize itself is
			// undocumented), and the attract text scales along with the panel
			screen.LocalScale = CabinetScale * ScreenScale;
			screen.AddComponent<WorldPanel>();
			screen.AddComponent<Gambit.UI.ArcadeScreenPanel>();

			// Camera anchor inward of the screen, looking outward at it (identity local
			// rotation = station +X, exactly like the old per-wall anchors)
			var anchor = new GameObject( true, "CameraAnchor" );
			anchor.Parent = station;
			anchor.LocalPosition = new Vector3( -CameraDistance, 0, ScreenHeight );

			var component = station.AddComponent<ArcadeStation>();
			component.CameraAnchor = anchor;

			if ( BuildCabinets )
				BuildCabinet( station, i );
		}

		return _spawned;
	}

	/// <summary>Ring radius for a given station count: keeps the chord between
	/// neighboring cabinets at the scene-tuned 8-station spacing (Radius), then
	/// clamps so the locked-camera anchors (CameraDistance outward of each screen,
	/// plus a margin for the player) stay inside the room walls.</summary>
	float RingRadius( int count )
	{
		int n = Math.Max( count, 2 );
		float r = Radius * ( MathF.Sin( MathF.PI / 8f ) / MathF.Sin( MathF.PI / n ) );
		float roomHalf = ( Components.Get<LobbyRoom>()?.RoomSize ?? 800f ) * 0.5f;
		return MathF.Min( r, roomHalf - CameraDistance - 30f );
	}

	/// <summary>
	/// Arcade-cabinet-shaped stack of box primitives (+X is behind the screen plane,
	/// the player faces local -X). Layout in base units, all multiplied by CabinetScale:
	/// - Back: full-height slab, front face at x=1, just behind the screen at x=0
	/// - Base: pedestal under the deck, front at x=-9, up to z=18
	/// - Kick: 45° slab joining the base front (x=-9, z=18) to the deck front (x=-15, z=24)
	/// - Deck: control deck slab, play surface on top at z=28, with joystick (player's
	///   left, +Y) and two rotate buttons (player's right, -Y) on it
	/// - Marquee: 30°-back-leaning slab over z 64..80, capped by a small Roof box
	/// The back face exposed between Deck and Marquee (z 28..64, full 36 width) is the
	/// screen slot — ScreenHeight should be its center, 46 * CabinetScale, and
	/// ScreenScale tunes the panel to fill its width.
	/// One collider for the whole cabinet, on its own uniformly-scaled GO — see
	/// LobbyRoom for why visuals and colliders are split. Rotated boxes are visuals
	/// only; the collider stays a single axis-aligned box.
	/// </summary>
	void BuildCabinet( GameObject station, int index )
	{
		var cabinet = new GameObject( true, "Cabinet" );
		cabinet.Parent = station;
		cabinet.LocalPosition = Vector3.Zero;

		float s = CabinetScale;

		var colliderGo = new GameObject( true, "Collider" );
		colliderGo.Parent = cabinet;
		colliderGo.LocalPosition = new Vector3( -1, 0, 40 ) * s;
		// "cabinet" is Ignored against "boardcube" in Collision.config — exploding
		// board cubes spawn overlapping this collider (see CubeBoardView)
		colliderGo.Tags.Add( "cabinet" );
		colliderGo.AddComponent<BoxCollider>().Scale = new Vector3( 28, 36, 80 ) * s;

		// Body
		AddBox( cabinet, "Back", new Vector3( 7, 0, 40 ) * s, new Vector3( 12, 36, 80 ) * s );
		AddBox( cabinet, "Base", new Vector3( -4, 0, 9 ) * s, new Vector3( 10, 36, 18 ) * s );
		AddBox( cabinet, "Kick", new Vector3( -12, 0, 21 ) * s, new Vector3( 3, 36, 9 ) * s,
			Rotation.FromPitch( -45f ) );
		AddBox( cabinet, "Deck", new Vector3( -8, 0, 26 ) * s, new Vector3( 14, 36, 4 ) * s );
		AddBox( cabinet, "Marquee", new Vector3( -5, 0, 72 ) * s, new Vector3( 4, 36, 18 ) * s,
			Rotation.FromPitch( 40f ) );
		AddBox( cabinet, "Roof", new Vector3( -2.5f, 0, 80 ) * s, new Vector3( 9, 36, 2 ) * s );

		AddMarqueeNumber( cabinet, index );

		// ── Screen hardware (#33) ──
		// Real lit geometry so the menu panels read as a screen attached to the
		// cabinet instead of floating UI: a recessed dark glass slab the translucent
		// panel content draws over, a bezel frame proud of it that catches the
		// marquee light, and a soft glow the screen casts back onto the cabinet.
		// All sized from ScreenOpeningHalf — the same number that drives the engaged
		// UI rect — so menus, glass and bezel track the ScreenScale slider together.
		float half = ScreenOpeningHalf;
		float lip  = 2f * s;

		// Glass slab: front face just behind the WorldPanel plane at x=0, edges
		// tucked behind the bezel. CubeBoardView slides this GO into the Back slab
		// while a cube board is out so the cubes never intersect it.
		// Neutral greys only — no new hues anywhere near the board: the colorblind
		// palettes depend on cubes reading as their exact configured colors.
		AddBox( cabinet, "ScreenGlass", new Vector3( 0.45f * s, 0, ScreenHeight ),
			new Vector3( 0.5f * s, (half + lip * 0.75f) * 2f, (half + lip * 0.75f) * 2f ),
			null, new Color( 0.035f, 0.035f, 0.035f ) );

		// Bezel reuses the cabinet's own (scene-tunable) hue, just lightened
		var bezelColor = new Color( CabinetColor.r * 1.6f, CabinetColor.g * 1.6f, CabinetColor.b * 1.6f );
		float bezelThick = 2f * s; // spans x -1*s..+1*s: back flush with the Back slab, front 1*s proud
		AddBox( cabinet, "BezelTop", new Vector3( 0, 0, ScreenHeight + half + lip * 0.5f ),
			new Vector3( bezelThick, (half + lip) * 2f, lip ), null, bezelColor );
		AddBox( cabinet, "BezelBottom", new Vector3( 0, 0, ScreenHeight - half - lip * 0.5f ),
			new Vector3( bezelThick, (half + lip) * 2f, lip ), null, bezelColor );
		AddBox( cabinet, "BezelLeft", new Vector3( 0, half + lip * 0.5f, ScreenHeight ),
			new Vector3( bezelThick, lip, half * 2f ), null, bezelColor );
		AddBox( cabinet, "BezelRight", new Vector3( 0, -half - lip * 0.5f, ScreenHeight ),
			new Vector3( bezelThick, lip, half * 2f ), null, bezelColor );

		// The "powered screen" cue: a soft light the screen throws onto the bezel,
		// deck and anyone standing at the cabinet. Strictly neutral white — a
		// tinted light would shift the board cubes' colors and break the
		// colorblind palettes.
		var screenGlowGo = new GameObject( true, "ScreenGlow" );
		screenGlowGo.Parent = cabinet;
		screenGlowGo.LocalPosition = new Vector3( -7f * s, 0, ScreenHeight );
		var screenGlow = screenGlowGo.AddComponent<PointLight>();
		screenGlow.LightColor = new Color( 0.6f, 0.6f, 0.6f );
		screenGlow.Radius = 55f * s;

		// Play surface (deck top, z=28). Player's left while facing the screen is +Y.
		var joyColor = new Color( 0.25f, 0.25f, 0.3f );
		AddBox( cabinet, "JoyShaft", new Vector3( -9, 9, 30.5f ) * s, new Vector3( 1.2f, 1.2f, 5 ) * s, null, joyColor );
		AddBox( cabinet, "JoyBall", new Vector3( -9, 9, 34 ) * s, new Vector3( 3, 3, 3 ) * s, null,
			new Color( 0.85f, 0.12f, 0.2f ) );

		var buttonColor = new Color( 0.95f, 0.85f, 0.2f );
		// y=-6 is the left button of the pair from the player's view → CCW, like Z/X keys
		AddBox( cabinet, "ButtonCCW", new Vector3( -9, -6, 28.6f ) * s, new Vector3( 5, 5, 1.6f ) * s, null, buttonColor );
		AddBox( cabinet, "ButtonCW", new Vector3( -9, -13, 28.6f ) * s, new Vector3( 5, 5, 1.6f ) * s, null, buttonColor );
		AddButtonGlyph( cabinet, new Vector3( -9, -6, 29.5f ) * s, "↺" );
		AddButtonGlyph( cabinet, new Vector3( -9, -13, 29.5f ) * s, "↻" );

		// Marquee light hung out in front of the cabinet, aimed back down at it
		// (position/pitch editor-tuned 2026-06-11; x is absolute, not scaled).
		// Only base-light properties are set — spot cone names are undocumented,
		// tune the cone in the inspector if needed.
		var lightGo = new GameObject( true, "MarqueeLight" );
		lightGo.Parent = cabinet;
		lightGo.LocalPosition = new Vector3( -50f, 0, 66f * s );
		lightGo.LocalRotation = Rotation.From( 40f, 180f, 0f );

		var light = lightGo.AddComponent<SpotLight>();
		light.LightColor = new Color( MarqueeBrightness, MarqueeBrightness, MarqueeBrightness );
		light.Radius = 350f * s;
		// Play-mode color owner: user tint/brightness (#49) × CubeBoardView duck (#48)
		lightGo.AddComponent<MarqueeGlow>();
	}

	void AddBox( GameObject parent, string name, Vector3 localPos, Vector3 size,
		Rotation? localRot = null, Color? tint = null )
	{
		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null )
		{
			Log.Warning( "[Gambit] models/dev/box.vmdl failed to load — arcade cabinets have colliders but no visuals" );
			return;
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
		renderer.Tint = tint ?? CabinetColor;
	}

	/// <summary>
	/// Tiny WorldPanel lying flat on a button top, showing a rotate-direction arrow.
	/// Pitch -90 points the panel front (+forward) straight up; yaw 180 first so the
	/// glyph's top edge ends up on the far (+X) side, upright for a player standing
	/// at -X looking down at the deck.
	/// </summary>
	void AddButtonGlyph( GameObject parent, Vector3 localPos, string glyph )
	{
		var go = new GameObject( true, $"Glyph {glyph}" );
		go.Parent = parent;
		go.LocalPosition = localPos;
		go.LocalRotation = Rotation.From( -90f, 180f, 0f );
		// WorldPanel intrinsic size matches the cabinet screen's (~36 units at scale 1),
		// so ~0.15 covers a 5-unit button face — tune in editor if mis-sized
		go.LocalScale = 0.15f * CabinetScale;
		go.AddComponent<WorldPanel>();
		go.AddComponent<Gambit.UI.ButtonGlyphPanel>().Glyph = glyph;
	}

	/// <summary>
	/// Cabinet number on the outward (player-facing) face of the leaning marquee
	/// slab. The marquee box is centered at (-5,0,72)·s, 4·s thick along its local
	/// -X, pitched back 40°; the panel sits just proud of that front face, leaning
	/// with it. Front (+forward) faces outward; FromYaw(180) keeps the digit
	/// un-mirrored. Facing/flip may need editor tuning (WorldPanel orientation is
	/// undocumented) — set the rotation's yaw to 0 if it reads backwards.
	/// </summary>
	void AddMarqueeNumber( GameObject cabinet, int index )
	{
		float s = CabinetScale;
		var lean = Rotation.FromPitch( 40f );
		var center = new Vector3( -5, 0, 72 ) * s;
		var faceNormal = lean * new Vector3( -1, 0, 0 ); // outward marquee front

		var go = new GameObject( true, $"MarqueeNumber {index}" );
		go.Parent = cabinet;
		go.LocalPosition = center + faceNormal * ( 2f * s + 0.2f );
		go.LocalRotation = lean * Rotation.FromYaw( 180f );
		// Marquee front face is ~36 wide × 18 tall (× s); WorldPanel intrinsic is
		// ~36 units at scale 1, so ~0.5·s keeps the digit on the slab — tune in editor.
		go.LocalScale = 0.5f * s;
		go.AddComponent<WorldPanel>();
		go.AddComponent<Gambit.UI.MarqueeNumberPanel>().Number = index.ToString();
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
