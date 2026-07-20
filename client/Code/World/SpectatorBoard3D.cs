using System;
using System.Collections.Generic;
using Gambit.Game;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Real 3D spectator board (M5 — the nuke-and-pave that replaces the flat
/// <c>SpectatorBoardPanel</c> WorldPanel, whose CSS glyph-atlas pieces would not paint
/// in-editor no matter how the atlas was regenerated/embedded). It builds a physical chess
/// board — a frame slab, 64 boxed squares, and a dedicated raking light — and populates it
/// with the <b>same</b> <see cref="ChessSetBuilder"/> meshes every table and puzzle already
/// render, driven by <see cref="SpectatorController.Fen"/>. That turns an untestable
/// panel-CSS problem into ordinary mesh rendering that's proven to work in the editor.
///
/// <para>Display-only: no cursor input, no move picking. The only feedback is a last-move
/// square highlight and a slide/hop animation on each position change (ported from
/// <see cref="ChessBoardView"/>, minus the seated-player interaction). With no live
/// position it idles on the start position — a set board waiting for a game.</para>
///
/// <para>The board is built <b>flat</b> (surface normal = local +Z, so pieces stand
/// straight up in +Z exactly like on a table). <see cref="SpectatorWall"/> owns how it
/// hangs: it stands the whole GO up above the wall and tilts it inward (pivoting off the
/// bottom edge) so the face angles down toward the room and the pieces cast shadows across it.
/// Because the piece meshes are children of this flat board, they need no per-piece rotation —
/// the mount rotation carries them.</para>
///
/// <para>Cosmetic and client-local (NotSaved/NotNetworked): each client reads its own
/// <see cref="SpectatorController"/> (the host-folded FEN of a live table), so nothing here
/// is networked.</para>
/// </summary>
public sealed class SpectatorBoard3D : Component, Component.ExecuteInEditor
{
	/// <summary>World-unit edge length of one square (SpectatorWall sizes the board through
	/// this and leaves the GO unscaled, so the board light's world-unit radius stays correct).
	/// Everything else — frame, tiles, piece scale, light offset/radius — is derived from it.</summary>
	[Property] public float CellSize { get; set; } = 8f;

	/// <summary>Thickness of the frame slab the squares sit on.</summary>
	[Property] public float FrameThickness { get; set; } = 1.4f;

	/// <summary>Thickness of each square tile.</summary>
	[Property] public float CellThickness { get; set; } = 0.5f;

	/// <summary>Piece size multiplier on the table-matched default (BoardSize / 26, the same
	/// ratio ChessRing uses so pieces stay proportional to their squares).</summary>
	[Property] public float PieceScaleMul { get; set; } = 1f;

	/// <summary>Seconds a piece takes to slide to its new square on a position change. Kept at or
	/// under the spectator replay's minimum per-move gap so fast games (bullet) stay crisp.</summary>
	[Property] public float MoveSeconds { get; set; } = 0.2f;

	/// <summary>Peak height of the slide hop, as a fraction of a square.</summary>
	[Property] public float MoveArc { get; set; } = 0.4f;

	/// <summary>Brightness of the raking light that pools on the board and casts the piece
	/// shadows. Multiplies white; tuned in-editor.</summary>
	[Property] public float LightBrightness { get; set; } = 5f;

	float BoardSize => CellSize * 8f;
	float PieceScale => BoardSize / 26f * PieceScaleMul;
	// Piece-base height: on top of the frame slab and the tile.
	float SurfaceZ => FrameThickness + CellThickness;

	// Reuse the table palette so the spectator set matches the tables players sit at.
	static readonly Color FrameColor = new( 0.12f, 0.08f, 0.05f );
	static readonly Color LastMoveTint = new( 0.45f, 0.38f, 0.10f );

	const string StartPlacement = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";

	GameObject _root;         // all built geometry (destroyed/rebuilt on hotload)
	GameObject _piecesRoot;
	readonly ModelRenderer[] _cells = new ModelRenderer[64];
	readonly GameObject[] _pieces = new GameObject[64];
	readonly char[] _rendered = new char[64];
	bool _built;

	sealed class Slide
	{
		public GameObject Piece;
		public Vector3 From, To;
		public float Age;
	}
	readonly List<Slide> _slides = new();

	protected override void OnEnabled() => Build();
	protected override void OnDisabled() => Clear();
	protected override void OnValidate() { if ( _built ) Build(); }

	/// <summary>Re-run the build after a code hotload (Editor/HotloadRebuild.cs).</summary>
	public void RebuildPreview() => Build();

	// ── Build the physical board ──

	void Build()
	{
		if ( !Active ) return;
		Clear();

		_root = new GameObject( true, "SpectatorBoard3D" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;
		_root.LocalPosition = Vector3.Zero;
		_root.LocalRotation = Rotation.Identity;

		// Frame slab under the squares.
		AddBox( _root, "BoardFrame",
			new Vector3( 0, 0, FrameThickness * 0.5f ),
			new Vector3( BoardSize + CellSize * 0.35f, BoardSize + CellSize * 0.35f, FrameThickness ),
			FrameColor );

		// 64 tiles. Square index = rank*8 + file (rank 0 = rank 1). Built flat in local XY:
		// file along X (a at −X), rank along Y (rank 1 at −Y) — after SpectatorWall stands the
		// board up, +Y becomes world up, so rank 1 lands at the bottom and the a-file on the
		// viewer's left (White at bottom-left, conventional).
		float tileZ = FrameThickness + CellThickness * 0.5f;
		for ( int sq = 0; sq < 64; sq++ )
		{
			int rank = sq >> 3, file = sq & 7;
			bool light = ( ( rank + file ) & 1 ) != 0; // a1 (0,0) dark, matches ChessRing
			var cell = AddBox( _root, $"Cell {(char)( 'a' + file )}{rank + 1}",
				CellCenter( sq, tileZ ),
				new Vector3( CellSize, CellSize, CellThickness ),
				BaseTint( light ) );   // mode-aware so a hotload in 2D paints cream/brown (M16)
			_cells[sq] = cell;
		}

		_piecesRoot = new GameObject( true, "PiecesView" );
		_piecesRoot.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_piecesRoot.Parent = _root;
		_piecesRoot.LocalPosition = Vector3.Zero;

		// Raking light: sits out in front of the face (+Z) and up toward the top (+Y), aimed
		// back at the board centre, so pieces standing out of the board throw shadows down
		// across it. A child of the flat board, so it rides the wall-mount rotation and stays
		// correctly placed relative to the face.
		var lightGo = new GameObject( true, "BoardLight" );
		lightGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		lightGo.Parent = _root;
		var lightPos = new Vector3( 0f, BoardSize * 0.35f, BoardSize * 0.85f );
		var aimAt = new Vector3( 0f, 0f, SurfaceZ );
		lightGo.LocalPosition = lightPos;
		lightGo.LocalRotation = Rotation.LookAt( ( aimAt - lightPos ).Normal );
		var spot = lightGo.AddComponent<SpotLight>();
		spot.LightColor = new Color( LightBrightness, LightBrightness, LightBrightness );
		spot.Radius = BoardSize * 3f;
		spot.ConeInner = 30f;
		spot.ConeOuter = 55f;
		spot.Shadows = true; // the whole point — pieces cast shadows across the tilted face

		for ( int i = 0; i < 64; i++ ) { _rendered[i] = '\0'; _pieces[i] = null; }
		_slides.Clear();
		_lastFen = null;
		_everSynced = false;
		_lastHighlight = (-1, -1);
		_built = true;

		// Fill immediately so the editor preview isn't a bare board.
		SyncPieces();
	}

	void Clear()
	{
		if ( _root.IsValid() ) _root.Destroy();
		_root = null;
		_piecesRoot = null;
		_slides.Clear();
		_built = false;
	}

	// ── Per-frame: mirror the controller's position ──

	protected override void OnUpdate()
	{
		if ( !_built ) return;
		SyncPieces();
		AdvanceSlides();
		PaintHighlight();
	}

	string _lastFen;
	bool _everSynced;
	// The render mode the wall's pieces were built in (M16); see ChessBoardView._renderedFlat.
	bool _renderedFlat;

	void SyncPieces()
	{
		// The wall reads upright for the viewer: White faces +Y here (world-up once the board stands
		// up), so glyphs point +Y = yaw 90. No seat concept — it's a spectator display. Stamped
		// before any SpawnPiece below.
		ChessSetBuilder.FlatUpYaw = 90f;

		// Play-mode change (M16): the FEN is unchanged, so destroy the pieces, retint the squares,
		// and let the diff below respawn all 64 through the mode-appropriate builder.
		if ( ChessSetBuilder.FlatMode != _renderedFlat )
		{
			_renderedFlat = ChessSetBuilder.FlatMode;
			ResetPieces();
			RetintCells();
		}

		var c = SpectatorController.Instance;
		var fen = c?.Fen;
		if ( _everSynced && ReferenceEquals( fen, _lastFen ) ) return;
		_everSynced = true;
		_lastFen = fen;

		// No live position → idle on the start position.
		var placement = string.IsNullOrEmpty( fen ) ? StartPlacement
			: fen.IndexOf( ' ' ) is var s && s > 0 ? fen[..s] : fen;
		var target = ParsePlacement( placement );

		List<int> removed = null, added = null;
		for ( int sq = 0; sq < 64; sq++ )
		{
			if ( _rendered[sq] == target[sq] ) continue;
			if ( _rendered[sq] != '\0' ) ( removed ??= new() ).Add( sq );
			if ( target[sq] != '\0' ) ( added ??= new() ).Add( sq );
		}
		if ( removed == null && added == null ) return;

		// Pair a vanished piece with an appearing one of the same char = a slide (this frees
		// castling's two movers, and a resync, from any special-casing).
		if ( removed != null && added != null )
		{
			foreach ( var from in removed.ToArray() )
			{
				char ch = _rendered[from];
				int ai = added.FindIndex( sq => target[sq] == ch );
				if ( ai < 0 ) continue;

				int to = added[ai];
				added.RemoveAt( ai );
				removed.Remove( from );
				// A capture puts the destination in `removed` too (it held the captured
				// piece) — drop it so the leftover pass doesn't destroy the arriving piece.
				removed.Remove( to );

				var piece = _pieces[from];
				_pieces[from] = null;
				_rendered[from] = '\0';

				if ( _pieces[to] != null ) { _pieces[to].Destroy(); _pieces[to] = null; }

				_pieces[to] = piece;
				_rendered[to] = ch;
				if ( piece.IsValid() )
				{
					_slides.RemoveAll( sl => sl.Piece == piece );
					_slides.Add( new Slide { Piece = piece, From = piece.LocalPosition, To = PieceLocal( to ), Age = 0f } );
				}
			}
		}

		if ( removed != null )
			foreach ( var sq in removed )
			{
				_pieces[sq]?.Destroy();
				_pieces[sq] = null;
				_rendered[sq] = '\0';
			}

		if ( added != null )
			foreach ( var sq in added )
			{
				if ( _pieces[sq] != null ) _pieces[sq].Destroy();
				_pieces[sq] = SpawnPiece( target[sq], sq );
				_rendered[sq] = target[sq];
			}
	}

	GameObject SpawnPiece( char fenChar, int sq )
	{
		bool white = char.IsUpper( fenChar );
		// Direction-dependent pieces (the knight) must face the enemy. Unlike the tables —
		// where ranks run along +X, so White faces yaw 0 — this board lays ranks along +Y
		// (rank 1 at −Y, rank 8 at +Y, so ranks stand vertically once the board is tilted up),
		// so "toward the enemy" is +Y for White (yaw 90°) and −Y for Black (yaw 270°).
		var piece = ChessSetBuilder.BuildPiece( _piecesRoot, PieceTypeOf( fenChar ), white, PieceScale, yaw: white ? 90f : 270f );
		if ( piece.IsValid() ) piece.LocalPosition = PieceLocal( sq );
		return piece;
	}

	void AdvanceSlides()
	{
		for ( int i = _slides.Count - 1; i >= 0; i-- )
		{
			var sl = _slides[i];
			if ( !sl.Piece.IsValid() ) { _slides.RemoveAt( i ); continue; }

			sl.Age += Time.Delta;
			float t = Math.Clamp( sl.Age / MathF.Max( MoveSeconds, 0.01f ), 0f, 1f );
			float eased = 1f - MathF.Pow( 1f - t, 3f );

			var pos = Vector3.Lerp( sl.From, sl.To, eased );
			// Hop along the board's out-of-face normal (+Z in flat space) so pieces arc over
			// each other rather than plough through.
			pos.z += MathF.Sin( t * MathF.PI ) * CellSize * MoveArc;
			sl.Piece.LocalPosition = pos;

			if ( t >= 1f ) { sl.Piece.LocalPosition = sl.To; _slides.RemoveAt( i ); }
		}
	}

	// ── Last-move highlight ──

	(int from, int to) _lastHighlight = (-1, -1);

	void PaintHighlight()
	{
		var (from, to) = LastMove( SpectatorController.Instance?.LastMoveUci );
		if ( (from, to) == _lastHighlight ) return;

		// Restore the previously-lit squares to their checker colour.
		Restore( _lastHighlight.from );
		Restore( _lastHighlight.to );
		_lastHighlight = (from, to);

		if ( from >= 0 && _cells[from].IsValid() ) _cells[from].Tint = LastMoveTint;
		if ( to >= 0 && _cells[to].IsValid() ) _cells[to].Tint = LastMoveTint;
	}

	void Restore( int sq )
	{
		if ( sq < 0 || !_cells[sq].IsValid() ) return;
		bool light = ( ( ( sq >> 3 ) + ( sq & 7 ) ) & 1 ) != 0;
		_cells[sq].Tint = BaseTint( light );
	}

	/// <summary>A square's own (unhighlighted) colour for the current play mode (M16): the classic
	/// cream/brown pair in 2D, the neutral pair otherwise. Shared by the build, the highlight
	/// restore, and the mode-switch retint so all three agree.</summary>
	static Color BaseTint( bool light ) => ChessSetBuilder.FlatMode
		? ( light ? ChessRing.Light2D : ChessRing.Dark2D )
		: ( light ? ChessRing.LightSquare : ChessRing.DarkSquare );

	/// <summary>Retint all 64 squares to the current mode's palette and drop the highlight latch so
	/// PaintHighlight re-applies the last-move squares next frame (M16 mode switch).</summary>
	void RetintCells()
	{
		for ( int sq = 0; sq < 64; sq++ )
		{
			if ( !_cells[sq].IsValid() ) continue;
			bool light = ( ( ( sq >> 3 ) + ( sq & 7 ) ) & 1 ) != 0;
			_cells[sq].Tint = BaseTint( light );
		}
		_lastHighlight = (-1, -1);
	}

	/// <summary>Destroy every piece and clear the render state so the next <see cref="SyncPieces"/>
	/// rebuilds all 64 (M16 play-mode change). Mirrors ChessBoardView.ResetPieces; the wall has no
	/// trays or performed pieces, so it is simpler.</summary>
	void ResetPieces()
	{
		for ( int i = 0; i < 64; i++ )
		{
			_pieces[i]?.Destroy();
			_pieces[i] = null;
			_rendered[i] = '\0';
		}
		_slides.Clear();
		_lastFen = null;
		_everSynced = false;
	}

	// ── Geometry / parsing ──

	/// <summary>Local centre of a square (file along X, rank along Y) at height z.</summary>
	Vector3 CellCenter( int sq, float z )
	{
		int rank = sq >> 3, file = sq & 7;
		return new Vector3( ( file - 3.5f ) * CellSize, ( rank - 3.5f ) * CellSize, z );
	}

	Vector3 PieceLocal( int sq ) => CellCenter( sq, SurfaceZ );

	static ChessPieceType PieceTypeOf( char fenChar ) => char.ToLowerInvariant( fenChar ) switch
	{
		'p' => ChessPieceType.Pawn,
		'n' => ChessPieceType.Knight,
		'b' => ChessPieceType.Bishop,
		'r' => ChessPieceType.Rook,
		'q' => ChessPieceType.Queen,
		_ => ChessPieceType.King,
	};

	/// <summary>FEN placement field → 64 chars indexed rank*8+file ('\0' = empty).</summary>
	static char[] ParsePlacement( string placement )
	{
		var squares = new char[64];
		int rank = 7, file = 0;
		foreach ( var ch in placement )
		{
			if ( ch == '/' ) { rank--; file = 0; }
			else if ( char.IsDigit( ch ) ) file += ch - '0';
			else if ( file < 8 && rank >= 0 ) squares[rank * 8 + file++] = ch;
		}
		return squares;
	}

	static (int from, int to) LastMove( string uci )
	{
		if ( uci is not { Length: >= 4 } ) return (-1, -1);
		return (Square( uci[0], uci[1] ), Square( uci[2], uci[3] ));
	}

	static int Square( char file, char rank )
	{
		if ( file is < 'a' or > 'h' || rank is < '1' or > '8' ) return -1;
		return ( rank - '1' ) * 8 + ( file - 'a' );
	}

	// ── Box primitive (box.vmdl is not 1×1×1 — same sizing trick as ChessRing.AddBox) ──

	ModelRenderer AddBox( GameObject parent, string name, Vector3 localPos, Vector3 size, Color tint )
	{
		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null || model.IsError )
		{
			Log.Warning( "[Gambit] models/dev/box.vmdl failed to load — spectator board has no geometry" );
			return null;
		}

		var go = new GameObject( true, name );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = parent;
		go.LocalPosition = localPos;

		var modelSize = model.Bounds.Size;
		go.LocalScale = new Vector3( size.x / modelSize.x, size.y / modelSize.y, size.z / modelSize.z );

		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = tint;
		return renderer;
	}
}
