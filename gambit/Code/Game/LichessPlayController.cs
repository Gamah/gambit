using System.Collections.Generic;
using System.Threading;
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
	/// HUD to show the play panel instead of the idle/link panel). Only true on the
	/// client that actually started the game — spectators stay Idle and watch via the
	/// synced relay (<see cref="Spectating"/>).</summary>
	public bool Active => _phase != PlayPhase.Idle;

	/// <summary>We are the challenger AND still looking for a random opponent via a
	/// held-open board seek (drives a "seeking…" note instead of "waiting to accept").</summary>
	public bool Seeking => _phase == PlayPhase.Challenging && _expectAny;

	/// <summary>This client should render the board for this table: either we're
	/// playing here (<see cref="Active"/>) or we're a spectator watching the relayed
	/// game (<see cref="Spectating"/>). <see cref="ChessBoardView"/> keys off this.</summary>
	public bool ShowsBoard => Active || Spectating;

	/// <summary>We're not a player here but the host is relaying a live lichess game at
	/// this table — render it read-only from the synced FEN.</summary>
	public bool Spectating => !Active && RelayLive && _spectatorGame != null;

	/// <summary>A lichess game occupies this board (we play it, or one is being relayed
	/// here) — the local two-seat game must not auto-start on top of it.</summary>
	public bool BlocksLocalGame => Active || RelayLive;

	// ── IBoardGame ──

	/// <summary>Player's own rules instance while <see cref="Active"/>; the spectator
	/// reconstruction from the relayed FEN while <see cref="Spectating"/>.</summary>
	public ChessGame Game => Spectating ? _spectatorGame : _playGame;
	ChessGame _playGame;
	ChessGame _spectatorGame;

	public bool IsMyTurn { get; private set; }
	public ChessSeat? LocalSeat => _phase is PlayPhase.Playing or PlayPhase.Over ? _myColor : null;
	public string LastMoveUci => Spectating ? RelayLastMove : ( Game?.LastMoveUci ?? _lastMoveUci );

	// ── Spectator relay (D7) ──
	// A lichess game is played entirely on one client (its token never crosses the
	// wire, D3), so other clients can't see it. The playing client folds its polled,
	// public position through the host into these synced fields — the direct analog of
	// LocalGameController.HostFold — and non-players render it read-only. Only the FEN
	// and player names travel (all public); no token, ever.
	[Sync( SyncFlags.FromHost )] public bool RelayLive { get; set; }
	[Sync( SyncFlags.FromHost )] public string RelayFen { get; set; }
	[Sync( SyncFlags.FromHost )] public string RelayLastMove { get; set; }
	[Sync( SyncFlags.FromHost )] public string RelayWhiteName { get; set; }
	[Sync( SyncFlags.FromHost )] public string RelayBlackName { get; set; }

	public bool TryMakeMove( string uci )
	{
		if ( _phase != PlayPhase.Playing || !IsMyTurn || _moveInFlight || _playGame == null ) return false;
		if ( !_playGame.ApplyUci( uci ) ) return false; // optimistic local apply; lichess is the authority

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

	/// <summary>Colour-pinned seat URL — a fallback only. We normally seat the player
	/// via the API (accept?color=), so this is offered just in case that ever fails.</summary>
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
	bool _expectAny;          // seek: adopt the first NEW game that appears
	HashSet<string> _preexistingIds; // games already ongoing when a seek began
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
		// Watchers (Idle here) keep a read-only board built from the relayed FEN.
		SyncSpectator();

		// Only the client that started a game here is non-Idle; everyone else no-ops.
		if ( _phase != PlayPhase.Challenging && _phase != PlayPhase.Playing ) return;

		// While a game is live, hold the game stream open so lichess sees us present —
		// without it the opponent's client shows us as having left after every move
		// (and can claim victory). Re-armed here if the connection ever drops.
		if ( _phase == PlayPhase.Playing ) EnsurePresence();

		if ( _polling || _moveInFlight || LichessApi.Busy ) return;
		if ( _sincePoll < PollInterval ) return;
		_sincePoll = 0f;
		Poll();
	}

	protected override void OnDestroy()
	{
		StopPresence();
		StopSeek();
	}

	// ── Spectator relay ──

	/// <summary>Non-player clients rebuild a read-only <see cref="ChessGame"/> from the
	/// host-folded <see cref="RelayFen"/> so <see cref="ChessBoardView"/> can render the
	/// lichess game the same way it renders any other position.</summary>
	void SyncSpectator()
	{
		if ( Active ) return; // we're the player — Game resolves to our own instance
		if ( !RelayLive || string.IsNullOrEmpty( RelayFen ) )
		{
			_spectatorGame = null;
			return;
		}
		if ( _spectatorGame != null && _spectatorGame.Fen == RelayFen ) return;
		if ( ChessGame.TryFromFen( RelayFen, out var g ) ) _spectatorGame = g;
	}

	/// <summary>Playing client → host: publish the current public position for watchers.
	/// Deduped so it only fires when something changed (idle while the opponent thinks).</summary>
	void RelayToSpectators()
	{
		if ( _phase != PlayPhase.Playing || _playGame == null ) return;

		var fen = _playGame.Fen;
		string me = LichessAuth.Username;
		if ( string.IsNullOrEmpty( me ) ) me = "You";
		string opp = OpponentName ?? "Opponent";
		string white = _myColor == ChessSeat.White ? me : opp;
		string black = _myColor == ChessSeat.White ? opp : me;

		if ( RelayLive && fen == RelayFen && white == RelayWhiteName && black == RelayBlackName )
			return;

		HostRelay( fen, LastMoveUci, white, black );
	}

	[Rpc.Host]
	void HostRelay( string fen, string lastMove, string white, string black )
	{
		if ( !Networking.IsHost ) return;
		RelayFen = fen;
		RelayLastMove = lastMove;
		RelayWhiteName = white;
		RelayBlackName = black;
		RelayLive = true;
	}

	/// <summary>Tear down the relayed board on every watcher (game over / cleared).</summary>
	void ClearRelay()
	{
		if ( !RelayLive && string.IsNullOrEmpty( RelayFen ) ) return;
		HostRelayClear();
	}

	[Rpc.Host]
	void HostRelayClear()
	{
		if ( !Networking.IsHost ) return;
		RelayLive = false;
		RelayFen = null;
		RelayLastMove = null;
		RelayWhiteName = null;
		RelayBlackName = null;
	}

	// ── Presence (held-open game stream) ──

	CancellationTokenSource _presenceCts;
	Task _presenceTask;
	RealTimeSince _sincePresenceStart = 999f;

	void EnsurePresence()
	{
		if ( string.IsNullOrEmpty( _gameId ) ) return;
		if ( _presenceTask is { IsCompleted: false } ) return; // already holding one open
		// If the stream keeps closing instantly (e.g. the game just ended, a beat
		// before the poll notices), don't reconnect every frame and trip rate limits.
		if ( _sincePresenceStart < 3f ) return;

		_presenceCts?.Cancel();
		_presenceCts = new CancellationTokenSource();
		_presenceTask = LichessApi.HoldGameStream( _gameId, LichessAuth.Token, _presenceCts.Token );
		_sincePresenceStart = 0f;
	}

	void StopPresence()
	{
		_presenceCts?.Cancel();
		_presenceCts = null;
		_presenceTask = null;
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

	/// <summary>Quick match: seek a <b>random</b> lichess opponent at Rapid 10+0 (the
	/// original M4 gate item). Unlike a direct challenge the colour is lichess's to
	/// decide, so we adopt whichever side it hands us on the first poll (and swoop the
	/// camera there if it differs from the seat we sat at). <paramref name="rated"/>
	/// drives the rated toggle.</summary>
	public async void QuickSeek( bool rated )
	{
		if ( !CanStart( out var seat ) ) return;

		// Snapshot the games already ongoing so the seek adopts only the NEW pairing,
		// never some pre-existing game of the player's.
		var pre = await LichessApi.GetAccountPlaying( LichessAuth.Token );
		if ( pre.Unauthorized ) { FailStart( pre ); return; }
		var preList = pre.Ok ? LichessApi.Deserialize<LichessNowPlaying>( pre.Body )?.nowPlaying : null;

		// A second start may have raced in during the await.
		if ( _phase == PlayPhase.Challenging || _phase == PlayPhase.Playing ) return;

		BeginChallenge( seat, expectOpponent: null, ai: false );
		_expectAny = true;
		_preexistingIds = CollectGameIds( preList );
		StatusText = rated ? "Seeking a rated Rapid opponent…" : "Seeking a casual Rapid opponent…";

		// Hold the seek open (bypasses the single-flight gate); it returns when lichess
		// pairs us. We detect the pairing via the poll, which runs concurrently.
		StopSeek();
		_seekCts = new CancellationTokenSource();
		_ = LichessApi.HoldSeek( rated, 10, 0, LichessAuth.Token, _seekCts.Token );

		_sincePoll = 999f; // start polling for the pairing promptly
	}

	static HashSet<string> CollectGameIds( List<NowPlayingGame> games )
	{
		var set = new HashSet<string>();
		if ( games != null )
			foreach ( var g in games )
				if ( !string.IsNullOrEmpty( g.gameId ) ) set.Add( g.gameId );
		return set;
	}

	CancellationTokenSource _seekCts;

	void StopSeek()
	{
		_seekCts?.Cancel();
		_seekCts = null;
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
		StopPresence(); // drop any leftover held stream from a previous game
		StopSeek();     // and any in-flight seek
		_myColor = seat;
		_expectOpponent = expectOpponent;
		_expectAi = ai;
		_expectAny = false;
		_preexistingIds = null;
		_gameId = null;
		_challengeId = null;
		_playGame = null;
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
		StatusText = "Taking your seat…";

		// Seat ourselves in our own open challenge via the API — accept it with our
		// colour (the accept endpoint's `color` query works on open challenges). No
		// browser needed on our side; SeatUrl is kept only as a fallback if this
		// fails. The opponent joins ShareUrl from any browser (incl. anonymous), and
		// the poll below takes the game live once both sides are in.
		SelfSeat( ColorWord( seat ) );

		_sincePoll = 999f;
	}

	async void SelfSeat( string color )
	{
		var res = await LichessApi.AcceptChallenge( _gameId, color, LichessAuth.Token );
		if ( res.Ok )
		{
			StatusText = null;
			Log.Info( "[Gambit] seated in the open game via API — share the opponent link." );
		}
		else
		{
			// Non-fatal: the player can still take the seat by opening SeatUrl in a
			// browser once (the HUD offers it), and the poll will pick the game up.
			StatusText = "Couldn't take your seat automatically — open your seat link once in a browser.";
			Log.Warning( $"[Gambit] self-seat via accept failed ({res.Status}): {LichessApi.Truncate( res.Body, 160 )}" );
		}
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
			AdoptSide( g );
			AdoptState( g );

			if ( _phase == PlayPhase.Challenging )
			{
				_phase = PlayPhase.Playing; // accepted / paired → live
				StatusText = null;
				StopSeek();       // paired — stop holding the seek open
				_expectAny = false;
			}

			RelayToSpectators(); // publish this position for watchers (D7)
		}
		finally
		{
			_polling = false;
		}
	}

	/// <summary>lichess is authoritative about which colour we're playing — a random
	/// seek may hand us the opposite side to the seat we sat at. Adopt it, and (for a
	/// seek) swoop the camera to the side we're actually playing.</summary>
	void AdoptSide( NowPlayingGame g )
	{
		var side = g.color == "black" ? ChessSeat.Black : ChessSeat.White;
		if ( side == _myColor ) return;
		_myColor = side;

		if ( _expectAny && LocalSeatNow is { } seat && seat != side )
			LobbyPlayer.Local?.JoinLichessSide( side );
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
			if ( _expectAny )
			{
				// The freshly-paired seek game = the one that wasn't ongoing at seek start.
				if ( _preexistingIds == null || !_preexistingIds.Contains( g.gameId ) ) return g;
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
		if ( server != null && ( _playGame == null || _playGame.Fen != server.Fen ) )
			_playGame = server;
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
		StopPresence(); // game's over — release the held-open game stream
		ClearRelay();   // and take the relayed board off every watcher

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
		StopPresence();
		StopSeek();
		ClearRelay();

		if ( _phase == PlayPhase.Challenging && !string.IsNullOrEmpty( _challengeId ) )
			await LichessApi.CancelChallenge( _challengeId, LichessAuth.Token );

		_phase = PlayPhase.Idle;
		_playGame = null;
		IsMyTurn = false;
		_gameId = null;
		_challengeId = null;
		_expectAny = false;
		_preexistingIds = null;
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
