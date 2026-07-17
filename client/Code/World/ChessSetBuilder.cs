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
	public static GameObject BuildPiece( GameObject parent, ChessPieceType type, bool white, float scale, float yaw = 0f )
	{
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

	/// <summary>A sphere at a tube frame's vertex, hiding the mitre where two segments
	/// meet. Same radius as the tubes, so it reads as the bend rather than as a knuckle.</summary>
	public static void BuildJoint( GameObject parent, string name, Vector3 pos, float radius, Color tint )
		=> AddSphere( parent, name, pos, radius * 2f, tint );

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
