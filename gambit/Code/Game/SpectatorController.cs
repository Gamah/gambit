using System.Collections.Generic;
using Gambit.Api;
using Gambit.Chess;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// Drives the spectator wall board (PLAN.md M5) — a big display panel that shows a
/// live chess position. Three sources, all reachable <b>without</b> the unavailable
/// ndjson stream (risk 1):
///
/// <list type="bullet">
/// <item><b>Featured board</b> — mirror a live game happening at an sbox table. Rides
/// the existing M4 <c>[Sync]</c> FEN relay (<see cref="LichessPlayController"/>) and the
/// host-folded local-game FEN, so it is real-time and needs no lichess API at all.</item>
/// <item><b>Lichess TV</b> — poll <c>GET /api/tv/channels</c> (plain-JSON snapshot) for a
/// channel's featured game, then poll its position via the game export (PGN → FEN).
/// Coarse-latency (a few seconds behind), not real-time.</item>
/// <item><b>Watch by ID</b> — same export poll for a specific game id. lichess delays the
/// public export ~3 moves for ongoing games (fair play) — surfaced in the UI.</item>
/// </list>
///
/// <para>Entirely client-local (each client polls lichess itself / reads its own synced
/// copy of the relay). One instance, on the SpectatorWall GO.</para>
/// </summary>
public sealed class SpectatorController : Component
{
	public static SpectatorController Instance { get; private set; }

	public enum Channel { Featured, LichessTv, WatchId }

	Channel _channel = Channel.Featured;
	public Channel Current => _channel;

	// ── What the board panel reads ──
	public string Fen { get; private set; }
	public string LastMoveUci { get; private set; }
	public string WhiteName { get; private set; } = "White";
	public string BlackName { get; private set; } = "Black";
	/// <summary>One-line label above the board ("FEATURED · Table 3", "LICHESS TV · rapid", …).</summary>
	public string ChannelLabel { get; private set; } = "SPECTATE";
	/// <summary>Status/help line under the board (delay notice, "no live games", errors).</summary>
	public string StatusText { get; private set; }
	public bool HasPosition => !string.IsNullOrEmpty( Fen );

	/// <summary>Full-move number the board is currently showing, from the FEN's last field
	/// (0 when there's no position). Derived, so it's free to poll every frame.</summary>
	public int MoveNumber
	{
		get
		{
			var fen = Fen;
			if ( string.IsNullOrEmpty( fen ) ) return 0;
			int sp = fen.LastIndexOf( ' ' );
			return sp >= 0 && sp + 1 < fen.Length && int.TryParse( fen[( sp + 1 )..], out var n ) && n > 0 ? n : 0;
		}
	}

	/// <summary>Time control in "3+2" form parsed from the export PGN, or null (the sbox-table
	/// featured source carries no PGN, so it's TV / watch-by-id only).</summary>
	public string TimeControl { get; private set; }

	// ── Player metadata for the scoreboard panel (parsed from the export PGN for lichess
	//    sources; unknown for the sbox-table featured source). ──
	/// <summary>Elo, or 0 when unknown (sbox players, unrated games, missing header).</summary>
	public int WhiteRating { get; private set; }
	public int BlackRating { get; private set; }
	/// <summary>Player title ("GM", "BOT", …) or null.</summary>
	public string WhiteTitle { get; private set; }
	public string BlackTitle { get; private set; }
	/// <summary>Live clock (seconds) for each side, or &lt; 0 when unknown. The side to move
	/// ticks down from the last polled value; the other side holds. Coarse — it restarts from
	/// the poll each time (the poll latency is already surfaced in <see cref="StatusText"/>).</summary>
	public double WhiteClock => LiveClock( true );
	public double BlackClock => LiveClock( false );
	public bool HasClocks => _whiteClockBase >= 0 && _blackClockBase >= 0;
	/// <summary>Whose clock is ticking (drives the scoreboard's active-clock highlight).</summary>
	public bool WhiteToMoveNow => _whiteToMove;

	double _whiteClockBase = -1, _blackClockBase = -1; // seconds as of the last poll
	RealTimeSince _sinceClock;                          // time since that poll
	bool _whiteToMove = true;                            // whose clock is ticking
	bool _clocksFrozen;                                  // game over → stop ticking

	double LiveClock( bool white )
	{
		double basis = white ? _whiteClockBase : _blackClockBase;
		if ( basis < 0 ) return -1;
		if ( !_clocksFrozen && white == _whiteToMove )
			return System.Math.Max( 0, basis - _sinceClock );
		return basis;
	}

	// ── TV / watch state ──
	string _tvChannelKey;             // null = auto-pick the best channel
	string _tvGameId;
	string _watchId;
	RealTimeSince _sincePoll = 999f;
	bool _tvGameOver;                 // featured game finished AND its fanfare shown — re-pick next poll
	bool _polling;
	// Poll every few seconds (lichess delays the public export anyway). We don't render straight
	// from the poll — see the buffered playout below.
	const float PollInterval = 3f;

	// ── Buffered playout (a jitter buffer) ──
	// Each poll batches several moves. Instead of showing them as they arrive (which bursts and
	// hangs, and can't pace bullet), we hold a cushion of PreRollLag moves and reveal them at the
	// game's OWN average rate — so the board plays smoothly and continuously, bullet included, at
	// the cost of running a few more seconds behind live (fine: it's delayed, not real-time). The
	// cushion absorbs poll jitter; "Buffering…" shows while it first fills; if we ever fall past
	// MaxLag we drop old moves to catch up.
	List<(string fen, string uci)> _positions = new();
	int _shownPly;                    // playout cursor (revealed plies, 1-based)
	string _shownGameId;              // game the buffer belongs to (id change ⇒ new game)
	bool _replayActive;               // a PGN source is driving playout (false for sbox tables)
	bool _playing;                    // past the pre-roll cushion; actively revealing
	int _prevCount;                   // _positions.Count at the previous poll (per-poll move delta)
	float _avgMovesPerPoll = 3f;      // EMA of moves added per poll (incoming-rate estimate)
	RealTimeSince _sinceReveal;
	float _nextGap;                   // seconds until the next ply is revealed
	const int PreRollLag = 8;         // buffer this many moves before starting to play (the "clumps")
	const int MaxLag = 22;            // fall this far behind ⇒ drop old moves to catch up
	const float MinReplayGap = 0.2f;  // = SpectatorBoard3D.MoveSeconds — fast games chain moves, no blur
	const float MaxReplayGap = 1.2f;  // slow games: don't stall too long between moves

	/// <summary>True while the pre-roll cushion is still filling (the walk-up board shows
	/// "Buffering…"). Distinct from <see cref="ShowingResult"/>.</summary>
	public bool Buffering => _replayActive && !_playing && !ShowingResult;

	// ── End-of-game fanfare ──
	bool _bufferGameOver;             // the buffered game has a decisive result queued
	string _resultDoneGameId;         // game whose fanfare has already been shown (don't repeat it)
	string _pendingHeadline, _pendingReason;
	int _pendingWinner;
	/// <summary>True while the end-of-game result banner is held on screen (before the next game).</summary>
	public bool ShowingResult { get; private set; }
	/// <summary>"White wins" / "Black wins" / "Draw" — set while <see cref="ShowingResult"/>.</summary>
	public string ResultHeadline { get; private set; }
	/// <summary>"Checkmate" / "on time" / "by resignation" / … — set while <see cref="ShowingResult"/>.</summary>
	public string ResultReason { get; private set; }
	/// <summary>+1 White won, −1 Black won, 0 draw — for banner colouring.</summary>
	public int ResultWinner { get; private set; }
	RealTimeSince _sinceResultShown;
	const float FanfareSeconds = 3f;  // hold the result at least this long before the next game

	protected override void OnEnabled() => Instance = this;
	protected override void OnDisabled() { if ( Instance == this ) Instance = null; }

	protected override void OnUpdate()
	{
		switch ( _channel )
		{
			case Channel.Featured:
				UpdateFeatured();
				break;
			case Channel.LichessTv:
			case Channel.WatchId:
				MaybePoll();
				AdvanceReplay();
				break;
		}
	}

	// ── Channel switching (called by SpectatorScreen) ──

	public void ShowFeatured()
	{
		_channel = Channel.Featured;
		_featuredIndex = 0;
		ClearPosition();
	}

	public void ShowLichessTv( string channelKey = null )
	{
		_channel = Channel.LichessTv;
		_tvChannelKey = channelKey;
		_tvGameId = null;
		_tvGameOver = false;
		ClearPosition();
		StatusText = "Loading lichess TV…";
		_sincePoll = 999f; // poll promptly
	}

	/// <summary>Watch a specific game by id or a lichess URL.</summary>
	public void WatchGame( string idOrUrl )
	{
		var id = ExtractGameId( idOrUrl );
		if ( string.IsNullOrEmpty( id ) )
		{
			StatusText = "Couldn't read a game id from that.";
			return;
		}
		_channel = Channel.WatchId;
		_watchId = id;
		ClearPosition();
		StatusText = "Loading game…";
		_sincePoll = 999f;
	}

	// ── Featured sbox game (real-time, no API) ──

	int _featuredIndex;

	/// <summary>Cycle to the next live sbox game (button on the wall screen).</summary>
	public void CycleFeatured()
	{
		_channel = Channel.Featured;
		_featuredIndex++;
	}

	void UpdateFeatured()
	{
		var live = CollectLiveTables();
		if ( live.Count == 0 )
		{
			ClearPosition();
			ChannelLabel = "FEATURED";
			StatusText = "No live games at the tables right now — try lichess TV.";
			return;
		}

		int idx = ( _featuredIndex % live.Count + live.Count ) % live.Count;
		var t = live[idx];
		Fen = t.fen;
		LastMoveUci = t.lastMove;
		WhiteName = t.white;
		BlackName = t.black;
		// The sbox-table relay carries names only — no lichess Elo/clock for this source.
		ClearPlayerMeta();
		ChannelLabel = live.Count > 1 ? $"FEATURED · Table {t.number} ({idx + 1}/{live.Count})" : $"FEATURED · Table {t.number}";
		StatusText = null;
	}

	/// <summary>Every sbox table currently showing a live game, read from synced/local
	/// state only (the M4 relay for lichess games, the host-folded FEN for local games) —
	/// so it works identically on every client with no token or API involved.</summary>
	List<(string fen, string lastMove, string white, string black, string number)> CollectLiveTables()
	{
		var list = new List<(string, string, string, string, string)>();
		foreach ( var st in Scene.GetAllComponents<ChessStation>() )
		{
			string number = TableNumber( st );

			var lp = LichessPlayController.For( st );
			if ( lp is { RelayLive: true } && !string.IsNullOrEmpty( lp.RelayFen ) )
			{
				list.Add( (lp.RelayFen, lp.RelayLastMove,
					lp.RelayWhiteName ?? "White", lp.RelayBlackName ?? "Black", number) );
				continue;
			}

			var lc = LocalGameController.For( st );
			if ( lc is { Playing: true } && lc.Game != null )
			{
				list.Add( (lc.Game.Fen, lc.Game.LastMoveUci,
					st.WhiteName ?? "White", st.BlackName ?? "Black", number) );
			}
		}
		return list;
	}

	static string TableNumber( ChessStation st )
	{
		// Stations are named "ChessStation{i}" by ChessRing.
		var name = st.GameObject.Name;
		int i = name.Length;
		while ( i > 0 && char.IsDigit( name[i - 1] ) ) i--;
		return i < name.Length ? name[i..] : "?";
	}

	// ── Lichess TV / watch-by-id (polled) ──

	void MaybePoll()
	{
		if ( _polling || LichessApi.Busy ) return;
		if ( _sincePoll < PollInterval ) return;
		_sincePoll = 0f;
		if ( _channel == Channel.LichessTv ) Poll( PollTv() );
		else Poll( PollWatch() );
	}

	async void Poll( System.Threading.Tasks.Task task )
	{
		_polling = true;
		try { await task; }
		finally { _polling = false; }
	}

	async System.Threading.Tasks.Task PollTv()
	{
		// (Re)pick the channel's featured game only when we have none, or when the one we were
		// watching has finished playing out AND its end-of-game fanfare has been shown
		// (_tvGameOver is set by the playout, not the poll — so we don't yank to a new game while
		// still buffering/replaying the current one). We poll our pinned game id until it ends, so
		// no periodic refresh is needed.
		if ( _tvGameId == null || _tvGameOver )
		{
			var chres = await LichessApi.GetTvChannels();
			if ( !chres.Ok )
			{
				// Keep showing the last position if we still have one; only surface the error
				// when the board is empty.
				if ( !HasPosition ) StatusText = "Couldn't reach lichess TV.";
				return;
			}

			var channels = LichessApi.Deserialize<Dictionary<string, LichessTvChannel>>( chres.Body );
			var ch = PickChannel( channels, _tvChannelKey );
			if ( ch == null || string.IsNullOrEmpty( ch.gameId ) )
			{
				if ( !HasPosition ) StatusText = "No featured game on that channel.";
				return;
			}

			// If lichess is still featuring the game we just finished + fanfared, wait for it to
			// rotate to a live one — retry next poll (keep _tvGameOver set) rather than re-showing
			// a finished game.
			if ( _tvGameOver && ch.gameId == _resultDoneGameId )
				return;

			// A new featured game — reset the board so the last position doesn't linger under
			// the incoming one.
			if ( ch.gameId != _tvGameId ) ClearPosition();
			_tvGameId = ch.gameId;
			_tvGameOver = false;
			WhiteName = ch.color == "black" ? "opponent" : ch.user?.name ?? "White";
			BlackName = ch.color == "black" ? ch.user?.name ?? "Black" : "opponent";
			ChannelLabel = $"LICHESS TV · {_tvChannelKey ?? "featured"}";
		}

		await FetchPosition( _tvGameId, "lichess TV updates a few seconds behind (fair-play delay)." );
	}

	async System.Threading.Tasks.Task PollWatch()
	{
		ChannelLabel = "WATCHING";
		await FetchPosition( _watchId, "Ongoing games are shown ~3 moves behind (lichess fair-play delay)." );
	}

	async System.Threading.Tasks.Task FetchPosition( string gameId, string delayNote )
	{
		// clocks:true so the PGN carries %clk comments for the scoreboard — the vendor parser
		// strips {…} comments before reading SAN, so FEN reconstruction is unaffected.
		var res = await LichessApi.GameExportPgn( gameId, clocks: true );
		if ( !res.Ok )
		{
			StatusText = res.Unauthorized ? "That game is private." : "Couldn't load that game.";
			return;
		}

		var positions = ChessGame.PgnPositions( res.Body );
		bool over = PgnGameOver( res.Body );
		_clocksFrozen = over;

		// Names/ratings/titles/clocks for the tags (headers are present even with no moves). Clock
		// ownership is decided by the LATEST position's side to move — not the (possibly lagging)
		// one the board is currently replaying.
		string latestFen = positions.Count > 0 ? positions[^1].fen : Fen;
		ApplyPlayerMeta( res.Body, latestFen );

		// Feed the buffered playout. Parse the result up front so the fanfare can fire once the
		// playout reaches the end. We do NOT re-pick on `over` here — the playout owns that, after
		// the board has actually replayed to the finish and the banner has had its moment.
		var result = over ? ParseResult( res.Body ) : default;
		IngestPositions( gameId, positions, over, result );
		StatusText = delayNote;
	}

	/// <summary>Fold a poll's position list into the jitter buffer. First sight of a game freezes
	/// on the current position and fills the pre-roll cushion; later polls extend the buffer and
	/// track the incoming move rate. AdvanceReplay does the actual paced reveal.</summary>
	void IngestPositions( string gameId, List<(string fen, string uci)> positions, bool over,
		(string headline, string reason, int winner) result )
	{
		if ( positions.Count == 0 )
		{
			// Headers-only (no moves yet) — nothing to replay; the board idles on the start
			// position (empty Fen). Keep any prior position we were showing.
			_positions = positions;
			_replayActive = false;
			return;
		}

		if ( gameId != _shownGameId )
		{
			// New game: tune in HERE (skip the history before now). The board freezes on the
			// current position and shows "Buffering…" until the cushion fills.
			_shownGameId = gameId;
			_positions = positions;
			_replayActive = true;
			_bufferGameOver = false;
			_shownPly = positions.Count;
			_prevCount = positions.Count;
			_avgMovesPerPoll = 3f;
			if ( over )
			{
				// Tuned into an already-finished game (watch-by-id) — show the final position and
				// fanfare it straight away, no buffering.
				_playing = true;
				_bufferGameOver = true;
				_pendingHeadline = result.headline;
				_pendingReason = result.reason;
				_pendingWinner = result.winner;
			}
			else
			{
				_playing = false; // fill the cushion before playing
			}
			ApplyShown();
			return;
		}

		// Same game, later poll: extend the buffer and update the incoming-rate estimate (EMA of
		// moves added per poll — what we pace playout to).
		int added = positions.Count - _prevCount;
		if ( added > 0 ) _avgMovesPerPoll = _avgMovesPerPoll * 0.6f + added * 0.4f;
		_prevCount = positions.Count;
		_positions = positions;

		// Queue the result; the fanfare fires only once playout reaches the final position.
		if ( over && !_bufferGameOver )
		{
			_bufferGameOver = true;
			_pendingHeadline = result.headline;
			_pendingReason = result.reason;
			_pendingWinner = result.winner;
		}

		int lag = positions.Count - _shownPly;
		// Start playing once the cushion has filled (or the game's already over — no point waiting).
		if ( !_playing && ( lag >= PreRollLag || _bufferGameOver ) ) _playing = true;
		// If we somehow fell way behind, drop old moves to catch back up toward the cushion.
		if ( _playing && lag > MaxLag ) _shownPly = positions.Count - PreRollLag;
	}

	/// <summary>Paced playout: reveal one buffered ply at the game's own average rate, hold the
	/// end-of-game banner, and release the next-game re-pick once it's had its moment.</summary>
	void AdvanceReplay()
	{
		if ( !_replayActive ) return;

		// Holding the end-of-game result banner (at least FanfareSeconds).
		if ( ShowingResult )
		{
			if ( _sinceResultShown >= FanfareSeconds )
			{
				ShowingResult = false;
				_resultDoneGameId = _shownGameId;                       // don't re-fanfare this game
				if ( _channel == Channel.LichessTv ) _tvGameOver = true; // now let PollTv pick the next game
			}
			return;
		}

		if ( !_playing ) return;

		// Reached the end of the buffer: if the game just ended (and we haven't already fanfared
		// it), fire the banner; else hold on the final position and wait for the next game.
		if ( _shownPly >= _positions.Count )
		{
			if ( _bufferGameOver && _shownGameId != _resultDoneGameId ) StartResult();
			return;
		}

		if ( _sinceReveal < _nextGap ) return;
		_shownPly++;
		_sinceReveal = 0f;
		ApplyShown();

		// Pace at the game's own average rate (EMA), nudged to bleed the cushion back toward its
		// target; once the game is over, drain fast to reach the final position for the fanfare.
		int lag = _positions.Count - _shownPly;
		if ( _bufferGameOver )
		{
			_nextGap = MinReplayGap;
		}
		else
		{
			float perInterval = System.Math.Max( 1f, _avgMovesPerPoll + ( lag - PreRollLag ) * 0.5f );
			_nextGap = System.Math.Clamp( PollInterval / perInterval, MinReplayGap, MaxReplayGap );
		}
	}

	void StartResult()
	{
		ShowingResult = true;
		_sinceResultShown = 0f;
		ResultHeadline = _pendingHeadline;
		ResultReason = _pendingReason;
		ResultWinner = _pendingWinner;
	}

	/// <summary>Winner + reason for the end-of-game fanfare, from the export PGN. Winner: +1 White,
	/// −1 Black, 0 draw. Reason is best-effort from the movetext + Termination header.</summary>
	(string headline, string reason, int winner) ParseResult( string pgn )
	{
		int winner = 0;
		int i = pgn.IndexOf( "[Result \"", System.StringComparison.Ordinal );
		if ( i >= 0 )
		{
			i += 9;
			int end = pgn.IndexOf( '"', i );
			var r = end > i ? pgn[i..end] : "";
			winner = r == "1-0" ? 1 : r == "0-1" ? -1 : 0;
		}

		var term = PgnHeader( pgn, "Termination" );
		bool onTime = term != null && term.IndexOf( "Time", System.StringComparison.OrdinalIgnoreCase ) >= 0;
		bool abandoned = term != null && term.IndexOf( "Aband", System.StringComparison.OrdinalIgnoreCase ) >= 0;
		bool checkmate = pgn.IndexOf( '#' ) >= 0; // '#' only appears on a checkmating SAN move

		string reason =
			winner == 0 ? "" :               // draw — the headline says it all
			checkmate ? "by checkmate" :
			onTime ? "on time" :
			abandoned ? "abandoned" :
			"by resignation";

		string headline = winner == 0 ? "Draw" : $"{( winner > 0 ? WhiteName : BlackName )} wins";
		return (headline, reason, winner);
	}

	/// <summary>Point Fen/LastMoveUci at the currently-revealed ply (same string instances until
	/// the ply changes, so the board's reference-equality "unchanged" check still holds).</summary>
	void ApplyShown()
	{
		if ( _positions.Count == 0 ) return;
		int idx = System.Math.Clamp( _shownPly, 1, _positions.Count ) - 1;
		Fen = _positions[idx].fen;
		LastMoveUci = _positions[idx].uci;
	}

	/// <summary>A PGN whose result header/terminator is decisive or drawn (not the ongoing
	/// "*") — the game is over. lichess sets the real result only once the game ends.</summary>
	static bool PgnGameOver( string pgn )
	{
		if ( string.IsNullOrEmpty( pgn ) ) return false;
		int i = pgn.IndexOf( "[Result \"", System.StringComparison.Ordinal );
		if ( i < 0 ) return false;
		i += 9;
		int end = pgn.IndexOf( '"', i );
		if ( end < 0 ) return false;
		var result = pgn[i..end];
		return result is "1-0" or "0-1" or "1/2-1/2";
	}

	static LichessTvChannel PickChannel( Dictionary<string, LichessTvChannel> channels, string key )
	{
		if ( channels == null || channels.Count == 0 ) return null;
		if ( !string.IsNullOrEmpty( key ) && channels.TryGetValue( key, out var wanted ) ) return wanted;

		// Auto-pick: prefer the common human channels by rating, else anything with a game.
		foreach ( var pref in new[] { "rapid", "classical", "blitz", "bullet" } )
			if ( channels.TryGetValue( pref, out var c ) && !string.IsNullOrEmpty( c.gameId ) ) return c;
		foreach ( var c in channels.Values )
			if ( !string.IsNullOrEmpty( c.gameId ) ) return c;
		return null;
	}

	/// <summary>Channel keys offered on the wall screen (the human time controls).</summary>
	public static readonly string[] TvChannels = { "rapid", "classical", "blitz", "bullet" };

	static string ExtractGameId( string input )
	{
		if ( string.IsNullOrWhiteSpace( input ) ) return null;
		input = input.Trim();
		// A bare id (8 chars for a full game, or the game-facing forms). Strip a URL down to
		// its last path segment and take the leading id chars.
		int slash = input.LastIndexOf( '/' );
		if ( slash >= 0 ) input = input[( slash + 1 )..];
		int q = input.IndexOfAny( new[] { '?', '#' } );
		if ( q >= 0 ) input = input[..q];
		// lichess game ids are 8 alphanumerics; a full game url may carry the 12-char id
		// (8 + colour suffix) — the first 8 identify the game either way.
		var id = new System.Text.StringBuilder();
		foreach ( var c in input )
		{
			if ( !char.IsLetterOrDigit( c ) ) break;
			id.Append( c );
			if ( id.Length == 8 ) break;
		}
		return id.Length == 8 ? id.ToString() : null;
	}

	void ClearPosition()
	{
		Fen = null;
		LastMoveUci = null;
		WhiteName = "White";
		BlackName = "Black";
		ClearPlayerMeta();

		// Reset the buffered playout + fanfare so the next game starts clean.
		_positions = new();
		_shownPly = 0;
		_shownGameId = null;
		_replayActive = false;
		_playing = false;
		_bufferGameOver = false;
		_resultDoneGameId = null;
		_prevCount = 0;
		ShowingResult = false;
	}

	void ClearPlayerMeta()
	{
		WhiteRating = BlackRating = 0;
		WhiteTitle = BlackTitle = null;
		TimeControl = null;
		_whiteClockBase = _blackClockBase = -1;
		_clocksFrozen = false;
	}

	// ── PGN scoreboard parsing (headers + %clk comments) ──

	/// <summary>Fill the scoreboard fields from an export PGN: both names + Elos + titles from
	/// the headers, and each side's clock from the last two <c>%clk</c> comments.</summary>
	void ApplyPlayerMeta( string pgn, string latestFen )
	{
		var w = PgnHeader( pgn, "White" );
		var b = PgnHeader( pgn, "Black" );
		if ( !string.IsNullOrEmpty( w ) ) WhiteName = w;
		if ( !string.IsNullOrEmpty( b ) ) BlackName = b;
		WhiteRating = ParseInt( PgnHeader( pgn, "WhiteElo" ) );
		BlackRating = ParseInt( PgnHeader( pgn, "BlackElo" ) );
		WhiteTitle = PgnHeader( pgn, "WhiteTitle" );
		BlackTitle = PgnHeader( pgn, "BlackTitle" );
		TimeControl = PrettyTimeControl( PgnHeader( pgn, "TimeControl" ) );

		// %clk comments alternate White, Black, White, Black… The last belongs to whoever just
		// moved, i.e. the OPPOSITE of the side to move (which is holding its previous value).
		var clocks = ExtractClocks( pgn );
		_whiteToMove = SideToMove( latestFen );
		if ( clocks.Count >= 2 )
		{
			double last = clocks[clocks.Count - 1], prev = clocks[clocks.Count - 2];
			if ( _whiteToMove ) { _blackClockBase = last; _whiteClockBase = prev; }
			else { _whiteClockBase = last; _blackClockBase = prev; }
			_sinceClock = 0f;
		}
		else
		{
			_whiteClockBase = _blackClockBase = -1;
		}
	}

	/// <summary>Value of a <c>[Key "value"]</c> PGN header, or null (lichess writes "?" for an
	/// unknown Elo/title — treated as null).</summary>
	static string PgnHeader( string pgn, string key )
	{
		if ( string.IsNullOrEmpty( pgn ) ) return null;
		var tag = "[" + key + " \"";
		int i = pgn.IndexOf( tag, System.StringComparison.Ordinal );
		if ( i < 0 ) return null;
		i += tag.Length;
		int end = pgn.IndexOf( '"', i );
		if ( end < 0 ) return null;
		var v = pgn[i..end];
		return v == "?" ? null : v;
	}

	static int ParseInt( string s ) => int.TryParse( s, out var n ) ? n : 0;

	/// <summary>lichess writes the PGN TimeControl header as "&lt;base-seconds&gt;+&lt;inc-seconds&gt;"
	/// (e.g. "180+2"). Turn it into the familiar minutes form ("3+2"); return null for
	/// correspondence / unlimited / unparsable ("-", "?", "1/86400").</summary>
	static string PrettyTimeControl( string tc )
	{
		if ( string.IsNullOrEmpty( tc ) || tc is "-" or "?" ) return null;
		int plus = tc.IndexOf( '+' );
		if ( plus < 0 ) return null; // correspondence ("1/86400") — not a base+inc clock
		if ( !int.TryParse( tc[..plus], out var baseSec ) || !int.TryParse( tc[( plus + 1 )..], out var inc ) )
			return null;
		double mins = baseSec / 60.0;
		// Whole minutes print bare (10+0); sub-minute controls keep one decimal (0.5+0).
		string b = mins == System.Math.Floor( mins ) ? ( (int)mins ).ToString() : mins.ToString( "0.#" );
		return $"{b}+{inc}";
	}

	/// <summary>True when it's White to move (FEN's active-colour field is not 'b').</summary>
	static bool SideToMove( string fen )
	{
		if ( string.IsNullOrEmpty( fen ) ) return true;
		int sp = fen.IndexOf( ' ' );
		return sp < 0 || sp + 1 >= fen.Length || fen[sp + 1] != 'b';
	}

	/// <summary>Every <c>%clk H:MM:SS(.f)</c> value in the PGN, in move order (seconds).</summary>
	static List<double> ExtractClocks( string pgn )
	{
		var list = new List<double>();
		if ( string.IsNullOrEmpty( pgn ) ) return list;
		int i = 0;
		while ( ( i = pgn.IndexOf( "%clk", i, System.StringComparison.Ordinal ) ) >= 0 )
		{
			i += 4;
			while ( i < pgn.Length && pgn[i] == ' ' ) i++;
			int start = i;
			while ( i < pgn.Length && ( char.IsDigit( pgn[i] ) || pgn[i] == ':' || pgn[i] == '.' ) ) i++;
			if ( ParseClk( pgn[start..i], out var sec ) ) list.Add( sec );
		}
		return list;
	}

	static bool ParseClk( string s, out double seconds )
	{
		seconds = 0;
		if ( string.IsNullOrEmpty( s ) ) return false;
		var parts = s.Split( ':' );
		double mult = 1, total = 0;
		for ( int k = parts.Length - 1; k >= 0; k-- )
		{
			if ( !double.TryParse( parts[k], out var v ) ) return false;
			total += v * mult;
			mult *= 60;
		}
		seconds = total;
		return true;
	}
}
