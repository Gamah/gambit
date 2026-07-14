using System.Collections.Generic;
using Sandbox;
using Gambit.Game;

namespace Gambit.World;

/// <summary>
/// Decorative floor art: a procedural N×N black/white checkerboard rendered by the
/// <c>shaders/floor_checker.shader</c> material on a single flat slab. Some cells
/// "pop" to the player's active colour-blind palette — <see cref="PopsPerColor"/>
/// cells per palette colour — switching instantly to new cells every
/// <see cref="PopInterval"/> seconds.
///
/// The checker colours are baked into a tiny BoardDim×BoardDim texture here and
/// point-sampled by the shader (one texel per cell), so the pop count is unbounded.
/// A second BoardDim×BoardDim map carries a per-cell <b>glyph index</b> (0 = none,
/// 1–6 = king/queen/rook/bishop/knight/pawn): the shader looks that piece up in the
/// CC0 glyph atlas (<c>Assets/textures/chess_glyphs.png</c>) and blends it over the
/// square in the OPPOSITE colour (white glyph on a dark cell, dark on a light cell) —
/// PLAN.md D6. The textures rebuild when the pops change or a property like BoardDim
/// changes.
///
/// Geometry builds in OnEnabled/OnValidate (NotSaved preview, like LobbyRoom); the pop
/// animation is gated to play mode by an OnStart flag (OnStart never runs in editor).
/// </summary>
public sealed class FloorCheckerboard : Component, Component.ExecuteInEditor
{
	const int GlyphCount = 6;     // king, queen, rook, bishop, knight, pawn (atlas cells)

	/// <summary>Cells per side of the checkerboard (tunable; texel-per-cell, no rebuild needed).</summary>
	[Property, Range( 2, 40 )] public int BoardDim { get; set; } = 20;

	/// <summary>Popped cells per piece type (×6 piece glyphs = total glyph pops).</summary>
	[Property, Range( 1, 20 )] public int PopsPerColor { get; set; } = 8;

	/// <summary>Black gridline border thickness per square edge, as a whole percent of the cell (0 = off).</summary>
	[Property, Range( 0, 10 )] public int BorderPercent { get; set; } = 1;

	/// <summary>Tile-bevel band width per edge as a whole percent of the cell (0 = flat, no raised-tile look).</summary>
	[Property, Range( 0, 30 )] public int BevelPercent { get; set; } = 3;

	/// <summary>How sharply the bevelled edges tilt — the groove "depth" amount.</summary>
	[Property, Range( 0, 2 )] public float BevelStrength { get; set; } = 0.15f;

	/// <summary>Floor surface roughness; lower is glossier so the grooves catch specular and read as tile.</summary>
	[Property, Range( 0, 1 )] public float Roughness { get; set; } = 0.3f;

	/// <summary>World size of the floor slab in units (match LobbyRoom.RoomSize for full-floor).</summary>
	[Property] public float Size { get; set; } = 800f;

	/// <summary>Slab thickness; the top sits just above the floor (Z=0) to avoid z-fighting.</summary>
	[Property] public float Thickness { get; set; } = 1f;

	[Property] public Color DarkColor { get; set; } = new Color( 0.012f, 0.012f, 0.012f );
	[Property] public Color LightColor { get; set; } = new Color( 0.90f, 0.90f, 0.92f );

	/// <summary>Seconds between pop re-picks (instant switch — no fade).</summary>
	[Property] public float PopInterval { get; set; } = 1.125f;

	GameObject _slab;
	ModelRenderer _renderer;
	Texture _popMap;
	Texture _glyphMap;
	Texture _glyphAtlas;

	// Current pops: cell id (y*BoardDim + x) → glyph index (1..6 piece types).
	readonly List<(int id, int glyphIdx)> _pops = new();

	bool _playing;           // set in OnStart — never true in the editor
	float _cycleStart;

	protected override void OnEnabled() => Rebuild();
	protected override void OnValidate() => Rebuild();

	/// <summary>Re-run after a code hotload (Editor/HotloadRebuild.cs).</summary>
	public void RebuildPreview() => Rebuild();

	protected override void OnStart()
	{
		_playing = true;
		Repick();
		BuildAndPush();
		_cycleStart = Time.Now;
	}

	protected override void OnDisabled() => Clear();

	void Rebuild()
	{
		if ( !Active ) return;
		Clear();

		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null )
		{
			Log.Warning( "[Gambit] models/dev/box.vmdl failed to load — floor checkerboard not built" );
			return;
		}

		var modelSize = model.Bounds.Size;

		_slab = new GameObject( true, "FloorCheckerboard_Slab" );
		_slab.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_slab.Parent = GameObject;
		_slab.LocalPosition = new Vector3( 0, 0, Thickness * 0.5f + 0.05f );
		_slab.LocalScale = new Vector3(
			Size / modelSize.x,
			Size / modelSize.y,
			Thickness / modelSize.z );

		_renderer = _slab.AddComponent<ModelRenderer>();
		_renderer.Model = model;
		_renderer.MaterialOverride = Material.FromShader( "shaders/floor_checker.shader" );

		// Show an initial board (with pops) in the editor preview too.
		Repick();
		BuildAndPush();
	}

	protected override void OnUpdate()
	{
		if ( !_playing || _renderer is null || !_renderer.IsValid() ) return;

		var data = PlayerData.Load();

		// Player toggle: hide/show the whole slab without tearing down the component.
		bool on = data?.CheckerboardFloor ?? true;
		if ( _slab.IsValid() && _slab.Enabled != on )
			_slab.Enabled = on;
		if ( !on ) return;

		// Pop frequency multiplier shortens/lengthens the base interval (≥0.25×).
		float rate = PlayerData.ClampPopRate( data?.FloorPopRate ?? 1f );
		float interval = PopInterval / rate;
		if ( Time.Now - _cycleStart >= interval )
		{
			Repick();
			BuildAndPush();
			_cycleStart = Time.Now;
		}
	}

	/// <summary>Pick PopsPerColor distinct cells per piece glyph, dispersed so same-glyph
	/// pops don't clump: glyphs are placed round-robin (kept spatially balanced) and each
	/// new pop is rejected if it lands within a min spacing of an existing pop of the same
	/// glyph. Pops land on BOTH square colours now (D6 dropped the whites-only filter);
	/// the shader draws the glyph in the opposite colour to whatever square it lands on.
	/// The spacing relaxes if the board is too dense to satisfy it, so placement still
	/// terminates.</summary>
	void Repick()
	{
		_pops.Clear();
		int n = BoardDim;
		var rand = System.Random.Shared;
		int per = System.Math.Max( PopsPerColor, 1 );

		var used = new HashSet<int>();
		var byGlyph = new List<(int x, int y)>[GlyphCount];
		for ( int g = 0; g < GlyphCount; g++ )
			byGlyph[g] = new List<(int, int)>();

		// Target Chebyshev spacing between same-glyph pops, from this glyph's density over
		// the whole board. e.g. 8 pops on a 20×20 → ~7 cells.
		int cells = System.Math.Max( 1, n * n );
		int spacing = System.Math.Max( 1, (int)System.Math.Sqrt( (double)cells / per ) );

		// Round-robin over glyphs so no single piece fills the board before the next
		// starts (keeps piece types interleaved as well as spread).
		for ( int s = 0; s < per; s++ )
		{
			for ( int g = 0; g < GlyphCount; g++ )
			{
				if ( !TryPlacePop( n, rand, used, byGlyph[g], spacing, out int x, out int y ) )
					return; // board can't fit any more spaced cells; stop adding
				used.Add( y * n + x );
				byGlyph[g].Add( (x, y) );
				_pops.Add( (y * n + x, g + 1) ); // glyph index is 1-based (0 = no glyph)
			}
		}
	}

	/// <summary>Find a free cell (either colour) at least <paramref name="spacing"/> away
	/// (Chebyshev) from every same-glyph pop, relaxing the distance toward 1 if it can't
	/// be met.</summary>
	static bool TryPlacePop( int n, System.Random rand, HashSet<int> used,
		List<(int x, int y)> sameGlyph, int spacing, out int px, out int py )
	{
		for ( int minDist = spacing; minDist >= 1; minDist-- )
		{
			for ( int attempt = 0; attempt < 64; attempt++ )
			{
				int x = rand.Next( 0, n );
				int y = rand.Next( 0, n );
				if ( used.Contains( y * n + x ) ) continue;  // no two pops on one cell

				bool ok = true;
				foreach ( var (sx, sy) in sameGlyph )
				{
					if ( System.Math.Max( System.Math.Abs( sx - x ), System.Math.Abs( sy - y ) ) < minDist )
					{
						ok = false;
						break;
					}
				}
				if ( ok )
				{
					px = x;
					py = y;
					return true;
				}
			}
		}
		px = py = 0;
		return false;
	}

	/// <summary>Bake the checker colours + per-cell glyph index into the two maps and push
	/// them (plus the glyph atlas) to the shader.</summary>
	void BuildAndPush()
	{
		if ( _renderer is null || !_renderer.IsValid() ) return;

		int n = BoardDim;

		// PopMap: the plain black/white checker colour per cell (pops no longer recolour a
		// cell — they overlay a glyph instead, D6).
		var checker = new byte[n * n * 4];
		for ( int y = 0; y < n; y++ )
		{
			for ( int x = 0; x < n; x++ )
			{
				bool white = ((x + y) & 1) == 1;
				WriteCell( checker, (y * n + x) * 4, white ? LightColor : DarkColor );
			}
		}

		_glyphAtlas ??= Texture.Load( FileSystem.Mounted, "textures/chess_glyphs.png" );

		// GlyphMap: R channel holds the glyph index (0 = none, 1..6 = piece), one texel per
		// cell. Kept separate from PopMap so the lit checker colour is never touched by an
		// alpha-premultiply. If the atlas failed to load, write no indices — a plain checker
		// floor, never solid-square artefacts from an unbound atlas sample.
		var glyphs = new byte[n * n * 4];
		if ( _glyphAtlas != null )
			foreach ( var (id, glyphIdx) in _pops )
				glyphs[id * 4] = (byte)glyphIdx; // R = index; G/B/A stay 0

		_popMap?.Dispose();
		_popMap = Texture.Create( n, n ).WithData( checker ).WithName( "floor_popmap" ).Finish();
		_glyphMap?.Dispose();
		_glyphMap = Texture.Create( n, n ).WithData( glyphs ).WithName( "floor_glyphmap" ).Finish();

		var so = _renderer.SceneObject;
		if ( so == null ) return;
		so.Attributes.Set( "PopMap", _popMap );
		so.Attributes.Set( "GlyphMap", _glyphMap );
		if ( _glyphAtlas != null )
			so.Attributes.Set( "GlyphAtlas", _glyphAtlas );
		so.Attributes.Set( "GlyphCount", (float)GlyphCount );
		so.Attributes.Set( "BoardDim", (float)n );
		so.Attributes.Set( "BorderWidth", System.Math.Clamp( BorderPercent, 0, 10 ) / 100f );
		so.Attributes.Set( "BevelWidth", System.Math.Clamp( BevelPercent, 0, 30 ) / 100f );
		so.Attributes.Set( "BevelStrength", System.Math.Max( BevelStrength, 0f ) );
		so.Attributes.Set( "FloorRoughness", System.Math.Clamp( Roughness, 0f, 1f ) );
	}

	static void WriteCell( byte[] data, int i, Color c )
	{
		data[i + 0] = ToByte( c.r );
		data[i + 1] = ToByte( c.g );
		data[i + 2] = ToByte( c.b );
		data[i + 3] = 255;
	}

	static byte ToByte( float v ) => (byte)System.Math.Clamp( v * 255f + 0.5f, 0f, 255f );

	void Clear()
	{
		_popMap?.Dispose();
		_popMap = null;
		_glyphMap?.Dispose();
		_glyphMap = null;
		// _glyphAtlas is a shared loaded asset, not a per-build texture — leave it cached.
		if ( _slab.IsValid() ) _slab.Destroy();
		_slab = null;
		_renderer = null;
	}
}
