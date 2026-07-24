using System;
using System.Linq;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Chess;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// A gamchess-authoritative RELAY game (matchmaking's "play in current sessions" mode,
/// M19): two players in SEPARATE lobbies, so there is no shared host to relay through —
/// gamchess is the authority instead. This is the ONE game type that cannot run without
/// gamchess, and it is what lets a player face themselves across two hosts.
///
/// <para>Structurally the lichess relay with gamchess in lichess's place, and simpler:
/// no token, no mirror, no seek. It POSTs the local player's moves, polls the opponent's,
/// and drives the same <see cref="IBoardGame"/> seam the board view / HUD / clock already
/// read — so a relay game renders and plays with no per-source branching, exactly as the
/// lichess one does. gamchess owns the CLOCK (ticked + flagged server-side); the client
/// runs the ticking side down locally between polls and never reads HIGH.</para>
///
/// <para>One relay game per client at a time, on the table the player engaged it from.
/// Attached beside <see cref="LocalGameController"/> / <see cref="LichessGameController"/>
/// by ChessRing.</para>
/// </summary>
public sealed class RelayGameController : Component, IBoardGame
{
	[Property] public ChessStation Station { get; set; }

	public static RelayGameController For( ChessStation station ) =>
		station?.Components.Get<RelayGameController>();

	/// <summary>A relay game owns this board right now.</summary>
	public bool Engaged { get; private set; }

	string _gameId;
	bool _iAmWhite;
	string _tcSpec;
	ChessGame _game;
	int _appliedPly;
	string _lastMoveUci;

	// Clocks in SECONDS, banked as of the last state (gamchess already ticked them to
	// "now" when it sent them); the ticking side counts down from _sinceBank.
	float _whiteBank, _blackBank;
	bool _untimed;
	RealTimeSince _sinceBank;
	float _lastRoundTrip;

	string _status = "live";   // live | white_won | black_won | draw | aborted
	string _reason = "";
	string _drawOffer = "";    // "" | "w" | "b"

	// For the archive (self-play games are logged too — white == black is fine).
	ulong _whiteSteam, _blackSteam;
	string _whiteName, _blackName;
	bool _archived;

	RealTimeUntil _pollNext;
	bool _polling;
	bool _moveInFlight;

	const float PollInterval = 1.2f;

	/// <summary>Start relaying a gamchess game on this station's board. <paramref name="tcSpec"/>
	/// is the PGN control ("180+2"/"-"); only used for the untimed check and the PGN header.</summary>
	public void Engage( string gameId, bool iAmWhite, string tcSpec )
	{
		if ( string.IsNullOrEmpty( gameId ) ) return;
		_gameId = gameId;
		_iAmWhite = iAmWhite;
		_tcSpec = tcSpec;
		_game = new ChessGame();
		_appliedPly = 0;
		_lastMoveUci = null;
		_status = "live";
		_reason = "";
		_drawOffer = "";
		_untimed = tcSpec is null or "" or "-";
		_whiteBank = _blackBank = 0f;
		_archived = false;
		_moveInFlight = false;

		ulong me = Connection.Local?.SteamId ?? 0;
		string myName = PlayerData.Load()?.DisplayName() ?? "You";
		_whiteSteam = _blackSteam = me;            // refined from the state on first poll
		_whiteName = iAmWhite ? myName : "Opponent";
		_blackName = iAmWhite ? "Opponent" : myName;

		Engaged = true;
		_pollNext = 0f; // poll immediately
	}

	public void Disengage()
	{
		Engaged = false;
		_gameId = null;
		_game = null;
	}

	// ── IBoardGame ──

	public ChessGame Game => _game;
	public bool Playing => Engaged && _status == "live" && _game != null;
	public bool GameOver => Engaged && _status != "live" && _game != null;
	public ChessSeat? LocalSeat => Engaged ? ( _iAmWhite ? ChessSeat.White : ChessSeat.Black ) : null;
	public string LastMoveUci => _lastMoveUci;

	public bool IsMyTurn =>
		Playing && _game != null && _game.WhiteToMove == _iAmWhite && !_moveInFlight;

	public float? SeatClock( ChessSeat seat )
	{
		if ( !Playing || _untimed || _game == null ) return null;
		float bank = seat == ChessSeat.White ? _whiteBank : _blackBank;
		var ticking = _game.WhiteToMove ? ChessSeat.White : ChessSeat.Black;
		// Only the side to move spends time; the idle side's bank is exact however stale
		// the frame is. Subtract a small transit lag off the ticking side and err LOW.
		if ( ticking != seat ) return MathF.Max( 0f, bank );
		float lag = MathF.Min( _lastRoundTrip, 2f );
		return MathF.Max( 0f, bank - lag - (float)_sinceBank );
	}

	public float? LocalSeatClock => LocalSeat is { } seat ? SeatClock( seat ) : null;

	public bool TryMakeMove( string uci )
	{
		if ( !IsMyTurn || string.IsNullOrEmpty( uci ) || uci.Length < 4 ) return false;
		if ( _game == null || !_game.LegalTargets( uci[..2] ).Contains( uci[2..4] ) ) return false;

		// Claim before awaiting (the TryArchive lesson: OnUpdate would fire a POST per
		// frame otherwise). We apply optimistically inside SendMove so the board moves now.
		_moveInFlight = true;
		_ = SendMove( uci );
		return true;
	}

	async Task SendMove( string uci )
	{
		// Optimistic local apply so our own move is instant; gamchess echoes it back on the
		// response, and Adopt rebuilds from its authoritative list.
		bool over = false;
		string result = null, reason = null;
		if ( _game != null && _game.ApplyUci( uci ) )
		{
			_lastMoveUci = _game.LastMoveUci;
			_appliedPly = _game.MoveCount;
			if ( _game.IsGameOver )
			{
				over = true;
				result = _game.Result switch
				{
					GameResult.WhiteWon => "white_won",
					GameResult.BlackWon => "black_won",
					_ => "draw",
				};
				reason = _game.ResultReason;
			}
		}

		var res = await MatchmakingApi.RelayMove( _gameId, uci, _game?.Fen ?? "", over, result, reason );
		_moveInFlight = false;
		if ( res.Ok || res.Status == 409 )
			Adopt( GamchessApi.Deserialize<MatchmakingApi.RelayState>( res.Body ) );
	}

	protected override void OnUpdate()
	{
		if ( !Engaged ) return;
		TryArchive();
		if ( _polling || _moveInFlight || (float)_pollNext > 0f ) return;
		_polling = true;
		_pollNext = PollInterval;
		_ = Poll();
	}

	async Task Poll()
	{
		RealTimeSince started = 0f;
		var res = await MatchmakingApi.RelayGet( _gameId, 0 ); // since 0 = the full move list
		_lastRoundTrip = MathF.Max( 0f, (float)started );
		_polling = false;
		if ( res.Ok )
			Adopt( GamchessApi.Deserialize<MatchmakingApi.RelayState>( res.Body ) );
	}

	/// <summary>Reconcile with gamchess's authoritative state: rebuild the board from the
	/// full move list if it advanced, re-bank the clocks, and pick up the result/offer.</summary>
	void Adopt( MatchmakingApi.RelayState st )
	{
		if ( st == null || !Engaged ) return;

		if ( st.Moves != null && st.Ply != _appliedPly )
			Rebuild( st.Moves, st.Ply );

		_untimed = st.Untimed;
		_whiteBank = st.WhiteMs / 1000f;
		_blackBank = st.BlackMs / 1000f;
		_sinceBank = 0f;
		_status = st.Status ?? "live";
		_reason = st.Reason ?? "";
		_drawOffer = st.DrawOffer ?? "";
		if ( ulong.TryParse( st.WhiteSteamId, out var wid ) ) _whiteSteam = wid;
		if ( ulong.TryParse( st.BlackSteamId, out var bid ) ) _blackSteam = bid;
	}

	/// <summary>Rebuild the position from gamchess's full UCI list — the same "no history,
	/// just replay the moves" approach the spectator mirror uses.</summary>
	void Rebuild( string[] moves, int ply )
	{
		var g = new ChessGame();
		foreach ( var uci in moves )
			if ( !g.ApplyUci( uci ) ) break; // desync guard — stop at the first bad move
		_game = g;
		_appliedPly = g.MoveCount;
		_lastMoveUci = moves.Length > 0 ? moves[^1] : _lastMoveUci;
		_ = ply;
	}

	// ── Offers (draw only; takeback isn't offered in a relay game v1) ──

	string MySide => _iAmWhite ? "w" : "b";
	string OppSide => _iAmWhite ? "b" : "w";

	public bool DrawOffered => Playing && _drawOffer == OppSide;
	public bool DrawPending => Playing && _drawOffer == MySide;

	public async void OfferDraw()
	{
		if ( !Playing ) return;
		// Offering into a standing offer accepts it, mirroring the local/lichess gesture.
		await Act( DrawOffered ? "draw-accept" : "draw-offer" );
	}

	public async void DeclineDraw()
	{
		if ( !Playing ) return;
		await Act( "draw-decline" );
	}

	public bool CanTakeback => false;
	public bool TakebackOffered => false;
	public bool TakebackPending => false;
	public void OfferTakeback() { }
	public void DeclineTakeback() { }

	/// <summary>Resign the relay game (the HUD's resign routes here when a relay owns the
	/// board). The game ends on gamchess's next state.</summary>
	public async void Resign()
	{
		if ( !Playing ) return;
		await Act( "resign" );
	}

	async Task Act( string action )
	{
		var res = await MatchmakingApi.RelayAction( _gameId, action );
		if ( res.Ok )
			Adopt( GamchessApi.Deserialize<MatchmakingApi.RelayState>( res.Body ) );
	}

	// ── Premove: not wired for relay v1 ──
	public string PremoveUci => null;
	public void SetPremove( string uci ) { }
	public void ClearPremove() { }
	public bool PremoveDropped => false;

	// ── Result display + archive ──

	public string ResultString => _status switch
	{
		"white_won" => "1-0",
		"black_won" => "0-1",
		"draw" => "1/2-1/2",
		_ => null,
	};

	public string OverReason => string.IsNullOrEmpty( _reason ) ? "Game over" : _reason;

	/// <summary>Log the finished game to the gamchess archive — including self-play games
	/// (white == black; the caller is a participant, so postGame accepts it). Idempotent on
	/// the relay game id, so both players POSTing is a no-op the second time.</summary>
	void TryArchive()
	{
		if ( !GameOver || _archived || _game == null || string.IsNullOrEmpty( _gameId ) ) return;
		if ( _game.MoveCount == 0 ) return; // nothing to archive
		_archived = true;

		_game.SetHeader( "Event", "Terry's Gambit online game" );
		_game.SetHeader( "Site", "Terry's Gambit (s&box)" );
		_game.SetHeader( "Date", DateTime.UtcNow.ToString( "yyyy.MM.dd" ) );
		_game.SetHeader( "White", string.IsNullOrEmpty( _whiteName ) ? "White" : _whiteName );
		_game.SetHeader( "Black", string.IsNullOrEmpty( _blackName ) ? "Black" : _blackName );
		_game.SetHeader( "Result", ResultString ?? "*" );
		_game.SetHeader( "TimeControl", string.IsNullOrEmpty( _tcSpec ) ? "-" : _tcSpec );

		_ = LocalGameController.ArchiveGame( _gameId, _game.Pgn, _whiteSteam, _blackSteam,
			ResultString ?? "*" );
	}
}
