using System.Collections.Generic;
using Gambit.Api;
using Gambit.Chess;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// Solve lichess puzzles at any chess board (PLAN.md M5). Pure request/response —
/// <c>GET /api/puzzle/{daily|next|id}</c> hands us the source game's PGN, the puzzle's
/// <c>initialPly</c>, and the full UCI <b>solution</b> (embedded for LOCAL validation).
/// There is no endpoint to submit a solve, so solving here NEVER affects the player's
/// lichess puzzle rating — the HUD says so.
///
/// <para>Entirely client-local (like <see cref="LichessPlayController"/> in-sbox play):
/// one seated player works a puzzle on their own board, nothing is <c>[Sync]</c>. It
/// implements <see cref="IBoardGame"/> so <see cref="ChessBoardView"/> renders and drives
/// it with zero per-source branching — the solver clicks moves exactly as in a real game.</para>
///
/// <para>lichess convention (confirmed against a live <c>/api/puzzle/daily</c>): the
/// position is the END of <c>game.pgn</c> — its last move is the opponent's setup move,
/// already played — and it is the SOLVER's turn. So <c>solution[0]</c> is the solver's
/// first move, <c>solution[1]</c> the opponent's reply (auto-played), <c>solution[2]</c>
/// the solver's next, and so on — the solver plays the EVEN indices.</para>
/// </summary>
public sealed class PuzzleController : Component, IBoardGame
{
	/// <summary>Seat/occupancy source for this table. Set by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }

	public static PuzzleController For( ChessStation station ) =>
		station?.Components.Get<PuzzleController>();

	enum Phase { Idle, Loading, Solving, Solved, Revealed }
	Phase _phase = Phase.Idle;

	/// <summary>The puzzle UI owns the board (drives the HUD panel + ChessBoardView).</summary>
	public bool Active => _phase != Phase.Idle;

	/// <summary>Solver is working the position — accept board input.</summary>
	public bool Solving => _phase == Phase.Solving;

	public bool Solved => _phase == Phase.Solved;
	public bool WasRevealed => _phase == Phase.Revealed;
	public bool Finished => _phase is Phase.Solved or Phase.Revealed;

	// ── HUD-facing ──
	public string PuzzleId { get; private set; }
	public int Rating { get; private set; }
	public string Themes { get; private set; }
	public string StatusText { get; private set; }
	public string Error { get; private set; }
	/// <summary>Reset to 0 whenever the solver plays a legal-but-wrong move, so the HUD
	/// can flash "not the move" briefly.</summary>
	public RealTimeSince SinceWrong { get; private set; } = 999f;
	public bool ShowWrong => SinceWrong < 2.5f && _phase == Phase.Solving;
	public string PuzzleUrl => string.IsNullOrEmpty( PuzzleId ) ? null : $"https://lichess.org/training/{PuzzleId}";

	// ── IBoardGame ──
	ChessGame _game;
	public ChessGame Game => _game;
	public bool Playing => _phase == Phase.Solving;
	public bool IsMyTurn =>
		_phase == Phase.Solving && _game != null && _pendingReply == null
		&& _game.WhiteToMove == ( _solverColor == ChessSeat.White );
	public ChessSeat? LocalSeat => Active ? _solverColor : null;
	public string LastMoveUci => _game?.LastMoveUci;

	public bool TryMakeMove( string uci )
	{
		if ( !IsMyTurn || uci == null ) return false;

		string expected = Index( _solutionIndex );
		if ( expected == null ) return false;

		if ( !MovesEqual( uci, expected ) )
		{
			// A legal move that isn't the puzzle line — let the solver try again (lichess
			// behaviour). Return false so the view snaps the piece back (the game is
			// unchanged), and flash the hint.
			StatusText = "✗ Not the move — try again.";
			SinceWrong = 0f;
			return false;
		}

		ApplyMove( expected );            // the solver's move
		_solutionIndex++;

		if ( _solutionIndex >= _solution.Count )
		{
			_phase = Phase.Solved;
			StatusText = "✓ Solved!";
			return true;
		}

		// Queue the opponent's reply — played after a short beat so it reads as a move.
		_pendingReply = Index( _solutionIndex );
		_replyDue = 0.45f;
		StatusText = "Good — now the reply…";
		return true;
	}

	// ── Internals ──
	List<string> _solution = new();
	int _solutionIndex;               // next expected move in the solution
	ChessSeat _solverColor;
	string _startFen;                 // position after the opponent's setup move (for Retry)
	bool _loading;

	string _pendingReply;             // opponent move waiting on the beat timer
	RealTimeUntil _replyDue;

	RealTimeUntil _revealStep;        // paces the auto-play during a reveal

	static string ColorWord( ChessSeat s ) => s == ChessSeat.White ? "white" : "black";

	ChessSeat? LocalSeatNow =>
		ChessStation.Active == Station && Station != null ? ChessStation.ActiveSeat : null;

	protected override void OnUpdate()
	{
		if ( _phase == Phase.Solving && _pendingReply != null && _replyDue )
		{
			ApplyMove( _pendingReply );
			_pendingReply = null;
			_solutionIndex++;
			StatusText = "Your move.";
		}

		if ( _phase == Phase.Revealed && _solutionIndex < _solution.Count && _revealStep )
		{
			ApplyMove( Index( _solutionIndex ) );
			_solutionIndex++;
			_revealStep = 0.6f;
			if ( _solutionIndex >= _solution.Count )
				StatusText = "Solution shown — try the next one.";
		}
	}

	// ── Starting ──

	/// <summary>Load the daily puzzle (same for everyone, no auth).</summary>
	public void StartDaily() => Load( () => LichessApi.GetPuzzleDaily() );

	/// <summary>Load the next puzzle. When signed in we send the token so lichess picks a
	/// puzzle matched to your rating (needs the puzzle:read scope) — but a token without
	/// that scope 403s, so we fall back to an unauthenticated random puzzle. Note: there is
	/// NO lichess API to submit a solve, so solving never changes your puzzle rating either
	/// way — puzzle:read only affects which puzzle you're handed.</summary>
	public void StartNext() => Load( NextRequest );

	async System.Threading.Tasks.Task<LichessApi.Result> NextRequest()
	{
		if ( LichessAuth.SignedIn )
		{
			var res = await LichessApi.GetPuzzleNext( LichessAuth.Token ); // rating-matched
			if ( res.Ok || res.Status != 403 ) return res;
			// 403 = the token lacks puzzle:read → fall back to a public random puzzle.
			Log.Info( "[Gambit] token has no puzzle:read scope — serving a random puzzle. Re-create the token to get rating-matched puzzles." );
		}
		return await LichessApi.GetPuzzleNext(); // unauthenticated random
	}

	async void Load( System.Func<System.Threading.Tasks.Task<LichessApi.Result>> request )
	{
		if ( _loading ) return;
		if ( LocalSeatNow is null ) { Error = "Sit at a board first."; return; }

		_loading = true;
		_phase = Phase.Loading;
		Error = null;
		StatusText = "Loading puzzle…";

		try
		{
			var res = await request();
			if ( !res.Ok ) { Fail( res.Error ?? "Couldn't fetch a puzzle." ); return; }

			var p = LichessApi.Deserialize<LichessPuzzleResponse>( res.Body );
			if ( !Build( p ) ) { Fail( "That puzzle didn't parse — try another." ); return; }
		}
		finally
		{
			_loading = false;
		}
	}

	bool Build( LichessPuzzleResponse p )
	{
		if ( p?.game?.pgn == null || p.puzzle?.solution == null || p.puzzle.solution.Count == 0 )
			return false;

		// The puzzle position is the END of game.pgn — its last move IS the opponent's
		// setup move (already played), and initialPly is the 0-based index of that move, so
		// the position is after initialPly+1 half-moves. From here the SOLVER moves first:
		// solution[0] is the solver's move, solution[1] the opponent's reply, and so on
		// (solver = even indices). Confirmed against a live /api/puzzle/daily.
		if ( !ChessGame.TryFromPgnAtPly( p.game.pgn, p.puzzle.initialPly + 1, out var pos ) )
			return false;

		_game = pos;
		_startFen = pos.Fen;
		_solution = p.puzzle.solution;
		_solutionIndex = 0;
		_solverColor = pos.WhiteToMove ? ChessSeat.White : ChessSeat.Black;
		PuzzleId = p.puzzle.id;
		Rating = p.puzzle.rating;
		Themes = p.puzzle.themes is { Count: > 0 } ? string.Join( ", ", p.puzzle.themes ) : null;
		_pendingReply = null;

		// A one-move puzzle would already be over — guard the edge.
		if ( _solutionIndex >= _solution.Count )
		{
			_phase = Phase.Solved;
			StatusText = "✓ Solved!";
		}
		else
		{
			_phase = Phase.Solving;
			StatusText = $"Your move — you're {ColorWord( _solverColor )}. Find the best line.";
		}

		// Orient the player to the side they're solving, like lichess flips the board.
		if ( LocalSeatNow is { } seat && seat != _solverColor )
			LobbyPlayer.Local?.JoinLichessSide( _solverColor );

		return true;
	}

	void Fail( string message )
	{
		Error = message;
		StatusText = null;
		_phase = Phase.Idle;
	}

	// ── Retry / reveal / close ──

	/// <summary>Restart the current puzzle from the beginning.</summary>
	public void Retry()
	{
		if ( _startFen == null || !ChessGame.TryFromFen( _startFen, out var pos ) ) return;
		_game = pos;
		_solutionIndex = 1;
		_pendingReply = null;
		_phase = Phase.Solving;
		Error = null;
		StatusText = $"Retry — you're {ColorWord( _solverColor )}.";
	}

	/// <summary>Give up and watch the solution auto-play from the current point.</summary>
	public void Reveal()
	{
		if ( _phase != Phase.Solving ) return;
		_pendingReply = null;
		_phase = Phase.Revealed;
		_revealStep = 0.2f;
		StatusText = "Showing the solution…";
	}

	/// <summary>Leave puzzle mode — back to the normal board panel.</summary>
	public void Close()
	{
		_phase = Phase.Idle;
		_game = null;
		_solution = new();
		_solutionIndex = 0;
		_pendingReply = null;
		PuzzleId = null;
		Themes = null;
		StatusText = null;
		Error = null;
	}

	/// <summary>Reset when the solver stands up / leaves the board.</summary>
	public void LeaveSeat()
	{
		if ( _phase != Phase.Idle ) Close();
	}

	// ── Move application (+ sound, M6) ──

	void ApplyMove( string uci )
	{
		if ( _game == null ) return;
		string before = _game.Fen;
		if ( !_game.ApplyUci( uci ) ) return;
		PlayMoveSound( before, _game.Fen );
	}

	/// <summary>Puzzle plays on the solver's own engaged board, so 2D sounds: pop on a
	/// capture, tick/tock by the side that just moved (M6 sound mapping).</summary>
	static void PlayMoveSound( string before, string after )
	{
		if ( before == null || after == null ) return;
		if ( CountPieces( before ) != CountPieces( after ) ) { Audio.SoundPlayer.PlayPop(); return; }
		if ( after.Contains( " b " ) ) Audio.SoundPlayer.PlayTick(); // white just moved
		else Audio.SoundPlayer.PlayTock();
	}

	static int CountPieces( string fen )
	{
		int n = 0;
		int end = fen.IndexOf( ' ' );
		if ( end < 0 ) end = fen.Length;
		for ( int i = 0; i < end; i++ )
			if ( char.IsLetter( fen[i] ) ) n++;
		return n;
	}

	string Index( int i ) => i >= 0 && i < _solution.Count ? _solution[i] : null;

	/// <summary>Compare two UCI moves, tolerating a missing promotion char (a bare
	/// pawn push to the last rank defaults to queen, matching lichess's UCI).</summary>
	static bool MovesEqual( string a, string b )
	{
		if ( a == null || b == null ) return false;
		a = a.ToLowerInvariant();
		b = b.ToLowerInvariant();
		if ( a == b ) return true;
		// "e7e8" vs "e7e8q": treat a 4-char move as the queen promotion of the 5-char one.
		if ( a.Length == 4 && b.Length == 5 && b[4] == 'q' && a == b[..4] ) return true;
		if ( b.Length == 4 && a.Length == 5 && a[4] == 'q' && b == a[..4] ) return true;
		return false;
	}
}
