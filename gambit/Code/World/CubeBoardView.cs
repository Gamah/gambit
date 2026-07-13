using System;
using System.Collections.Generic;
using Gambit.Game;
using Gambit.Theme;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Physical 10×10 cube board in the cabinet's screen slot, replacing the
/// HudPainter board while the local player plays or watches a replay.
/// Always a non-networked GO aligned with the station (never NetworkSpawned):
/// ArcadeStation.Enter() creates the local player's own view (reads the
/// controllers), and every other client creates spectator views driven by a
/// RemoteBoard sim fed from the relayed move stream (Remote != null) — so
/// effects follow each viewer's local explodiness/gravity settings.
///
/// Lifecycle: cubes spawn hidden inside the cabinet's Back slab (behind the
/// opaque attract WorldPanel at x=0) and slide out through the screen plane in
/// a diagonal wave once the seed/grid is applied. While active they mirror the
/// game state every frame — colors, resolve flash, the 90ms rotation arc, and
/// the 2×2 selector cubes. On completion they either explode with real physics
/// or slide back into the cabinet. Each resolved cube also shatters into a
/// 8×8 sheet of mini cubes flung away from the board at match
/// time, leaving the slot empty (visual only — game state untouched). The razor
/// Complete/GameOver overlay is held back until the outro clears
/// (OutroBlocking, checked by GameHud).
///
/// Station-local frame: +X is behind the screen plane, the player looks along
/// +X from -X, viewer-right is -Y, up is +Z. So col 0 (viewer left) is +Y and
/// row 0 (top) is high Z. A clockwise-on-screen rotation is a positive
/// rotation about +X (verified by endpoints: TL offset (+y,+z) lands on TR
/// (-y,+z) at 90° going over the top) — if it spins the wrong way in the
/// editor, flip the sign on `angle` in OrbitPosition.
/// </summary>
public sealed class CubeBoardView : Component
{
	/// <summary>True while a completion outro (explosion / slide-in) is still
	/// playing — GameHud delays the Complete/GameOver overlay until this clears.
	/// Only the local player's own view writes this; remote views never do.</summary>
	public static bool OutroBlocking { get; private set; }

	/// <summary>The station this board sits on (set by the creator); controls
	/// which cabinet's joystick/buttons animate.</summary>
	public ArcadeStation Station { get; set; }

	/// <summary>When set, this view renders another player's board from the
	/// relayed move stream instead of the local controllers.</summary>
	public RemoteBoard Remote { get; set; }

	/// <summary>Uniform multiplier on the whole board geometry (cubes, spacing,
	/// selectors — everything derives from the cabinet scale). 1 = cabinet-sized;
	/// the giant SpectatorBoard over the leaderboard wall uses 5.</summary>
	public float SizeScale { get; set; } = 1f;

	/// <summary>Skip all cabinet-furniture animation (joystick / buttons / glass /
	/// marquee duck). The free-floating SpectatorBoard has no cabinet to drive and
	/// must never grab the engaged player's controls.</summary>
	public bool SuppressCabinet { get; set; }

	/// <summary>Nothing on screen and no effects in flight — safe to destroy.</summary>
	public bool Idle => _phase == Phase.Hidden && _debris.Count == 0 && _shards.Count == 0;

	// Geometry comes from ArcadeRing (BoardSize / CubeSize / CabinetScale),
	// snapshotted in OnStart — fallbacks only apply if the ring is missing.

	// ── Timings ──
	const float SlideDuration  = 0.45f; // per-cube slide out/in
	const float StaggerPerDiag = 0.02f; // extra delay per (row+col) for the wave
	const float DebrisLifetime = 4f;    // explosion cubes despawn after this
	const float DebrisShrink   = 0.5f;  // shrink-out window at end of lifetime
	const float ShardLifetime  = 0.5f;  // match shards fade out and despawn over this
	const float HalfGravity    = 400f;  // shards fall at half the ~800 u/s² world gravity
	const float ExplodeOverlayDelay = 1.5f; // razor overlay returns while debris still flies

	enum Phase { Hidden, SlideOut, Active, SlideIn, Explode }

	Phase _phase = Phase.Hidden;
	TimeSince _phaseTime;
	bool _outroForCompletion; // outro triggered by game end (blocks overlay) vs. backing out

	float _scale, _step, _cubeSize, _halfSpan, _topZ, _hiddenX, _restX;

	GameObject[] _cubes;            // unscaled parents (colliders go here when exploding)
	ModelRenderer[] _cubeVisuals;   // scaled box.vmdl children (tint target)
	int[] _lastColor;               // last non-zero color per cell, for explosion tinting

	GameObject _mySelector;
	ModelRenderer[] _mySelectorBars;
	readonly Dictionary<int, (GameObject Go, ModelRenderer[] Bars)> _oppSelectors = new();

	GameObject _rotateGlyph;
	Gambit.UI.RotateGlyphPanel _rotateGlyphPanel;
	int _glyphRow, _glyphCol, _glyphDir;       // the group the live glyph was fired from
	TimeSince _glyphFade = 999f;               // own fade clock, decoupled from the rotation anim
	RotateAnimRequest _seenGlyphAnim;          // last latched anim (reference compare, like the button anims)
	bool _glyphWarmed;                         // panel given one invisible render during slide-out
	float _squeezeT;                           // 0..1 selector-squeeze clock, advanced on its own (capped) timer
	RotateAnimRequest _seenSqueezeAnim;        // anim the squeeze clock is tracking

	readonly List<(GameObject Go, GameObject Visual, Vector3 BaseScale, TimeSince Spawn)> _debris = new();
	readonly List<(GameObject Go, ModelRenderer Renderer, Color BaseTint, TimeSince Spawn)> _shards = new();

	int[] _prevGrid; // last-seen grid, to spot cells resolving (for "match" explodiness)

	// Block/dir of the most recent rotation anim seen — at the frame the grid
	// changes the anim is already gone (or a queued one started), so this is
	// what distinguishes "hole rotated into this cell" from "cell resolved".
	int _lastAnimRow, _lastAnimCol, _lastAnimDir;
	bool _haveLastAnim;

	Color[] _palette;
	string _cachedScheme;

	Material _material;

	protected override void OnStart()
	{
		ComputeGeometry();

		// Let the relayed sim play its move SFX from the acting selector's world position
		// rather than a fixed board point (geometry is ready now).
		if ( Remote != null )
			Remote.SelectorWorldPos = SelectorWorldPos;

		var matPath = ArcadeRing.Instance?.CubeMaterial;
		if ( !string.IsNullOrWhiteSpace( matPath ) )
		{
			_material = Material.Load( matPath );
			if ( _material == null )
				Log.Warning( $"[Gambit] Cube material '{matPath}' failed to load — using the model's default." );
		}
	}

	protected override void OnDestroy()
	{
		DestroyCubes();
		DestroyDebris();
		RestoreControls();
		if ( Remote == null )
			OutroBlocking = false;
	}

	protected override void OnUpdate()
	{
		UpdateDebris();
		if ( !SuppressCabinet )
		{
			UpdateControls();
			UpdateGlass();
			UpdateMarquee();
		}

		var ctrl = GameController.Instance;
		var mp   = MultiplayerController.Instance;

		bool soloMode, wantBoard, gameEnded;
		if ( Remote != null )
		{
			Remote.Update();
			soloMode  = false;
			wantBoard = !Remote.Cleared && !Remote.Finished;
			gameEnded = Remote.Finished;
		}
		else
		{
			soloMode  = ctrl?.State is GameState.Playing or GameState.Complete && ctrl.Board != null;
			wantBoard = (ctrl?.State == GameState.Playing && ctrl.Board != null) || mp?.State == MpState.Playing;
			gameEnded = ctrl?.State == GameState.Complete || mp?.State == MpState.GameOver;
		}

		switch ( _phase )
		{
			case Phase.Hidden:
				if ( wantBoard )
				{
					SpawnCubes();
					_phase = Phase.SlideOut;
					_phaseTime = 0;
				}
				break;

			case Phase.SlideOut:
			case Phase.Active:
				if ( gameEnded )
				{
					StartOutro( forCompletion: true );
					break;
				}
				if ( !wantBoard )
				{
					// Backed out mid-game — retract quietly, no overlay gating
					StartOutro( forCompletion: false );
					break;
				}
				UpdateBoard( ctrl, mp, soloMode );
				if ( _phase == Phase.SlideOut && _phaseTime > SlideDuration + StaggerPerDiag * (GameBoard.Size * 2 - 2) )
					_phase = Phase.Active;
				break;

			case Phase.SlideIn:
			{
				float total = SlideDuration + StaggerPerDiag * (GameBoard.Size * 2 - 2);
				UpdateSlideIn();
				if ( _phaseTime > total )
				{
					DestroyCubes();
					_phase = Phase.Hidden;
				}
				break;
			}

			case Phase.Explode:
				// Cubes are debris now; just wait out the overlay delay
				if ( _phaseTime > ExplodeOverlayDelay )
					_phase = Phase.Hidden;
				break;
		}

		if ( Remote == null )
		{
			OutroBlocking = _outroForCompletion &&
				(_phase == Phase.SlideIn || (_phase == Phase.Explode && _phaseTime <= ExplodeOverlayDelay));
			if ( !OutroBlocking && _phase == Phase.Hidden )
				_outroForCompletion = false;
		}
	}

	// ── Board state → cube transforms/tints ──

	void UpdateBoard( GameController ctrl, MultiplayerController mp, bool soloMode )
	{
		if ( _cubes == null ) return;

		var palette = GetPalette();
		int[] grid;
		RotateAnimRequest anim;
		float prog;
		bool[] flash; // resolve flash; null in MP (no client-side resolution)
		if ( Remote != null )
		{
			// flash null: remote boards never resolve locally, so resolved cells are
			// detected (and shattered) from the grid diff, exactly like MP.
			grid = Remote.Board.Cells; anim = Remote.PendingAnim; prog = Remote.AnimProgress; flash = null;
		}
		else if ( soloMode )
		{
			grid = ctrl.Board.Cells; anim = ctrl.PendingAnim; prog = ctrl.AnimProgress; flash = ctrl.FlashCells;
		}
		else
		{
			grid = mp.Grid; anim = mp.PendingAnim; prog = mp.AnimProgress; flash = null;
		}

		// "Match" explodiness: cells that just resolved shatter instead of going
		// black. Purely visual — the grid itself is never touched. A 0 rotating
		// into a cell is a moved hole, not a resolve: solo distinguishes via
		// FlashCells (set only on resolve), MP by checking whether the cell's
		// pre-image under the last rotation was already 0.
		// Explodiness is always "match": resolved cells shatter mid-game.
		if ( _prevGrid != null )
		{
			for ( int i = 0; i < grid.Length; i++ )
			{
				if ( _prevGrid[i] != 0 && grid[i] == 0
					&& (flash != null ? flash[i] : !MovedHole( i )) )
					ExplodeCell( i );
			}
		}
		_prevGrid ??= new int[grid.Length];
		for ( int i = 0; i < grid.Length; i++ )
			_prevGrid[i] = grid[i];
		if ( anim != null )
		{
			_lastAnimRow = anim.Row; _lastAnimCol = anim.Col; _lastAnimDir = anim.Dir;
			_haveLastAnim = true;
		}

		bool sliding = _phase == Phase.SlideOut;

		for ( int row = 0; row < GameBoard.Size; row++ )
		{
			for ( int col = 0; col < GameBoard.Size; col++ )
			{
				int idx = row * GameBoard.Size + col;
				var cube = _cubes[idx];
				if ( !cube.IsValid() ) continue;

				int colorId;
				Vector3 pos;

				if ( anim != null && InAnimBlock( anim, row, col ) )
				{
					colorId = anim.PreCells[idx];
					pos = OrbitPosition( anim, prog, row, col );
				}
				else
				{
					colorId = grid[idx];
					pos = CellRest( row, col );
				}

				if ( colorId != 0 )
					_lastColor[idx] = colorId;

				if ( sliding )
					pos.x = SlideX( row, col, outward: true );

				cube.LocalPosition = pos;

				var tint = palette[colorId];
				if ( flash != null && flash[idx] )
					tint = Color.Lerp( tint, Color.White, 0.7f );
				_cubeVisuals[idx].Tint = Dimmed( tint );
				// Match mode renders solved cells as holes (the cube shattered)
				_cubeVisuals[idx].Enabled = colorId != 0;
			}
		}

		UpdateSelectors( ctrl, mp, soloMode, palette, visible: _phase == Phase.Active );

		// One invisible render during the slide-out so the panel's template/stylesheet
		// build happens behind that animation, not on the first rotation.
		if ( _phase == Phase.SlideOut && !_glyphWarmed && _rotateGlyph.IsValid() )
		{
			_rotateGlyph.Enabled = true;
			_rotateGlyph.LocalPosition = new Vector3( _restX - _cubeSize, 0, _topZ - _halfSpan );
			_rotateGlyphPanel.Alpha = 0f;
			_glyphWarmed = true;
		}
	}

	void UpdateSlideIn()
	{
		if ( _cubes == null ) return;
		UpdateSelectorsHidden();
		for ( int row = 0; row < GameBoard.Size; row++ )
		{
			for ( int col = 0; col < GameBoard.Size; col++ )
			{
				var cube = _cubes[row * GameBoard.Size + col];
				if ( !cube.IsValid() ) continue;
				var pos = CellRest( row, col );
				pos.x = SlideX( row, col, outward: false );
				cube.LocalPosition = pos;
			}
		}
	}

	float SlideX( int row, int col, bool outward )
	{
		float delay = (row + col) * StaggerPerDiag;
		float t = Math.Clamp( ((float)_phaseTime - delay) / SlideDuration, 0f, 1f );
		t = EaseOut( t );
		return outward ? MathX.Lerp( _hiddenX, _restX, t ) : MathX.Lerp( _restX, _hiddenX, t );
	}

	// Rest position: cube back edge flush with the screen plane, body fully proud
	Vector3 CellRest( int row, int col ) =>
		new( _restX, _halfSpan - (col + 0.5f) * _step, _topZ - (row + 0.5f) * _step );

	static bool InAnimBlock( RotateAnimRequest anim, int row, int col ) =>
		row >= anim.Row && row <= anim.Row + 1 && col >= anim.Col && col <= anim.Col + 1;

	Vector3 OrbitPosition( RotateAnimRequest anim, float progress, int row, int col )
	{
		// Block center sits on the shared corner of the 2×2
		float cy = _halfSpan - (anim.Col + 1) * _step;
		float cz = _topZ - (anim.Row + 1) * _step;

		float dy = col == anim.Col ? _step * 0.5f : -_step * 0.5f;
		float dz = row == anim.Row ? _step * 0.5f : -_step * 0.5f;

		// CW on screen = positive rotation about +X (see class comment)
		float angle = (anim.Dir == 0 ? 90f : -90f) * EaseRotate( progress ) * (MathF.PI / 180f);
		float cos = MathF.Cos( angle );
		float sin = MathF.Sin( angle );

		return new Vector3( _restX, cy + dy * cos - dz * sin, cz + dy * sin + dz * cos );
	}

	static float EaseOut( float t ) => 1f - MathF.Pow( 1f - t, 3f );

	/// <summary>ArcadeRing.CubeBrightness — scales the board cube tints.</summary>
	static Color Dimmed( Color c )
	{
		float b = ArcadeRing.Instance?.CubeBrightness ?? 1f;
		return new Color( c.r * b, c.g * b, c.b * b, c.a );
	}

	// Rotations use ease-in-out: the front-loaded ease-out read as too fast
	static float EaseRotate( float t ) => t * t * (3f - 2f * t);

	// ── Selector rings ──
	// A square-edged ring framing the 2×2: four bars whose thickness is exactly
	// the gap between cubes (step - cubeSize, set by the BoardSize/CubeSize knobs),
	// centered on the gap channels surrounding the block. Front face sits slightly
	// behind the cubes' near surface — mine closest, opponents a bit deeper (they
	// may overlap each other).

	const float SelectorSlideSpeed = 18f;  // exponential slide-between-cells rate
	const float SelectorSqueeze    = 0.667f; // peak inward shrink at 45° (mid-spin) → 1/3 of full radius
	const float SelectorSqueezeDur = 0.14f;  // squeeze runs over the rotation (≈ controllers' AnimDuration)
	const float SqueezeMaxStep     = 0.02f;  // cap per-frame clock advance so a frame hitch can't snap the scale

	void UpdateSelectors( GameController ctrl, MultiplayerController mp, bool soloMode, Color[] palette, bool visible )
	{
		if ( !visible )
		{
			UpdateSelectorsHidden();
			return;
		}

		float cubeFront = _restX - _cubeSize * 0.5f;
		float selDepth  = _cubeSize * 0.9f;
		// Ring front face stands proud of the cube near surface (outset) by the same
		// ratio it used to sit recessed (inset): front = cubeFront - ratio*cubeSize.
		float myX  = cubeFront - _cubeSize * 0.12f + selDepth * 0.5f;
		float oppX = cubeFront - _cubeSize * 0.35f + selDepth * 0.5f;

		if ( Remote != null )
		{
			// Mode-agnostic: render every relayed selector. Color 0 = a white "mine"
			// ring (solo); nonzero colors are MP players' rings in their palette color.
			// The first entry is the occupant's own (drawn nearest, at myX).
			var rAnim   = Remote.PendingAnim;
			float rProg = Remote.AnimProgress;
			var sels = Remote.Selectors;
			// Replays have no local occupant (NoOwnSelector): draw every selector as a
			// color-keyed peer ring so each player stays stable. Otherwise entry 0 is the
			// occupant's own white "mine" ring.
			bool noOwn = Remote.NoOwnSelector;
			var rSeen = new HashSet<int>();
			if ( sels != null )
			{
				for ( int i = 0; i < sels.Count; i++ )
				{
					var s = sels[i];
					var c = s.Color == 0 || s.Color >= palette.Length ? Color.White : palette[s.Color];
					if ( i == 0 && !noOwn )
					{
						PlaceSelector( ref _mySelector, ref _mySelectorBars, "Selector",
							s.Row, s.Col, myX, selDepth, c, rAnim, rProg, mineRing: true, spinAnyMine: true );
					}
					else
					{
						rSeen.Add( s.Color );
						_oppSelectors.TryGetValue( s.Color, out var entry );
						var go = entry.Go; var bars = entry.Bars;
						PlaceSelector( ref go, ref bars, $"Selector {s.Color}", s.Row, s.Col,
							oppX, selDepth, c, rAnim, rProg, mineRing: false, spinAnyMine: true );
						_oppSelectors[s.Color] = (go, bars);
					}
				}
			}
			if ( noOwn && _mySelector.IsValid() ) _mySelector.Enabled = false;
			foreach ( var (color, sel) in _oppSelectors )
				if ( !rSeen.Contains( color ) && sel.Go.IsValid() ) sel.Go.Enabled = false;
			// Glyph pins to the group of the occupant's rotation (also fine for replays).
			UpdateRotateGlyph( rAnim );
			return;
		}

		var anim   = soloMode ? ctrl.PendingAnim : mp.PendingAnim;
		float prog = soloMode ? ctrl.AnimProgress : mp.AnimProgress;

		if ( soloMode )
		{
			PlaceSelector( ref _mySelector, ref _mySelectorBars, "Selector",
				ctrl.SelectorRow, ctrl.SelectorCol, myX, selDepth, Color.White, anim, prog, mineRing: true );
			UpdateRotateGlyph( anim );
			foreach ( var (_, sel) in _oppSelectors )
				if ( sel.Go.IsValid() ) sel.Go.Enabled = false;
			return;
		}

		var myColor = mp.MyColor >= 0 && mp.MyColor < palette.Length ? palette[mp.MyColor] : Color.White;
		PlaceSelector( ref _mySelector, ref _mySelectorBars, "Selector",
			mp.SelectorRow, mp.SelectorCol, myX, selDepth, myColor, anim, prog, mineRing: true );
		// Only own rotations (anim.Mine) drive the glyph; opponents' moves don't.
		UpdateRotateGlyph( anim != null && anim.Mine ? anim : null );

		var seen = new HashSet<int>();
		if ( mp.Selectors != null )
		{
			foreach ( var sel in mp.Selectors )
			{
				if ( sel.Color == mp.MyColor ) continue;
				seen.Add( sel.Color );
				_oppSelectors.TryGetValue( sel.Color, out var entry );
				var go = entry.Go; var bars = entry.Bars;
				var c = sel.Color >= 0 && sel.Color < palette.Length ? palette[sel.Color] : Color.White;
				PlaceSelector( ref go, ref bars, $"Selector {sel.Color}", sel.Row, sel.Col, oppX, selDepth, c, anim, prog, mineRing: false );
				_oppSelectors[sel.Color] = (go, bars);
			}
		}
		foreach ( var (color, sel) in _oppSelectors )
		{
			if ( !seen.Contains( color ) && sel.Go.IsValid() )
				sel.Go.Enabled = false;
		}
	}

	void UpdateSelectorsHidden()
	{
		if ( _mySelector.IsValid() ) _mySelector.Enabled = false;
		foreach ( var (_, sel) in _oppSelectors )
			if ( sel.Go.IsValid() ) sel.Go.Enabled = false;
		HideRotateGlyph();
	}

	// ── Rotate glyph pop-up ──
	// A ↻/↺ arrow that pops up centered on the group a rotation was fired from,
	// raised just past the cube front faces, and fades out over its own clock
	// (longer than the rotation anim). It stays pinned to the fired group — it
	// does not follow the selector or spin with the cubes.

	const float GlyphPanelUnits = 25f;  // tunes glyph world size (smaller = bigger arrow); calibrated to the 360px font
	const float GlyphFadeTime   = 0.5f; // pop-and-fade duration, decoupled from the 90ms rotation
	const float GlyphStandoff   = 0.6f; // glyph stood off the cube faces by this × cube depth (front = -X)

	void UpdateRotateGlyph( RotateAnimRequest anim )
	{
		// Latch on a freshly-fired rotation (new anim object) and restart the fade.
		if ( anim != null && anim != _seenGlyphAnim )
		{
			_seenGlyphAnim = anim;
			_glyphRow = anim.Row; _glyphCol = anim.Col; _glyphDir = anim.Dir;
			_glyphFade = 0f;
		}

		if ( _glyphFade >= GlyphFadeTime )
		{
			HideRotateGlyph();
			return;
		}

		EnsureRotateGlyph();
		_rotateGlyph.Enabled = true;
		// ~0.65 cell wide (recomputed in case the cube-size setting changed).
		_rotateGlyph.LocalScale = (0.65f * _step) / GlyphPanelUnits;

		// Centered on the fired group, stood off along the board-local front axis
		// (-X, toward the viewer). Camera-independent so the spectator board draws
		// it the same way. Offset scales with cube depth to clear the faces.
		float cubeFront = _restX - _cubeSize * 0.5f;
		float glyphX = cubeFront - _cubeSize * GlyphStandoff;
		_rotateGlyph.LocalPosition = new Vector3( glyphX,
			_halfSpan - (_glyphCol + 1) * _step, _topZ - (_glyphRow + 1) * _step );

		float t = Math.Clamp( (float)_glyphFade / GlyphFadeTime, 0f, 1f );
		// Faces the player via yaw 180 (flip to 0 in the editor if it reads mirrored),
		// and rolls a quarter turn in place over the fade — CW = +X, matching the cubes.
		float angle = (_glyphDir == 0 ? 90f : -90f) * EaseRotate( t );
		_rotateGlyph.LocalRotation = Rotation.FromAxis( new Vector3( 1, 0, 0 ), angle ) * Rotation.FromYaw( 180f );

		_rotateGlyphPanel.Glyph = _glyphDir == 0 ? "↻" : "↺";
		// Hold strong early, then ease out (less rushed than a linear fade).
		_rotateGlyphPanel.Alpha = 1f - t * t;
	}

	void HideRotateGlyph()
	{
		if ( _rotateGlyph.IsValid() ) _rotateGlyph.Enabled = false;
	}

	// Built once at board spawn (hidden) rather than lazily on the first rotation:
	// instantiating the WorldPanel + Razor panel on the first fire stalled that
	// frame, which inflated Time.Delta and made the 90ms rotation arc jump ahead.
	// Warming it during the slide-out hides the cost behind that animation.
	void EnsureRotateGlyph()
	{
		if ( _rotateGlyph.IsValid() ) return;
		_rotateGlyph = new GameObject( true, "RotateGlyph" );
		_rotateGlyph.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_rotateGlyph.Parent = GameObject;
		_rotateGlyph.AddComponent<WorldPanel>();
		_rotateGlyphPanel = _rotateGlyph.AddComponent<Gambit.UI.RotateGlyphPanel>();
		_rotateGlyph.Enabled = false;
	}

	void PlaceSelector( ref GameObject go, ref ModelRenderer[] bars, string name,
		int row, int col, float x, float depth, Color color, RotateAnimRequest anim, float prog,
		bool mineRing, bool spinAnyMine = false )
	{
		bool fresh = !go.IsValid();
		if ( fresh )
			(go, bars) = MakeSelectorRing( name, depth );

		bool wasHidden = fresh || !go.Enabled;
		go.Enabled = true;

		// Ring center = the block's shared corner; bars sit at ±step around it
		var target = new Vector3( x, _halfSpan - (col + 1) * _step, _topZ - (row + 1) * _step );
		// Slide between cells; snap when (re)appearing so it doesn't sweep in from stale spots
		go.LocalPosition = wasHidden ? target
			: Vector3.Lerp( go.LocalPosition, target, Math.Clamp( Time.Delta * SelectorSlideSpeed, 0f, 1f ) );

		// Ride the rotation arc with the cubes when this ring frames the
		// animating block (own anims spin own rings, opponent anims theirs)
		// Spin the ring that frames the rotating block. Remote views (spinAnyMine) can't
		// know whose move it was, so they spin whichever selector sits on the block.
		float angle = 0f;
		float squeeze = 1f;
		if ( anim != null && anim.Row == row && anim.Col == col && (spinAnyMine || anim.Mine == mineRing) )
		{
			angle = (anim.Dir == 0 ? 90f : -90f) * EaseRotate( prog );
			// Squeeze inward on the rotating cubes mid-spin, then ease back out. Driven
			// by its own clock (reset when a new rotation latches) advanced by real time
			// but capped per frame, so a frame hitch can't snap it — it lerps over the
			// whole rotation and hits 1/3 radius at the 45° midpoint.
			if ( anim != _seenSqueezeAnim )
			{
				_seenSqueezeAnim = anim;
				_squeezeT = 0f;
			}
			_squeezeT = Math.Clamp( _squeezeT + Math.Min( Time.Delta, SqueezeMaxStep ) / SelectorSqueezeDur, 0f, 1f );
			squeeze = 1f - SelectorSqueeze * MathF.Sin( _squeezeT * MathF.PI );
		}
		go.LocalRotation = Rotation.FromAxis( new Vector3( 1, 0, 0 ), angle );
		// Scale only the in-plane radius (Y/Z), not depth (X) — a uniform scale would
		// shrink the bar depth toward the ring centre and pull the front face back in,
		// making the outset ring read as inset mid-squeeze.
		go.LocalScale = new Vector3( 1f, squeeze, squeeze );

		foreach ( var bar in bars )
			bar.Tint = color;
	}

	/// <summary>World position of a 2×2 selector top-left, matching the ring center in
	/// <see cref="PlaceSelector"/>. Wired into RemoteBoard so spectator/replay move SFX
	/// emanate from the acting selector. Depth (x) uses the rest plane; only pan/distance
	/// nuance matters for audio.</summary>
	public Vector3 SelectorWorldPos( int row, int col )
	{
		var local = new Vector3( _restX, _halfSpan - (col + 1) * _step, _topZ - (row + 1) * _step );
		return GameObject.WorldTransform.PointToWorld( local );
	}

	(GameObject, ModelRenderer[]) MakeSelectorRing( string name, float depth )
	{
		float gap   = _step - _cubeSize;
		float outer = 2f * _step + gap;  // vertical bars span the full frame height
		float inner = 2f * _step - gap;  // horizontal bars butt against them

		var root = new GameObject( true, name );
		root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		root.Parent = GameObject;
		root.LocalRotation = Rotation.Identity;

		var bars = new ModelRenderer[4];
		bars[0] = AddVisual( root, new Vector3( 0,  _step, 0 ), new Vector3( depth, gap, outer ) );
		bars[1] = AddVisual( root, new Vector3( 0, -_step, 0 ), new Vector3( depth, gap, outer ) );
		bars[2] = AddVisual( root, new Vector3( 0, 0,  _step ), new Vector3( depth, inner, gap ) );
		bars[3] = AddVisual( root, new Vector3( 0, 0, -_step ), new Vector3( depth, inner, gap ) );
		return (root, bars);
	}

	// ── Spawn / teardown ──

	// Recomputed on every spawn (not just OnStart) so the Settings cube-size slider
	// applies to the next board slide-out — including remote spectator boards, which
	// share this component.
	void ComputeGeometry()
	{
		var ring = ArcadeRing.Instance;
		_scale    = (ring?.CabinetScale ?? 1.5f) * SizeScale;
		float span = (ring?.BoardSize ?? 28f) * _scale;
		_step     = span / GameBoard.Size;
		_cubeSize = _step * (ring?.CubeSize ?? 0.78f)
			* PlayerData.ClampScale( PlayerData.Load()?.CubeSizeScale ?? 1f );
		_halfSpan = span * 0.5f;
		_topZ     = (ring?.ScreenHeight ?? 70f) + _halfSpan;
		// Fully inside the Back slab (x 1..13 × scale), hidden behind the opaque
		// attract panel at x=0 until the slide-out
		_hiddenX  = 1f * _scale + _cubeSize;
		// At rest the cube sits mostly behind the screen plane; only CubeProtrusion
		// fraction of a step protrudes toward the player (front face at -protrusion).
		float protrusion = _step * (ring?.CubeProtrusion ?? 0.08f);
		_restX    = _cubeSize * 0.5f - protrusion;
	}

	void SpawnCubes()
	{
		ComputeGeometry();
		DestroyCubes();
		_prevGrid = null;
		_haveLastAnim = false;
		_cubes = new GameObject[GameBoard.CellCount];
		_cubeVisuals = new ModelRenderer[GameBoard.CellCount];
		_lastColor = new int[GameBoard.CellCount];

		// Warm the rotate glyph now so the first move doesn't hitch the rotation arc.
		_glyphWarmed = false;
		EnsureRotateGlyph();

		for ( int row = 0; row < GameBoard.Size; row++ )
		{
			for ( int col = 0; col < GameBoard.Size; col++ )
			{
				int idx = row * GameBoard.Size + col;
				(_cubes[idx], _cubeVisuals[idx]) = MakeBox( $"Cube {row},{col}", _cubeSize );
				var pos = CellRest( row, col );
				pos.x = _hiddenX;
				_cubes[idx].LocalPosition = pos;
			}
		}
	}

	/// <summary>Unscaled parent + scaled box.vmdl child, so a BoxCollider can be
	/// added to the parent later without the non-uniform-scale physics freeze.</summary>
	(GameObject, ModelRenderer) MakeBox( string name, Vector3 size )
	{
		var go = new GameObject( true, name );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = GameObject;
		go.LocalRotation = Rotation.Identity;

		return (go, AddVisual( go, Vector3.Zero, size ));
	}

	/// <summary>Scaled box.vmdl child renderer under an unscaled parent.</summary>
	ModelRenderer AddVisual( GameObject parent, Vector3 localPos, Vector3 size )
	{
		var model = Model.Load( "models/dev/box.vmdl" );

		var visual = new GameObject( true, "Visual" );
		visual.Parent = parent;
		visual.LocalPosition = localPos;
		var b = model.Bounds.Size;
		visual.LocalScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );

		var renderer = visual.AddComponent<ModelRenderer>();
		renderer.Model = model;
		if ( _material != null )
			renderer.MaterialOverride = _material;
		return renderer;
	}

	void DestroyCubes()
	{
		if ( _cubes != null )
		{
			foreach ( var go in _cubes )
				if ( go.IsValid() ) go.Destroy();
			_cubes = null;
			_cubeVisuals = null;
		}
		if ( _mySelector.IsValid() ) _mySelector.Destroy();
		_mySelector = null;
		_mySelectorBars = null;
		foreach ( var (_, sel) in _oppSelectors )
			if ( sel.Go.IsValid() ) sel.Go.Destroy();
		_oppSelectors.Clear();
		if ( _rotateGlyph.IsValid() ) _rotateGlyph.Destroy();
		_rotateGlyph = null;
		_rotateGlyphPanel = null;
		_seenGlyphAnim = null;
		_glyphFade = 999f;
	}

	// ── Outro ──

	void StartOutro( bool forCompletion )
	{
		_outroForCompletion = forCompletion;
		_phaseTime = 0;

		if ( forCompletion )
		{
			StartExplode();
			_phase = Phase.Explode;
		}
		else
		{
			_phase = Phase.SlideIn;
		}
	}

	/// <summary>Turn every cube into a physics rigidbody flung off the cabinet.
	/// Cubes get the "boardcube" tag, which the collision rules set to Ignore
	/// against "cabinet" — they spawn overlapping the cabinet's collider, so
	/// without that they'd depenetrate violently instead of flying. (The tag
	/// matrix is global, so they ignore every cabinet, not just their own; the
	/// outward velocity bias keeps that from being visible.)</summary>
	void StartExplode()
	{
		if ( _cubes == null ) return;

		UpdateSelectorsHidden();
		var palette = GetPalette();
		var outward = -WorldRotation.Forward; // toward the player, away from the cabinet
		var rand = Random.Shared;
		Vector3 Jitter() => new( rand.Float( -1f, 1f ), rand.Float( -1f, 1f ), rand.Float( -1f, 1f ) );

		for ( int idx = 0; idx < _cubes.Length; idx++ )
		{
			var go = _cubes[idx];
			if ( !go.IsValid() ) continue;

			// Match-mode holes already shattered — nothing left to fling
			if ( _cubeVisuals[idx].IsValid() && !_cubeVisuals[idx].Enabled )
			{
				go.Destroy();
				continue;
			}

			go.SetParent( null, true ); // keep world position
			go.Tags.Add( "boardcube" );

			// Solved cells are all dark by the end — explode in their last live color
			int color = _lastColor[idx];
			if ( color > 0 && color < palette.Length )
				_cubeVisuals[idx].Tint = Dimmed( palette[color] );

			go.AddComponent<BoxCollider>().Scale = _cubeSize;

			var body = go.AddComponent<Rigidbody>();
			body.Gravity = true;
			body.Velocity = outward * rand.Float( 120f, 280f )
				+ Jitter() * rand.Float( 30f, 90f )
				+ Vector3.Up * rand.Float( 60f, 180f );
			body.AngularVelocity = Jitter() * rand.Float( 2f, 9f );

			var visual = _cubeVisuals[idx].GameObject;
			_debris.Add( (go, visual, visual.LocalScale, 0f) );
		}

		_cubes = null;
		_cubeVisuals = null;
		if ( _mySelector.IsValid() ) _mySelector.Destroy();
		_mySelector = null;
		_mySelectorBars = null;
		foreach ( var (_, sel) in _oppSelectors )
			if ( sel.Go.IsValid() ) sel.Go.Destroy();
		_oppSelectors.Clear();
		if ( _rotateGlyph.IsValid() ) _rotateGlyph.Destroy();
		_rotateGlyph = null;
		_rotateGlyphPanel = null;
		_seenGlyphAnim = null;
		_glyphFade = 999f;
	}

	/// <summary>"Match" explodiness: a just-resolved cell shatters into an 8×8 sheet
	/// of mini cubes flung outward (away from the board) that fade out over
	/// ShardLifetime. The board cube
	/// itself survives (hidden while its cell is 0) so holes can rotate around
	/// the board. Visual only — game state untouched.</summary>
	void ExplodeCell( int idx )
	{
		var go = _cubes?[idx];
		if ( !go.IsValid() ) return;

		var palette = GetPalette();

		var center = go.WorldPosition;
		var rot = WorldRotation;
		var outward = -rot.Forward;
		var rand = Random.Shared;
		Vector3 Jitter() => new( rand.Float( -1f, 1f ), rand.Float( -1f, 1f ), rand.Float( -1f, 1f ) );
		// Confetti: each shard picks a random active color (1–4) regardless of source color
		Color RandColor() => Dimmed( palette[rand.Int( 1, Math.Min( 4, palette.Length - 1 ) )] );

		const int Div = 8; // flat 8×8 sheet of mini cubes on the cube face
		float mini = _cubeSize / Div;
		float off = (Div - 1) / 2f; // center the sheet: indices 0..Div-1 → -off..off
		for ( int y = 0; y < Div; y++ )
		{
			for ( int z = 0; z < Div; z++ )
			{
				var shardTint = RandColor();
				var piece = new GameObject( true, "CubeShard" );
				piece.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
				piece.WorldPosition = center + rot * new Vector3( 0, (y - off) * mini, (z - off) * mini );
				piece.WorldRotation = rot;
				piece.Tags.Add( "boardcube" );

				var visual = AddVisual( piece, Vector3.Zero, mini );
				visual.Tint = shardTint;

				piece.AddComponent<BoxCollider>().Scale = mini;
				var body = piece.AddComponent<Rigidbody>();
				// Half gravity for shards (applied manually in UpdateDebris — the
				// engine Gravity bool can't be scaled).
				body.Gravity = false;
				// Fan each shard out from the cell center (in the face plane) so the
				// sheet spreads apart, plus heavy random scatter and a modest outward
				// push — no longer a tight slab moving as one. Velocity halved.
				// Scaled by SizeScale so the spectator board (SizeScale≈18) flings
				// shards as energetically as the cabinet (SizeScale=1).
				var fan = rot * new Vector3( 0, y - off, z - off ).Normal;
				body.Velocity = (outward * rand.Float( 15f, 45f )
					+ fan * rand.Float( 30f, 80f )
					+ Jitter() * rand.Float( 25f, 65f )) * SizeScale;
				body.AngularVelocity = Jitter() * rand.Float( 4f, 16f );

				_shards.Add( (piece, visual, shardTint, 0f) );
			}
		}
	}

	/// <summary>True if cell idx went to 0 because the last rotation moved an
	/// existing hole into it (its pre-image in _prevGrid was already 0), as
	/// opposed to a genuine resolve.</summary>
	bool MovedHole( int idx )
	{
		if ( !_haveLastAnim ) return false;
		int r = _lastAnimRow, c = _lastAnimCol;
		int row = idx / GameBoard.Size, col = idx % GameBoard.Size;
		if ( row < r || row > r + 1 || col < c || col > c + 1 ) return false;

		bool top = row == r, left = col == c;
		int sr, sc;
		if ( _lastAnimDir == 0 ) // CW: newTL=BL, newTR=TL, newBR=TR, newBL=BR
		{
			if ( top && left )  { sr = r + 1; sc = c; }
			else if ( top )     { sr = r;     sc = c; }
			else if ( !left )   { sr = r;     sc = c + 1; }
			else                { sr = r + 1; sc = c + 1; }
		}
		else // CCW: newTL=TR, newTR=BR, newBR=BL, newBL=TL
		{
			if ( top && left )  { sr = r;     sc = c + 1; }
			else if ( top )     { sr = r + 1; sc = c + 1; }
			else if ( !left )   { sr = r + 1; sc = c; }
			else                { sr = r;     sc = c; }
		}
		return _prevGrid[sr * GameBoard.Size + sc] == 0;
	}

	void UpdateDebris()
	{
		for ( int i = _shards.Count - 1; i >= 0; i-- )
		{
			var s = _shards[i];
			if ( s.Spawn > ShardLifetime )
			{
				if ( s.Go.IsValid() ) s.Go.Destroy();
				_shards.RemoveAt( i );
				continue;
			}
			// Manual half gravity (engine gravity is off on shards), scaled by
			// SizeScale so the big spectator board falls at the same visual rate.
			if ( s.Go.IsValid() && s.Go.Components.Get<Rigidbody>() is { } rb )
				rb.Velocity += Vector3.Down * (HalfGravity * SizeScale * Time.Delta);
			if ( s.Renderer.IsValid() )
				s.Renderer.Tint = s.BaseTint.WithAlpha( 1f - (float)s.Spawn / ShardLifetime );
		}

		for ( int i = _debris.Count - 1; i >= 0; i-- )
		{
			var d = _debris[i];
			if ( d.Spawn > DebrisLifetime )
			{
				if ( d.Go.IsValid() ) d.Go.Destroy();
				_debris.RemoveAt( i );
				continue;
			}

			float shrinkT = ((float)d.Spawn - (DebrisLifetime - DebrisShrink)) / DebrisShrink;
			if ( shrinkT > 0f && d.Visual.IsValid() )
				d.Visual.LocalScale = d.BaseScale * Math.Clamp( 1f - shrinkT, 0f, 1f );
		}
	}

	void DestroyDebris()
	{
		foreach ( var d in _debris )
			if ( d.Go.IsValid() ) d.Go.Destroy();
		_debris.Clear();
		foreach ( var s in _shards )
			if ( s.Go.IsValid() ) s.Go.Destroy();
		_shards.Clear();
	}

	// ── Cabinet controls (joystick tilt / button press) ──
	// Animates the station's own JoyShaft/JoyBall/Button boxes (built by
	// ArcadeRing.BuildCabinet) in place: joystick tilts toward each local
	// selector move, the matching rotate button sinks into the deck on each of
	// the local player's rotations (replay moves count as local; MP opponent
	// anims are skipped via RotateAnimRequest.Mine). Purely cosmetic local
	// transform changes — the host never animates these, so nothing fights the
	// network sync.

	const float JoyTiltAngle    = 20f;   // degrees
	const float JoyTiltTime     = 0.22f; // out-and-back envelope
	const float ButtonPressTime = 0.16f;

	GameObject _joyShaft, _joyBall, _btnCw, _btnCcw, _glyphCw, _glyphCcw;
	Vector3 _joyShaftRest, _joyBallRest, _btnCwRest, _btnCcwRest, _glyphCwRest, _glyphCcwRest;
	bool _controlsFound;

	// Screen glass slab (ArcadeRing.BuildCabinet) — slides into the Back slab
	// while a cube board is out so the cubes never intersect it
	const float GlassRecess    = 11f; // base units — fully inside the Back slab (x 1..13)
	const float GlassSlideRate = 8f;  // exponential ease toward the target
	GameObject _glass;
	Vector3 _glassRest;
	bool _glassFound;

	// Marquee spot (ArcadeRing.BuildCabinet) — ducked to MarqueeDuck while a cube
	// board is out so the cone doesn't paint a hotspot/self-shadows across it (#48).
	// MarqueeGlow owns the light color (user tint/brightness, #49); this view only
	// drives its Duck factor.
	const float LightDuckRate = 8f; // exponential ease toward the target
	MarqueeGlow _marquee;
	bool _marqueeFound;

	Vector3 _joyDir;
	TimeSince _joyAnim = 999f, _cwAnim = 999f, _ccwAnim = 999f;
	int _prevSelRow = -1, _prevSelCol = -1;
	RotateAnimRequest _seenAnim;

	void FindControls()
	{
		var station = Station ?? ArcadeStation.Active;
		if ( station == null ) return;

		static GameObject Find( GameObject root, string name )
		{
			foreach ( var child in root.Children )
			{
				if ( child.Name == name ) return child;
				var hit = Find( child, name );
				if ( hit != null ) return hit;
			}
			return null;
		}

		var root = station.GameObject;
		if ( !_glassFound )
		{
			_glass = Find( root, "ScreenGlass" );
			if ( _glass.IsValid() )
			{
				_glassRest = _glass.LocalPosition;
				_glassFound = true;
			}
		}
		if ( !_marqueeFound )
		{
			var lightGo = Find( root, "MarqueeLight" );
			_marquee = lightGo.IsValid() ? lightGo.GetComponent<MarqueeGlow>() : null;
			_marqueeFound = _marquee.IsValid();
		}
		_joyShaft = Find( root, "JoyShaft" );
		_joyBall  = Find( root, "JoyBall" );
		_btnCw    = Find( root, "ButtonCW" );
		_btnCcw   = Find( root, "ButtonCCW" );
		_glyphCw  = Find( root, "Glyph ↻" );
		_glyphCcw = Find( root, "Glyph ↺" );
		if ( !_joyShaft.IsValid() || !_joyBall.IsValid() || !_btnCw.IsValid() || !_btnCcw.IsValid() )
			return;

		_joyShaftRest = _joyShaft.LocalPosition;
		_joyBallRest  = _joyBall.LocalPosition;
		_btnCwRest    = _btnCw.LocalPosition;
		_btnCcwRest   = _btnCcw.LocalPosition;
		if ( _glyphCw.IsValid() )  _glyphCwRest  = _glyphCw.LocalPosition;
		if ( _glyphCcw.IsValid() ) _glyphCcwRest = _glyphCcw.LocalPosition;
		_controlsFound = true;
	}

	void UpdateControls()
	{
		if ( !_controlsFound )
		{
			FindControls();
			if ( !_controlsFound ) return;
		}

		// Only this board's own signals: solo SelectorRow/Col covers play and
		// replay; MP SelectorRow/Col is the own cursor (opponents live in
		// Selectors); remote views animate their occupant's cabinet
		var ctrl = GameController.Instance;
		var mp   = MultiplayerController.Instance;
		int row = -1, col = -1;
		RotateAnimRequest anim = null;
		if ( Remote != null )
		{
			if ( !Remote.Cleared && !Remote.Finished )
			{
				row = Remote.SelectorRow; col = Remote.SelectorCol; anim = Remote.PendingAnim;
			}
		}
		else if ( ctrl?.State == GameState.Playing )
		{
			row = ctrl.SelectorRow; col = ctrl.SelectorCol; anim = ctrl.PendingAnim;
		}
		else if ( mp?.State == MpState.Playing )
		{
			row = mp.SelectorRow; col = mp.SelectorCol; anim = mp.PendingAnim;
		}

		if ( row < 0 )
		{
			_prevSelRow = -1; _prevSelCol = -1;
		}
		else
		{
			if ( _prevSelRow >= 0 && (row != _prevSelRow || col != _prevSelCol) )
			{
				// Station frame: screen-up = push away from the player (+X),
				// player's left (col 0) = +Y
				_joyDir = new Vector3( -Math.Sign( row - _prevSelRow ), -Math.Sign( col - _prevSelCol ), 0 );
				_joyAnim = 0;
			}
			_prevSelRow = row; _prevSelCol = col;

			if ( anim != null && anim != _seenAnim )
			{
				_seenAnim = anim;
				if ( anim.Mine )
				{
					if ( anim.Dir == 0 ) _cwAnim = 0;
					else _ccwAnim = 0;
				}
			}
		}

		ApplyJoystick();
		ApplyButton( _btnCw, _btnCwRest, _glyphCw, _glyphCwRest, _cwAnim );
		ApplyButton( _btnCcw, _btnCcwRest, _glyphCcw, _glyphCcwRest, _ccwAnim );
	}

	void ApplyJoystick()
	{
		var rot = Rotation.Identity;
		float t = (float)_joyAnim / JoyTiltTime;
		if ( t < 1f && _joyDir.LengthSquared > 0f )
		{
			float k = MathF.Sin( Math.Clamp( t, 0f, 1f ) * MathF.PI ); // out and back
			rot = Rotation.FromAxis( Vector3.Cross( Vector3.Up, _joyDir.Normal ), JoyTiltAngle * k );
		}

		// Pivot on the deck surface (z=28 in cabinet base units) under the shaft
		var pivot = new Vector3( _joyShaftRest.x, _joyShaftRest.y, 28f * _scale );
		if ( _joyShaft.IsValid() )
		{
			_joyShaft.LocalPosition = pivot + rot * (_joyShaftRest - pivot);
			_joyShaft.LocalRotation = rot;
		}
		if ( _joyBall.IsValid() )
		{
			_joyBall.LocalPosition = pivot + rot * (_joyBallRest - pivot);
			_joyBall.LocalRotation = rot;
		}
	}

	void ApplyButton( GameObject btn, Vector3 rest, GameObject glyph, Vector3 glyphRest, TimeSince since )
	{
		float t = (float)since / ButtonPressTime;
		float k = t < 1f ? MathF.Sin( Math.Clamp( t, 0f, 1f ) * MathF.PI ) : 0f;
		var offset = Vector3.Down * (1.2f * _scale * k); // sink into the deck, just shy of the 1.6 button height
		if ( btn.IsValid() ) btn.LocalPosition = rest + offset;
		if ( glyph.IsValid() ) glyph.LocalPosition = glyphRest + offset;
	}

	/// <summary>Ease the station's glass slab back into the cabinet while any board
	/// phase is live (slide/active/explode), and home again once the slot is empty.</summary>
	void UpdateGlass()
	{
		if ( !_glassFound || !_glass.IsValid() ) return;

		float targetX = _glassRest.x + (_phase == Phase.Hidden ? 0f : GlassRecess * _scale);
		var pos = _glass.LocalPosition;
		pos.x = MathX.Lerp( pos.x, targetX, Math.Clamp( Time.Delta * GlassSlideRate, 0f, 1f ) );
		_glass.LocalPosition = pos;
	}

	/// <summary>Ease the station's marquee spot down to MarqueeDuck while any board
	/// phase is live, and back to full brightness once the slot is empty (#48).</summary>
	void UpdateMarquee()
	{
		if ( !_marqueeFound || !_marquee.IsValid() ) return;

		float target = _phase == Phase.Hidden ? 1f : ArcadeRing.Instance?.MarqueeDuck ?? 0.3f;
		_marquee.Duck = MathX.Lerp( _marquee.Duck, target, Math.Clamp( Time.Delta * LightDuckRate, 0f, 1f ) );
	}

	void RestoreControls()
	{
		if ( _glassFound && _glass.IsValid() )
			_glass.LocalPosition = _glassRest;
		if ( _marqueeFound && _marquee.IsValid() )
			_marquee.Duck = 1f;
		if ( !_controlsFound ) return;
		if ( _joyShaft.IsValid() ) { _joyShaft.LocalPosition = _joyShaftRest; _joyShaft.LocalRotation = Rotation.Identity; }
		if ( _joyBall.IsValid() )  { _joyBall.LocalPosition = _joyBallRest;   _joyBall.LocalRotation = Rotation.Identity; }
		if ( _btnCw.IsValid() )    _btnCw.LocalPosition = _btnCwRest;
		if ( _btnCcw.IsValid() )   _btnCcw.LocalPosition = _btnCcwRest;
		if ( _glyphCw.IsValid() )  _glyphCw.LocalPosition = _glyphCwRest;
		if ( _glyphCcw.IsValid() ) _glyphCcw.LocalPosition = _glyphCcwRest;
	}

	Color[] GetPalette()
	{
		var scheme = PlayerData.Load()?.ColorScheme ?? "normal";
		if ( scheme != _cachedScheme )
		{
			_cachedScheme = scheme;
			_palette = Colors.GetPalette( scheme );
		}
		return _palette ??= Colors.Normal;
	}
}
