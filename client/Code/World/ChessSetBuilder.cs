using System;
using System.Collections.Generic;
using Sandbox;

namespace Gambit.World;

/// <summary>The six chess piece types, in no particular order.</summary>
public enum ChessPieceType { Pawn, Knight, Bishop, Rook, Queen, King }

/// <summary>
/// Builds chess pieces (D5): first tries a real model at
/// models/chess/{type}.vmdl (the future Poly Haven import — a drop-in swap),
/// and falls back to procedural geometry. The fallback bodies are lathed
/// surfaces of revolution (Mesh + Model.Builder) — the way real pieces are
/// turned — dressed with dev-sphere heads and a few boxes (rook merlons,
/// knight head, king cross). All dimensions are in base units; the root GO is
/// uniformly scaled by <c>scale</c>, and a piece's local origin is the center
/// of its footprint at board-surface height (z=0), so callers just parent it
/// and set LocalPosition to the square center.
/// </summary>
public static class ChessSetBuilder
{
	// Piece tints: warm ivory vs near-black walnut. Both must read against both
	// square colors, so neither is pure white/black.
	public static readonly Color WhiteColor = new( 0.85f, 0.81f, 0.72f );
	public static readonly Color BlackColor = new( 0.09f, 0.07f, 0.06f );

	/// <summary>Standard back-rank piece order, queenside (file a) first.</summary>
	public static readonly ChessPieceType[] BackRank =
	{
		ChessPieceType.Rook, ChessPieceType.Knight, ChessPieceType.Bishop,
		ChessPieceType.Queen, ChessPieceType.King,
		ChessPieceType.Bishop, ChessPieceType.Knight, ChessPieceType.Rook,
	};

	/// <summary>Overall height of each piece in base units — the model path
	/// scales its mesh to the same height so a future import keeps the board's
	/// proportions.</summary>
	public static float PieceHeight( ChessPieceType type ) => type switch
	{
		ChessPieceType.Pawn => 3.2f,
		ChessPieceType.Knight => 4.4f,
		ChessPieceType.Bishop => 4.8f,
		ChessPieceType.Rook => 4.0f,
		ChessPieceType.Queen => 5.6f,
		ChessPieceType.King => 6.4f,
		_ => 4f,
	};

	/// <summary>
	/// Build one piece under <paramref name="parent"/> and return its root GO.
	/// <paramref name="yaw"/> is the piece's facing (knights look at the enemy);
	/// pass the side's "toward the opponent" yaw.
	/// </summary>
	// ── 2D play mode (M16) ──
	//
	// FlatMode is the render dispatch gate: set true by ChessRing.ApplyPlayModeSetting when the
	// player's PlayMode is "2d", it makes EVERY board (tables, the north wall, the ring's preview
	// set) build flat glyph sprites instead of lathed bodies — with ZERO call-site changes, because
	// every one of them goes through this one BuildPiece seam. The pieces are still real per-piece
	// GameObjects, so slides/trays/captures/highlights keep working; only the geometry differs.

	/// <summary>When true, <see cref="BuildPiece"/> builds a flat top-down glyph quad instead of a
	/// 3D body. Toggled by ChessRing off <see cref="Gambit.Game.PlayerData.PlayMode"/>.</summary>
	public static bool FlatMode;

	public static GameObject BuildPiece( GameObject parent, ChessPieceType type, bool white, float scale, float yaw = 0f )
	{
		// 2D dispatch (M16): one line, every board. Falls through to the 3D path if the flat quad
		// can't be built (missing atlas/shader), so the board is never left empty.
		if ( FlatMode )
		{
			var flat = BuildFlatPiece( parent, type, white, scale );
			if ( flat != null ) return flat;
		}

		var root = new GameObject( true, $"{( white ? "White" : "Black" )} {type}" );
		root.Parent = parent;
		root.LocalRotation = Rotation.FromYaw( yaw );
		root.LocalScale = scale;

		var tint = white ? WhiteColor : BlackColor;

		// Drop-in real-model path (CLAUDE.md D5): import pieces as models/chess/*.vmdl
		// on the user's machine and the procedural fallback below never runs.
		// Model.Load never returns null for a missing path — it hands back the
		// engine ERROR model — so gate on IsError, not null.
		var model = Model.Load( $"models/chess/{type.ToString().ToLowerInvariant()}.vmdl" );
		if ( model != null && !model.IsError )
		{
			var renderer = root.AddComponent<ModelRenderer>();
			renderer.Model = model;
			renderer.Tint = tint;
			float h = model.Bounds.Size.z;
			if ( h > 0.01f )
				root.LocalScale = PieceHeight( type ) * scale / h;
			return root;
		}

		// Lathed body (shared per type — tint is per renderer).
		var body = root.AddComponent<ModelRenderer>();
		body.Model = LatheModel( type );
		body.Tint = tint;

		// Dressing that a lathe can't produce.
		switch ( type )
		{
			case ChessPieceType.Pawn:
				AddSphere( root, "Head", new Vector3( 0, 0, 2.55f ), 1.25f, tint );
				break;

			case ChessPieceType.Rook:
				// Four merlons standing on the crown rim (rim wall spans r 0.78–1.0)
				for ( int m = 0; m < 4; m++ )
				{
					float ang = m * MathF.PI * 0.5f;
					AddBox( root, $"Merlon{m}",
						new Vector3( MathF.Cos( ang ) * 0.87f, MathF.Sin( ang ) * 0.87f, 3.82f ),
						new Vector3( 0.24f, 0.5f, 0.45f ), tint,
						Rotation.FromYaw( m * 90f ) );
				}
				break;

			case ChessPieceType.Knight:
				// Neck leans toward the opponent (local +X = facing after yaw)
				AddBox( root, "Neck", new Vector3( 0.05f, 0, 2.35f ), new Vector3( 1.0f, 1.0f, 2.5f ), tint,
					Rotation.FromPitch( -16f ) );
				AddBox( root, "Head", new Vector3( 0.72f, 0, 3.6f ), new Vector3( 1.7f, 0.95f, 0.95f ), tint,
					Rotation.FromPitch( -8f ) );
				AddBox( root, "EarL", new Vector3( 0.2f, 0.26f, 4.2f ), new Vector3( 0.25f, 0.2f, 0.45f ), tint );
				AddBox( root, "EarR", new Vector3( 0.2f, -0.26f, 4.2f ), new Vector3( 0.25f, 0.2f, 0.45f ), tint );
				break;

			case ChessPieceType.Bishop:
				AddSphere( root, "Tip", new Vector3( 0, 0, 4.55f ), 0.45f, tint );
				break;

			case ChessPieceType.Queen:
				AddSphere( root, "Orb", new Vector3( 0, 0, 5.25f ), 0.6f, tint );
				break;

			case ChessPieceType.King:
				AddBox( root, "CrossV", new Vector3( 0, 0, 5.95f ), new Vector3( 0.32f, 0.32f, 1.0f ), tint );
				AddBox( root, "CrossH", new Vector3( 0, 0, 6.05f ), new Vector3( 0.32f, 0.9f, 0.32f ), tint );
				break;
		}

		return root;
	}

	// ── Flat glyph pieces (M16 2D play mode) ──
	//
	// Each piece is the engine's built-in SpriteRenderer showing a per-piece PNG
	// (chess2d_{w|b}_{type}.png). The sprite path does unlit + alpha-cutoff + billboard ITSELF, so
	// there is NO custom shader and NO material to author — which is the whole point: an earlier
	// custom-shader attempt rendered as the pink/black error material because a newly-added .shader
	// isn't reliably compiled/mounted in the editor. SpriteRenderer uses the engine's own
	// pre-compiled sprite shader, which is always present. One SpriteRenderer per piece GameObject,
	// so the slide/tray/capture/highlight code that moves the GameObject keeps working unchanged.

	/// <summary>Sprite size in the piece's local units. DERIVED, not guessed: a piece root is scaled
	/// by <c>scale</c> = <c>PieceScale</c> = <c>TableScale·(BoardSize/26)</c>, and a cell's pitch in
	/// that space is <c>(BoardSize/8)·TableScale</c>. Size·scale = cell pitch cancels to <b>26/8</b>
	/// — independent of BoardSize and TableScale — so a 26/8 sprite fills one square on every
	/// board.</summary>
	const float FlatCellBase = 26f / 8f;

	/// <summary>Lift the sprite a hair along the board's up (× <c>scale</c>) so it never z-fights the
	/// cell top it sits on. The piece origin is already at the cell's top surface.</summary>
	const float FlatLift = 0.2f;

	// Cached Sprite per (type, white) — Sprite.FromTexture wraps the loaded PNG. A missing texture
	// caches nothing and returns null, so BuildPiece falls back to a 3D body.
	static readonly Dictionary<(ChessPieceType, bool), Sprite> _flatSprites = new();

	/// <summary>The sprite for one piece, or null if its PNG can't be loaded (→ 3D fallback).</summary>
	static Sprite FlatSprite( ChessPieceType type, bool white )
	{
		var key = (type, white);
		if ( _flatSprites.TryGetValue( key, out var cached ) && cached != null )
			return cached;

		string path = $"textures/chess2d_{( white ? "w" : "b" )}_{type.ToString().ToLowerInvariant()}.png";
		var tex = Texture.Load( FileSystem.Mounted, path );
		if ( tex == null )
		{
			Log.Warning( $"[Gambit] 2D mode: {path} not mounted — falling back to 3D pieces. Run "
				+ "scripts/gen_glyph_atlas.py --2d and add textures/*.png to the .sbproj Resources." );
			return null;
		}

		var sprite = Sprite.FromTexture( tex );
		_flatSprites[key] = sprite;
		return sprite;
	}

	/// <summary>
	/// Build one flat glyph piece under <paramref name="parent"/> and return its root GO, matching
	/// <see cref="BuildPiece"/>'s contract: parented, uniformly scaled by <paramref name="scale"/>,
	/// origin at the centre of its footprint on the board surface — so the caller positions it with
	/// LocalPosition exactly as it does a 3D piece, and every slide/tray/highlight path is unchanged.
	///
	/// <para>The sprite BILLBOARDS: under the top-down seat camera it lies flat on the board, and at
	/// other tables / the wall it faces the viewer (a readable card) rather than going edge-on.
	/// Unlit (the PNG carries fill + outline), alpha-clipped (order-independent, no blend/sort).</para>
	///
	/// <para>Returns null if the piece texture is missing, so the caller can fall back to 3D.</para>
	/// </summary>
	static GameObject BuildFlatPiece( GameObject parent, ChessPieceType type, bool white, float scale )
	{
		var sprite = FlatSprite( type, white );
		if ( sprite == null ) return null;

		var root = new GameObject( true, $"{( white ? "White" : "Black" )} {type} (flat)" );
		root.Parent = parent;
		root.LocalScale = scale;

		// The sprite sits on a child lifted along the board's up so it clears the cell top. The ROOT
		// is what the caller positions and what slides/trays move; the child rides it.
		var spriteGo = new GameObject( true, "Sprite" );
		spriteGo.Parent = root;
		spriteGo.LocalPosition = new Vector3( 0, 0, FlatLift );

		var sr = spriteGo.AddComponent<SpriteRenderer>();
		sr.Sprite = sprite;
		sr.Size = new Vector2( FlatCellBase, FlatCellBase );
		sr.Billboard = SpriteRenderer.BillboardMode.Always;
		sr.Lighting = false;   // unlit — the PNG is the final colour
		sr.Opaque = true;      // alpha-CLIP via AlphaCutoff (below), not blend: order-independent
		sr.AlphaCutoff = 0.5f;
		sr.Shadows = false;
		return root;
	}

	// ── Lathed bodies ──

	// One generated Model per piece type, shared by every piece on every board
	// (color is the renderer Tint). Statics reset on hotload, so profile edits
	// show up like any other hotloaded change.
	static readonly Dictionary<ChessPieceType, Model> _latheCache = new();

	static Model LatheModel( ChessPieceType type )
	{
		if ( _latheCache.TryGetValue( type, out var cached ) && cached.IsValid() )
			return cached;

		var model = BuildLathe( $"chess_{type}", Profile( type ) );
		_latheCache[type] = model;
		return model;
	}

	/// <summary>
	/// Turning profile per piece as (radius, z) pairs, bottom to top. A radius
	/// of 0 closes the surface (apex); profiles that step back down in z model
	/// hollow cups (rook crown, queen/king crowns) — the lathe handles inward
	/// and downward bands with correct normals.
	/// </summary>
	static Vector2[] Profile( ChessPieceType type ) => type switch
	{
		// Base pad → taper → stem → collar → (head is a sphere)
		ChessPieceType.Pawn => new Vector2[]
		{
			new( 1.05f, 0f ), new( 1.05f, 0.28f ), new( 0.72f, 0.55f ), new( 0.5f, 0.9f ),
			new( 0.38f, 1.6f ), new( 0.44f, 1.95f ), new( 0.68f, 2.05f ), new( 0.3f, 2.2f ),
			new( 0f, 2.25f ),
		},

		// Cylindrical body flaring into a hollow crown (merlons are boxes)
		ChessPieceType.Rook => new Vector2[]
		{
			new( 1.15f, 0f ), new( 1.15f, 0.3f ), new( 0.85f, 0.6f ), new( 0.68f, 1.0f ),
			new( 0.62f, 2.6f ), new( 0.8f, 2.9f ), new( 1.0f, 3.0f ), new( 1.0f, 3.6f ),
			new( 0.78f, 3.6f ), new( 0.78f, 3.3f ), new( 0f, 3.3f ),
		},

		// Just the turned pedestal — neck/head/ears are boxes
		ChessPieceType.Knight => new Vector2[]
		{
			new( 1.15f, 0f ), new( 1.15f, 0.3f ), new( 0.85f, 0.55f ), new( 0.65f, 0.8f ),
			new( 0.55f, 1.1f ), new( 0.75f, 1.3f ), new( 0f, 1.3f ),
		},

		// Slender stem, collar, mitre bulb tapering to the tip sphere
		ChessPieceType.Bishop => new Vector2[]
		{
			new( 1.1f, 0f ), new( 1.1f, 0.3f ), new( 0.78f, 0.6f ), new( 0.5f, 1.0f ),
			new( 0.4f, 2.4f ), new( 0.44f, 2.6f ), new( 0.72f, 2.7f ), new( 0.45f, 2.85f ),
			new( 0.62f, 3.2f ), new( 0.68f, 3.6f ), new( 0.45f, 4.1f ), new( 0.2f, 4.4f ),
			new( 0f, 4.5f ),
		},

		// Tall stem into an out-flared hollow crown cup (orb is a sphere)
		ChessPieceType.Queen => new Vector2[]
		{
			new( 1.2f, 0f ), new( 1.2f, 0.32f ), new( 0.85f, 0.65f ), new( 0.55f, 1.1f ),
			new( 0.42f, 3.0f ), new( 0.5f, 3.3f ), new( 0.8f, 3.45f ), new( 0.5f, 3.6f ),
			new( 0.65f, 4.3f ), new( 0.95f, 4.9f ), new( 0.55f, 5.05f ), new( 0f, 5.05f ),
		},

		// Tallest stem, crown closing to a small flat top under the cross boxes
		ChessPieceType.King => new Vector2[]
		{
			new( 1.2f, 0f ), new( 1.2f, 0.32f ), new( 0.85f, 0.65f ), new( 0.58f, 1.1f ),
			new( 0.45f, 3.4f ), new( 0.55f, 3.7f ), new( 0.85f, 3.85f ), new( 0.55f, 4.0f ),
			new( 0.7f, 4.7f ), new( 0.95f, 5.3f ), new( 0.6f, 5.45f ), new( 0.35f, 5.5f ),
			new( 0f, 5.5f ),
		},

		_ => new Vector2[] { new( 1f, 0f ), new( 1f, 1f ), new( 0f, 1f ) },
	};

	/// <summary>
	/// Revolve a profile around +Z into a runtime Model. Each band between
	/// consecutive profile points gets its own vertex ring pair with the band's
	/// normal, so ledges stay crisp while the circumference shades smooth.
	/// Winding follows the engine convention (right-hand cross of the ordered
	/// edges = outward normal, same as TerrainClipmap).
	/// </summary>
	static Model BuildLathe( string name, Vector2[] profile )
	{
		const int Segments = 24;

		var verts = new List<Vertex>();
		var indices = new List<int>();

		for ( int i = 0; i < profile.Length - 1; i++ )
		{
			float r0 = profile[i].x, z0 = profile[i].y;
			float r1 = profile[i + 1].x, z1 = profile[i + 1].y;
			float dr = r1 - r0, dz = z1 - z0;
			if ( MathF.Abs( dr ) < 1e-4f && MathF.Abs( dz ) < 1e-4f ) continue;

			// Outward band normal in the (radial, z) plane
			var n2 = new Vector2( dz, -dr ).Normal;

			int baseIndex = verts.Count;
			for ( int s = 0; s <= Segments; s++ )
			{
				float t = (float)s / Segments;
				float ang = t * MathF.PI * 2f;
				float ca = MathF.Cos( ang ), sa = MathF.Sin( ang );

				var normal = new Vector3( n2.x * ca, n2.x * sa, n2.y );
				var tangent = new Vector3( -sa, ca, 0 );
				verts.Add( new Vertex( new Vector3( r0 * ca, r0 * sa, z0 ), normal, tangent, new Vector4( t, z0, 0, 0 ) ) );
				verts.Add( new Vertex( new Vector3( r1 * ca, r1 * sa, z1 ), normal, tangent, new Vector4( t, z1, 0, 0 ) ) );
			}

			for ( int s = 0; s < Segments; s++ )
			{
				int i0 = baseIndex + s * 2; // i0 = lower ring, i0+1 = upper, +2/+3 = next segment
				indices.Add( i0 ); indices.Add( i0 + 2 ); indices.Add( i0 + 1 );
				indices.Add( i0 + 1 ); indices.Add( i0 + 2 ); indices.Add( i0 + 3 );
			}
		}

		var mesh = new Mesh( name, Material.Load( "materials/default.vmat" ) );
		mesh.CreateVertexBuffer( verts.Count, verts );
		mesh.CreateIndexBuffer( indices.Count, indices );

		var bounds = BBox.FromPositionAndSize( Vector3.Zero, 0.1f );
		foreach ( var v in verts )
			bounds = bounds.AddPoint( v.Position );
		mesh.Bounds = bounds;

		return Model.Builder.AddMesh( mesh ).Create();
	}

	// ── Tubes (M13) ──
	//
	// A lathe CANNOT bend a tube — it revolves a profile about one axis. So a bent-tube
	// frame is built as straight segments with a sphere at each vertex to hide the mitre,
	// and every segment is a PAIR OF ENDPOINTS in the caller's local space. That is the
	// whole reason to prefer it over a swept mesh on this host: an endpoint is arithmetic,
	// and CLAUDE.md's rule is that nothing here can render, so check where the EDGES land.

	/// <summary>One unit cylinder — radius 1, height 1, spanning z 0..1 — shared by every
	/// tube on every chair. A rectangle profile revolved IS a cylinder, so this is the
	/// existing lathe with a four-point profile; the (0,0)→(1,0) and (1,1)→(0,1) bands are
	/// its end caps, and BuildLathe's normal rule already faces them −Z and +Z.</summary>
	static Model _tubeModel;

	static Model TubeModel()
	{
		if ( _tubeModel != null && _tubeModel.IsValid() ) return _tubeModel;
		_tubeModel = BuildLathe( "chess_tube", new Vector2[]
		{
			new( 0f, 0f ), new( 1f, 0f ), new( 1f, 1f ), new( 0f, 1f ),
		} );
		return _tubeModel;
	}

	/// <summary>
	/// A straight tube of <paramref name="radius"/> from <paramref name="a"/> to
	/// <paramref name="b"/>, both in <paramref name="parent"/>'s local space.
	///
	/// <para><b>The rotation is the one thing here that could be silently 90° wrong</b>, so
	/// it is derived rather than guessed. The lathe's axis is <b>+Z</b>;
	/// <c>Rotation.LookAt</c> makes <b>+X</b> forward. <c>Rotation.FromPitch(90)</c> maps
	/// +X → −Z (ChessRing's TableLight pins that sign: <c>FromPitch(90f); // forward
	/// straight down</c>), which is a +90° turn about +Y, and the same turn maps +Z → +X.
	/// So <c>LookAt(dir) * FromPitch(90)</c> sends +Z → +X → dir. </para>
	///
	/// <para><b>The single-argument LookAt is load-bearing, not shorthand.</b> The two-arg
	/// <c>LookAt(dir, Vector3.Up)</c> computes <c>(up − forward·dot).Normal</c> — for a
	/// VERTICAL tube that is the zero vector, and the rotation is degenerate. A chair's
	/// risers are vertical, so that is four segments per chair, not an exotic case. The
	/// one-arg overload carries the engine's own guard for exactly this
	/// (<c>if forward.WithZ(0).IsNearZeroLength → LookAt(forward, Vector3.Left)</c>), and
	/// a cylinder is symmetric about its axis so the roll it picks cannot matter.</para>
	/// </summary>
	public static GameObject BuildTube( GameObject parent, string name, Vector3 a, Vector3 b,
		float radius, Color tint )
	{
		var dir = b - a;
		float length = dir.Length;
		if ( length < 0.001f ) return null; // a zero-length segment has no direction to face

		var go = new GameObject( true, name );
		go.Parent = parent;
		go.LocalPosition = a;
		go.LocalRotation = Rotation.LookAt( dir ) * Rotation.FromPitch( 90f );
		// The model is a UNIT cylinder, so this is a plain scale rather than the
		// box.vmdl bounds trick — local Z runs along the segment, X/Y are the radius.
		go.LocalScale = new Vector3( radius, radius, length );

		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = TubeModel();
		renderer.Tint = tint;
		return go;
	}

	// ── Bent tube (M13) ──
	//
	// Real bends, not mitred corners with a sphere over the join. A tube frame IS its
	// bends — that is what makes it read as bent tube rather than as welded pipe — so the
	// corners get swept arcs and the whole polyline becomes ONE mesh.
	//
	// One mesh per frame rather than a GameObject per segment also happens to be much
	// cheaper: a chair was ~22 renderers (10 tubes + 12 spheres) and is now 2.

	/// <summary>Facets per 90° of bend. 6 puts a vertex every 15°, which at these radii is
	/// well under a pixel of chord error.</summary>
	const int BendFacets = 6;

	/// <summary>Vertices around the tube's circumference. 12 is what a 0.75-radius tube
	/// needs to read as round at arm's length.</summary>
	const int TubeSides = 12;

	/// <summary>
	/// Sweep a tube of <paramref name="radius"/> along <paramref name="points"/>, rounding
	/// every interior corner to <paramref name="bendRadius"/>, as ONE mesh.
	///
	/// <para><b>The corner maths, since nothing here can render it.</b> At a vertex V
	/// between P and N: the turn angle φ is the angle between the incoming and outgoing
	/// directions, and an arc of radius R tangent to both lines must start
	/// <c>d = R·tan(φ/2)</c> back along the incoming leg and end d forward along the
	/// outgoing one. For the 90° corners of a chair frame that is simply d = R. The arc's
	/// centre sits perpendicular to the incoming direction at that tangent point, offset by
	/// R toward the turn — and the arc is then swept about <c>dirIn × dirOut</c>.</para>
	///
	/// <para><b>The bend is clamped to what the legs can afford</b>: a corner cannot eat
	/// more than half of either leg, or consecutive bends would overlap and the tube would
	/// fold through itself. A chair's shortest leg is the back riser (7.35), so this bites
	/// only if someone turns the radius up past ~3.6.</para>
	///
	/// <para>A rotation-minimising frame carries the ring around the path, so the tube does
	/// not twist through the bends. The seed <c>up</c> is picked off whichever world axis
	/// the first segment is least parallel to — a cylinder is symmetric, so any stable
	/// choice does; an UNSTABLE one (Vector3.Up against a vertical riser) is the degenerate
	/// case Rotation.LookAt's own guard exists for.</para>
	/// </summary>
	public static GameObject BuildTubePath( GameObject parent, string name, Vector3[] points,
		float radius, float bendRadius, Color tint )
	{
		if ( points is not { Length: >= 2 } ) return null;

		var path = RoundCorners( points, bendRadius );
		if ( path.Count < 2 ) return null;

		var go = new GameObject( true, name );
		go.Parent = parent;

		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = SweepTube( name, path, radius );
		renderer.Tint = tint;
		return go;
	}

	/// <summary>Replace each interior corner with an arc of <see cref="BendFacets"/> chords
	/// per 90°, leaving the straight runs between them.</summary>
	static List<Vector3> RoundCorners( Vector3[] points, float bendRadius )
	{
		var path = new List<Vector3> { points[0] };

		for ( int i = 1; i < points.Length - 1; i++ )
		{
			var p = points[i - 1];
			var v = points[i];
			var n = points[i + 1];

			var dirIn = ( v - p ).Normal;
			var dirOut = ( n - v ).Normal;

			float dot = Math.Clamp( dirIn.Dot( dirOut ), -1f, 1f );
			float phi = MathF.Acos( dot );

			// Straight through, or doubling back (which has no tangent arc at all).
			if ( phi < 0.01f || phi > MathF.PI - 0.01f )
			{
				path.Add( v );
				continue;
			}

			float r = bendRadius;
			float d = r * MathF.Tan( phi * 0.5f );

			// A corner may not eat more than half of either leg it sits on, or two bends
			// meet in the middle and the tube folds through itself.
			float maxD = MathF.Min( ( v - p ).Length, ( n - v ).Length ) * 0.5f;
			if ( d > maxD && d > 0.0001f )
			{
				r *= maxD / d;
				d = maxD;
			}

			if ( r < 0.0001f ) { path.Add( v ); continue; }

			var start = v - dirIn * d;
			var end = v + dirOut * d;

			// Perpendicular to dirIn, pointing INTO the turn: the part of dirOut that
			// dirIn doesn't already account for.
			var perp = ( dirOut - dirIn * dot ).Normal;
			var centre = start + perp * r;
			var axis = dirIn.Cross( dirOut ).Normal;
			var spoke = start - centre;

			int facets = Math.Max( 1, (int)MathF.Ceiling( phi / ( MathF.PI * 0.5f ) * BendFacets ) );
			path.Add( start );
			for ( int f = 1; f < facets; f++ )
			{
				float a = phi * f / facets;
				path.Add( centre + Rotate( spoke, axis, a ) );
			}
			path.Add( end );
		}

		path.Add( points[^1] );
		return path;
	}

	/// <summary>Rodrigues' rotation. Hand-rolled rather than Rotation.FromAxis so the whole
	/// arc is plain arithmetic with no quaternion round-trip per facet.</summary>
	static Vector3 Rotate( Vector3 v, Vector3 axis, float radians )
	{
		float c = MathF.Cos( radians ), s = MathF.Sin( radians );
		return v * c + axis.Cross( v ) * s + axis * axis.Dot( v ) * ( 1f - c );
	}

	/// <summary>Extrude a ring of <see cref="TubeSides"/> vertices along the path and cap
	/// both ends. Bounds are accumulated from the real vertices — a wrong BBox culls the
	/// whole chair the moment the camera looks slightly away from it.</summary>
	static Model SweepTube( string name, List<Vector3> path, float radius )
	{
		var verts = new List<Vertex>();
		var indices = new List<int>();

		// Seed the frame off whichever world axis the first segment is LEAST parallel to,
		// so a vertical riser can't pick a degenerate up. Any stable choice does — a
		// cylinder has no roll to get wrong.
		var dir0 = ( path[1] - path[0] ).Normal;
		var seed = MathF.Abs( dir0.z ) < 0.9f ? Vector3.Up : Vector3.Forward;
		var up = ( seed - dir0 * seed.Dot( dir0 ) ).Normal;

		for ( int i = 0; i < path.Count; i++ )
		{
			// Tangent: the average of the legs either side, so a bend's ring splits the
			// angle and the tube's skin stays smooth across it.
			Vector3 tangent;
			if ( i == 0 ) tangent = ( path[1] - path[0] ).Normal;
			else if ( i == path.Count - 1 ) tangent = ( path[^1] - path[^2] ).Normal;
			else tangent = ( ( path[i] - path[i - 1] ).Normal + ( path[i + 1] - path[i] ).Normal ).Normal;

			// Rotation-minimising: re-project the previous up rather than recomputing it
			// from a fixed axis, so the ring doesn't spin as the path turns.
			up = ( up - tangent * up.Dot( tangent ) ).Normal;
			var right = tangent.Cross( up ).Normal;

			for ( int s = 0; s <= TubeSides; s++ )
			{
				float t = (float)s / TubeSides;
				float a = t * MathF.PI * 2f;
				var normal = ( right * MathF.Cos( a ) + up * MathF.Sin( a ) ).Normal;
				verts.Add( new Vertex( path[i] + normal * radius, normal, right,
					new Vector4( t, i, 0, 0 ) ) );
			}
		}

		int ring = TubeSides + 1;
		for ( int i = 0; i < path.Count - 1; i++ )
		{
			for ( int s = 0; s < TubeSides; s++ )
			{
				int a = i * ring + s;
				int b = a + ring;
				indices.Add( a ); indices.Add( b ); indices.Add( a + 1 );
				indices.Add( a + 1 ); indices.Add( b ); indices.Add( b + 1 );
			}
		}

		AddCap( verts, indices, path[0], -( path[1] - path[0] ).Normal, radius, flip: false );
		AddCap( verts, indices, path[^1], ( path[^1] - path[^2] ).Normal, radius, flip: true );

		var mesh = new Mesh( name, Material.Load( "materials/default.vmat" ) );
		mesh.CreateVertexBuffer( verts.Count, verts );
		mesh.CreateIndexBuffer( indices.Count, indices );

		var bounds = BBox.FromPositionAndSize( path[0], 0.1f );
		foreach ( var v in verts )
			bounds = bounds.AddPoint( v.Position );
		mesh.Bounds = bounds;

		return Model.Builder.AddMesh( mesh ).Create();
	}

	/// <summary>A flat disc closing one end of the sweep. Its own vertices, because a cap's
	/// normal is the axis and the tube's is radial — sharing them would smear the rim.</summary>
	static void AddCap( List<Vertex> verts, List<int> indices, Vector3 centre, Vector3 normal,
		float radius, bool flip )
	{
		var seed = MathF.Abs( normal.z ) < 0.9f ? Vector3.Up : Vector3.Forward;
		var up = ( seed - normal * seed.Dot( normal ) ).Normal;
		var right = normal.Cross( up ).Normal;

		int centreIndex = verts.Count;
		verts.Add( new Vertex( centre, normal, right, new Vector4( 0.5f, 0.5f, 0, 0 ) ) );

		for ( int s = 0; s <= TubeSides; s++ )
		{
			float a = (float)s / TubeSides * MathF.PI * 2f;
			var offset = right * MathF.Cos( a ) + up * MathF.Sin( a );
			verts.Add( new Vertex( centre + offset * radius, normal, right,
				new Vector4( 0.5f + MathF.Cos( a ) * 0.5f, 0.5f + MathF.Sin( a ) * 0.5f, 0, 0 ) ) );
		}

		for ( int s = 0; s < TubeSides; s++ )
		{
			int a = centreIndex + 1 + s;
			if ( flip ) { indices.Add( centreIndex ); indices.Add( a ); indices.Add( a + 1 ); }
			else { indices.Add( centreIndex ); indices.Add( a + 1 ); indices.Add( a ); }
		}
	}

	// ── Primitive dressing ──

	/// <summary>Same dev-box sizing trick as ChessRing.AddBox (box.vmdl is not 1×1×1),
	/// kept separate so the builder has no ring dependency.</summary>
	static void AddBox( GameObject parent, string name, Vector3 localPos, Vector3 size, Color tint, Rotation? localRot = null )
	{
		AddDevModel( parent, "models/dev/box.vmdl", name, localPos, size, tint, localRot );
	}

	static void AddSphere( GameObject parent, string name, Vector3 localPos, float diameter, Color tint )
	{
		AddDevModel( parent, "models/dev/sphere.vmdl", name, localPos, new Vector3( diameter ), tint, null );
	}

	static void AddDevModel( GameObject parent, string path, string name, Vector3 localPos, Vector3 size, Color tint, Rotation? localRot )
	{
		var model = Model.Load( path );
		if ( model == null || model.IsError )
		{
			Log.Warning( $"[Gambit] {path} failed to load — chess pieces are missing parts" );
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
		renderer.Tint = tint;
	}
}
