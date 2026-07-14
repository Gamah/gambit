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
	const float PollInterval = 3f;    // coarse — lichess delays public game export anyway
	// lichess rotates a channel's featured game and games end; re-pick the featured game
	// this often so the board keeps following live play instead of freezing on a finished
	// game (a finished game also forces an immediate re-pick via _tvGameOver).
	const float TvChannelRefresh = 20f;

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

		if ( ChessGame.TryFromPgn( res.Body, out var g ) )
		{
			Fen = g.Fen;
			LastMoveUci = g.LastMoveUci;
			StatusText = delayNote;
		}
		else
		{
			// A game with no moves yet exports as headers only — keep whatever we had.
			StatusText = delayNote;
		}

		bool over = PgnGameOver( res.Body );
		_clocksFrozen = over;
		// Names/ratings/titles/clocks for the scoreboard (headers are present even with no moves).
		ApplyPlayerMeta( res.Body );

		// On TV, note when the featured game has finished so PollTv re-picks the channel's
		// next game rather than freezing on the final position. Watch-by-id stays put.
		if ( _channel == Channel.LichessTv && over )
			_tvGameOver = true;
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
	}

	void ClearPlayerMeta()
	{
		WhiteRating = BlackRating = 0;
		WhiteTitle = BlackTitle = null;
		_whiteClockBase = _blackClockBase = -1;
		_clocksFrozen = false;
	}

	// ── PGN scoreboard parsing (headers + %clk comments) ──

	/// <summary>Fill the scoreboard fields from an export PGN: both names + Elos + titles from
	/// the headers, and each side's clock from the last two <c>%clk</c> comments.</summary>
	void ApplyPlayerMeta( string pgn )
	{
		var w = PgnHeader( pgn, "White" );
		var b = PgnHeader( pgn, "Black" );
		if ( !string.IsNullOrEmpty( w ) ) WhiteName = w;
		if ( !string.IsNullOrEmpty( b ) ) BlackName = b;
		WhiteRating = ParseInt( PgnHeader( pgn, "WhiteElo" ) );
		BlackRating = ParseInt( PgnHeader( pgn, "BlackElo" ) );
		WhiteTitle = PgnHeader( pgn, "WhiteTitle" );
		BlackTitle = PgnHeader( pgn, "BlackTitle" );

		// %clk comments alternate White, Black, White, Black… The last belongs to whoever just
		// moved, i.e. the OPPOSITE of the side to move (which is holding its previous value).
		var clocks = ExtractClocks( pgn );
		_whiteToMove = SideToMove( Fen );
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
