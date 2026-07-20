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

		/// <summary>Seconds this slide WAITS at its origin for a terry's hand to come and
		/// pick the piece up — the remote-client giveaway was the piece departing the
		/// instant the FEN landed while the hand was still a second away. Zero for slides
		/// nobody is going to perform (no seated terry, tray walks, resyncs); expiring
		/// degrades to the plain slide, so a missing hand costs nothing but the wait.</summary>
		public float HoldForHand;

		/// <summary>This slide's destination is a BOARD square (the pairing pass made it)
		/// rather than a tray. Board slides are reality-lagging — a new diff fast-forwards
		/// them (see SyncPieces); tray trips are pure cosmetics and play out.</summary>
		public bool ToBoard;
	}

	/// <summary>Wall seconds until the gesturing hand is over the from-square: the
	/// Reaching + Lifting deadlines through the speed slider, plus a small breath. The
	/// piece's wait (HoldForHand) is this by construction — see the slide comment.</summary>
	static float HandArrivalSeconds()
	{
		float speed = Gambit.Chess.TerryPose.SpeedScale <= 0f ? 1f : Gambit.Chess.TerryPose.SpeedScale;
		return ( Gambit.Chess.TerryPose.ReachTime + Gambit.Chess.TerryPose.LiftTime ) / speed + 0.1f;
	}

	/// <summary>Is a seated terry going to perform the moving side's move at this table?
	/// Mirrors the gate SeatedTerry itself animates under.</summary>
	bool HandWillPerform( char moverPiece )
	{
		if ( ChessRing.Instance is not { TerrySeated: true } ) return false;
		if ( !SeatedHandSpikes.HandsOn ) return false;
		if ( Station is not { } st ) return false;
		return st.SeatTaken( char.IsUpper( moverPiece ) ? ChessSeat.White : ChessSeat.Black );
	}

	readonly List<Slide> _slides = new();

	/// <summary>Destroy every piece and tray GameObject and clear all in-flight render state, so the
	/// next <see cref="SyncPieces"/> rebuilds all 64 from scratch (M16 play-mode change). Unlike
	/// <see cref="EnsureBoard"/>, which drops the whole PiecesView parent, this keeps the root and
	/// destroys the pieces individually — a mode switch shouldn't tear down and re-resolve the board.
	/// Resetting <c>_lastFen</c>/<c>_everSynced</c> is what makes the FEN early-return fall through to
	/// an all-additions respawn.</summary>
	void ResetPieces()
	{
		for ( int i = 0; i < 64; i++ )
		{
			_pieces[i]?.Destroy();
			_pieces[i] = null;
			_rendered[i] = '\0';
		}
		foreach ( var t in _trayWhite ) t.Go?.Destroy();
		foreach ( var t in _trayBlack ) t.Go?.Destroy();
		foreach ( var c in _captured ) c.Go?.Destroy();
		_trayWhite.Clear();
		_trayBlack.Clear();
		_captured.Clear();
		_slides.Clear();
		_performedWhite = null;
		_performedBlack = null;
		_lastFen = null;
		_everSynced = false;
	}

	// ── The performed piece (M14): the WRIST is a child of the PIECE ──
	//
	// ONE clock. The piece runs its hold-then-slide (this view owns it, and the hold is
	// derived from the hand's approach deadlines); the seated terry's wrist is DERIVED
	// from the live piece GameObject every frame — approaching while the piece holds,
	// glued above it while it slides, easing home after it lands. There is no reverse
	// channel any more: the old carry layer (the piece riding the hand bone, the grab
	// radius, piece-led placement, the release settle) was two independent authorities
	// glued together, and every timing bug in the look pass was that glue tearing.
	// Deleted, not tuned (owner decision, 2026-07-19). The spectator wall is untouched:
	// SpectatorBoard3D never had hands and keeps the plain slide.
	GameObject _performedWhite;
	GameObject _performedBlack;

	/// <summary>The piece this colour's seated hand should be riding right now, or null.
	/// Set when a performed slide is created; read live by <see cref="SeatedTerry"/> each
	/// frame and fed to the hand driver, which derives the wrist from its position. Never
	/// cleared on a schedule — the hand's own pose clock decides when it stops caring, and
	/// a destroyed piece (promotion, resync) reads as null via IsValid.</summary>
	public GameObject PerformedPiece( bool white )
	{
		var go = white ? _performedWhite : _performedBlack;
		return go.IsValid() ? go : null;
	}

	/// <summary>The piece GameObject currently rendered on <paramref name="square"/> (rank*8+file),
	/// or null. DEBUG-only seam for <c>gambit_terry_scholars</c>, which drives a fake gesture demo
	/// on an idle board: it needs the real piece meshes so the hand rides a genuine piece (the
	/// bounds-top grasp path), and this hands them over without exposing the internal array.
	/// The demo tracks and restores positions itself — this view stays FEN-authoritative.</summary>
	public GameObject PieceAt( int square )
	{
		if ( square < 0 || square >= 64 ) return null;
		var go = _pieces[square];
		return go.IsValid() ? go : null;
	}

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
		_performedWhite = null;
		_performedBlack = null;
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
	// The render mode + glyph up-direction the pieces on the board were built in (M16). A play-mode
	// change flips ChessSetBuilder.FlatMode, and sitting down / switching seats changes which way
	// the flat glyphs should read — neither changes the FEN, so the reference check below would
	// never rebuild. This guard forces a full respawn when either changes.
	bool _renderedFlat;
	float _renderedUpYaw;

	/// <summary>Which way flat glyphs on THIS table should read (M16). If the local player is seated
	/// here, orient to their side (White seat +X = yaw 0, Black seat −X = yaw 180) so they read their
	/// own board upright — matching their per-seat top-down camera. Otherwise (spectating/roaming)
	/// White-up. Only meaningful in flat mode.</summary>
	float LocalSeatUpYaw() =>
		ChessStation.Active == Station && Station != null
			? ( ChessStation.ActiveSeat == ChessSeat.White ? 0f : 180f )
			: 0f;

	void SyncPieces()
	{
		// Play-mode change or a seat change (M16): the FEN is unchanged, so destroy every piece and
		// let the diff below respawn all 64 as flat glyphs (in the right orientation) or 3D bodies.
		// Keyed on the render-relevant bits, not SettingsModel.SettingsVersion, so a brightness-slider
		// drag doesn't thrash the whole board.
		float upYaw = ChessSetBuilder.FlatMode ? LocalSeatUpYaw() : 0f;
		if ( ChessSetBuilder.FlatMode != _renderedFlat || upYaw != _renderedUpYaw )
		{
			_renderedFlat = ChessSetBuilder.FlatMode;
			_renderedUpYaw = upYaw;
			ResetPieces();
		}
		// Stamp the up-direction the builder reads for every SpawnPiece below (per-board, so this
		// view's value can't be clobbered by another board building in the same frame — each view
		// stamps-then-spawns synchronously).
		ChessSetBuilder.FlatUpYaw = upYaw;

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

		// ── Jump to reality: a NEW diff obsoletes every board slide still playing. ──
		// The hand-hold pins a mover at its ORIGIN for ~0.4s, and a premove replies
		// within a few frames of the move it was armed against — so without this, the
		// reply's diff found the just-moved piece still sitting on its origin square and
		// (on a premove CAPTURE) adopted it into the tray from there: the move never
		// rendered on any machine. Snap held/in-flight board slides to their
		// destinations BEFORE diffing forward — the new pairing then reads true
		// positions for its Froms, and a captured mover walks to the tray from the
		// square it actually reached. Tray trips keep playing: they lag nothing.
		for ( int i = _slides.Count - 1; i >= 0; i-- )
		{
			var s = _slides[i];
			if ( !s.ToBoard ) continue;
			if ( s.Piece.IsValid() ) s.Piece.LocalPosition = s.To;
			_slides.RemoveAt( i );
		}

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
				bool performed = HandWillPerform( c );
				_slides.Add( new Slide
				{
					Piece = piece,
					From = piece.LocalPosition,
					To = SquareLocal( toSquare ),
					Age = 0f,
					Seconds = MoveSeconds,
					Arc = MoveArc,
					// DERIVED from the gesture timeline, never a free knob: the hold only
					// exists so the hand can arrive, and the hand arrives on the Reaching/
					// Lifting deadlines — so the piece waits exactly that long (plus a
					// breath) and not an instant more. (The old fixed 1.2s was the
					// "a far capture takes over 2 seconds" bug.)
					HoldForHand = performed ? HandArrivalSeconds() : 0f,
					ToBoard = true,
				} );
				// The mover this colour's hand should ride — the wrist derives from this
				// GameObject live (see the performed-piece block above).
				if ( performed )
				{
					if ( char.IsUpper( c ) ) _performedWhite = piece;
					else _performedBlack = piece;
				}
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

		// A performed piece that just LEFT the board (captured by the reply — the premove
		// case — or consumed by a promotion) must stop being anyone's hand target: the
		// glued wrist would follow it into the tray otherwise. The gesture then finishes
		// over the true squares via the driver's fallback path.
		if ( _performedWhite.IsValid() && System.Array.IndexOf( _pieces, _performedWhite ) < 0 )
			_performedWhite = null;
		if ( _performedBlack.IsValid() && System.Array.IndexOf( _pieces, _performedBlack ) < 0 )
			_performedBlack = null;

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
		for ( int i = _slides.Count - 1; i >= 0; i-- )
		{
			var slide = _slides[i];
			if ( !slide.Piece.IsValid() )
			{
				_slides.RemoveAt( i );
				continue;
			}

			// Waiting for the hand: the piece sits on its origin square while the hand's
			// approach deadlines run (HoldForHand is derived from exactly them), then the
			// slide plays and the glued hand rides it. No grab, no early release — the
			// piece is the authority and the hand follows it, never the reverse.
			if ( slide.HoldForHand > 0f )
			{
				slide.HoldForHand -= Time.Delta;
				slide.Piece.LocalPosition = slide.From;
				continue;
			}

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
			Selected, _hoverSquare, _targets, premoveFrom,
			// FlatMode belongs in the hash so a play-mode change (M16) forces one repaint —
			// that is what swaps the base squares to the 2D cream/brown palette and back.
			HashCode.Combine( premoveTo, ChessSetBuilder.FlatMode ) );
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
				// Restore the square's own checker color — the 2D cream/brown palette in flat
				// mode (M16), the neutral pair otherwise.
				tint = ChessSetBuilder.FlatMode
					? ( light ? ChessRing.Light2D : ChessRing.Dark2D )
					: ( light ? ChessRing.LightSquare : ChessRing.DarkSquare );

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
