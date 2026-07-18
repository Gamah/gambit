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
/// vanishing pieces are captures, unmatched appearing ones are spawns
/// (promotion). No incremental board state to corrupt — any FEN resync renders
/// correctly by the same path.
///
/// A captured piece is NOT destroyed: it is offered to the trays (M11), which
/// adopt it and walk it to its owner's side of the table. What ends up in a tray
/// is decided by CapturedMaterial.Lost from the position alone, never by counting
/// captures as they happen — so a resync or a late joiner shows the right trays
/// having seen none of it. See the "Captured pieces" block below.
///
/// Input while seated: ray from the cursor to the board plane picks squares;
/// first click selects an own piece (targets highlight), second click commits —
/// via the controller, which validates with the embedded rules before anything
/// touches the network. A pawn reaching the last rank parks as PendingPromotion
/// until GameHud's picker chooses a piece.
///
/// The same two clicks do two jobs. On our turn they play a move, against the
/// LEGAL targets. On the opponent's they arm a premove, against the permissive
/// PremoveTargets — a different question with a different answer. See
/// CanPremove and ChessGame.PremoveTargets.
///
/// Highlights reuse the board's cell boxes: tint swaps, no extra geometry.
/// </summary>
public sealed class ChessBoardView : Component
{
	/// <summary>Set by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }

	/// <summary>Set by ChessRing at build.</summary>
	[Property] public LocalGameController Controller { get; set; }

	/// <summary>Set by ChessRing at build. Drives the board while a real lichess
	/// game is running at this table (M8).</summary>
	[Property] public LichessGameController Lichess { get; set; }

	/// <summary>Which controller owns the board.
	///
	/// <para>Nothing below ever branches on the source, and it is why M8 needed no
	/// renderer change: everything here reads <see cref="IBoardGame"/> and cannot tell a
	/// lichess game from a local one. The seam was built for exactly this.</para>
	///
	/// <para>The resolution itself now lives on <see cref="BoardGame.Source"/> — the view,
	/// the sounds and the table clock all ask it, so they cannot end up describing
	/// different games. The lichess controller claims the board only once the local player
	/// has opted in at this table; otherwise the local game owns it, unchanged.</para></summary>
	IBoardGame Source => BoardGame.Source( Controller, Lichess );

	/// <summary>The local player may arm a premove right now: a live game they're
	/// seated in, with the BOARD saying the opponent is on move.
	///
	/// <para>Goes through <see cref="IBoardGame"/> like everything else here, because
	/// premove belongs to any game with a clock — see <see cref="IBoardGame.PremoveUci"/>
	/// for why it isn't lichess-only.</para>
	///
	/// <para>Deliberately not <c>!IsMyTurn</c>, which is a stronger claim than it
	/// looks: on lichess it also goes false while our own move is in flight, and the
	/// board doesn't advance until lichess confirms. In that window "not my turn" is
	/// true while the position on screen is still the one BEFORE our move — arming
	/// there would premove a knight that has already left the square it's drawn on,
	/// and it would silently evaporate when it fired.</para></summary>
	bool CanPremove =>
		Source is { Playing: true, Game: { } game, LocalSeat: { } seat }
		&& game.WhiteToMove != ( seat == ChessSeat.White );

	/// <summary>Seconds a piece takes to slide to its new square.</summary>
	[Property] public float MoveSeconds { get; set; } = 0.22f;

	/// <summary>Peak height of the slide arc, as a fraction of a square.</summary>
	[Property] public float MoveArc { get; set; } = 0.35f;

	/// <summary>Seconds a captured piece takes to travel to its owner's tray.
	/// Longer than a move: it is a much longer trip, and the point is that you see
	/// where it went. It runs UNDER the capturing piece's own slide, so a capture
	/// reads as two things happening at once rather than a piece blinking out.</summary>
	[Property] public float CaptureSeconds { get; set; } = 0.45f;

	/// <summary>Arc height for that trip, as a fraction of a square. Taller than a
	/// move's — the piece is being lifted off the board, not shoved across it, and
	/// a flat path would drag it through the frame.</summary>
	[Property] public float CaptureArc { get; set; } = 1.1f;

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
	// An armed premove: purple, because it is the one highlight that shows a move
	// that HASN'T happened and might never. It must not be mistaken for the gold
	// of a live selection or the olive of a move lichess actually played.
	static readonly Color PremoveTint = new( 0.52f, 0.16f, 0.72f );

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
		public float Seconds;
		public float Arc;
	}

	readonly List<Slide> _slides = new();

	// ── The hand carry (M14): the piece rides the terry's hand ──
	//
	// While a seated terry's hand is replaying a move (TerryPose Lifting/Carrying/
	// Dropping), the moved piece is positioned at the HAND BONE each frame instead of
	// running its own slide clock — that is what turns "a hand gesturing near a sliding
	// piece" into "a terry picking up and moving a piece". SeatedTerry reports the carry
	// per frame (it owns which avatar and which pose); this view owns which GameObject
	// that means and what happens when the carry ends.
	//
	// The report is VOLATILE by design — good for a fraction of a second, re-asserted
	// every frame. The abandon rule (a ply landing mid-animation), a hand toggled off, a
	// player standing up: all of them simply stop the reports, and the piece falls back
	// to its slide from wherever the hand left it (a short settle, below). Nothing here
	// has to know WHY a carry ended. The spectator wall is untouched: SpectatorBoard3D
	// never had hands and keeps the plain slide, per the design decision.
	GameObject _carried;       // piece riding a hand right now (the mover; a capture's victim keeps its tray slide)
	GameObject _carriedPrev;   // last frame's, to detect release and hand the slide back
	float _carryStamp = -1f;
	Vector3 _carryWorld;       // world position for the piece's base this frame

	/// <summary>How far below the wrist bone the held piece's base hangs — the piece sits
	/// in the fingers, not impaled on the forearm. Tune in-editor.</summary>
	const float CarryHang = 8f;

	/// <summary>The short settle a released piece plays from wherever the hand left it to
	/// its true square — covers both a finished drop (hand is already over the square, so
	/// this is invisible polish) and an abandoned carry (the piece must not teleport).</summary>
	const float SettleSeconds = 0.18f;

	/// <summary>Called by <see cref="SeatedTerry"/> every frame a seat's hand is replaying
	/// a move on this board. Attacker only: the destination square owns the mover's
	/// GameObject once SyncPieces has applied the FEN, which it always has by the time a
	/// pose is animating (both react to the same ply change).</summary>
	public void ReportHandCarry( in Gambit.Chess.HandPose pose, LobbyPlayer avatar )
	{
		if ( pose.Phase is not ( Gambit.Chess.HandPhase.Lifting
			or Gambit.Chess.HandPhase.Carrying or Gambit.Chess.HandPhase.Dropping ) ) return;
		if ( pose.ToSquare is < 0 or > 63 ) return;
		if ( avatar?.HandBoneWorld() is not { } hand ) return;

		var piece = _pieces[pose.ToSquare];
		if ( piece == null ) return;

		// GRAB ON CONTACT. Gluing the piece to a hand that is still travelling — or that
		// can't reach the square at all (the residual squares the slide finishes) — yanks
		// it backward off its slide toward a hand somewhere else entirely, which is
		// exactly the "completely busted" look the first two-client session reported.
		// Until the hand closes within GrabRadius of the piece, the slide keeps the piece
		// and the hand merely chases it; once held, it stays held for the whole gesture
		// (the hand may then swing wide without dropping it).
		if ( !ReferenceEquals( piece, _carried )
			&& ( piece.WorldPosition - hand ).Length > GrabRadius ) return;

		_carried = piece;
		_carryStamp = Time.Now;
		_carryWorld = hand + Vector3.Down * CarryHang;
	}

	/// <summary>How close the hand must get to a piece before the piece leaves its slide
	/// for the hand. Roughly the hand's own grasp envelope; tune in-editor.</summary>
	const float GrabRadius = 9f;

	// ── Captured pieces ──
	//
	// Each player's losses sit in a tray on their own side of the table. Two rules
	// make this survive things the animation alone would not:
	//
	//  1. **Tray contents are a pure function of the FEN**, never an accumulated
	//     tally. This view rebuilds from the FEN alone and has no history: a
	//     late joiner or a resync starts with _rendered empty and the first
	//     SyncPieces is all-additions. An event-counted tray would be empty for
	//     everyone who didn't watch the whole game — which is most spectators, and
	//     every player at a table that resynced.
	//  2. **The animation is a transient overlay on top of that.** When the diff
	//     happens to have the dying piece's GameObject in hand, the tray adopts it
	//     so it walks over; when it doesn't, the tray just spawns the piece in
	//     place. Same result either way, so nothing depends on having seen it.
	readonly List<(char Ch, GameObject Go)> _trayWhite = new();
	readonly List<(char Ch, GameObject Go)> _trayBlack = new();

	// Pieces that left the board during THIS diff and haven't found a home yet.
	// Not necessarily captures — a resync drops pieces too, which is why these are
	// offered to the trays rather than pushed at them.
	readonly List<(char Ch, GameObject Go)> _captured = new();

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
		// AFTER UpdateInput, never inside it: UpdateInput early-returns on several paths
		// (not your turn, promotion picker up, camera still blending) having already reset
		// _hoverSquare to -1 at the top. Publishing from inside would only ever run on the
		// paths that reach the bottom, so the synced hand would STICK on the last square you
		// hovered the moment the turn flipped away from you.
		PublishHandState();
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
		// The old PiecesView (and every tray piece parented to it) has just gone
		// with the destroy above — drop the dangling handles, or SyncTray reuses
		// GameObjects that no longer exist.
		_trayWhite.Clear();
		_trayBlack.Clear();
		_captured.Clear();
		_slides.Clear();
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
		var fen = Source?.Game?.Fen;
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
				// On a capture the destination is ALSO in `removed` (it held the
				// captured piece); drop it too, or the leftover-removals pass below
				// would destroy the piece we're about to slide onto it.
				removed.Remove( toSquare );

				var piece = _pieces[from];
				_pieces[from] = null;
				_rendered[from] = '\0';

				// The destination may hold a captured piece — hand it to the trays
				// rather than destroy it, so it can walk off the board. Read
				// _rendered[toSquare] BEFORE the overwrite below: it is still the
				// victim's char here, and is the only record of what died.
				if ( _pieces[toSquare] != null )
				{
					_captured.Add( ( _rendered[toSquare], _pieces[toSquare] ) );
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
					Seconds = MoveSeconds,
					Arc = MoveArc,
				} );
			}
		}

		// Leftover removals: captures the pairing pass couldn't match (en passant
		// takes this path, not the one above — the pawn vanishes from a square the
		// mover never lands on), the pawn behind a promotion, and resync artifacts.
		// Offer them all to the trays; SyncTrays decides which were really taken.
		if ( removed != null )
		{
			foreach ( var sq in removed )
			{
				if ( _pieces[sq] != null ) _captured.Add( ( _rendered[sq], _pieces[sq] ) );
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

		SyncTrays( target );
	}

	/// <summary>Reconcile both trays against the position, then dispose of anything
	/// that left the board without belonging in one.</summary>
	void SyncTrays( char[] target )
	{
		SyncTray( target, white: true, _trayWhite );
		SyncTray( target, white: false, _trayBlack );

		// Whatever is left wasn't a capture — a resync dropped it, or it was the
		// pawn consumed by a promotion. Neither belongs in a tray.
		foreach ( var (_, go) in _captured ) go?.Destroy();
		_captured.Clear();
	}

	/// <summary>Bring one player's tray in line with what the FEN says they've lost.
	/// Reuses the pieces already in the tray, adopts the ones that just died, and
	/// spawns whatever is still missing.</summary>
	void SyncTray( char[] target, bool white, List<(char Ch, GameObject Go)> tray )
	{
		var ring = ChessRing.Instance;
		if ( ring == null ) return;

		// The tray IS this list — no history, no tally. See CapturedMaterial.
		var want = CapturedMaterial.Lost( target, white );

		// Resolve a GameObject for each wanted piece, cheapest source first.
		var have = new List<(char Ch, GameObject Go)>( tray );
		var next = new List<(char Ch, GameObject Go)>();
		foreach ( var ch in want )
		{
			int i = have.FindIndex( e => e.Ch == ch && e.Go.IsValid() );
			if ( i >= 0 ) { next.Add( have[i] ); have.RemoveAt( i ); continue; }

			// Just died on the board: adopt the GO so it travels instead of
			// blinking out here and reappearing there.
			int c = _captured.FindIndex( e => e.Ch == ch && e.Go.IsValid() );
			if ( c >= 0 ) { next.Add( _captured[c] ); _captured.RemoveAt( c ); continue; }

			next.Add( ( ch, null ) ); // nothing to adopt — spawned at its slot below
		}

		foreach ( var (_, go) in have ) go?.Destroy();

		tray.Clear();
		for ( int i = 0; i < next.Count && i < CapturedMaterial.MaxSlots; i++ )
		{
			var (ch, go) = next[i];
			var to = ring.TraySlotLocalPosition( white, i );

			if ( go == null || !go.IsValid() )
			{
				// A joiner, or a resync: no journey to show, so don't invent one.
				go = SpawnTrayPiece( ch );
				if ( go == null ) continue;
				go.LocalPosition = to;
			}
			// Already on its way to this exact slot — leave it alone. Without this
			// check, the NEXT move restarts a still-travelling piece's slide from
			// wherever it had got to, which resets its arc and its easing: in bullet
			// a captured piece would visibly stutter its way to the tray, and a slot
			// away from the board is a long enough trip to still be mid-flight when
			// the next move lands.
			else if ( _slides.FindIndex( s => s.Piece == go && ( s.To - to ).LengthSquared <= 0.01f ) < 0
				&& ( go.LocalPosition - to ).LengthSquared > 0.01f )
			{
				// Either it has just been taken, or a higher-value capture pushed
				// it along a slot. Same trip, same easing.
				_slides.RemoveAll( s => s.Piece == go );
				_slides.Add( new Slide
				{
					Piece = go,
					From = go.LocalPosition,
					To = to,
					Age = 0f,
					Seconds = CaptureSeconds,
					Arc = CaptureArc,
				} );
			}

			tray.Add( ( ch, go ) );
		}
	}

	GameObject SpawnTrayPiece( char fenChar )
	{
		var ring = ChessRing.Instance;
		if ( ring == null ) return null;

		bool white = char.IsUpper( fenChar );
		// Facing matches the owner's board pieces — a tray is pieces set aside, not
		// a scoreboard, so they keep looking the way they did.
		return ChessSetBuilder.BuildPiece( _piecesRoot, PieceTypeOf( fenChar ), white, ring.PieceScale, yaw: white ? 0f : 180f );
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
		// ── The hand carry, resolved before any slide advances ──
		bool carryFresh = _carried.IsValid() && Time.Now - _carryStamp < 0.15f;

		// Release: the carry ended (or switched pieces) since last frame. Hand the piece
		// back to its slide FROM WHERE IT IS — a finished drop settles invisibly onto its
		// square; an abandoned one glides there instead of teleporting.
		if ( _carriedPrev.IsValid() && ( !carryFresh || !ReferenceEquals( _carried, _carriedPrev ) ) )
		{
			var s = _slides.Find( x => x.Piece == _carriedPrev );
			if ( s != null )
			{
				s.From = _carriedPrev.LocalPosition;
				s.Age = 0f;
				s.Seconds = SettleSeconds;
				s.Arc = 0f;
			}
			_carriedPrev = null;
		}

		if ( carryFresh )
		{
			_carriedPrev = _carried;
			_carried.WorldPosition = _carryWorld;
		}
		else
		{
			_carried = null;
		}

		for ( int i = _slides.Count - 1; i >= 0; i-- )
		{
			var slide = _slides[i];
			if ( !slide.Piece.IsValid() )
			{
				_slides.RemoveAt( i );
				continue;
			}

			// A carried piece's slide neither ages nor writes position — the hand owns
			// the piece, and the slide is only kept alive as the fallback the release
			// path above re-arms.
			if ( carryFresh && ReferenceEquals( slide.Piece, _carried ) )
				continue;

			slide.Age += Time.Delta;
			// Duration and arc ride the slide, not the component: a move across a
			// square and a captured piece's trip to the tray are different journeys.
			float seconds = slide.Seconds > 0f ? slide.Seconds : MoveSeconds;
			float t = Math.Clamp( slide.Age / seconds, 0f, 1f );
			float eased = 1f - MathF.Pow( 1f - t, 3f );

			var pos = Vector3.Lerp( slide.From, slide.To, eased );
			// Gentle arc so pieces hop rather than plough through each other
			float cell = ChessRing.Instance?.CellWorldSize ?? 5f;
			pos.z += MathF.Sin( t * MathF.PI ) * cell * slide.Arc;
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

		// Only the seated local player interacts, only while the game is live, and
		// not while the promotion picker is up. On their own turn they move; on the
		// opponent's turn they may arm a premove, and nothing else.
		var source = Source;
		bool premoving = CanPremove;

		// The turn flipped under a live selection: the squares lit up are answers to
		// a question nobody is asking any more. PremoveTargets is deliberately
		// permissive, so leaving them up once it IS your turn would paint illegal
		// moves green and then refuse them on click.
		if ( Selected != null && _targetsArePremove != premoving )
			ClearSelection();

		if ( source == null || ( !source.IsMyTurn && !premoving ) || PendingPromotion != null )
		{
			// Game ended or reset from under us (resign/abandon produce no board
			// diff) — drop any half-finished input state.
			if ( !( source?.Playing ?? false ) )
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

		var game = source.Game;
		string clicked = SquareName( square );
		bool ownPiece = IsOwnPiece( _rendered[square] );

		// Any click cancels an armed premove. It is the only way to take one back,
		// and re-arming costs the same two clicks that armed it — so a click that
		// goes on to arm a new one just replaces it.
		source.ClearPremove();

		if ( Selected == null )
		{
			if ( ownPiece ) Select( clicked, premoving );
		}
		else if ( clicked == Selected )
		{
			ClearSelection();
		}
		else if ( _targets.Contains( clicked ) )
		{
			if ( premoving )
			{
				// Promotion is decided here rather than by the picker: the picker
				// is a modal on a board it isn't your turn to touch, and a premoved
				// pawn may never reach the last rank at all. Queen, silently — the
				// same default the rest of the world's premoves use, and the piece
				// you want in all but a rare underpromotion.
				string promo = game.IsPromotion( Selected, clicked ) ? "q" : "";
				source.SetPremove( Selected + clicked + promo );
				ClearSelection();
			}
			else if ( game.IsPromotion( Selected, clicked ) )
			{
				// Park until GameHud's picker calls ChoosePromotion
				PendingPromotion = (Selected, clicked);
				ClearSelection();
			}
			else
			{
				source.TryMakeMove( Selected + clicked );
				ClearSelection();
			}
		}
		else if ( ownPiece )
		{
			Select( clicked, premoving ); // switch selection
		}
		else
		{
			ClearSelection();
		}
	}

	/// <summary>
	/// Publish this player's hover + selection so the OTHER clients can float their terry's
	/// hand over the square they're thinking about (M13).
	///
	/// <para><b><c>_hoverSquare</c> already means exactly the right thing</b>, which is why
	/// there is no new predicate here. It is only assigned once UpdateInput is past its own
	/// gate — the player's turn or a premove, no promotion picker up, the camera settled —
	/// so "hovered" already means "a square this player can act on". Everywhere else it is
	/// −1, and −1 packs to "no hand on the board".</para>
	///
	/// <para>Change-gated: one comparison, so a mouse moving WITHIN a square costs nothing
	/// and a still cursor costs nothing at all. [Sync] diffs anyway — this matches the house
	/// style (PaintHighlights' hash gate), and it is strictly better than that one, because
	/// a hash gate skips work while this skips a network write.</para></summary>
	void PublishHandState()
	{
		// MY table only. There is one ChessBoardView per station and they ALL run OnUpdate,
		// so without this every other table in the ring also writes LobbyPlayer.Local's
		// HandState — and since UpdateInput bails early for a table you aren't seated at,
		// they'd all publish "no hand". Whichever view updated last would win, so the hand
		// would flicker or vanish depending on component order. The change gate can't save
		// it: the two writers genuinely disagree, so it passes both ways.
		if ( ChessStation.Active != Station ) return;
		if ( LobbyPlayer.Local is not { } me ) return;

		int packed = LobbyPlayer.PackHand( _hoverSquare, SquareIndexOf( Selected ) );
		if ( me.HandState != packed ) me.HandState = packed;
	}

	/// <summary>Square name ("e4") → the rank*8+file index everything else here uses, or −1.</summary>
	static int SquareIndexOf( string square )
	{
		if ( square is not { Length: >= 2 } ) return -1;
		int file = square[0] - 'a';
		int rank = square[1] - '1';
		if ( file is < 0 or > 7 || rank is < 0 or > 7 ) return -1;
		return rank * 8 + file;
	}

	/// <summary>Select a piece and work out where it may go.
	///
	/// <para><paramref name="premoving"/> switches which rules answer that: on your
	/// turn, the legal moves; on the opponent's, the permissive premove mobility
	/// (see <see cref="ChessGame.PremoveTargets"/>), because the position a premove
	/// is aimed at doesn't exist yet.</para></summary>
	void Select( string square, bool premoving )
	{
		Selected = square;
		_targetsArePremove = premoving;
		_targets = premoving
			? Source.Game.PremoveTargets( square )
			: Source.Game.LegalTargets( square );
	}

	/// <summary>Which rules produced <see cref="_targets"/>. Kept so the selection
	/// can be dropped when the turn flips out from under it.</summary>
	bool _targetsArePremove;

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
		Source.TryMakeMove( pending.From + pending.To + piece );
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

		var source = Source;
		var game = source?.Game;
		string lastMove = game != null ? game.LastMoveUci ?? source.LastMoveUci : null;
		string checkedKing = game?.CheckedKingSquare;
		// Targets light up on your turn AND while arming a premove — in premove
		// mode they're PremoveTargets, so the green means "you can aim here", not
		// "this is legal".
		bool interactive = ( source?.IsMyTurn == true || CanPremove ) && PendingPromotion == null;
		// From/To derived here rather than on the seam: the controllers hold ONE value
		// (the whole premove), and two halves of it would be two things to keep in step.
		string premove = source?.PremoveUci;
		string premoveFrom = premove is { Length: >= 4 } ? premove[..2] : null;
		string premoveTo = premove is { Length: >= 4 } ? premove[2..4] : null;

		// Repaint only when an input into the tint decision changed — idle
		// tables (and non-hovered frames) skip the 64-cell walk entirely. While
		// cells are still binding, repaint regardless so latecomers get tinted.
		// The premove squares are part of the tint decision, so they belong in the
		// hash: leave them out and an armed premove paints only if something else
		// happened to change in the same frame — which, while the opponent thinks,
		// nothing does.
		int hash = HashCode.Combine( lastMove, checkedKing, interactive,
			Selected, _hoverSquare, _targets, premoveFrom, premoveTo );
		if ( !binding && hash == _lastPaintHash ) return;
		_lastPaintHash = hash;

		string lastFrom = null, lastTo = null;
		if ( lastMove is { Length: >= 4 } )
		{
			lastFrom = lastMove[..2];
			lastTo = lastMove[2..4];
		}

		// _hoverSquare is only set when UpdateInput ran to completion — the local
		// player's own turn, or while they can arm a premove — so the hover glow
		// naturally appears only when the player can act.
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
			else if ( premoveFrom == name || premoveTo == name )
				// Above check and last-move: while a premove is armed it's the most
				// important thing on the board, because it's the thing about to
				// happen without you.
				tint = PremoveTint;
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
		fenChar != '\0' && Source?.LocalSeat is { } seat
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
