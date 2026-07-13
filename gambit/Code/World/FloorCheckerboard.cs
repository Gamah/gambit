using System.Collections.Generic;
using Sandbox;
using Gambit.Game;
using Gambit.Theme;

namespace Gambit.World;

/// <summary>
/// Decorative floor art: a procedural N×N black/white checkerboard rendered by the
/// <c>shaders/floor_checker.shader</c> material on a single flat slab. Some cells
/// "pop" to the player's active colour-blind palette — <see cref="PopsPerColor"/>
/// cells per palette colour — switching instantly to new cells every
/// <see cref="PopInterval"/> seconds.
///
/// The whole board (checker + pops) is baked into a tiny BoardDim×BoardDim texture
/// here and point-sampled by the shader (one texel per cell), so the pop count is
/// unbounded and palette/pops are a single mechanism. The texture is rebuilt when the
/// pops change, when the colour scheme changes (read from <see cref="PlayerData"/> the
/// same way GameHud/CubeBoardView do — so palette changes update live), or when a
/// property like BoardDim changes.
///
/// Geometry builds in OnEnabled/OnValidate (NotSaved preview, like LobbyRoom); the pop
/// animation is gated to play mode by an OnStart flag (OnStart never runs in editor).
/// </summary>
public sealed class FloorCheckerboard : Component, Component.ExecuteInEditor
{
	const int ColorCount = 4;     // active palette colours (Red/Blue/Green/Yellow)

	/// <summary>Cells per side of the checkerboard (tunable; texel-per-cell, no rebuild needed).</summary>
	[Property, Range( 2, 40 )] public int BoardDim { get; set; } = 20;

	/// <summary>Popped cells per palette colour (×4 colours = total pops).</summary>
	[Property, Range( 1, 20 )] public int PopsPerColor { get; set; } = 16;

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

	// Current pops: cell id (y*BoardDim + x) → palette colour index (0..3).
	readonly List<(int id, int colorIdx)> _pops = new();

	string _scheme;          // cached colour scheme, to detect live changes
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

		// Live palette swap: rebuild (same cells, new colours) when the scheme changes.
		if ( CurrentScheme() != _scheme )
			BuildAndPush();

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

	static string CurrentScheme() => PlayerData.Load()?.ColorScheme ?? "normal";

	/// <summary>Pick PopsPerColor distinct white cells per palette colour, dispersed so
	/// same-colour pops don't clump: colours are placed round-robin (kept spatially
	/// balanced) and each new pop is rejected if it lands within a min spacing of an
	/// existing pop of the same colour. The spacing relaxes if the board is too dense
	/// to satisfy it, so placement still terminates.</summary>
	void Repick()
	{
		_pops.Clear();
		int n = BoardDim;
		var rand = System.Random.Shared;
		int per = System.Math.Max( PopsPerColor, 1 );

		var used = new HashSet<int>();
		var byColor = new List<(int x, int y)>[ColorCount];
		for ( int c = 0; c < ColorCount; c++ )
			byColor[c] = new List<(int, int)>();

		// Target Chebyshev spacing between same-colour pops, from this colour's density
		// on the white squares (≈ half the board). e.g. 16 pops on a 20×20 → ~3 cells.
		int whiteCells = System.Math.Max( 1, (n * n) / 2 );
		int spacing = System.Math.Max( 1, (int)System.Math.Sqrt( (double)whiteCells / per ) );

		// Round-robin over colours so no single colour fills the board before the next
		// starts (keeps colours interleaved as well as spread).
		for ( int s = 0; s < per; s++ )
		{
			for ( int c = 0; c < ColorCount; c++ )
			{
				if ( !TryPlacePop( n, rand, used, byColor[c], spacing, out int x, out int y ) )
					return; // board can't fit any more spaced white cells; stop adding
				used.Add( y * n + x );
				byColor[c].Add( (x, y) );
				_pops.Add( (y * n + x, c) );
			}
		}
	}

	/// <summary>Find a free white cell at least <paramref name="spacing"/> away (Chebyshev)
	/// from every same-colour pop, relaxing the distance toward 1 if it can't be met.</summary>
	static bool TryPlacePop( int n, System.Random rand, HashSet<int> used,
		List<(int x, int y)> sameColor, int spacing, out int px, out int py )
	{
		for ( int minDist = spacing; minDist >= 1; minDist-- )
		{
			for ( int attempt = 0; attempt < 64; attempt++ )
			{
				int x = rand.Next( 0, n );
				int y = rand.Next( 0, n );
				if ( ((x + y) & 1) == 0 ) continue;          // white cells only (odd parity)
				if ( used.Contains( y * n + x ) ) continue;  // no two pops on one cell

				bool ok = true;
				foreach ( var (sx, sy) in sameColor )
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

	/// <summary>Bake checker + pops into the texture and push it to the shader.</summary>
	void BuildAndPush()
	{
		if ( _renderer is null || !_renderer.IsValid() ) return;

		_scheme = CurrentScheme();
		int n = BoardDim;
		var palette = Colors.GetPalette( _scheme ); // [0]=background, [1..4]=active colours

		var data = new byte[n * n * 4];
		for ( int y = 0; y < n; y++ )
		{
			for ( int x = 0; x < n; x++ )
			{
				bool white = ((x + y) & 1) == 1;
				WriteCell( data, (y * n + x) * 4, white ? LightColor : DarkColor );
			}
		}

		foreach ( var (id, colorIdx) in _pops )
			WriteCell( data, id * 4, palette[colorIdx + 1] );

		_popMap?.Dispose();
		_popMap = Texture.Create( n, n ).WithData( data ).WithName( "floor_popmap" ).Finish();
		_renderer.SceneObject?.Attributes.Set( "PopMap", _popMap );
		_renderer.SceneObject?.Attributes.Set( "BoardDim", (float)n );
		_renderer.SceneObject?.Attributes.Set( "BorderWidth", System.Math.Clamp( BorderPercent, 0, 10 ) / 100f );
		_renderer.SceneObject?.Attributes.Set( "BevelWidth", System.Math.Clamp( BevelPercent, 0, 30 ) / 100f );
		_renderer.SceneObject?.Attributes.Set( "BevelStrength", System.Math.Max( BevelStrength, 0f ) );
		_renderer.SceneObject?.Attributes.Set( "FloorRoughness", System.Math.Clamp( Roughness, 0f, 1f ) );
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
		if ( _slab.IsValid() ) _slab.Destroy();
		_slab = null;
		_renderer = null;
	}
}
