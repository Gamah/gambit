using System;
using System.Collections.Generic;
using Gambit.Chess;
using Gambit.Game;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Renders one table's chess position and turns cursor input into moves.
/// Client-side only — every client owns its board's piece GameObjects (the
/// "everything cosmetic is local" doctrine): on start it deletes the static
/// start-position set ChessRing built and re-spawns pieces it can move.
///
/// Rendering is a FEN diff: whenever the controller's position changes, pieces
/// whose square/char pair vanished are matched to appearing pairs of the same
/// char and lerped there (handles castling's two movers for free); unmatched
/// vanishing pieces are captures (destroyed), unmatched appearing ones are
/// spawns (promotion). No incremental board state to corrupt — any FEN resync
/// renders correctly by the same path.
///
/// Input while seated and it's our turn: ray from the cursor to the board
/// plane picks squares; first click selects an own piece (legal targets
/// highlight), second click moves — via the controller, which validates with
/// the embedded rules before anything touches the network. A pawn reaching the
/// last rank parks as PendingPromotion until GameHud's picker chooses a piece.
/// Highlights reuse the board's cell boxes: tint swaps, no extra geometry.
/// </summary>
public sealed class ChessBoardView : Component
{
	/// <summary>Set by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }

	/// <summary>Set by ChessRing at build.</summary>
	[Property] public LocalGameController Controller { get; set; }

	/// <summary>Seconds a piece takes to slide to its new square.</summary>
	[Property] public float MoveSeconds { get; set; } = 0.22f;

	/// <summary>Peak height of the slide arc, as a fraction of a square.</summary>
	[Property] public float MoveArc { get; set; } = 0.35f;

	// Highlight tints over the cell boxes. Deliberately the SAME on light and
	// dark squares, and kept saturated + medium-low in value: the overhead table
	// spotlight (ChessRing.MarqueeBrightness ~3.3) multiplies these before
	// tonemapping, so a bright/pale tint on an already-light square blows out to
	// white and loses its hue (that's why light-square highlights were
	// invisible). Tiers by hue: selected = gold, legal target = green (brighter
	// under the cursor = "click to move here"), hover = blue (cursor square),
	// check = red, last move = dim olive.
	static readonly Color SelectedTint = new( 0.90f, 0.62f, 0.05f );
	// Legal targets read as a set of lighter greens; the one under the cursor
	// (the square you'd actually move to) is a darker, deeper green so it's
	// unmistakably "this one" rather than just another option or the teal cursor.
	// Each keeps a light/dark VALUE variant — saturated enough that the hue
	// survives the table light, but bright on light squares and dim on dark ones
	// so the board's own checker color still reads through the green.
	static readonly Color TargetLightTint = new( 0.24f, 0.72f, 0.18f );
	static readonly Color TargetDarkTint = new( 0.12f, 0.44f, 0.09f );
	static readonly Color TargetHoverLightTint = new( 0.10f, 0.36f, 0.08f );
	static readonly Color TargetHoverDarkTint = new( 0.04f, 0.19f, 0.04f );
	static readonly Color HoverTint = new( 0.20f, 0.45f, 0.90f );
	static readonly Color LastMoveTint = new( 0.45f, 0.38f, 0.10f );
	static readonly Color CheckTint = new( 0.85f, 0.14f, 0.10f );

	const string StartPlacement = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";

	// square index = rank * 8 + file
	readonly GameObject[] _pieces = new GameObject[64];
	readonly char[] _rendered = new char[64];
	ModelRenderer[] _cells; // by square index
	GameObject _table;
	GameObject _piecesRoot;
	bool _ready;
	bool _cellsComplete;   // all 64 cell renderers bound
	int _cellBindFrames;   // frames spent waiting on cells (diagnostic)

	sealed class Slide
	{
		public GameObject Piece;
		public Vector3 From, To;
		public float Age;
	}

	readonly List<Slide> _slides = new();

	// ── Input state ──

	/// <summary>Currently selected own-piece square ("e2"), or null.</summary>
	public string Selected { get; private set; }

	List<string> _targets = new();
	int _hoverSquare = -1;

	/// <summary>A move waiting on the GameHud promotion picker: (from, to), or null.</summary>
	public (string From, string To)? PendingPromotion { get; private set; }

	protected override void OnUpdate()
	{
		if ( !EnsureBoard() ) return;

		SyncPieces();
		AdvanceSlides();
		UpdateInput();
		PaintHighlights();
	}

	/// <summary>
	/// Lazily bind to the station's board hierarchy, retrying until it exists.
	/// On a networked client the station's child GameObjects (Table, its 64
	/// cells, their ModelRenderers) can attach a frame or two AFTER this
	/// component starts, so binding once in OnStart would silently miss them and
	/// leave the board dead. Piece rendering only needs the Table; cell
	/// renderers (for highlights) are resolved separately in PaintHighlights as
	/// they stream in, so pieces never wait on them.
	/// </summary>
	bool EnsureBoard()
	{
		if ( _ready ) return true;

		_table ??= GameObject.Children.Find( c => c.Name == "Table" );
		if ( _table == null ) return false;

		// Replace ChessRing's static preview set with one this view owns/animates
		_table.Children.Find( c => c.Name == "Pieces" )?.Destroy();

		_piecesRoot = new GameObject( true, "PiecesView" );
		_piecesRoot.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_piecesRoot.Parent = _table;
		_piecesRoot.LocalPosition = Vector3.Zero;
		_piecesRoot.LocalRotation = Rotation.Identity;

		for ( int i = 0; i < 64; i++ ) _rendered[i] = '\0';
		_ready = true;
		return true;
	}

	/// <summary>Bind the 64 cell renderers by square name ("Cell e4"). Idempotent
	/// and cumulative — safe to call every frame while cells replicate in.
	/// Returns true once all 64 are bound.</summary>
	bool ResolveCells()
	{
		_cells ??= new ModelRenderer[64];
		int found = 0;
		foreach ( var child in _table.Children )
		{
			if ( !child.Name.StartsWith( "Cell " ) || !TryParseSquare( child.Name[5..], out int sq ) )
				continue;
			_cells[sq] ??= child.GetComponent<ModelRenderer>();
			if ( _cells[sq] != null ) found++;
		}
		return found == 64;
	}

	// ── Rendering ──

	string _lastFen;
	bool _everSynced;

	void SyncPieces()
	{
		// ChessGame.Fen returns the same string instance until a move mutates
		// the board, so this reference check makes idle tables (most of a big
		// lobby, most frames) free.
		var fen = Controller?.Game?.Fen;
		if ( _everSynced && ReferenceEquals( fen, _lastFen ) ) return;
		_everSynced = true;
		_lastFen = fen;

		var target = ParsePlacement( fen == null ? StartPlacement : fen[..fen.IndexOf( ' ' )] );

		// Collect the diff
		List<int> removed = null, added = null;
		for ( int sq = 0; sq < 64; sq++ )
		{
			if ( _rendered[sq] == target[sq] ) continue;
			if ( _rendered[sq] != '\0' ) ( removed ??= new() ).Add( sq );
			if ( target[sq] != '\0' ) ( added ??= new() ).Add( sq );
		}
		if ( removed == null && added == null ) return;

		// Any position change invalidates the current selection — and a pending
		// promotion (a diff can only arrive under one via resync or a new game;
		// our own promotion move clears PendingPromotion before it applies).
		ClearSelection();
		PendingPromotion = null;

		// Pair a vanished piece with an appearing one of the same char = a slide
		if ( removed != null && added != null )
		{
			foreach ( var from in removed.ToArray() )
			{
				char c = _rendered[from];
				int to = added.FindIndex( sq => target[sq] == c );
				if ( to < 0 ) continue;

				int toSquare = added[to];
				added.RemoveAt( to );
				removed.Remove( from );

				var piece = _pieces[from];
				_pieces[from] = null;
				_rendered[from] = '\0';

				// The destination may hold a captured piece — clear it first
				if ( _pieces[toSquare] != null )
				{
					_pieces[toSquare].Destroy();
					_pieces[toSquare] = null;
				}

				_pieces[toSquare] = piece;
				_rendered[toSquare] = c;
				// A piece can move again while still sliding (fast play, resync)
				// — the stale slide would keep overwriting the new one's target
				_slides.RemoveAll( s => s.Piece == piece );
				_slides.Add( new Slide
				{
					Piece = piece,
					From = piece.LocalPosition,
					To = SquareLocal( toSquare ),
					Age = 0f,
				} );
			}
		}

		// Leftover removals: captures (or resync artifacts) — just delete
		if ( removed != null )
		{
			foreach ( var sq in removed )
			{
				_pieces[sq]?.Destroy();
				_pieces[sq] = null;
				_rendered[sq] = '\0';
			}
		}

		// Leftover additions: spawns (initial fill, promotions, resyncs)
		if ( added != null )
		{
			foreach ( var sq in added )
			{
				if ( _pieces[sq] != null ) _pieces[sq].Destroy();
				_pieces[sq] = SpawnPiece( target[sq], sq );
				_rendered[sq] = target[sq];
			}
		}
	}

	GameObject SpawnPiece( char fenChar, int square )
	{
		var ring = ChessRing.Instance;
		if ( ring == null ) return null;

		bool white = char.IsUpper( fenChar );

		// Same facing rule as the ring's preview set: pieces look at the enemy
		var piece = ChessSetBuilder.BuildPiece( _piecesRoot, PieceTypeOf( fenChar ), white, ring.PieceScale, yaw: white ? 0f : 180f );
		piece.LocalPosition = SquareLocal( square );
		return piece;
	}

	void AdvanceSlides()
	{
		for ( int i = _slides.Count - 1; i >= 0; i-- )
		{
			var slide = _slides[i];
			if ( !slide.Piece.IsValid() )
			{
				_slides.RemoveAt( i );
				continue;
			}

			slide.Age += Time.Delta;
			float t = Math.Clamp( slide.Age / MoveSeconds, 0f, 1f );
			float eased = 1f - MathF.Pow( 1f - t, 3f );

			var pos = Vector3.Lerp( slide.From, slide.To, eased );
			// Gentle arc so pieces hop rather than plough through each other
			float cell = ChessRing.Instance?.CellWorldSize ?? 5f;
			pos.z += MathF.Sin( t * MathF.PI ) * cell * MoveArc;
			slide.Piece.LocalPosition = pos;

			if ( t >= 1f )
			{
				slide.Piece.LocalPosition = slide.To;
				_slides.RemoveAt( i );
			}
		}
	}

	// ── Input ──

	void UpdateInput()
	{
		_hoverSquare = -1;

		// Only the seated local player interacts, only while the game is live,
		// only on their turn, and not while the promotion picker is up.
		if ( Controller == null || !Controller.IsMyTurn || PendingPromotion != null )
		{
			// Game ended or reset from under us (resign/abandon produce no board
			// diff) — drop any half-finished input state.
			if ( !( Controller?.Playing ?? false ) )
			{
				ClearSelection();
				PendingPromotion = null;
			}
			return;
		}
		if ( LobbyPlayer.Local is not { CameraSettled: true } ) return;

		int square = SquareUnderCursor();
		_hoverSquare = square;

		if ( !Input.Pressed( "Select" ) || square < 0 )
			return;

		var game = Controller.Game;
		string clicked = SquareName( square );
		bool ownPiece = IsOwnPiece( _rendered[square] );

		if ( Selected == null )
		{
			if ( ownPiece ) Select( clicked );
		}
		else if ( clicked == Selected )
		{
			ClearSelection();
		}
		else if ( _targets.Contains( clicked ) )
		{
			if ( game.IsPromotion( Selected, clicked ) )
			{
				// Park until GameHud's picker calls ChoosePromotion
				PendingPromotion = (Selected, clicked);
				ClearSelection();
			}
			else
			{
				Controller.TryMakeLocalMove( Selected + clicked );
				ClearSelection();
			}
		}
		else if ( ownPiece )
		{
			Select( clicked ); // switch selection
		}
		else
		{
			ClearSelection();
		}
	}

	void Select( string square )
	{
		Selected = square;
		_targets = Controller.Game.LegalTargets( square );
	}

	void ClearSelection()
	{
		Selected = null;
		if ( _targets.Count > 0 ) _targets = new List<string>();
	}

	/// <summary>GameHud promotion picker chose a piece ('q','r','b','n').</summary>
	public void ChoosePromotion( char piece )
	{
		if ( PendingPromotion is not { } pending ) return;
		PendingPromotion = null;
		Controller.TryMakeLocalMove( pending.From + pending.To + piece );
	}

	/// <summary>GameHud promotion picker dismissed without choosing.</summary>
	public void CancelPromotion() => PendingPromotion = null;

	/// <summary>
	/// Board square under the mouse cursor: the cursor ray projected onto the
	/// board-surface plane, in station-local space. Pieces deliberately do NOT
	/// intercept the ray — the pick is always the square under the cursor on
	/// the board itself, so tall pieces never block or divert selection. (The
	/// cells are visuals without colliders; plane math beats scene tracing.)
	/// </summary>
	int SquareUnderCursor()
	{
		var ring = ChessRing.Instance;
		var camera = Scene?.Camera;
		if ( ring == null || camera == null || Station == null ) return -1;

		var ray = camera.ScreenPixelToRay( Mouse.Position );

		// Station-local ray (stations are unscaled, yaw-only)
		var origin = Station.GameObject.WorldTransform.PointToLocal( ray.Position );
		var dir = Station.GameObject.WorldRotation.Inverse * ray.Forward;

		if ( MathF.Abs( dir.z ) < 0.0001f ) return -1;
		float t = ( ring.BoardSurfaceZ - origin.z ) / dir.z;
		if ( t <= 0f ) return -1;

		var local = origin + dir * t;
		float cell = ring.CellWorldSize;

		// Inverse of ChessRing.CellCenter (×TableScale): x = (rank-3.5)·cell, y = (3.5-file)·cell
		int rank = (int)MathF.Floor( local.x / cell + 4f );
		int file = (int)MathF.Floor( 4f - local.y / cell );
		if ( rank is < 0 or > 7 || file is < 0 or > 7 ) return -1;
		return rank * 8 + file;
	}

	static ChessPieceType PieceTypeOf( char fenChar ) => char.ToLowerInvariant( fenChar ) switch
	{
		'p' => ChessPieceType.Pawn,
		'n' => ChessPieceType.Knight,
		'b' => ChessPieceType.Bishop,
		'r' => ChessPieceType.Rook,
		'q' => ChessPieceType.Queen,
		_ => ChessPieceType.King,
	};

	// ── Highlights ──

	int _lastPaintHash;

	void PaintHighlights()
	{
		// Cell renderers may still be replicating on a fresh client — keep
		// binding until all 64 are found, and while binding, force a repaint
		// every frame so cells that just appeared pick up the current state.
		bool binding = !_cellsComplete;
		if ( binding )
		{
			_cellsComplete = ResolveCells();
			_cellBindFrames++;
			if ( !_cellsComplete && _cellBindFrames == 300 ) // ~5s at 60fps
			{
				int bound = 0;
				foreach ( var c in _cells )
					if ( c != null ) bound++;
				Log.Warning( $"[Gambit] ChessBoardView bound only {bound}/64 board cells — highlights may be incomplete" );
			}
		}

		if ( _cells == null ) return;

		var game = Controller?.Game;
		string lastMove = game != null ? game.LastMoveUci ?? Controller.LastMoveUci : null;
		string checkedKing = game?.CheckedKingSquare;
		bool interactive = Controller?.IsMyTurn == true && PendingPromotion == null;

		// Repaint only when an input into the tint decision changed — idle
		// tables (and non-hovered frames) skip the 64-cell walk entirely. While
		// cells are still binding, repaint regardless so latecomers get tinted.
		int hash = HashCode.Combine( lastMove, checkedKing, interactive,
			Selected, _hoverSquare, _targets );
		if ( !binding && hash == _lastPaintHash ) return;
		_lastPaintHash = hash;

		string lastFrom = null, lastTo = null;
		if ( lastMove is { Length: >= 4 } )
		{
			lastFrom = lastMove[..2];
			lastTo = lastMove[2..4];
		}

		// _hoverSquare is only set on the local player's own turn (UpdateInput),
		// so the hover glow naturally appears only when the player can act.
		for ( int sq = 0; sq < 64; sq++ )
		{
			var renderer = _cells[sq];
			if ( renderer == null ) continue;

			bool light = ( ( sq >> 3 ) + ( sq & 7 ) ) % 2 != 0; // matches ChessRing parity
			string name = SquareNames[sq];
			bool hovered = sq == _hoverSquare;

			Color tint;
			if ( Selected == name )
				tint = SelectedTint;
			else if ( interactive && _targets.Contains( name ) )
				// Legal move target — deeper green under the cursor ("move here");
				// light/dark variant keeps the square's checker color reading through
				tint = hovered
					? ( light ? TargetHoverLightTint : TargetHoverDarkTint )
					: ( light ? TargetLightTint : TargetDarkTint );
			else if ( checkedKing == name )
				tint = CheckTint;
			else if ( hovered )
				// The square under the cursor — constant "you're pointing here" feedback
				tint = HoverTint;
			else if ( lastFrom == name || lastTo == name )
				tint = LastMoveTint;
			else
				// Restore the square's own checker color
				tint = light ? ChessRing.LightSquare : ChessRing.DarkSquare;

			if ( renderer.Tint != tint )
				renderer.Tint = tint;
		}
	}

	/// <summary>The square holds one of the local seated player's pieces.</summary>
	bool IsOwnPiece( char fenChar ) =>
		fenChar != '\0' && Controller?.LocalSeat is { } seat
		&& char.IsUpper( fenChar ) == ( seat == ChessSeat.White );

	// ── Square helpers ──

	static readonly string[] SquareNames = BuildSquareNames();

	static string[] BuildSquareNames()
	{
		var names = new string[64];
		for ( int sq = 0; sq < 64; sq++ )
			names[sq] = $"{(char)( 'a' + ( sq & 7 ) )}{(char)( '1' + ( sq >> 3 ) )}";
		return names;
	}

	static Vector3 SquareLocal( int square ) =>
		ChessRing.Instance?.SquareLocalPosition( square & 7, square >> 3 ) ?? Vector3.Zero;

	static string SquareName( int square ) => SquareNames[square];

	static bool TryParseSquare( string name, out int square )
	{
		square = -1;
		if ( name is not { Length: 2 } ) return false;
		if ( name[0] is < 'a' or > 'h' || name[1] is < '1' or > '8' ) return false;
		square = ( name[1] - '1' ) * 8 + ( name[0] - 'a' );
		return true;
	}

	/// <summary>Placement field of a FEN → 64 chars indexed rank·8+file ('\0' = empty).</summary>
	static char[] ParsePlacement( string placement )
	{
		var squares = new char[64];
		int rank = 7, file = 0;
		foreach ( var c in placement )
		{
			if ( c == '/' ) { rank--; file = 0; }
			else if ( char.IsDigit( c ) ) file += c - '0';
			else if ( file < 8 && rank >= 0 )
				squares[rank * 8 + file++] = c;
		}
		return squares;
	}
}
