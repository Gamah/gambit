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
	RealTimeSince _sinceTvChannel = 999f; // time since the featured game id was (re)picked
	bool _tvGameOver;                 // the featured game finished — re-pick on the next poll
	bool _polling;
	// Poll rarely: lichess already delays the public export a few moves, so there's no benefit
	// to a tight loop. Each poll usually spans several moves; we replay them one at a time,
	// spread across the interval (AdvanceReplay), so the board keeps moving continuously between
	// polls instead of teleporting a burst of moves then sitting idle.
	const float PollInterval = 8f;
	// lichess rotates a channel's featured game and games end; re-pick the featured game
	// this often so the board keeps following live play instead of freezing on a finished
	// game (a finished game also forces an immediate re-pick via _tvGameOver).
	const float TvChannelRefresh = 20f;

	// ── Move replay: reveal buffered plies one at a time (a smooth slide each) rather than
	//    teleporting several moves when a poll spans several. Paced to the data rate — the
	//    backlog is spread across roughly one poll interval, clamped so moves neither blur
	//    together nor stall. ──
	List<(string fen, string uci)> _positions = new();
	int _shownPly;                    // plies currently revealed to the board (1-based count)
	bool _hasShownPly;                // seen at least one position (first sight snaps to current)
	bool _replayActive;               // a PGN source is driving replay (false for sbox tables)
	string _shownGameId;              // game the buffer belongs to (id change ⇒ snap, don't replay)
	RealTimeSince _sinceReveal;
	float _nextGap;                   // seconds until the next ply is revealed
	// The export gives us only whole-second %clk, so there's no true sub-second move timing to
	// replay bullet by (real-time needs the wss relay — deferred). Client-side, the best we can
	// do is play each buffered move DISCRETELY: let its slide fully finish (its full ease-out
	// slope, not a cut-off linear jerk), then a short beat, then the next. When we're near live
	// that beat gives a natural cadence; when a poll drops several moves we drop the beat and
	// chain the completed slides back-to-back so the board drains promptly instead of crawling.
	const float ReplaySlide = 0.22f;  // ≥ SpectatorBoard3D.MoveSeconds — a move's slide finishes within this
	const float ReplayHold = 0.13f;   // the small beat between moves once we're near live
	const int ReplayHoldBacklog = 4;  // ≥ this far behind ⇒ drop the beat, chain slides back-to-back
	const int ReplayBacklogCap = 12;  // snap forward if we somehow fall this far behind

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
		_sinceTvChannel = 999f;
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
		// (Re)pick the channel's current featured game when we have none, when the one we
		// were watching ended, or periodically — lichess rotates the featured game and games
		// finish, so a pinned id would eventually freeze the board on a stale/finished game.
		if ( _tvGameId == null || _tvGameOver || _sinceTvChannel > TvChannelRefresh )
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

			// A new featured game — reset the board so the last position doesn't linger under
			// the incoming one.
			if ( ch.gameId != _tvGameId ) ClearPosition();
			_tvGameId = ch.gameId;
			_tvGameOver = false;
			_sinceTvChannel = 0f;
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

		// Feed the replay buffer; AdvanceReplay reveals the plies one at a time.
		IngestPositions( gameId, positions );
		StatusText = delayNote;

		// On TV, note when the featured game has finished so PollTv re-picks the channel's
		// next game rather than freezing on the final position. Watch-by-id stays put.
		if ( _channel == Channel.LichessTv && over )
			_tvGameOver = true;
	}

	/// <summary>Take a poll's full position list and either snap to the current position (first
	/// sight of a game) or leave the backlog for AdvanceReplay to reveal one move at a time.</summary>
	void IngestPositions( string gameId, List<(string fen, string uci)> positions )
	{
		if ( positions.Count == 0 )
		{
			// Headers-only (no moves yet) — nothing to replay; the board idles on the start
			// position (empty Fen). Keep any prior position we were showing.
			_positions = positions;
			_replayActive = false;
			return;
		}

		bool freshGame = gameId != _shownGameId || !_hasShownPly;
		_shownGameId = gameId;
		_positions = positions;
		_replayActive = true;

		if ( freshGame )
		{
			// First sight of this game: jump straight to the current position (lichess already
			// delays us a few moves — don't replay the whole game from move 1).
			_shownPly = positions.Count;
			_hasShownPly = true;
		}
		else
		{
			if ( _shownPly > positions.Count ) _shownPly = positions.Count; // game replaced/shrank
			int backlog = positions.Count - _shownPly;
			if ( backlog > ReplayBacklogCap ) _shownPly = positions.Count - ReplayBacklogCap;
			_nextGap = 0f; // start revealing on the next frame
		}
		ApplyShown();
	}

	/// <summary>Reveal the next buffered ply once its adaptive gap has elapsed, then pace the
	/// remaining backlog across roughly one poll interval (clamped).</summary>
	void AdvanceReplay()
	{
		if ( !_replayActive || _shownPly >= _positions.Count ) return;
		if ( _sinceReveal < _nextGap ) return;

		_shownPly++;
		_sinceReveal = 0f;
		ApplyShown();

		// Let this move's slide finish, then hold a short beat — unless we're several moves
		// behind, in which case drop the beat and chain the completed slides back-to-back so the
		// buffer drains promptly instead of growing.
		int backlog = _positions.Count - _shownPly;
		_nextGap = ReplaySlide + ( backlog >= ReplayHoldBacklog ? 0f : ReplayHold );
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

		// Reset the replay buffer so the next game snaps to its current position instead of
		// trying to continue from a stale ply.
		_positions = new();
		_shownPly = 0;
		_hasShownPly = false;
		_replayActive = false;
		_shownGameId = null;
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
