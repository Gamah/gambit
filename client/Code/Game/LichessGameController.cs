using System;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Api.Lichess;
using Gambit.Chess;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// A real lichess game, rendered on a Gambit table (M8; rebuilt by HTTPFIX).
///
/// <para>Slots in beside <see cref="LocalGameController"/> behind
/// <see cref="IBoardGame"/>, so <see cref="ChessBoardView"/> renders it with no
/// change at all — that seam was built for exactly this, and it survived the
/// transport being replaced underneath it without a single consumer changing.</para>
///
/// <para><b>What HTTPFIX changed: the transport, and only the transport.</b> This
/// used to long-poll gamchess every ~5s for a game gamchess was playing on the
/// player's behalf with a token it held. It now holds lichess's own ndjson game
/// stream, with a token on this machine, and BUILDS <see cref="LichessPlayState"/>
/// from the <c>gameFull</c>/<c>gameState</c> lines. Every public member below
/// means what it meant before.</para>
///
/// <para><b>Deleted with the poll, and not to be reintroduced:</b> the version
/// cursor, <c>_bankLag</c>, <c>_lastRoundTrip</c> and the
/// <c>clock_age_ms</c>/<c>hold_ms</c> reconciliation. A stream has no hold to
/// measure and no cursor to reconcile; M18 deleted the same machinery when TV
/// moved off its long poll. Keeping it would reintroduce the M11 sawtooth, where
/// the clock ticked down then jumped back UP.</para>
///
/// <para><b>Lichess is the only authority here.</b> This controller adjudicates
/// nothing and never decides a game is over — it rebuilds the position from the
/// UCI list lichess sends. It DOES run the ticking seat's clock down locally
/// between moves, because lichess only sends a clock on a move and a frozen clock
/// reads as a stopped game rather than a thinking player.</para>
///
/// <para>Because every state carries the WHOLE move list from the start, a
/// dropped line, a duplicate, or a reconnect costs nothing: we rebuild rather
/// than reconcile. There is no incremental state to corrupt.</para>
///
/// <para><b>Never required.</b> Every failure path degrades to "lichess play
/// didn't happen" and leaves the local game untouched. Note what that now covers
/// that it did not: gamchess being down does NOT stop a lichess game, because
/// gamchess is not on its path.</para>
/// </summary>
public sealed class LichessGameController : Component, IBoardGame
{
	/// <summary>Occupancy/seat source for this table. Set by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }

	/// <summary>The local table controller beside us — the source of the table's
	/// client_game_id, seats and time control, and the thing we are standing in
	/// for while a lichess game runs.</summary>
	[Property] public LocalGameController Local { get; set; }

	/// <summary>The controller living beside the given station, or null.</summary>
	public static LichessGameController For( ChessStation station ) =>
		station?.Components.Get<LichessGameController>();

	/// <summary>Latest state built from lichess's stream, or null before the first
	/// line lands.</summary>
	public LichessPlayState State { get; private set; }

	/// <summary>True from the moment this client asks for a lichess game until the
	/// table goes idle. The view and HUD read this to know which controller owns
	/// the board.</summary>
	public bool Engaged { get; private set; }

	/// <summary>Why the last attempt failed, for the HUD. Null when nothing's wrong.</summary>
	public string Error { get; private set; }

	// ── IBoardGame ──

	/// <summary>Position rebuilt from lichess's move list — the participant's own
	/// (<see cref="_game"/>) while <see cref="Engaged"/>, the relayed spectator copy
	/// (<see cref="_mirrorGame"/>) while <see cref="Mirroring"/>. Null until either lands.</summary>
	public ChessGame Game => Engaged ? _game : _mirrorGame;

	ChessGame _game;

	/// <summary>A lichess game is live at this table right now — ours (streamed), or
	/// someone else's (mirrored).</summary>
	public bool Playing => Engaged
		? State != null && State.status == "live" && !State.finished
		: Mirroring;

	// ── The spectator mirror (M14) ──
	//
	// A lichess game was INVISIBLE to every non-participant by construction: nothing
	// about it was networked — each participant talks to lichess privately, a solo
	// flow (seek / challenge / shareable link) starts no local game at all, and
	// Engaged only ever goes true on the client that asked. So a bystander (and every
	// joined client) saw a frozen board, heard nothing, and the seated terries never
	// moved. The room seeing the game IS the product; this is the relay.
	//
	// UNTOUCHED BY HTTPFIX, and worth knowing why: MirrorMoves/MirrorLive are fed by
	// the PARTICIPANT'S OWN OBSERVATIONS, never by gamchess. Moving where those
	// observations come from changed nothing here — the path just got more direct.

	/// <summary>The relayed UCI move list of the lichess game at this table, folded by
	/// the host from participant reports. Null/empty when no lichess game is on.</summary>
	[Sync( SyncFlags.FromHost )] public string MirrorMoves { get; set; }

	/// <summary>The relayed game is live right now (drops false when it finishes or
	/// the participant stands down).</summary>
	[Sync( SyncFlags.FromHost )] public bool MirrorLive { get; set; }

	/// <summary>The lichess challenge id for a PAIRED table game, published by White
	/// so Black can accept it.
	///
	/// <para><b>This is how Black learns the challenge, and the obvious alternative is
	/// wrong.</b> The reflex is "Black opens the event stream and waits for a challenge
	/// event" — don't: <c>/api/stream/event</c> is one-per-token and opening a second
	/// silently kills the first, so every extra flow that watches it is a hazard. White's
	/// challenge response carries the id, both seats are in the SAME s&amp;box lobby, and
	/// the station already syncs state, so the id rides the lobby instead. That preserves
	/// exactly the property the deleted server relay documented: <b>the paired flow never
	/// watches the event stream</b>, which was worth having when a server held the stream
	/// and is worth much more now a client does.</para>
	///
	/// <para>A challenge id is not a secret — it is in the URL of a public lichess
	/// challenge page. The authority is each client's own token.</para></summary>
	[Sync( SyncFlags.FromHost )] public string ChallengeId { get; set; }

	/// <summary>This client is showing someone ELSE's lichess game from the relay.
	/// Mutually exclusive with <see cref="Engaged"/> by construction.</summary>
	public bool Mirroring => !Engaged && MirrorLive && _mirrorGame != null;

	ChessGame _mirrorGame;
	string _mirrorRendered;   // the move list _mirrorGame was built from
	string _mirrorLastUci;
	string _reportedMoves;    // participant-side: last list/liveness sent to the host,
	bool _reportedLive;       // so the RPC fires per change, not per frame

	/// <summary>Participant → host: fold the observed game into the synced mirror.
	/// Longer-list-wins makes the two paired participants' identical reports
	/// idempotent, and keeps a straggler's stale short list from rewinding the board.</summary>
	[Rpc.Host]
	void ReportMirror( string moves, bool live )
	{
		moves ??= "";
		if ( moves.Length > ( MirrorMoves?.Length ?? -1 ) ) MirrorMoves = moves;
		MirrorLive = live;
	}

	/// <summary>White → host: publish the challenge id so Black can accept it.
	/// First-writer-wins within a game, so a repeat is a no-op.</summary>
	[Rpc.Host]
	void ReportChallenge( string id )
	{
		if ( string.IsNullOrEmpty( ChallengeId ) ) ChallengeId = id;
	}

	/// <summary>Participant side, every frame while engaged: keep the host's mirror
	/// current. One RPC per change (a move landing, the game going live/finished).</summary>
	void MaintainMirrorReport()
	{
		if ( !Engaged ) return;

		string moves = _renderedMoves ?? "";
		bool live = State != null && State.status == "live" && !State.finished;
		if ( moves == _reportedMoves && live == _reportedLive ) return;

		_reportedMoves = moves;
		_reportedLive = live;
		ReportMirror( moves, live );
	}

	/// <summary>Spectator side, every frame: keep the display game in step with the
	/// synced list. Same rebuild-from-scratch as <see cref="Rebuild"/>, same refusal
	/// to render a move our rules won't take.</summary>
	void MaintainMirrorGame()
	{
		if ( Engaged || !MirrorLive || string.IsNullOrEmpty( MirrorMoves ) )
		{
			_mirrorGame = null;
			_mirrorRendered = null;
			_mirrorLastUci = null;
			return;
		}

		if ( MirrorMoves == _mirrorRendered && _mirrorGame != null ) return;
		_mirrorRendered = MirrorMoves;

		var game = new ChessGame();
		string last = null;
		foreach ( var uci in MirrorMoves.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( !game.ApplyUci( uci ) )
			{
				Log.Warning( $"[Gambit] mirrored lichess move refused ({uci}) — spectator board frozen" );
				return;
			}
			last = uci;
		}
		_mirrorGame = game;
		_mirrorLastUci = last;
	}

	/// <summary>lichess says this game is over and we're still showing it.
	///
	/// <para>An ABORT is deliberately not a game over: lichess aborts a game nobody
	/// moved in, scores nothing and rates nothing, and <see cref="Adopt"/> hands the
	/// board straight back rather than displaying a result.</para></summary>
	public bool GameOver => Engaged && State is { finished: true } && ResultString != null;

	/// <summary>Seconds left on a seat's clock, per lichess, counted down locally
	/// between moves.
	///
	/// <para>An unlimited game has no clock and lichess sends 0 for it, which would
	/// read as a permanently flagged clock. The table's own control is what tells us.</para>
	///
	/// <para><b>lichess only sends a clock when a MOVE happens</b>, so a raw value is
	/// frozen for the whole of a think — which reads as a stopped clock, not a thinking
	/// player. So we bank the value lichess sent and run the SIDE TO MOVE's clock down
	/// from it, snapping both back on the next state.</para>
	///
	/// <para><b>The house rule: a live clock must never read HIGHER than the time
	/// actually left.</b> Reading low is explicitly permitted; reading high is not.
	/// With a STREAM the correction that used to be needed is gone: a frame arrives as
	/// lichess sends it, so the whole-second floor <c>TimeControl.Format</c> already
	/// applies absorbs the sub-second transport latency for free. What is left is
	/// <see cref="ClockLeadSeconds"/> — a small deliberate undershoot, the same free
	/// insurance in the one permitted direction that <c>LichessTvSource</c> takes.
	/// We never adjudicate: a local clock reaching 0 clamps at 0 and waits for lichess
	/// to call the flag.</para></summary>
	public float? SeatClock( ChessSeat seat )
	{
		if ( State is null ) return null;
		if ( Local?.Tc.IsUnlimited ?? false ) return null;

		// A FINISHED game freezes both clocks on their final banked values. Without
		// this SeatClock reads null the instant lichess says "finished" — and
		// TableClock.Face then falls back to the STARTING time control, so the clocks
		// visibly reset a beat before the board does.
		bool finished = State is { finished: true };
		if ( !Playing && !finished ) return null;

		float bank = seat == ChessSeat.White ? _whiteBank : _blackBank;
		// Only the side to move in a LIVE game is spending time. The idle side's bank
		// is exact however stale the frame is, and a finished game's clocks are frozen
		// on both sides — so the countdown applies to the ticking seat alone.
		// Subtracting from anything else would invent a loss of time that never happened.
		if ( TickingSeat != seat ) return MathF.Max( 0f, bank );
		return MathF.Max( 0f, bank - ClockLeadSeconds - (float)_sinceBank );
	}

	/// <summary>A deliberate undershoot, in seconds. Not a latency estimate — the
	/// stream removed the leg worth estimating. It is free insurance in the one
	/// direction the house rule permits, on a value we would otherwise be reading
	/// exactly at the boundary. Same constant and same reasoning as
	/// <c>LichessTvSource.ClockLeadSeconds</c>.</summary>
	const float ClockLeadSeconds = 0.25f;

	/// <summary>Whose clock is running: the side to move in the position lichess last
	/// sent. Null when no game is live.</summary>
	public ChessSeat? TickingSeat =>
		Playing && Game != null ? ( Game.WhiteToMove ? ChessSeat.White : ChessSeat.Black ) : null;

	// Banked clocks in SECONDS, and when they landed. Snapped on every state lichess
	// sends — which, unlike a timed-out long poll, only ever arrives because something
	// really changed. That is why there is no version gate here any more: there is no
	// duplicate to guard against, so no sawtooth to prevent.
	float _whiteBank, _blackBank;
	RealTimeSince _sinceBank;

	/// <summary>Seconds left on the local player's own clock. Null when we hold no seat
	/// in this game.</summary>
	public float? LocalSeatClock => LocalSeat is { } seat ? SeatClock( seat ) : null;

	/// <summary>The side the local player holds in the lichess game, or null.
	///
	/// <para>Read from lichess's own <c>gameFull</c>, not by matching SteamIDs: in a
	/// SEEK the opponent is a stranger with no SteamID to match against, and lichess
	/// knows what game it actually started, so if its answer ever disagreed with the
	/// local station the board must follow lichess.</para></summary>
	public ChessSeat? LocalSeat => State?.your_color switch
	{
		"white" => ChessSeat.White,
		"black" => ChessSeat.Black,
		_ => null,
	};

	/// <summary>This is a game against someone who isn't sitting opposite. Nobody is
	/// in the other seat.</summary>
	public bool IsSeek => State?.seek ?? false;

	/// <summary>The opponent's lichess name, whichever side they're on.</summary>
	public string OpponentName => LocalSeat switch
	{
		ChessSeat.White => State?.black_name,
		ChessSeat.Black => State?.white_name,
		_ => null,
	};

	public bool IsMyTurn =>
		Playing && Game != null && LocalSeat is { } seat
		&& Game.WhiteToMove == ( seat == ChessSeat.White ) && !_moveInFlight;

	/// <summary>UCI of the last move, for the last-move highlight.</summary>
	public string LastMoveUci => Engaged ? _lastMoveUci : _mirrorLastUci;

	string _lastMoveUci;

	/// <summary>Submit a move, straight to lichess. The board doesn't change until
	/// lichess confirms it on the stream.</summary>
	public bool TryMakeMove( string uci )
	{
		if ( !IsMyTurn || string.IsNullOrEmpty( uci ) ) return false;

		// Validate against the local rules first — the same courtesy the local
		// table pays. An illegal move never reaches the network.
		if ( Game == null || !Game.LegalTargets( uci[..2] ).Contains( uci[2..4] ) ) return false;

		// Claim before awaiting, or OnUpdate fires a POST per frame until the
		// first returns — the TryArchive lesson.
		_moveInFlight = true;
		_ = SendMove( uci );
		return true;
	}

	bool _moveInFlight;

	// ── Premove ──

	/// <summary>The move armed to play the instant it becomes legal, as UCI, or
	/// null. ONE, deliberately: lichess allows a single premove, and a queue would
	/// need a plan for the moment move two turns out to be illegal.
	///
	/// <para>Stored as SQUARES rather than anything derived from the position it
	/// was armed in. <see cref="Rebuild"/> throws the board away and rebuilds it
	/// from lichess's move list on every state, so a premove holding a reference
	/// into the old position would be stale before it ever fired.</para>
	///
	/// <para><b>Premove is not a lichess concept</b> — there is no API surface for
	/// it and no server involvement. It is just "POST the move the instant it is
	/// legal", so ours is client-only by nature rather than by choice, and that did
	/// not change when the token moved.</para></summary>
	string _premoveUci;

	/// <summary>The armed premove as UCI, or null.</summary>
	public string PremoveUci => _premoveUci;

	/// <summary>Arm a premove. The view decides the moment; this only sanity-checks.
	///
	/// <para>Deliberately NOT guarded on <c>IsMyTurn</c>: that also goes false while
	/// our own move is in flight, which is a real window in which the board still
	/// shows the pre-move position.</para></summary>
	public void SetPremove( string uci )
	{
		if ( !Playing || LocalSeat == null ) return;
		if ( uci is not { Length: >= 4 } ) return;
		_premoveUci = uci;
	}

	public void ClearPremove() => _premoveUci = null;

	/// <summary>Play the armed premove if the position that just arrived makes it
	/// legal. Called once per adopted state, straight after the rebuild.
	///
	/// <para>An illegal premove is DROPPED, not held. It was aimed at a position
	/// the opponent didn't play into; keeping it armed would fire it at some later
	/// position it was never meant for — which is how a premove ends up hanging a
	/// queen two moves after you forgot about it.</para></summary>
	void FirePremove()
	{
		if ( _premoveUci == null ) return;

		if ( !Playing || LocalSeat == null ) { _premoveUci = null; return; }

		// Not our turn yet (or our own last move is still in flight) — keep it
		// armed and try again on the next state.
		if ( !IsMyTurn ) return;

		string uci = _premoveUci;

		// Disarm BEFORE playing, not after: TryMakeMove can refuse, and a premove
		// left armed through its own refusal would re-fire on every state for the
		// rest of the game.
		_premoveUci = null;

		if ( !TryMakeMove( uci ) )
			_premoveDropped = BoardGame.PremoveDroppedSeconds;
	}

	RealTimeUntil _premoveDropped;

	/// <summary>The last premove was refused, within the notice window.</summary>
	public bool PremoveDropped => (float)_premoveDropped > 0f;

	async Task SendMove( string uci )
	{
		var res = await LichessBoard.Move( _gameId, uci );
		_moveInFlight = false;

		if ( res.Ok ) return;

		// lichess refused it (not your turn, game gone, token revoked). Say so and
		// let the next state re-assert the true position — we never guessed at one.
		Error = res.Reason;
		if ( res.Unauthorized ) Error = "Lichess rejected the token — link your account again.";
		Log.Info( $"[Gambit] lichess refused a move: {Error}" );
	}

	// ── State ──

	string _clientGameId;   // the table id we asked to play for
	string _gameId;         // lichess's own game id, once there is one
	string _renderedMoves;  // the move list our Game was built from

	LichessStream _stream;      // the game stream
	LichessStream _seekStream;  // a held seek, whose connection IS the seek
	IDisposable _events;        // our reference on the one-per-token event stream

	/// <summary>Have we already told the host this game's result? Claimed once, so
	/// a repeated finished state doesn't re-report.</summary>
	bool _reportedResult;

	/// <summary>Have we already archived this finished game to gamchess?</summary>
	bool _archived;

	/// <summary>A ClientGameId whose lichess play already failed. Never asked for
	/// again.
	///
	/// <para>Needed because failing hands the board back (Engaged goes false), and
	/// AutoEngage's whole job is to engage an un-engaged lichess table — so without
	/// this the two would ping-pong: fail, disengage, re-request, fail, forever.
	/// Survives Clear() on purpose; only a NEW game at this table resets it.</para></summary>
	string _failedGameId;

	/// <summary>
	/// Play this table's game on lichess, against the player opposite.
	///
	/// <para>Called only by a SEATED client, for itself. The other seat's client
	/// does the same, independently.</para>
	///
	/// <para><b>The two-intent rule survives, with a different justification.</b> It
	/// used to be the consent story: gamchess held both players' tokens, so a
	/// one-sided start could have dragged anyone into a game. Now each client acts
	/// with its own token and can only commit itself. What both intents still buy is
	/// DIRECTORY DISCLOSURE — gamchess reveals the opposite seat's lichess username
	/// only once both have asked, and only to those two.</para>
	///
	/// <para>White then challenges Black by name and publishes the challenge id;
	/// Black accepts it. The named opponent consenting is not the authorisation here
	/// (they are sitting opposite and asked for this) — the authorisation is that
	/// each seat spent its own grant.</para>
	/// </summary>
	public void RequestPlay()
	{
		if ( Engaged || Local == null || Station == null ) return;
		if ( LocalStationSeat == null ) return;   // only the two players ask

		string id = Local.ClientGameId;
		if ( string.IsNullOrEmpty( id ) ) return;
		if ( id == _failedGameId ) return;       // already refused; don't loop on it

		// Bullet can never reach lichess from any path — the Board API refuses
		// anything faster than blitz. Don't offer it, and don't spend a request.
		if ( !LichessTable.CanMirror( Local.Tc ) ) return;
		if ( !LichessTokenStore.Linked ) return;

		ulong white = Station.WhiteSteamId, black = Station.BlackSteamId;
		if ( white == 0 || black == 0 ) return;

		Engaged = true;
		_clientGameId = id;
		Error = null;
		Fresh( seek: false, status: "waiting" );
		_ = RunPaired( id, white, black, Local.Tc );
	}

	/// <summary>The paired flow: rendezvous, then White challenges and Black
	/// accepts by the synced id.
	///
	/// <para><b>The two seats do different work, and deliberately so.</b> Only WHITE
	/// needs the rendezvous to come back ready, because only White needs a name to
	/// challenge. Black posts its intent once — which is what lets White's next poll
	/// succeed — and then waits on the synced challenge id. Having both seats poll
	/// would double the requests to learn something one of them never uses.</para></summary>
	async Task RunPaired( string id, ulong white, ulong black, TimeControl tc )
	{
		bool iAmWhite = LocalStationSeat == ChessSeat.White;

		// ① Post this seat's intent. The FIRST answer is the one that can tell us
		//    something is wrong (not linked, not seated, a bad game id), so it is
		//    checked whichever seat we are.
		var res = await LichessApi.Rendezvous( id, white, black );
		if ( !res.Ok )
		{
			Fail( id, ReadError( res ) );
			return;
		}
		var rv = GamchessApi.Deserialize<LichessRendezvous>( res.Body );
		if ( rv == null )
		{
			Fail( id, "gamchess didn't answer the rendezvous." );
			return;
		}

		if ( !iAmWhite )
		{
			// ③ Black: wait for White's challenge id and accept it. Both clients
			//    engage on the same synced flag at game start, so this is a wait of
			//    a second or two in practice, not a race.
			RealTimeSince forId = 0f;
			while ( string.IsNullOrEmpty( ChallengeId ) && (float)forId < PairingTimeoutSeconds )
			{
				if ( !Engaged || _clientGameId != id ) return;
				await GameTask.DelaySeconds( 0.25f );
			}
			if ( string.IsNullOrEmpty( ChallengeId ) )
			{
				Fail( id, "The other seat never issued the lichess challenge." );
				return;
			}

			var accept = await LichessBoard.AcceptChallenge( ChallengeId );
			if ( !accept.Ok )
			{
				Fail( id, accept.Reason );
				return;
			}
			StartGameStream( ChallengeId );
			return;
		}

		// ② White: keep posting until Black has too, then challenge them by name.
		//    Re-posting IS the re-check — there is no separate read.
		RealTimeSince waiting = 0f;
		while ( !rv.ready && (float)waiting < PairingTimeoutSeconds )
		{
			await GameTask.DelaySeconds( 1f );
			if ( !Engaged || _clientGameId != id ) return;

			res = await LichessApi.Rendezvous( id, white, black );
			if ( !res.Ok )
			{
				Fail( id, ReadError( res ) );
				return;
			}
			rv = GamchessApi.Deserialize<LichessRendezvous>( res.Body ) ?? rv;
		}

		if ( !rv.ready || string.IsNullOrEmpty( rv.opponent ) )
		{
			Fail( id, "The other seat didn't join the lichess game." );
			return;
		}

		var (ch, cres) = await LichessBoard.ChallengeUser( rv.opponent, tc, rated: false, color: "white" );
		if ( ch == null )
		{
			Fail( id, cres.Reason );
			return;
		}
		if ( !Engaged || _clientGameId != id )
		{
			// Stood up while the challenge was in flight. WITHDRAW IT: hanging up does
			// not withdraw a challenge, and an un-cancelled one stays acceptable for
			// hours — a stranger accepting later would start a real game on this
			// player's account at a board nobody is sitting at.
			_ = LichessBoard.CancelChallenge( ch.id );
			return;
		}
		ReportChallenge( ch.id );
		StartGameStream( ch.id );
	}

	/// <summary>How long each seat waits on the other before giving up. Generous:
	/// both clients engage on the same synced flag at game start, so in practice this
	/// resolves in a second or two, and the only thing a long timeout costs is how
	/// long a genuinely broken pairing sits there saying "waiting".</summary>
	const float PairingTimeoutSeconds = 60f;

	/// <summary>
	/// Find a RANDOM lichess opponent from this table.
	///
	/// <para>Needs only this player — you are spending your own grant to play a
	/// stranger who opts in on lichess's side by their own choice, so there is
	/// nobody here to get consent from. It works at a table you're sitting at alone.</para>
	///
	/// <para><b>A seek needs the event stream and the paired flow does not</b>: a
	/// real-time seek's response carries no game id — it is a stream of empty lines
	/// whose only job is to stay open, and closing it cancels the seek. lichess's own
	/// instruction is to learn about the game from <c>gameStart</c> on the event
	/// stream, which is why one is taken here.</para>
	/// </summary>
	public void RequestSeek( bool rated, string ratingRange = null, string color = null )
	{
		if ( Engaged || Local == null || Station == null ) return;
		if ( LocalStationSeat == null ) return;
		if ( !LichessTable.CanSeek( Local.Tc ) ) return;
		if ( !LichessTokenStore.Linked ) return;

		// ratingRange is accepted and IGNORED, on purpose. Omitting it is not
		// laziness: for a real-time hook lila discards a default range and centres a
		// Gaussian band on the seeker's REAL rating, which it knows and we don't. So
		// empty is the strongest value available, and anything we computed would be
		// worse-informed. See LICHESS.md before ever sending one.
		_ = ratingRange;

		var seek = LichessBoard.Seek( Local.Tc, rated, color, out string why );
		if ( seek == null )
		{
			// Includes our own 5/min self-limit refusing it. Report and stop —
			// never retry, which is how a throttle becomes a ban.
			Error = why;
			return;
		}

		Engaged = true;
		Seeking = true;
		_clientGameId = GamchessApi.NewClientGameId();
		Error = null;
		Fresh( seek: true, status: "waiting" );

		_seekStream = seek;
		_events = LichessEventStream.Listen( OnEvent );
	}

	/// <summary>We asked for a random opponent and are still waiting for one. Drops
	/// to false once a game exists.</summary>
	public bool Seeking { get; private set; }

	/// <summary>We challenged a named lichess user and are waiting for them to
	/// accept. Drops to false once a game exists (or the challenge is declined).</summary>
	public bool Challenging { get; private set; }

	/// <summary>The named user we're challenging, for the HUD's waiting line. Null
	/// unless a challenge is in flight.</summary>
	public string ChallengeOpponent { get; private set; }

	/// <summary>We minted a shareable link and are waiting for a browser opponent to
	/// open it. Drops to false once the game goes live.</summary>
	public bool Opening { get; private set; }

	/// <summary>The link to hand the browser opponent while <see cref="Opening"/> (and
	/// on into the game — harmless once they've joined). Null for every other flow.</summary>
	public string ShareUrl => State?.share_url;

	/// <summary>Waiting on an opponent who isn't in this lobby — a lobby seek, a direct
	/// challenge someone hasn't accepted, or a shareable link nobody has opened yet. All
	/// are cancelled the same way and none is a table game, so the code that treats them
	/// alike reads this rather than any one flag.</summary>
	public bool AwaitingOpponent => Seeking || Challenging || Opening;

	/// <summary>
	/// Challenge a SPECIFIC lichess user by name.
	///
	/// <para>Reaches blitz where a seek cannot (lichess gates a challenge at blitz,
	/// a seek at rapid), and works at a table you're sitting at alone. Like a seek it
	/// needs only this player: the named user accepts in their own client.</para>
	///
	/// <para><b>Short-lived, and that is the cost of not porting
	/// <c>keepAliveStream</c>.</b> A real-time challenge is swept ~20s after lichess
	/// last saw it. The keep-alive stream exists to bump that, but its own trap
	/// (closing it does NOT withdraw the challenge) already bit this project once, and
	/// dropping it removes a third stream shape from the client. The HUD says the
	/// invitation is brief rather than pretending otherwise.</para>
	/// </summary>
	public void RequestChallenge( string opponent, bool rated )
	{
		if ( Engaged || Local == null || Station == null ) return;
		if ( LocalStationSeat is not { } seat ) return;
		if ( !LichessTable.CanMirror( Local.Tc ) ) return;
		if ( string.IsNullOrWhiteSpace( opponent ) ) return;
		if ( !LichessTokenStore.Linked ) return;

		Engaged = true;
		Challenging = true;
		ChallengeOpponent = opponent.Trim();
		_clientGameId = GamchessApi.NewClientGameId();
		Error = null;
		Fresh( seek: true, status: "challenging" );

		// The colour defaults to the SEAT you hold: a physical board has sides, and
		// the lichess game should mirror the one you're sitting at.
		string color = seat == ChessSeat.White ? "white" : "black";
		_ = RunChallenge( ChallengeOpponent, Local.Tc, rated, color );
	}

	async Task RunChallenge( string opponent, TimeControl tc, bool rated, string color )
	{
		var (ch, res) = await LichessBoard.ChallengeUser( opponent, tc, rated, color );
		if ( ch == null )
		{
			// lichess's own words are the useful ones ("No such user", "does not
			// accept challenges"). Report and stop — never retry.
			Error = res.Reason;
			Challenging = false;
			ChallengeOpponent = null;
			Engaged = false;
			Log.Info( $"[Gambit] lichess challenge refused: {Error}" );
			return;
		}

		_pendingChallengeId = ch.id;
		if ( State != null ) State.url = ch.url;

		// A stranger's acceptance arrives as gameStart on the event stream — there
		// is nothing else that reports it, now that keepAliveStream isn't used.
		_events = LichessEventStream.Listen( OnEvent );
	}

	string _pendingChallengeId;

	/// <summary>
	/// Mint a SHAREABLE link and play whoever opens it, on THIS board.
	///
	/// <para>The subtlest flow, and the one M8 got wrong. Create the open challenge
	/// ANONYMOUSLY (a <c>board:play</c> token 403s <c>/api/challenge/open</c>), then
	/// ACCEPT it with the player's own token to seat them — <b>skipping that accept is
	/// the M8 bug</b>, which left the creator's seat empty so the game never started —
	/// then publish the OPPOSITE colour's url and watch the event stream for the
	/// browser opponent joining.</para>
	///
	/// <para>Blitz+ only, because our side plays through the Board API. The colour is
	/// which side WE take; "random"/"" accepts without one and we learn our side from
	/// <c>gameFull</c>, same as a seek.</para>
	/// </summary>
	public void RequestOpenLink( bool rated, string color )
	{
		if ( Engaged || Local == null || Station == null ) return;
		if ( LocalStationSeat == null ) return;          // must be seated to spend our grant
		if ( !LichessTable.CanMirror( Local.Tc ) ) return;
		if ( !LichessTokenStore.Linked ) return;

		Engaged = true;
		Opening = true;
		_clientGameId = GamchessApi.NewClientGameId();
		Error = null;
		Fresh( seek: true, status: "waiting" );
		_ = RunOpen( Local.Tc, rated, color );
	}

	async Task RunOpen( TimeControl tc, bool rated, string color )
	{
		// ① Anonymously — the ONE call that must present no token.
		var (open, res) = await LichessBoard.OpenChallenge( tc, rated );
		if ( open == null )
		{
			Error = res.Reason;
			Opening = false;
			Engaged = false;
			Log.Info( $"[Gambit] lichess open link refused: {Error}" );
			return;
		}

		// ② Seat OURSELVES in it with our own token. Without this the challenge sits
		//    there anon-vs-anon and nothing ever starts.
		var accept = await LichessBoard.AcceptChallenge( open.id, color );
		if ( !accept.Ok )
		{
			// Best-effort withdraw: we created it anonymously, so a cancel may well be
			// refused — an unjoined open challenge expires on its own in 24h.
			_ = LichessBoard.CancelChallenge( open.id );
			Error = accept.Reason;
			Opening = false;
			Engaged = false;
			return;
		}

		// ③ Hand out the OPPOSITE colour's url.
		if ( State != null )
		{
			State.share_url = color switch
			{
				"white" => open.urlBlack,
				"black" => open.urlWhite,
				_ => open.url,
			};
			State.url = open.url;
		}

		_pendingChallengeId = open.id;
		_events = LichessEventStream.Listen( OnEvent );
	}

	/// <summary>The one-per-token event stream, for the flows that need it: a seek
	/// (whose own response carries no game id), a challenge to a stranger (whose
	/// acceptance has no other signal), and a shareable link (waiting for a browser
	/// opponent). The paired flow deliberately never reaches here.</summary>
	void OnEvent( LichessEvent ev )
	{
		if ( !Engaged ) return;

		switch ( ev.type )
		{
			case "gameStart":
				if ( ev.game?.gameId is not { Length: > 0 } id ) return;
				// A stale gameStart from an earlier flow would hijack this table. When
				// we know which challenge we are waiting for, insist on it.
				if ( _pendingChallengeId != null && id != _pendingChallengeId ) return;
				StartGameStream( id );
				break;

			case "challengeDeclined":
				if ( ev.challenge?.id == _pendingChallengeId )
				{
					string who = ChallengeOpponent ?? "They";
					Clear();
					Error = $"{who} declined the challenge.";
				}
				break;

			case "challengeCanceled":
				if ( ev.challenge?.id == _pendingChallengeId )
				{
					Clear();
					Error = "The challenge was cancelled.";
				}
				break;
		}
	}

	/// <summary>Open the game stream and stop waiting. Idempotent for a given id.</summary>
	void StartGameStream( string gameId )
	{
		if ( string.IsNullOrEmpty( gameId ) || _gameId == gameId ) return;

		_gameId = gameId;
		_stream?.Dispose();
		_stream = LichessBoard.StreamGame( gameId );

		if ( State != null )
		{
			State.game_id = gameId;
			State.status = "waiting";   // "live" once gameFull confirms it
		}

		// The seek is done: its held connection was the seek, and lichess has paired
		// us. Dropping it now is what stops us being pairable a second time.
		DropSeek();
	}

	/// <summary>Done with a FINISHED game (the New Game button, or standing up).</summary>
	public void DismissFinished()
	{
		if ( State is not { finished: true } ) return;
		Clear();
	}

	/// <summary>Withdraw an opponent request we're still waiting on — a seek, or a
	/// challenge a named user hasn't answered.
	///
	/// <para>Load-bearing for BOTH, in different ways. A seek's held connection IS the
	/// seek, so dropping it removes us from lichess's lobby. A challenge is NOT
	/// withdrawn by hanging up — an explicit <c>/cancel</c> is required, because
	/// closing a stream only stops lichess's pings and leaves the invitation
	/// acceptable for hours. Without this a player who walked away is dropped into a
	/// game nobody is sitting at.</para></summary>
	public void CancelWaiting()
	{
		if ( !Engaged ) return;
		if ( Playing ) return;   // too late — that's a resign, not a cancel

		string challenge = _pendingChallengeId;
		Clear();
		if ( !string.IsNullOrEmpty( challenge ) )
			_ = LichessBoard.CancelChallenge( challenge );
	}

	/// <summary>Where the local player is sitting at this table, per the station.
	/// <para>Distinct from <see cref="LocalSeat"/>, which reads the side lichess gave
	/// us: this one answers "should I be asking?", that one answers "which side am I
	/// playing?".</para></summary>
	ChessSeat? LocalStationSeat =>
		ChessStation.Active == Station && Station != null ? ChessStation.ActiveSeat : null;

	/// <summary>Start a fresh state object for a new flow.</summary>
	void Fresh( bool seek, string status )
	{
		State = new LichessPlayState { status = status, seek = seek, moves = "" };
		_renderedMoves = null;
		_game = null;
		_lastMoveUci = null;
		_gameId = null;
		_pendingChallengeId = null;
	}

	/// <summary>A paired flow failed before a game existed. Hands the board straight
	/// back and unfreezes the table's clocks.</summary>
	void Fail( string id, string why )
	{
		// A late failure for a game the table has already moved on from must not
		// clear the one now in flight.
		if ( id != _clientGameId ) return;

		Clear();
		_failedGameId = id;
		Error = why;

		// The host froze this table's clocks when it set LichessGame. Only a seated
		// client can see that lichess said no, so only we can unfreeze it — without
		// this the players get a live board with dead clocks and no explanation.
		Local?.ReportLichessFailed();
		Log.Info( $"[Gambit] lichess play refused: {why}" );
	}

	/// <summary>
	/// Start (or stop) relaying automatically, following the host's decision.
	///
	/// <para>The host freezes <c>LocalGameController.LichessGame</c> at game start
	/// from both seats' opt-in flags, so both clients see the same answer at the
	/// same moment and each acts for itself.</para>
	/// </summary>
	void AutoEngage()
	{
		if ( Local == null ) return;

		// A SEEK or a CHALLENGE is not a table game — it has its own id and starts
		// from a table the local controller knows nothing about. Leave it entirely
		// alone or we'd cancel the player's seek the instant they asked for it.
		if ( AwaitingOpponent || IsSeek ) return;

		// The table went idle, or the game wasn't a lichess one — hand the board
		// back to the local controller.
		bool tableIdle = !Local.Playing && !Local.GameOver;
		if ( !Local.LichessGame || tableIdle )
		{
			if ( Engaged ) Clear();
			if ( tableIdle ) _failedGameId = null;
			return;
		}

		if ( !Engaged && Local.Playing )
			RequestPlay();
	}

	/// <summary>Stand down: the table went idle, or the player left. Drops every
	/// connection and hands the board back to the local controller.</summary>
	public void Clear()
	{
		// Tell the room the show is over BEFORE forgetting we were in it.
		if ( Engaged && _reportedLive )
		{
			_reportedLive = false;
			ReportMirror( _reportedMoves ?? "", false );
		}
		_reportedMoves = null;

		// EVERY CONNECTION, ON EVERY EXIT PATH. A leaked game stream tells lichess
		// this player is still present (so the opponent gets no "opponent gone"
		// claim); a leaked event-stream reference holds this token's ONE slot, so
		// the next seek silently never starts. Both fail with no error.
		DropStreams();

		Engaged = false;
		Seeking = false;
		Challenging = false;
		Opening = false;
		ChallengeOpponent = null;
		_reportedResult = false;
		_archived = false;
		State = null;
		_game = null;
		Error = null;
		_clientGameId = null;
		_gameId = null;
		_pendingChallengeId = null;
		_renderedMoves = null;
		_lastMoveUci = null;
		_premoveUci = null;   // a premove must never outlive the game it was armed in
		_whiteBank = 0f;      // banked clocks belong to the game that's ending
		_blackBank = 0f;
		if ( Networking.IsHost ) ChallengeId = null;
	}

	void DropSeek()
	{
		_seekStream?.Dispose();
		_seekStream = null;
	}

	void DropStreams()
	{
		DropSeek();
		_stream?.Dispose();
		_stream = null;
		_events?.Dispose();
		_events = null;
	}

	/// <summary>Teardown and hotload. <b>Not optional</b> — see
	/// <see cref="DropStreams"/>: an orphaned read task leaves a live HTTP
	/// connection to lichess, which means "present" and "your event stream slot is
	/// taken", and nothing errors.</summary>
	protected override void OnDestroy() => DropStreams();

	protected override void OnDisabled() => DropStreams();

	protected override void OnUpdate()
	{
		AutoEngage();

		// The spectator mirror runs on EVERY client every frame.
		MaintainMirrorReport();
		MaintainMirrorGame();
		if ( Networking.IsHost && MirrorLive && Station is { AnySeatTaken: false } )
		{
			MirrorLive = false;
			MirrorMoves = null;
		}

		if ( !Engaged ) return;

		// The table reset under us (both players stood up) — drop it. Only for a
		// table game: a seek or challenge mints its own id and the table never knows it.
		if ( !AwaitingOpponent && !IsSeek && Local != null && Local.ClientGameId != _clientGameId )
		{
			Clear();
			return;
		}

		// Pump the event stream on the GAME THREAD. Reads complete on a thread-pool
		// thread, so this is where a listener may touch scene state — and the only
		// place it may.
		if ( _events != null ) LichessEventStream.Pump();

		// A seek's stream carries nothing to read; draining it is just how we notice
		// lichess closed it (paired, or expired).
		if ( _seekStream != null )
		{
			_seekStream.Drain();
			if ( _seekStream.Ended && _gameId == null )
			{
				// lichess closed the seek without pairing us, and no gameStart arrived.
				Clear();
				Error = "Lichess ended the seek without finding an opponent.";
				return;
			}
		}

		PumpGameStream();
	}

	/// <summary>Drain the game stream and fold each line into <see cref="State"/>.</summary>
	void PumpGameStream()
	{
		if ( _stream == null ) return;

		var lines = _stream.Drain();
		if ( lines != null )
		{
			foreach ( var line in lines ) Consume( line );
		}

		if ( _stream.Error is { } err && State is { finished: false } )
			Error = err;

		// A clean EOF on a game stream means the game is over — OR that this token
		// opened a second event stream somewhere else and lichess dropped us. There
		// is no way to tell those apart, so we do NOT declare a result: we stop, and
		// the last state lichess actually sent stands.
		if ( _stream.Ended && State is { finished: false } )
		{
			Log.Info( "[Gambit] the lichess game stream closed without a final state" );
			Error = "Lichess closed the game stream.";
			_stream.Dispose();
			_stream = null;
		}
	}

	/// <summary>One ndjson line from the game stream.</summary>
	void Consume( string line )
	{
		// Peek at the discriminator before committing to a shape — chatLine and
		// opponentGone arrive with neither.
		var head = LichessClient.Parse<LichessGameState>( line );
		if ( head?.type == null ) return;

		switch ( head.type )
		{
			case "gameFull":
				var full = LichessClient.Parse<LichessGameFull>( line );
				if ( full == null ) return;
				AdoptFull( full );
				break;

			case "gameState":
				AdoptState( head );
				break;

			// chatLine, opponentGone, and anything lichess adds later. opponentGone
			// is worth having and is not wired up — see PLAN.
			default:
				return;
		}
	}

	/// <summary>The first line of a game stream, and of every reconnect: names,
	/// clock, our colour, and the whole move list.</summary>
	void AdoptFull( LichessGameFull full )
	{
		State ??= new LichessPlayState();

		State.game_id = full.id;
		State.white_name = NameOf( full.white );
		State.black_name = NameOf( full.black );

		// WHICH SIDE ARE WE? From lichess, by matching our own linked id against the
		// two player ids — not from the station's seats. In a seek there is no
		// SteamID to match, and lichess is the authority either way.
		string me = LichessTokenStore.LichessId;
		if ( !string.IsNullOrEmpty( me ) )
		{
			if ( string.Equals( full.white?.id, me, StringComparison.OrdinalIgnoreCase ) )
				State.your_color = "white";
			else if ( string.Equals( full.black?.id, me, StringComparison.OrdinalIgnoreCase ) )
				State.your_color = "black";
		}

		if ( full.clock != null )
		{
			State.white_inc_ms = full.clock.increment;
			State.black_inc_ms = full.clock.increment;
		}

		if ( full.state != null ) AdoptState( full.state );

		static string NameOf( LichessGamePlayer p ) =>
			string.IsNullOrEmpty( p?.name ) ? "Anonymous" : p.name;
	}

	/// <summary>A gameState: the whole game so far. Rebuild from it.</summary>
	void AdoptState( LichessGameState st )
	{
		if ( State == null ) return;

		// Once lichess has actually paired us, we're no longer waiting.
		Seeking = false;
		Challenging = false;
		Opening = false;
		ChallengeOpponent = null;

		State.moves = st.moves ?? "";
		State.lichess_status = st.status;
		State.winner = st.winner;
		State.finished = LichessStatus.Finished( st.status );
		State.status = State.finished ? "over" : "live";
		State.white_draw = st.wdraw;
		State.black_draw = st.bdraw;
		State.white_takeback = st.wtakeback;
		State.black_takeback = st.btakeback;
		State.white_time_ms = st.wtime;
		State.black_time_ms = st.btime;

		// Snap the banked clocks. NO version gate: a stream only sends a state
		// because something really changed, so there is no duplicate to guard
		// against — which is exactly what made the M11 long poll sawtooth.
		_whiteBank = st.wtime / 1000f;
		_blackBank = st.btime / 1000f;
		_sinceBank = 0f;

		// A fresh state supersedes whatever went wrong last time — otherwise one
		// refused move would replace the turn indicator for the rest of the game.
		Error = null;

		// Lichess says it's over. The host's own rules never saw a single move of
		// this game, so its Phase would sit at Playing forever and the table would
		// never reset or offer a rematch — it has to be told.
		if ( State.finished && !State.seek && !_reportedResult )
		{
			_reportedResult = true;

			if ( ResultString is string result )
			{
				Local?.ReportLichessResult( result, OverReason );
			}
			else
			{
				// An abort has no result to report. Say so — without this the board
				// silently flips from the aborted position to a fresh local game with
				// running clocks and no explanation.
				//
				// Order matters twice: read OverReason BEFORE Clear() nulls State, and
				// set Error AFTER Clear() wipes it.
				string why = OverReason ?? "Aborted";
				string id = _clientGameId;

				Local?.ReportLichessFailed();
				Clear();
				_failedGameId = id;
				Error = $"lichess {why.ToLower()} the game";
				return;
			}
		}

		Rebuild( State.moves );

		// After the rebuild, so _game holds the finishing move.
		TryArchiveFinished();

		// After the rebuild, never before: the premove is aimed at the position
		// lichess just sent, and IsMyTurn reads the board Rebuild just built.
		FirePremove();
	}

	/// <summary>Archive a finished relayed lichess game to gamchess, once, so it lands
	/// in the private archive and the web viewer the same as a local game does.
	///
	/// <para><see cref="_game"/> is rebuilt from lichess's own authoritative move list,
	/// so its history is intact by construction — none of the resync-stub hazard that
	/// gates the local path. Both seats of a paired game post the same
	/// <c>client_game_id</c> and the server dedups.</para>
	///
	/// <para>An abort (<see cref="ResultString"/> null) archives nothing — same reason
	/// it sounds no fanfare and reports no result: lichess scored nothing.</para></summary>
	void TryArchiveFinished()
	{
		if ( _archived || !Engaged ) return;
		if ( State is not { finished: true } ) return;
		if ( ResultString is not string result ) return;   // abort: nothing to archive
		if ( _game == null || _game.MoveCount == 0 ) return;
		if ( Local is not { } local || Station is not { } station ) return;
		if ( string.IsNullOrEmpty( _clientGameId ) ) return;

		// Only a seat archives (the seats' SteamIDs are the archive's identity), and
		// the server 403s anyone else anyway. For a solo game one seat is 0 (the
		// stranger), which the server accepts as an empty seat.
		ulong me = Connection.Local?.SteamId ?? 0;
		if ( me == 0 || ( me != station.WhiteSteamId && me != station.BlackSteamId ) ) return;

		_archived = true;
		_ = LocalGameController.ArchiveGame( _clientGameId, BuildArchivePgn( result ),
			station.WhiteSteamId, station.BlackSteamId, result );
	}

	/// <summary>PGN for the archive: the lichess names, the table's time control, and the
	/// lichess result. No <c>%clk</c> — lichess sends a clock per move but this controller
	/// doesn't log it per ply. The result comes from lichess, never
	/// <c>_game.ResultString</c>: a game ended by resignation or flag is not terminal on
	/// the board.</summary>
	string BuildArchivePgn( string result )
	{
		_game.SetHeader( "Event", "Terry's Gambit (lichess)" );
		_game.SetHeader( "Site", "lichess.org" );
		_game.SetHeader( "Date", DateTime.UtcNow.ToString( "yyyy.MM.dd" ) );
		_game.SetHeader( "White", NameOr( State?.white_name ) );
		_game.SetHeader( "Black", NameOr( State?.black_name ) );
		_game.SetHeader( "Result", result );
		_game.SetHeader( "TimeControl", Local?.Tc.PgnSpec ?? "-" );
		return _game.Pgn;

		static string NameOr( string n ) => string.IsNullOrEmpty( n ) ? "Anonymous" : n;
	}

	/// <summary>
	/// Rebuild <see cref="Game"/> from lichess's UCI move list.
	///
	/// <para>Rebuilt from the start every time the list changes, rather than
	/// applying a delta. That sounds wasteful and isn't: a game is a few dozen
	/// moves, the rules are the vendored library that runs perft here, and it
	/// buys total immunity to ordering — there is no incremental state that can
	/// drift from lichess's.</para>
	/// </summary>
	void Rebuild( string moves )
	{
		moves ??= "";
		if ( moves == _renderedMoves && _game != null ) return;
		_renderedMoves = moves;

		var game = new ChessGame();
		_lastMoveUci = null;

		if ( moves.Length > 0 )
		{
			foreach ( var uci in moves.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
			{
				if ( !game.ApplyUci( uci ) )
				{
					// lichess sent something our rules won't take. That is either a
					// variant we never asked for or a bug on our side; either way the
					// honest thing is to stop rather than render a wrong board.
					Log.Warning( $"[Gambit] lichess sent a move our rules refused ({uci}) — board frozen" );
					return;
				}
				_lastMoveUci = uci;
			}
		}
		_game = game;
	}

	// ── Endings ──

	/// <summary>Local seated player resigns the lichess game.</summary>
	public void ResignLocal()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessBoard.Resign( _gameId );
	}

	/// <summary>Offer a draw, or accept one already offered — lichess treats both
	/// as the same call, and the state tells us which it'll be.</summary>
	public void OfferDraw()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessBoard.Draw( _gameId, accept: true );
	}

	/// <summary>True when the OTHER side has a draw offer standing.</summary>
	public bool DrawOffered =>
		State != null && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? State.black_draw : State.white_draw );

	/// <summary>True when WE have a draw offer standing.</summary>
	public bool DrawPending =>
		State != null && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? State.white_draw : State.black_draw );

	/// <summary>Decline the draw the opponent is offering.</summary>
	public void DeclineDraw()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessBoard.Draw( _gameId, accept: false );
	}

	/// <summary>Propose a takeback, or accept one already proposed — one call for
	/// both, exactly as with a draw.
	///
	/// <para>Nothing here reports whether it landed, because lichess doesn't tell
	/// us: it drops a takeback proposed before both sides have moved and still
	/// answers 200. <see cref="TakebackOffered"/> on the next state is the truth.</para></summary>
	public void OfferTakeback()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessBoard.Takeback( _gameId, accept: true );
	}

	/// <summary>Decline the takeback the opponent is proposing.</summary>
	public void DeclineTakeback()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessBoard.Takeback( _gameId, accept: false );
	}

	/// <summary>True when the OTHER side has a takeback proposal standing.</summary>
	public bool TakebackOffered =>
		State != null && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? State.black_takeback : State.white_takeback );

	/// <summary>True when WE have a takeback proposal standing — the button
	/// becomes "waiting", not a second proposal.</summary>
	public bool TakebackPending =>
		State != null && LocalSeat is { } seat
		&& ( seat == ChessSeat.White ? State.white_takeback : State.black_takeback );

	/// <summary>Takeback needs a move from each side; lichess silently drops one
	/// proposed earlier, so the button is hidden rather than dead.</summary>
	public bool CanTakeback =>
		Playing && LocalSeat != null && MoveCount >= 2;

	/// <summary>How many half-moves lichess has confirmed. Counted off the state's
	/// own move list rather than the rebuilt board, so it can't disagree with what
	/// lichess is gating on.</summary>
	int MoveCount =>
		string.IsNullOrWhiteSpace( State?.moves ) ? 0
			: State.moves.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length;

	/// <summary>Result string for the HUD, once lichess says it's over — or null
	/// when lichess ended the game WITHOUT a result.
	///
	/// <para>An aborted game is not a draw. lichess aborts a game nobody moved in,
	/// scores nothing and rates nothing, so falling through to "1/2-1/2" (as any
	/// "finished with no winner" rule would) would invent a half point that neither
	/// player earned.</para></summary>
	public string ResultString
	{
		get
		{
			if ( State == null || !State.finished ) return null;
			if ( State.lichess_status is "aborted" or "noStart" ) return null;
			return State.winner switch
			{
				"white" => "1-0",
				"black" => "0-1",
				_ => "1/2-1/2",
			};
		}
	}

	/// <summary>Why it ended, in lichess's words, mapped to ours.</summary>
	public string OverReason => State?.lichess_status switch
	{
		"mate" => "Checkmate",
		"resign" => "Resignation",
		"stalemate" => "Stalemate",
		"timeout" or "outoftime" => "Out of time",
		"draw" => "Draw",
		"aborted" => "Aborted",
		"noStart" => "Never started",
		"insufficientMaterialClaim" => "Insufficient material",
		_ => State != null && State.finished ? "Game over" : null,
	};

	static string ReadError( GamchessApi.Result res )
	{
		var body = GamchessApi.Deserialize<GamchessError>( res.Body );
		return !string.IsNullOrEmpty( body?.error ) ? body.error
			: res.Error ?? "Something went wrong.";
	}
}

/// <summary>Reply from <c>POST /api/v1/lichess/rendezvous</c> — who is sitting
/// opposite, once both seats have asked.</summary>
public sealed class LichessRendezvous
{
	public bool ready { get; set; }
	public string your_color { get; set; }
	public string opponent { get; set; }
	public string opponent_id { get; set; }
}
