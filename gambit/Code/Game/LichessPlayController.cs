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
		string before = _playGame.Fen;
		if ( !_playGame.ApplyUci( uci ) ) return false; // optimistic local apply; lichess is the authority

		PlayMoveSound( before, _playGame.Fen, positional: false ); // our move, on our own board
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

	/// <summary>Incoming lichess challenges (someone challenged YOU — from the web, mobile,
	/// or another client), polled while idle + seated here (M4 #4). The HUD lists them with
	/// Accept / Decline. id, challenger name, speed.</summary>
	public IReadOnlyList<(string id, string from, string speed)> Incoming => _incoming;
	readonly List<(string id, string from, string speed)> _incoming = new();

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

	RealTimeSince _sinceChallengePoll = 999f;
	bool _challengePolling;

	const float PollInterval = 1.5f; // ~lichess-friendly; a full-minute 429 back-off would flag-loss anyway
	const float ChallengePollInterval = 4f; // idle inbound-challenge watch — slower, it's just a notification

	ChessSeat? LocalSeatNow =>
		ChessStation.Active == Station && Station != null ? ChessStation.ActiveSeat : null;

	static string ColorWord( ChessSeat seat ) => seat == ChessSeat.White ? "white" : "black";

	protected override void OnUpdate()
	{
		// Watchers (Idle here) keep a read-only board built from the relayed FEN.
		SyncSpectator();

		// Host recycle: if the table has emptied but a relay is still live, the player
		// left without a clean LeaveSeat (an abrupt disconnect) — drop it so watchers
		// don't stare at a frozen board and the next sitter starts fresh. Mirrors
		// LichessGameController's auto-recycle. The graceful stand-up path clears the
		// relay itself via LeaveSeat → ClearPlay, so this is only the safety net.
		if ( Networking.IsHost && RelayLive && Station != null
			&& Station.WhiteSteamId == 0 && Station.BlackSteamId == 0 )
			ClearRelayFields();

		// Idle + seated here: watch for INCOMING challenges so they can be accepted in
		// sbox (only the station the local player is sitting at polls — challenges are
		// account-global, so one poller is enough).
		if ( _phase == PlayPhase.Idle ) { PollIncomingIfSeated(); return; }
		if ( _phase == PlayPhase.Over ) return;

		// Remaining: Challenging or Playing — the client that started a game here.

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
		string before = _spectatorGame?.Fen;
		if ( ChessGame.TryFromFen( RelayFen, out var g ) )
		{
			_spectatorGame = g;
			// Positional move sound at the table we're watching (M6 sound mapping).
			if ( before != null ) PlayMoveSound( before, RelayFen, positional: true );
		}
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
		ClearRelayFields();
	}

	/// <summary>Host-side field reset — reached inline from the auto-recycle path and
	/// via <see cref="HostRelayClear"/>'s RPC (only the host writes FromHost syncs).</summary>
	void ClearRelayFields()
	{
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

	// ── Incoming challenges (M4 #4) ──

	void PollIncomingIfSeated()
	{
		// Only poll while the local player is actually sitting here and signed in.
		if ( LocalSeatNow == null || !LichessAuth.SignedIn )
		{
			if ( _incoming.Count > 0 ) _incoming.Clear();
			return;
		}
		if ( _challengePolling || _polling || LichessApi.Busy ) return;
		if ( _sinceChallengePoll < ChallengePollInterval ) return;
		_sinceChallengePoll = 0f;
		PollIncoming();
	}

	async void PollIncoming()
	{
		_challengePolling = true;
		try
		{
			var res = await LichessApi.GetChallenges( LichessAuth.Token );
			// Don't treat a 401 here as a dead token — a token missing only the
			// challenge:read scope 401s this endpoint while still being fine for play. Just
			// skip quietly (real token death is caught by the play/account polls).
			if ( !res.Ok ) return; // unauthorized/transient — try again next tick

			var list = LichessApi.Deserialize<LichessChallengeList>( res.Body );
			_incoming.Clear();
			if ( list?.@in != null )
				foreach ( var c in list.@in )
					if ( !string.IsNullOrEmpty( c.id ) )
						_incoming.Add( (c.id, c.challenger?.name ?? "someone", c.speed ?? "") );
		}
		finally
		{
			_challengePolling = false;
		}
	}

	/// <summary>Accept an incoming challenge and play it on this board. Colours were set by
	/// the challenger, so lichess assigns ours on accept — we adopt the new game via the
	/// poll (like a seek) and swoop the camera to whichever side we're given.</summary>
	public async void AcceptIncoming( string id )
	{
		if ( string.IsNullOrEmpty( id ) ) return;
		if ( !CanStart( out var seat ) ) return;

		// Snapshot ongoing games so the poll adopts only the newly-accepted one.
		var pre = await LichessApi.GetAccountPlaying( LichessAuth.Token );
		if ( pre.Unauthorized ) { FailStart( pre ); return; }
		var preList = pre.Ok ? LichessApi.Deserialize<LichessNowPlaying>( pre.Body )?.nowPlaying : null;
		if ( _phase == PlayPhase.Challenging || _phase == PlayPhase.Playing ) return;

		BeginChallenge( seat, expectOpponent: null, ai: false );
		_expectAny = true;
		_preexistingIds = CollectGameIds( preList );
		StatusText = "Accepting the challenge…";

		var res = await LichessApi.AcceptChallenge( id, null, LichessAuth.Token );
		if ( !res.Ok )
			// Non-fatal: if it doesn't land the poll simply finds no new game and standing
			// up clears the stuck Challenging state.
			Log.Warning( $"[Gambit] accept incoming failed ({res.Status}): {LichessApi.Truncate( res.Body, 160 )}" );

		_sincePoll = 999f;
	}

	/// <summary>Decline an incoming challenge — drop it locally and tell lichess.</summary>
	public async void DeclineIncoming( string id )
	{
		if ( string.IsNullOrEmpty( id ) ) return;
		_incoming.RemoveAll( c => c.id == id );
		await LichessApi.DeclineChallenge( id, LichessAuth.Token );
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

		var ch = LichessApi.Deserialize<LichessChallenge>( res.Body );
		_challengeId = ch?.id;
		StatusText = $"Waiting for {username} to accept…";
		Log.Info( $"[Gambit] challenge sent to {username}: {ch?.url ?? "(no url in reply)"} — they accept on lichess or their sbox client." );
		_sincePoll = 999f; // start polling promptly
	}

	/// <summary>Head-to-head (M4 #3): challenge the signed-in lichess player sitting on
	/// the other side of this board to a real lichess game. Same as
	/// <see cref="ChallengeUser"/>, but the opponent's username comes from the opposite
	/// seat's synced lichess name and — so neither player has to leave sbox — we tell
	/// their client to auto-accept this exact challenge. Colours follow the seats (we
	/// challenge with our own side; lichess gives the opponent the other, which is the
	/// seat they're on).</summary>
	public async void ChallengeSeatedOpponent()
	{
		if ( !CanStart( out var seat ) ) return;

		var oppSeat = seat == ChessSeat.White ? ChessSeat.Black : ChessSeat.White;
		var opp = Station?.SeatLichessName( oppSeat );
		if ( string.IsNullOrEmpty( opp ) )
		{
			_error = "The player across isn't signed in to lichess.";
			return;
		}

		BeginChallenge( seat, expectOpponent: opp, ai: false );
		StatusText = $"Challenging {opp}…";

		var res = await LichessApi.ChallengeUser( opp, ColorWord( seat ), 600, 0, LichessAuth.Token );
		if ( !res.Ok ) { FailStart( res ); return; }

		var chSeated = LichessApi.Deserialize<LichessChallenge>( res.Body );
		_challengeId = chSeated?.id;
		StatusText = $"Waiting for {opp} to accept…";
		Log.Info( $"[Gambit] head-to-head challenge sent to {opp}: {chSeated?.url ?? "(no url)"} — their sbox client auto-accepts; also visible on lichess." );

		// Ask the seated opponent's client to accept this specific challenge. Broadcast
		// straight from here (the same client→all pattern as NetChessMove) — lichess only
		// lets them accept a challenge it actually sent them, so it's self-verifying.
		if ( !string.IsNullOrEmpty( _challengeId ) )
			NetAskAccept( _challengeId, (int)oppSeat, LichessAuth.Username ?? "" );

		_sincePoll = 999f;
	}

	/// <summary>Challenger → everyone: the player seated at <paramref name="targetSeat"/>
	/// of this board should accept challenge <paramref name="challengeId"/>. Reaches all
	/// clients; only the one whose local player holds that seat (signed in and idle) acts —
	/// everyone else, including the challenger, no-ops.</summary>
	[Rpc.Broadcast]
	void NetAskAccept( string challengeId, int targetSeat, string challengerName )
	{
		if ( string.IsNullOrEmpty( challengeId ) ) return;
		var seat = (ChessSeat)targetSeat;
		if ( LocalSeatNow != seat ) return;      // not the challenged seat on this client
		if ( !LichessAuth.SignedIn ) return;     // can't play a Board-API game without a token
		if ( _phase != PlayPhase.Idle ) return;  // already busy with something else
		AutoAccept( challengeId, seat, challengerName );
	}

	/// <summary>Accept a head-to-head challenge aimed at our seat and fall into the normal
	/// poll-driven play loop (matched by the challenger's username, like
	/// <see cref="ChallengeUser"/>).</summary>
	async void AutoAccept( string challengeId, ChessSeat seat, string challenger )
	{
		bool hasName = !string.IsNullOrEmpty( challenger );
		BeginChallenge( seat, expectOpponent: hasName ? challenger : null, ai: false );
		_challengeId = challengeId;
		StatusText = hasName ? $"Accepting {challenger}'s challenge…" : "Accepting the challenge…";

		var res = await LichessApi.AcceptChallenge( challengeId, null, LichessAuth.Token );
		if ( !res.Ok )
			// Non-fatal: if it hasn't landed yet the poll still adopts the game by opponent
			// name once it goes live, and standing up clears a stuck Challenging state.
			Log.Warning( $"[Gambit] head-to-head auto-accept failed ({res.Status}): {LichessApi.Truncate( res.Body, 160 )}" );

		_sincePoll = 999f; // poll picks up the now-live game
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
		_incoming.Clear();
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
		{
			string before = _playGame?.Fen;
			_playGame = server;
			// The opponent's move arriving via the poll (our own confirmed move leaves the
			// FEN unchanged from the optimistic apply, so before==after → silent here). 2D
			// since it's our own board (M6 sound mapping).
			if ( before != null ) PlayMoveSound( before, server.Fen, positional: false );
		}
	}

	/// <summary>Tick for White's move, tock for Black's, pop for any capture — mirrors
	/// LocalGameController.PlayMoveSound. 2D on our own engaged board, positional when
	/// watching another table (M6 sound mapping).</summary>
	void PlayMoveSound( string before, string after, bool positional )
	{
		if ( string.IsNullOrEmpty( before ) || string.IsNullOrEmpty( after ) ) return;
		bool capture = CountPieces( before ) != CountPieces( after );
		bool whiteMoved = after.Contains( " b " ); // after a white move it's Black to move

		if ( positional )
		{
			if ( capture ) Audio.SoundPlayer.PlayPopAt( WorldPosition );
			else Audio.SoundPlayer.PlayTickAt( WorldPosition );
			return;
		}

		if ( capture ) Audio.SoundPlayer.PlayPop();
		else if ( whiteMoved ) Audio.SoundPlayer.PlayTick();
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

	/// <summary>The seated player stood up / left the board (or a dev reset). Tear down
	/// whatever this controller was doing so the table is free for the next person:
	/// forfeit a live game, cancel a pending challenge/seek/open game, drop the
	/// spectator relay, and return to <see cref="PlayPhase.Idle"/>. No-op on every
	/// client except the one that owns the game (everyone else is already Idle).</summary>
	public void LeaveSeat()
	{
		if ( _phase == PlayPhase.Idle ) return;

		// Walking away from a live game is a resignation on lichess. Fire it directly
		// (not ResignGame, which waits for the next poll) since we clear to Idle below
		// and stop polling immediately.
		if ( _phase == PlayPhase.Playing && !string.IsNullOrEmpty( _gameId ) )
			_ = LichessApi.BoardResign( _gameId, LichessAuth.Token );

		ClearPlay(); // cancels a pending challenge/seek/open, drops the relay, → Idle
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
