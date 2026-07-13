using System.Collections.Generic;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Chess;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// In-sbox lichess play (PLAN.md M4), driven by <b>polling</b> because incremental
/// ndjson streaming is unavailable under s&amp;box <c>Http</c> (risk 1). The
/// signed-in player sits at a board, challenges a lichess user (or Stockfish) to a
/// casual Rapid 10+0 game, and plays it on the sbox board; the opponent plays in a
/// browser / on lichess. Each poll of <c>GET /api/account/playing</c> returns the
/// game's current FEN + whose turn it is; moves out go through
/// <c>POST /api/board/game/{id}/move/{uci}</c> (an ordinary short request). This is
/// fine for Rapid/Classical (moves are slow); it can't do bullet — and lichess bars
/// bullet on the Board API anyway.
///
/// <para>Purely local: this client polls with <b>its own</b> token and renders on
/// its own board. Nothing here is <c>[Sync]</c> (the token must never cross the
/// wire, D3), so a third client can't yet spectate an in-sbox lichess game — that
/// needs a host-folded FEN relay (deferred, PLAN.md M4). One instance per station;
/// only the instance on the client that actually challenged leaves
/// <see cref="PlayPhase.Idle"/>, so every other station/client no-ops in OnUpdate.</para>
///
/// <para>Implements <see cref="IBoardGame"/> so <see cref="ChessBoardView"/> renders
/// and drives it exactly like the local game.</para>
/// </summary>
public sealed class LichessPlayController : Component, IBoardGame
{
	/// <summary>Seat/occupancy source for this table. Set by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }

	public static LichessPlayController For( ChessStation station ) =>
		station?.Components.Get<LichessPlayController>();

	enum PlayPhase { Idle, Challenging, Playing, Over }
	PlayPhase _phase = PlayPhase.Idle;

	/// <summary>A challenge is out, waiting for the opponent to accept.</summary>
	public bool Challenging => _phase == PlayPhase.Challenging;

	/// <summary>A game is live on the board right now (IBoardGame).</summary>
	public bool Playing => _phase == PlayPhase.Playing;

	/// <summary>The game finished and its result is on display.</summary>
	public bool Over => _phase == PlayPhase.Over;

	/// <summary>This controller is doing something lichess-play related (drives the
	/// HUD to show the play panel instead of the idle/link panel).</summary>
	public bool Active => _phase != PlayPhase.Idle;

	// ── IBoardGame ──

	public ChessGame Game { get; private set; }
	public bool IsMyTurn { get; private set; }
	public ChessSeat? LocalSeat => _phase is PlayPhase.Playing or PlayPhase.Over ? _myColor : null;
	public string LastMoveUci => Game?.LastMoveUci ?? _lastMoveUci;

	public bool TryMakeMove( string uci )
	{
		if ( _phase != PlayPhase.Playing || !IsMyTurn || _moveInFlight || Game == null ) return false;
		if ( !Game.ApplyUci( uci ) ) return false; // optimistic local apply; lichess is the authority

		IsMyTurn = false;
		_lastMoveUci = uci;
		PostMove( uci );
		return true;
	}

	// ── HUD-facing state ──

	public string OpponentName { get; private set; }
	public int MyClockSeconds { get; private set; }
	public string StatusText { get; private set; }
	public string OverText { get; private set; }
	public string Error => _error;
	public string GameUrl => string.IsNullOrEmpty( _gameId ) ? null : $"https://lichess.org/{_gameId}";

	/// <summary>This game was started as an open challenge vs an anonymous browser
	/// (drives the "share this / take your seat" HUD instead of "waiting to accept").</summary>
	public bool IsOpenGame { get; private set; }

	/// <summary>Colour-pinned URL the player opens once in a browser to take their
	/// own seat (lichess has no API to seat yourself in an open challenge).</summary>
	public string SeatUrl { get; private set; }

	/// <summary>Colour-pinned URL to hand the anonymous opponent's browser.</summary>
	public string ShareUrl { get; private set; }

	public RealTimeSince SinceCopied { get; private set; } = 999f;

	// ── Internals ──

	ChessSeat _myColor;
	string _gameId;
	string _challengeId;
	string _expectOpponent;   // username we challenged (matches the nowPlaying game)
	bool _expectAi;
	string _lastMoveUci;
	string _error;

	RealTimeSince _sincePoll;
	bool _polling;
	bool _moveInFlight;

	const float PollInterval = 1.5f; // ~lichess-friendly; a full-minute 429 back-off would flag-loss anyway

	ChessSeat? LocalSeatNow =>
		ChessStation.Active == Station && Station != null ? ChessStation.ActiveSeat : null;

	static string ColorWord( ChessSeat seat ) => seat == ChessSeat.White ? "white" : "black";

	protected override void OnUpdate()
	{
		// Only the client that started a game here is non-Idle; everyone else no-ops.
		if ( _phase != PlayPhase.Challenging && _phase != PlayPhase.Playing ) return;
		if ( _polling || _moveInFlight || LichessApi.Busy ) return;
		if ( _sincePoll < PollInterval ) return;
		_sincePoll = 0f;
		Poll();
	}

	// ── Starting a game ──

	/// <summary>Challenge a lichess user to a casual Rapid 10+0 game, playing the
	/// side we're seated on. They accept on lichess.org; the game then shows up in
	/// the poll and goes live on the board.</summary>
	public async void ChallengeUser( string username )
	{
		if ( !CanStart( out var seat ) ) return;
		username = username?.Trim();
		if ( string.IsNullOrEmpty( username ) ) { _error = "Enter a lichess username."; return; }

		BeginChallenge( seat, expectOpponent: username, ai: false );
		StatusText = $"Challenging {username}…";

		var res = await LichessApi.ChallengeUser( username, ColorWord( seat ), 600, 0, LichessAuth.Token );
		if ( !res.Ok ) { FailStart( res ); return; }

		_challengeId = LichessApi.Deserialize<LichessChallenge>( res.Body )?.id;
		StatusText = $"Waiting for {username} to accept…";
		_sincePoll = 999f; // start polling promptly
	}

	/// <summary>Challenge Stockfish (level 1–8) — starts immediately, so we get the
	/// game id straight back. Zero-setup way to exercise the play loop.</summary>
	public async void ChallengeAi( int level )
	{
		if ( !CanStart( out var seat ) ) return;

		BeginChallenge( seat, expectOpponent: null, ai: true );
		StatusText = $"Starting a game vs Stockfish level {level}…";

		var res = await LichessApi.ChallengeAi( level, ColorWord( seat ), 600, 0, LichessAuth.Token );
		if ( !res.Ok ) { FailStart( res ); return; }

		_gameId = LichessApi.Deserialize<LichessChallenge>( res.Body )?.id; // AI reply IS the game
		_sincePoll = 999f;
	}

	bool CanStart( out ChessSeat seat )
	{
		seat = default;
		if ( _phase == PlayPhase.Challenging || _phase == PlayPhase.Playing ) return false;
		if ( !LichessAuth.SignedIn ) { _error = "Sign in to lichess first."; return false; }
		if ( LocalSeatNow is not { } s ) { _error = "Sit at a side first."; return false; }
		seat = s;
		return true;
	}

	void BeginChallenge( ChessSeat seat, string expectOpponent, bool ai )
	{
		_myColor = seat;
		_expectOpponent = expectOpponent;
		_expectAi = ai;
		_gameId = null;
		_challengeId = null;
		Game = null;
		IsMyTurn = false;
		_lastMoveUci = null;
		OpponentName = null;
		OverText = null;
		_error = null;
		IsOpenGame = false;
		SeatUrl = null;
		ShareUrl = null;
		_phase = PlayPhase.Challenging;
	}

	/// <summary>Create an open challenge vs an anonymous browser and sit in on it in
	/// sbox on the side we're seated at. The open challenge is joinable by anyone
	/// (incl. logged-out) via a colour URL — but lichess has NO API to seat the
	/// creator, so the player opens <see cref="SeatUrl"/> once in a browser to take
	/// their own seat; the anon opponent opens <see cref="ShareUrl"/>. The moment
	/// both sides are seated the game appears in account/playing and goes live on
	/// the board (matched by id — an open challenge's id is also the game id).</summary>
	public async void PlayOpenGame()
	{
		if ( !CanStart( out var seat ) ) return;

		BeginChallenge( seat, expectOpponent: null, ai: false );
		IsOpenGame = true;
		StatusText = "Creating an open game…";

		var res = await LichessApi.CreateOpenChallenge( 600, 0, "Terry's Gambit", LichessAuth.Token );
		if ( !res.Ok ) { FailStart( res ); return; }

		var oc = LichessApi.Deserialize<LichessOpenChallenge>( res.Body );
		if ( oc == null || string.IsNullOrEmpty( oc.id )
			|| string.IsNullOrEmpty( oc.urlWhite ) || string.IsNullOrEmpty( oc.urlBlack ) )
		{
			_error = "lichess sent an unexpected reply";
			Log.Warning( $"[Gambit] open-game reply missing urls: {LichessApi.Truncate( res.Body, 200 )}" );
			_phase = PlayPhase.Idle;
			return;
		}

		_gameId = oc.id;        // open-challenge id == the game id it becomes
		_challengeId = oc.id;
		SeatUrl = seat == ChessSeat.White ? oc.urlWhite : oc.urlBlack;
		ShareUrl = seat == ChessSeat.White ? oc.urlBlack : oc.urlWhite;
		StatusText = null;
		_sincePoll = 999f;      // poll; goes live once both seats fill
	}

	void FailStart( LichessApi.Result res )
	{
		_error = res.Unauthorized ? "lichess didn't accept the token — sign in again."
			: res.Error ?? "lichess wouldn't start the game";
		Log.Warning( $"[Gambit] lichess play start failed ({res.Status}): {LichessApi.Truncate( res.Body, 200 )}" );
		_phase = PlayPhase.Idle;
		StatusText = null;
	}

	// ── Polling ──

	async void Poll()
	{
		_polling = true;
		try
		{
			var res = await LichessApi.GetAccountPlaying( LichessAuth.Token );
			if ( res.Unauthorized ) { FailStart( res ); return; }
			if ( !res.Ok ) return; // transient — try again next tick

			var games = LichessApi.Deserialize<LichessNowPlaying>( res.Body )?.nowPlaying;
			var g = FindOurGame( games );

			if ( g == null )
			{
				// Our live game vanished from the ongoing list ⇒ it ended.
				if ( _phase == PlayPhase.Playing && !string.IsNullOrEmpty( _gameId ) )
					await FinishGame();
				return; // still Challenging (not accepted yet), or nothing to do
			}

			_gameId = g.gameId;
			OpponentName = g.opponent?.username
				?? ( g.opponent?.ai is { } lvl ? $"Stockfish level {lvl}" : "Opponent" );
			MyClockSeconds = g.secondsLeft;
			AdoptState( g );

			if ( _phase == PlayPhase.Challenging )
			{
				_phase = PlayPhase.Playing; // accepted → live
				StatusText = null;
			}
		}
		finally
		{
			_polling = false;
		}
	}

	NowPlayingGame FindOurGame( List<NowPlayingGame> games )
	{
		if ( games == null ) return null;
		foreach ( var g in games )
		{
			if ( !string.IsNullOrEmpty( _gameId ) )
			{
				if ( g.gameId == _gameId ) return g;
				continue;
			}
			if ( _expectAi && g.opponent?.ai != null ) return g;
			if ( !_expectAi && _expectOpponent != null && g.opponent?.username != null
				&& g.opponent.username.ToLowerInvariant() == _expectOpponent.ToLowerInvariant() )
				return g;
		}
		return null;
	}

	/// <summary>Adopt the server's position. Polling is gated so it never runs while
	/// our own move POST is in flight (that request holds LichessApi's single-flight
	/// gate), and by the time the POST returns the server reflects the move — so
	/// adopting the server FEN is a no-op for our own move and only pulls in the
	/// opponent's reply. A rejected move reverts here on the next poll.</summary>
	void AdoptState( NowPlayingGame g )
	{
		IsMyTurn = g.isMyTurn && !_moveInFlight;
		_lastMoveUci = string.IsNullOrEmpty( g.lastMove ) ? _lastMoveUci : g.lastMove;

		var server = BuildGame( g.fen, g.color, g.isMyTurn );
		if ( server != null && ( Game == null || Game.Fen != server.Fen ) )
			Game = server;
	}

	/// <summary>Turn a nowPlaying FEN into a rules instance. lichess usually sends a
	/// full FEN; if it's placement-only, derive side-to-move from the API's colour +
	/// isMyTurn and disable castling/en-passant (can't be inferred — lichess rejects
	/// an illegal castle on POST anyway).</summary>
	static ChessGame BuildGame( string fen, string myColorWord, bool isMyTurn )
	{
		if ( string.IsNullOrWhiteSpace( fen ) ) return null;
		if ( ChessGame.TryFromFen( fen, out var full ) ) return full;

		bool whiteToMove = ( myColorWord == "white" ) == isMyTurn;
		string side = whiteToMove ? "w" : "b";
		return ChessGame.TryFromFen( $"{fen} {side} - - 0 1", out var patched ) ? patched : null;
	}

	// ── Moves ──

	async void PostMove( string uci )
	{
		_moveInFlight = true;
		try
		{
			var res = await LichessApi.BoardMove( _gameId, uci, LichessAuth.Token );
			if ( !res.Ok )
			{
				_error = res.Error ?? "lichess rejected the move";
				Log.Warning( $"[Gambit] board move {uci} failed ({res.Status}): {LichessApi.Truncate( res.Body, 160 )}" );
				// leave reconciliation to the next poll (adopts the server FEN → reverts)
			}
			else
			{
				_error = null;
			}
		}
		finally
		{
			_moveInFlight = false;
			_sincePoll = 999f; // poll promptly for the opponent's reply / to confirm
		}
	}

	// ── Endings ──

	/// <summary>Resign the live game (HUD button / leaving mid-game). The poll then
	/// sees it drop from the ongoing list and moves us to the over screen.</summary>
	public async void ResignGame()
	{
		if ( _phase != PlayPhase.Playing || string.IsNullOrEmpty( _gameId ) ) return;
		await LichessApi.BoardResign( _gameId, LichessAuth.Token );
		_sincePoll = 999f;
	}

	async Task FinishGame()
	{
		_phase = PlayPhase.Over;
		IsMyTurn = false;
		StatusText = null;

		var res = await LichessApi.GameExport( _gameId );
		var st = res.Ok ? LichessApi.Deserialize<LichessGameStatus>( res.Body ) : null;
		OverText = DescribeEnd( st );
	}

	string DescribeEnd( LichessGameStatus st )
	{
		if ( st == null || string.IsNullOrEmpty( st.status ) )
			return "Game over — view it on lichess.";

		string reason = st.status switch
		{
			"mate" => "checkmate",
			"resign" => "resignation",
			"outoftime" => "time",
			"stalemate" => "stalemate",
			"draw" => "draw",
			"timeout" => "abandonment",
			"aborted" => "the game was aborted",
			_ => st.status,
		};

		if ( string.IsNullOrEmpty( st.winner ) )
			return st.status == "aborted" ? "Game aborted." : $"Draw — {reason}.";

		bool iWon = ( st.winner == "white" ) == ( _myColor == ChessSeat.White );
		return iWon ? $"You won — {reason}." : $"You lost — {reason}.";
	}

	/// <summary>Cancel a pending (not-yet-accepted) challenge, or clear the over
	/// screen — back to the idle panel so the board can be reused.</summary>
	public async void ClearPlay()
	{
		if ( _phase == PlayPhase.Challenging && !string.IsNullOrEmpty( _challengeId ) )
			await LichessApi.CancelChallenge( _challengeId, LichessAuth.Token );

		_phase = PlayPhase.Idle;
		Game = null;
		IsMyTurn = false;
		_gameId = null;
		_challengeId = null;
		StatusText = null;
		OverText = null;
		_error = null;
	}

	public void CopyGameUrl() => Copy( GameUrl );

	/// <summary>Click-to-copy any of the play/seat/share URLs (no in-game browser
	/// open API — CLAUDE.md).</summary>
	public void Copy( string url )
	{
		if ( string.IsNullOrEmpty( url ) ) return;
		Sandbox.UI.Clipboard.SetText( url );
		SinceCopied = 0f;
	}
}
