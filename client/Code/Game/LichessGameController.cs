using System;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Chess;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// A real lichess game, rendered on a Gambit table (M8).
///
/// <para>Slots in beside <see cref="LocalGameController"/> behind
/// <see cref="IBoardGame"/>, so <see cref="ChessBoardView"/> renders it with no
/// change at all — that seam was built for exactly this.</para>
///
/// <para><b>Lichess is the only authority here.</b> This controller runs no
/// clock, adjudicates nothing, and never decides a game is over: it polls
/// gamchess for lichess's state and rebuilds the position from the UCI move list
/// lichess sends. That mirrors the local table's rule that only the host's tick
/// counts — one authority, and it isn't us.</para>
///
/// <para>Because every state carries the WHOLE move list from the start, a
/// dropped poll, a duplicate, or an out-of-order answer costs nothing: we rebuild
/// rather than reconcile. There is no incremental state to corrupt.</para>
///
/// <para>Local moves are optimistic only in the view's sense — we ask gamchess to
/// play them and wait for lichess to confirm via the next poll. We never apply a
/// move to <see cref="Game"/> ourselves, because lichess may refuse it (not your
/// turn, already flagged) and a board that showed a move lichess rejected would
/// be lying.</para>
///
/// <para><b>Never required.</b> Every failure path here degrades to "lichess play
/// didn't happen" and leaves the local game untouched.</para>
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

	/// <summary>Latest state gamchess published, or null before the first answer.</summary>
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

	/// <summary>A lichess game is live at this table right now — ours (polled), or
	/// someone else's (mirrored). Spectator-side participant gates all also check
	/// <c>LocalSeat</c>, so exposing Playing for a mirrored game enables the seam
	/// consumers (view, sounds, hands) without enabling any move/offer path.</summary>
	public bool Playing => Engaged
		? State != null && State.status == "live" && !State.finished
		: Mirroring;

	// ── The spectator mirror (M14) ──
	//
	// A lichess game was INVISIBLE to every non-participant by construction: nothing
	// about it was networked — each participant polls gamchess privately, a solo flow
	// (seek / challenge / shareable link) starts no local game at all, and Engaged only
	// ever goes true on the client that asked. So a bystander (and every joined client)
	// saw a frozen board, heard nothing, and the seated terries never moved — which
	// surfaced as "the joiner sees no animation" the first time two clients watched a
	// real lichess game. The room seeing the game IS the product; this is the relay.
	//
	// Shape: the same trust story as NetChessMove. The PARTICIPANT reports the observed
	// move list to the host ([Rpc.Host]); the host folds it into [Sync] fields; every
	// non-engaged client rebuilds a display-only ChessGame from the synced list, exactly
	// as Rebuild() does from the poll (rebuild-from-scratch, no incremental drift), and
	// exposes it through the SAME IBoardGame seam — so the view, the sounds and the
	// hands all light up for spectators with zero per-feature work, and a late joiner
	// gets the whole game off the snapshot for free. lichess stays the only authority:
	// this list is display, never archived, never moved-on, and both paired
	// participants reporting the same list is idempotent by the longer-list-wins fold.

	/// <summary>The relayed UCI move list of the lichess game at this table, folded by
	/// the host from participant reports. Null/empty when no lichess game is on.</summary>
	[Sync( SyncFlags.FromHost )] public string MirrorMoves { get; set; }

	/// <summary>The relayed game is live right now (drops false when it finishes or
	/// the participant stands down).</summary>
	[Sync( SyncFlags.FromHost )] public bool MirrorLive { get; set; }

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
	/// board straight back rather than displaying a result. Sounding a fanfare over a
	/// game that never started would be announcing a non-event — same reasoning as
	/// <see cref="ResultString"/> refusing to call it a draw.</para></summary>
	public bool GameOver => Engaged && State is { finished: true } && ResultString != null;

	/// <summary>Seconds left on a seat's clock, per lichess, counted down locally
	/// between moves.
	///
	/// <para>An unlimited game has no clock and lichess sends 0 for it, which would
	/// read as a permanently flagged clock. The table's own control is what tells us
	/// (a seek can never be unlimited — lichess's lobby refuses a clockless real-time
	/// seek — but a direct CHALLENGE can, so this is not gated on the game being a
	/// table game).</para>
	///
	/// <para><b>lichess only sends a clock when a MOVE happens</b>, so a raw value is
	/// frozen for the whole of a think — which reads as a stopped clock, not a thinking
	/// player. The freeze here was an MVP; a real game needs a ticking clock. So we bank
	/// the value lichess sent, and run the SIDE TO MOVE's clock down from it, snapping
	/// both back on the next state. This is exactly <see cref="LichessTvSource"/>'s
	/// machinery, including the correction that keeps it honest.</para>
	///
	/// <para><b>The house rule: a live clock must never read HIGHER than the time
	/// actually left.</b> A banked value is already stale on arrival by the lichess →
	/// gamchess → client latency, so counting down from it raw reads HIGH. We subtract
	/// that staleness (<see cref="_bankLag"/>, from the relay's clock_age_ms/hold_ms plus
	/// our measured round trip) and err deliberately LOW. We never adjudicate: a local
	/// clock reaching 0 clamps at 0 and waits for lichess to call the flag.</para></summary>
	public float? SeatClock( ChessSeat seat )
	{
		if ( !Playing || State is null ) return null;
		if ( Local?.Tc.IsUnlimited ?? false ) return null;

		float bank = seat == ChessSeat.White ? _whiteBank : _blackBank;
		// Only the side to move is spending time. The idle side's bank is exact
		// however stale the frame is, so the lag applies to the ticking seat alone —
		// subtracting it from both would invent a loss of time that never happened.
		if ( TickingSeat != seat ) return MathF.Max( 0f, bank );
		return MathF.Max( 0f, bank - _bankLag - (float)_sinceBank );
	}

	/// <summary>Whose clock is running: the side to move in the position lichess last
	/// sent. Null when no game is live. Drives the countdown in <see cref="SeatClock"/>.</summary>
	public ChessSeat? TickingSeat =>
		Playing && Game != null ? ( Game.WhiteToMove ? ChessSeat.White : ChessSeat.Black ) : null;

	// ── Local clock countdown (see SeatClock) ──
	// Banked clocks in SECONDS, when they landed, and how stale they were on arrival.
	// Snapped only on real news (a version advance) so a timed-out long poll can't
	// re-snap to an already-stale value and make the clock jump back UP — the sawtooth
	// that reads HIGH, the one thing the house rule forbids.
	float _whiteBank, _blackBank;
	RealTimeSince _sinceBank;
	float _bankLag;
	float _lastRoundTrip;

	/// <summary>Ceiling on the staleness correction — a backstop against a nonsense
	/// measurement, not a tuning knob. Eating a player's whole clock because one poll
	/// took a minute would be worse than the small bias it corrects.</summary>
	const float MaxClockLagSeconds = 10f;

	/// <summary>How stale this frame's clocks were on arrival: gamchess's own staleness
	/// (clock_age_ms) plus our network time (round trip − hold). Uses the FULL remaining
	/// round trip rather than halving it — the house rule is one-directional, so an
	/// undershoot is free where a fair estimate is a coin-flip on the forbidden outcome.
	/// Zero when the relay sends nothing (older gamchess), which keeps the old behaviour
	/// rather than breaking. Same as LichessTvSource.LagOf.</summary>
	float ClockLag( LichessPlayState st )
	{
		float age = st.clock_age_ms / 1000f;
		float hold = st.hold_ms / 1000f;
		float network = MathF.Max( 0f, _lastRoundTrip - hold );
		return Math.Clamp( age + network, 0f, MaxClockLagSeconds );
	}

	/// <summary>Seconds left on the local player's own clock. Null when we hold no seat
	/// in this game.</summary>
	public float? LocalSeatClock => LocalSeat is { } seat ? SeatClock( seat ) : null;

	/// <summary>The side the local player holds in the lichess game, or null.
	///
	/// <para>Read from <c>your_color</c>, which gamchess stamps per caller — not by
	/// matching SteamIDs. Two reasons: in a SEEK the opponent is a stranger with no
	/// SteamID to match against, and in either flow gamchess knows what game it
	/// actually started, so if its answer ever disagreed with the local station the
	/// board must follow lichess.</para></summary>
	public ChessSeat? LocalSeat => State?.your_color switch
	{
		"white" => ChessSeat.White,
		"black" => ChessSeat.Black,
		_ => null,
	};

	/// <summary>This is a game against a random lichess opponent, not the player
	/// opposite. Nobody is sitting in the other seat.</summary>
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

	/// <summary>Submit a move: ask gamchess, which asks lichess with our token.
	/// The board doesn't change until lichess confirms it on the next poll.</summary>
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
	/// from lichess's move list on every poll, so a premove holding a reference
	/// into the old position would be stale before it ever fired.</para></summary>
	string _premoveUci;

	/// <summary>The armed premove as UCI, or null. One value rather than two so a
	/// caller can watch the whole premove change — the HUD's repaint hash has no
	/// room to spend two slots on halves of the same thing.</summary>
	public string PremoveUci => _premoveUci;

	/// <summary>Arm a premove. The view decides the moment; this only sanity-checks.
	///
	/// <para>Deliberately NOT guarded on <c>IsMyTurn</c>: that also goes false while
	/// our own move is in flight, which is a real window in which the board still
	/// shows the pre-move position. The view gates on the board's own turn instead —
	/// see ChessBoardView.CanPremove.</para></summary>
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
	/// <para>It costs no clock time in any sense we control: lichess starts your
	/// clock when it publishes the opponent's move, and this fires on the poll
	/// that carries it — so a premove spends one poll's latency and no thinking
	/// time. It is not free, it is just as fast as knowing is possible.</para>
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
		// left armed through its own refusal would re-fire every poll for the rest
		// of the game.
		_premoveUci = null;

		// Use the answer — see IBoardGame.PremoveDropped for why throwing it away was
		// worse than it sounds. This catches a premove our OWN rules refuse (the
		// opponent didn't play into the position it was aimed at). A premove lichess
		// refuses after we've sent it surfaces through Error instead, on the next poll.
		if ( !TryMakeMove( uci ) )
			_premoveDropped = BoardGame.PremoveDroppedSeconds;
	}

	RealTimeUntil _premoveDropped;

	/// <summary>The last premove was refused, within the notice window.</summary>
	public bool PremoveDropped => (float)_premoveDropped > 0f;

	async Task SendMove( string uci )
	{
		var res = await LichessApi.Move( _clientGameId, uci );
		_moveInFlight = false;

		if ( res.Ok ) return;

		// lichess refused it (not your turn, game gone, token revoked). Say so and
		// let the next poll re-assert the true position — we never guessed at one.
		Error = ReadError( res );
		Log.Info( $"[Gambit] lichess refused a move: {Error}" );
	}

	// ── State ──

	string _clientGameId;   // the table id we asked to play, and the poll key
	ulong _version;         // long-poll cursor
	bool _pollInFlight;
	string _renderedMoves;  // the move list our Game was built from

	/// <summary>Have we already told the host this game's result? Claimed once, so
	/// a poll that repeats a finished state doesn't re-report every ~5s.</summary>
	bool _reportedResult;

	/// <summary>A ClientGameId whose lichess play already failed. Never asked for
	/// again.
	///
	/// <para>Needed because failing hands the board back (Engaged goes false), and
	/// AutoEngage's whole job is to engage an un-engaged lichess table — so without
	/// this the two would ping-pong: fail, disengage, re-request, fail, forever.
	/// Survives Clear() on purpose; only a NEW game at this table resets it.</para></summary>
	string _failedGameId;

	/// <summary>
	/// Ask gamchess to play this table's game on lichess.
	///
	/// <para>Called only by a SEATED client, for itself. The other seat's client
	/// makes the same call independently — gamchess pairs the two intents and only
	/// then issues a challenge. See LichessApi.Play for why that is the whole
	/// authorisation story rather than a formality.</para>
	///
	/// <para>Spectators never call this: they aren't seated, and gamchess would
	/// refuse them anyway.</para>
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

		ulong white = Station.WhiteSteamId, black = Station.BlackSteamId;
		if ( white == 0 || black == 0 ) return;

		Engaged = true;
		_clientGameId = id;
		_version = 0;
		Error = null;
		_ = SendPlay( id, white, black, Local.Tc );
	}

	/// <summary>
	/// Find a RANDOM lichess opponent from this table.
	///
	/// <para>Needs only this player — no pairing, because there is nobody to get
	/// consent from: you are spending your own grant to play a stranger who opts in
	/// on lichess's side by their own choice. So it works at a table you're sitting
	/// at alone, and the other seat is irrelevant.</para>
	///
	/// <para>The id is minted here rather than taken from the table: a seek isn't a
	/// table game, no local game starts, and there's nobody to share a rendezvous
	/// key with. It's just this client's handle on its own seek.</para>
	/// </summary>
	public void RequestSeek( bool rated, string ratingRange = null, string color = null )
	{
		if ( Engaged || Local == null || Station == null ) return;
		if ( LocalStationSeat == null ) return;
		if ( !LichessTable.CanSeek( Local.Tc ) ) return;

		Engaged = true;
		Seeking = true;
		_clientGameId = GamchessApi.NewClientGameId();
		_version = 0;
		Error = null;
		_ = SendSeek( _clientGameId, Local.Tc, rated, ratingRange, color );
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
	/// are cancelled the same way (the leave path, the cancel button) and none is a table
	/// game, so the code that treats them alike reads this rather than any one flag.</summary>
	public bool AwaitingOpponent => Seeking || Challenging || Opening;

	/// <summary>
	/// Challenge a SPECIFIC lichess user by name.
	///
	/// <para>Reaches blitz where a seek cannot (lichess gates a challenge at blitz,
	/// a seek at rapid), and works at a table you're sitting at alone — the opponent
	/// isn't in this lobby. Like a seek it needs only this player: the named user
	/// accepts in their own client, so there is nobody here to get consent from.</para>
	///
	/// <para>The colour defaults to the SEAT you hold: a physical board has sides,
	/// and the lichess game should mirror the one you're sitting at. That is also why
	/// this needs a station seat — without one we'd have no side to ask for.</para>
	///
	/// <para>The id is minted here, not taken from the table: a challenge to a
	/// stranger is not the table's two-seat game, no local game starts, and there is
	/// nobody to share a rendezvous key with.</para>
	/// </summary>
	public void RequestChallenge( string opponent, bool rated )
	{
		if ( Engaged || Local == null || Station == null ) return;
		if ( LocalStationSeat is not { } seat ) return;
		if ( !LichessTable.CanMirror( Local.Tc ) ) return;
		if ( string.IsNullOrWhiteSpace( opponent ) ) return;

		Engaged = true;
		Challenging = true;
		ChallengeOpponent = opponent.Trim();
		_clientGameId = GamchessApi.NewClientGameId();
		_version = 0;
		Error = null;
		string color = seat == ChessSeat.White ? "white" : "black";
		_ = SendChallenge( _clientGameId, ChallengeOpponent, Local.Tc, rated, color );
	}

	/// <summary>
	/// Mint a SHAREABLE link and play whoever opens it, on THIS board.
	///
	/// <para>Like a seek/challenge it needs only this player — the opponent is an
	/// anonymous browser, no lichess account required their side. gamchess seats our
	/// authed account in an open challenge (see the server's runOpen) and relays our
	/// side here; the returned <see cref="ShareUrl"/> is what we hand out.</para>
	///
	/// <para>Blitz+ only, because our side plays through the Board API. The colour is
	/// which side WE take — "random"/"" lets lichess pick, and we learn it from
	/// <see cref="LocalSeat"/> once the game starts, same as a seek.</para>
	/// </summary>
	public void RequestOpenLink( bool rated, string color )
	{
		if ( Engaged || Local == null || Station == null ) return;
		if ( LocalStationSeat == null ) return;          // must be seated to spend our grant
		if ( !LichessTable.CanMirror( Local.Tc ) ) return; // blitz+ — our side relays via the board API

		Engaged = true;
		Opening = true;
		_clientGameId = GamchessApi.NewClientGameId();
		_version = 0;
		Error = null;
		_ = SendOpen( _clientGameId, Local.Tc, rated, color );
	}

	async Task SendOpen( string id, TimeControl tc, bool rated, string color )
	{
		var res = await LichessApi.OpenLink( id, tc, rated, color );
		if ( res.Ok )
		{
			Adopt( GamchessApi.Deserialize<LichessPlayState>( res.Body ) );
			return;
		}

		Error = ReadError( res );
		Opening = false;
		Engaged = false;
		Log.Info( $"[Gambit] lichess open link refused: {Error}" );
	}

	async Task SendChallenge( string id, string opponent, TimeControl tc, bool rated, string color )
	{
		var res = await LichessApi.Challenge( id, opponent, tc, rated, color );
		if ( res.Ok )
		{
			Adopt( GamchessApi.Deserialize<LichessPlayState>( res.Body ) );
			return;
		}

		// lichess's own words are the useful ones ("No such user", "does not accept
		// challenges"). Report and stop — never retry, the etiquette rule.
		Error = ReadError( res );
		Challenging = false;
		ChallengeOpponent = null;
		Engaged = false;
		Log.Info( $"[Gambit] lichess challenge refused: {Error}" );
	}

	async Task SendSeek( string id, TimeControl tc, bool rated, string ratingRange, string color )
	{
		var res = await LichessApi.Seek( id, LichessTable.SeekTimeMinutes( tc ),
			tc.IncrementSeconds, rated, ratingRange, color );
		if ( res.Ok )
		{
			Adopt( GamchessApi.Deserialize<LichessPlayState>( res.Body ) );
			return;
		}

		// Expected outcomes here include the shared 5/min lobby budget being spent.
		// Report it and stop — never retry, which is how a throttle becomes a ban.
		Error = ReadError( res );
		Seeking = false;
		Engaged = false;
		Log.Info( $"[Gambit] lichess seek refused: {Error}" );
	}

	/// <summary>Done with a FINISHED game (the New Game button, or standing up): drop it
	/// locally and tell gamchess to release the server-side play — the pending slot and
	/// the token's event stream — so the NEXT link/seek starts clean. Without this the
	/// play lingers until the 10-minute sweep, which after a link game can leave a stale
	/// gameStart on the event stream that a fresh link would trip on.</summary>
	public void DismissFinished()
	{
		if ( State is not { finished: true } || string.IsNullOrEmpty( _clientGameId ) ) return;
		string id = _clientGameId;
		Clear();
		_ = LichessApi.Cancel( id ); // releases the pending slot + cancels the play's context
	}

	/// <summary>Withdraw an opponent request we're still waiting on — a seek, or a
	/// challenge a named user hasn't answered.
	///
	/// <para>Load-bearing for BOTH, in different ways gamchess handles. A seek's held
	/// connection IS the seek, so dropping it removes us from lichess's lobby. A
	/// challenge is NOT withdrawn by hanging up — gamchess POSTs an explicit /cancel
	/// (closing the keep-alive stream only stops lichess's pings, leaving the
	/// invitation acceptable for hours). Either way, without this a player who walked
	/// away is dropped into a game nobody is sitting at.</para></summary>
	public void CancelWaiting()
	{
		if ( !Engaged || string.IsNullOrEmpty( _clientGameId ) ) return;
		if ( Playing ) return;   // too late — that's a resign, not a cancel

		string id = _clientGameId;
		Clear();
		_ = LichessApi.Cancel( id );
	}

	/// <summary>Where the local player is sitting at this table, per the station.
	/// <para>Distinct from <see cref="LocalSeat"/>, which reads the seats gamchess
	/// echoed back for the lichess game: this one answers "should I be asking?",
	/// that one answers "which side am I playing?".</para></summary>
	ChessSeat? LocalStationSeat =>
		ChessStation.Active == Station && Station != null ? ChessStation.ActiveSeat : null;

	async Task SendPlay( string id, ulong white, ulong black, TimeControl tc )
	{
		var res = await LichessApi.Play( id, white, black, tc );
		if ( res.Ok )
		{
			Adopt( GamchessApi.Deserialize<LichessPlayState>( res.Body ) );
			return;
		}

		// Hand the board straight back, exactly as SendSeek does. Staying Engaged
		// with a null Game would blank the board — ChessBoardView.Source would keep
		// resolving to us and render nothing — while the local game carried on
		// invisibly underneath. AutoEngage can't rescue that: the table isn't idle
		// and LichessGame is still true.
		// A late failure for a game the table has already moved on from must not
		// clear the one now in flight.
		if ( id != _clientGameId ) return;

		string why = ReadError( res );
		Clear();
		_failedGameId = id;
		Error = why;

		// The host froze this table's clocks when it set LichessGame. Only a seated
		// client can see that lichess said no, so only we can unfreeze it — without
		// this the players get a live board with dead clocks and no explanation.
		Local?.ReportLichessFailed();
		Log.Info( $"[Gambit] lichess play refused: {Error}" );
	}

	/// <summary>
	/// Start (or stop) relaying automatically, following the host's decision.
	///
	/// <para>The host freezes <c>LocalGameController.LichessGame</c> at game start
	/// from both seats' opt-in flags, so both clients see the same answer at the
	/// same moment and each asks gamchess for itself. Driving off the synced flag
	/// rather than a button press is what keeps the two seats in step — they don't
	/// have to click at the same time, only both agree before the game starts.</para>
	/// </summary>
	void AutoEngage()
	{
		if ( Local == null ) return;

		// A SEEK or a CHALLENGE is not a table game — it has its own id, it starts
		// from a table the local controller knows nothing about. So none of the
		// table-following logic below applies: leave it entirely alone or we'd cancel
		// the player's seek/challenge the instant they asked for it. IsSeek covers it
		// once state has landed (a challenge is stranger-opposite too); AwaitingOpponent
		// covers the window before the first answer.
		if ( AwaitingOpponent || IsSeek ) return;

		// The table went idle, or the game wasn't a lichess one — hand the board
		// back to the local controller. (Idle is "neither playing nor showing a
		// result"; the result stays on display while anyone lingers.)
		bool tableIdle = !Local.Playing && !Local.GameOver;
		if ( !Local.LichessGame || tableIdle )
		{
			if ( Engaged ) Clear();
			// An idle table means the next game is a fresh one — stop remembering
			// that the last one was refused.
			if ( tableIdle ) _failedGameId = null;
			return;
		}

		if ( !Engaged && Local.Playing )
			RequestPlay();
	}

	/// <summary>Stand down: the table went idle, or the player left. Stops polling
	/// and hands the board back to the local controller.</summary>
	public void Clear()
	{
		// Tell the room the show is over BEFORE forgetting we were in it — a participant
		// standing down (game finished, table reset, player left) is exactly when the
		// spectators' mirrored board must stop claiming a live game.
		if ( Engaged && _reportedLive )
		{
			_reportedLive = false;
			ReportMirror( _reportedMoves ?? "", false );
		}
		_reportedMoves = null;

		Engaged = false;
		Seeking = false;
		Challenging = false;
		Opening = false;
		ChallengeOpponent = null;
		_pollBackoff = 0f;   // never let a dead game's backoff gag the next one
		_reportedResult = false;
		State = null;
		Game = null;
		Error = null;
		_clientGameId = null;
		_version = 0;
		_renderedMoves = null;
		_lastMoveUci = null;
		_premoveUci = null;   // a premove must never outlive the game it was armed in
		_whiteBank = 0f;      // banked clocks belong to the game that's ending
		_blackBank = 0f;
		_bankLag = 0f;
	}

	protected override void OnUpdate()
	{
		AutoEngage();

		// The spectator mirror runs on EVERY client every frame — the participant half
		// keeps the host's synced copy current, the spectator half keeps the display
		// game in step with it, and the host retires a mirror whose table has emptied
		// (belt-and-braces for a participant that vanished without a live=false).
		MaintainMirrorReport();
		MaintainMirrorGame();
		if ( Networking.IsHost && MirrorLive && Station is { AnySeatTaken: false } )
		{
			MirrorLive = false;
			MirrorMoves = null;
		}

		if ( !Engaged || string.IsNullOrEmpty( _clientGameId ) ) return;

		// The table reset under us (both players stood up) — drop it. Only for a
		// table game: a seek or challenge mints its own id and the table never knows it.
		if ( !AwaitingOpponent && !IsSeek && Local != null && Local.ClientGameId != _clientGameId )
		{
			Clear();
			return;
		}

		// Don't poll until the request POST (SendPlay/SendSeek/SendChallenge/SendOpen)
		// has come back and Adopted the first state. Engaged + _clientGameId are set
		// synchronously in RequestX, but the play doesn't exist on gamchess until the
		// POST lands — so a poll fired in that window 404s ("gamchess has no record"),
		// Clear()s us, and abandons the request client-side while the server keeps its
		// pending slot. State != null means the POST landed and the play exists.
		if ( State == null ) return;

		// One poll at a time. The request hangs server-side for ~5s, so this is a
		// long poll and not a busy loop: it re-issues as each answer lands.
		if ( _pollInFlight ) return;
		if ( (float)_pollBackoff > 0f ) return;
		if ( State.finished ) return; // nothing more to hear

		_pollInFlight = true;
		_ = Poll();
	}

	/// <summary>Gate on re-polling after a failure.
	///
	/// <para>Load-bearing: a long poll re-issues the instant its answer lands, which
	/// is right when the answer is a 200 held for ~5s and catastrophic when it isn't.
	/// GamchessApi's circuit breaker only opens on 5xx and transport errors, so a
	/// 4xx returns immediately and <c>OnUpdate</c> would fire another request the
	/// very next frame — hundreds per second at our own server, for as long as
	/// someone sat at the table.</para></summary>
	RealTimeUntil _pollBackoff;

	/// <summary>How long to wait after a poll we can't act on.</summary>
	const float PollBackoffSeconds = 3f;

	async Task Poll()
	{
		// Time our own round trip so ClockLag can take the network leg off the frame's
		// age. Most of a long poll's round trip is gamchess WAITING, not the network —
		// hold_ms in the answer is what lets us subtract that back out.
		RealTimeSince sent = 0f;
		var res = await LichessApi.PollState( _clientGameId, _version );
		_lastRoundTrip = (float)sent;
		_pollInFlight = false;

		if ( !res.Ok )
		{
			// 404 means gamchess has no such game: it aged out of the relay's store,
			// or never started. Nothing more will ever come of this one, so stop
			// rather than back off — hand the board back to the local game.
			if ( res.NotFound )
			{
				// Blacklist BEFORE clearing wipes the id: without this, AutoEngage
				// re-requests the same game next frame and we loop POST→404→POST.
				string dead = _clientGameId;
				bool wasTableGame = !IsSeek;
				Log.Info( "[Gambit] gamchess has no record of this lichess game — dropping it" );
				Clear();
				if ( wasTableGame )
				{
					_failedGameId = dead;
					Local?.ReportLichessFailed();
				}
				Error = "gamchess lost track of this game.";
				return;
			}

			// Anything else (offline, 401, 400): wait before asking again. Never
			// fatal — the local game is untouched either way.
			_pollBackoff = PollBackoffSeconds;
			return;
		}

		_pollBackoff = 0f;
		Adopt( GamchessApi.Deserialize<LichessPlayState>( res.Body ) );
	}

	/// <summary>Take a published state and rebuild the board from it.</summary>
	void Adopt( LichessPlayState st )
	{
		if ( st == null ) return;

		// A game that failed (seek budget spent, nobody took it, lichess refused,
		// cancelled) hands the board straight back to the local game.
		//
		// Clear(), not two assignments: leaving State set would keep IsSeek true
		// forever, and AutoEngage early-returns on IsSeek — so one failed seek would
		// stop that table ever auto-engaging a lichess game again, while the host
		// went on freezing its clocks because LichessGame was still set. Dead table.
		if ( st.status == "failed" )
		{
			string why = string.IsNullOrEmpty( st.error ) ? "lichess couldn't start the game." : st.error;
			bool wasTableGame = !st.seek;
			string id = _clientGameId;

			Clear();
			// A table game must not be retried — AutoEngage would re-ask forever. A
			// seek mints a fresh id per attempt, so there's nothing to blacklist and
			// the player is free to press the button again.
			if ( wasTableGame )
			{
				_failedGameId = id;
				Local?.ReportLichessFailed();   // unfreeze the table's clocks
			}
			Error = why;
			Log.Info( $"[Gambit] lichess game failed: {Error}" );
			return;
		}

		// Is this real news, or a timed-out long poll answering with the same state?
		// Only real news re-snaps the clocks (see the snap below) — computed before
		// _version moves. `!=`, not `>`: a relay that restarted would reset the version.
		bool clockNews = st.version != _version;

		State = st;
		_version = st.version;

		// Snap the banked clocks to lichess's values, but only on real news. Re-snapping
		// on a timed-out poll would reset the countdown to an already-stale value, so the
		// clock would tick down then jump back UP — the sawtooth that reads HIGH. Placed
		// before Rebuild so nothing reads a half-updated board; the banks don't depend on
		// it. A finished game doesn't tick (SeatClock returns null once !Playing), so
		// snapping through the game-over branch below is harmless.
		if ( clockNews )
		{
			_whiteBank = st.white_time_ms / 1000f;
			_blackBank = st.black_time_ms / 1000f;
			_bankLag = ClockLag( st );
			_sinceBank = 0f;
		}

		// A fresh answer supersedes whatever went wrong last time — otherwise one
		// refused move would replace the turn indicator for the rest of the game
		// (GameHud reads Error ahead of everything else).
		Error = string.IsNullOrEmpty( st.error ) ? null : st.error;

		// Once lichess has actually paired us, we're no longer waiting. A challenge
		// stays "challenging" until the opponent accepts, so it drops later than a
		// seek — but both clear the moment a game is live.
		if ( st.status == "live" || st.finished )
		{
			Seeking = false;
			Challenging = false;
			Opening = false;
			ChallengeOpponent = null;
		}

		// Lichess says it's over. The host's own rules never saw a single move of
		// this game, so its Phase would sit at Playing forever and the table would
		// never reset or offer a rematch — it has to be told. Both seats report;
		// the host's guards make the second a no-op. A seek isn't a table game and
		// has nothing to report.
		if ( st.finished && !st.seek && !_reportedResult )
		{
			_reportedResult = true;

			// An abort has no result to report (see ResultString). Release the table
			// the same way a refusal does — nothing happened on lichess, so the
			// table falls back to being an ordinary local game rather than showing
			// a score nobody earned.
			if ( ResultString is string result )
			{
				Local?.ReportLichessResult( result, OverReason );
			}
			else
			{
				// Say so. Without this the board silently flips from the aborted
				// position to a fresh local game with running clocks and no
				// explanation — the exact failure the lichess-err line exists for.
				//
				// Order matters twice: read OverReason BEFORE Clear() nulls State,
				// and set Error AFTER Clear() wipes it.
				string why = OverReason ?? "Aborted";
				string id = _clientGameId;

				Local?.ReportLichessFailed();
				Clear();
				// Clearing hands the board back immediately, which opens a window
				// where a non-host client's LichessGame hasn't synced false yet —
				// AutoEngage would re-POST this very game. Blacklisting the id shuts
				// that window.
				_failedGameId = id;
				Error = $"lichess {why.ToLower()} the game";
			}
		}

		Rebuild( st.moves );

		// After the rebuild, never before: the premove is aimed at the position
		// lichess just sent, and IsMyTurn reads the board Rebuild just built.
		FirePremove();
	}

	/// <summary>
	/// Rebuild <see cref="Game"/> from lichess's UCI move list.
	///
	/// <para>Rebuilt from the start every time the list changes, rather than
	/// applying a delta. That sounds wasteful and isn't: a game is a few dozen
	/// moves, the rules are the vendored library that runs perft here, and it
	/// buys total immunity to poll ordering — there is no incremental state that
	/// can drift from lichess's.</para>
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
		_ = LichessApi.Resign( _clientGameId );
	}

	/// <summary>Offer a draw, or accept one already offered — lichess treats both
	/// as the same call, and the state tells us which it'll be.</summary>
	public void OfferDraw()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessApi.OfferDraw( _clientGameId );
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
		_ = LichessApi.DeclineDraw( _clientGameId );
	}

	/// <summary>Propose a takeback, or accept one already proposed — one call for
	/// both, exactly as with a draw.
	///
	/// <para>Nothing here reports whether it landed, because lichess doesn't tell
	/// us: it drops a takeback proposed before both sides have moved and still
	/// answers 200. <see cref="TakebackOffered"/> on the next poll is the truth,
	/// which is why the button doesn't try to look like it worked.</para></summary>
	public void OfferTakeback()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessApi.OfferTakeback( _clientGameId );
	}

	/// <summary>Decline the takeback the opponent is proposing.</summary>
	public void DeclineTakeback()
	{
		if ( !Playing || LocalSeat == null ) return;
		_ = LichessApi.DeclineTakeback( _clientGameId );
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
