using System.Collections.Generic;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// Drives the west-wall spectator board: mirrors a live game at an sbox table, or
/// shows lichess TV (M9).
///
/// <para>Tables are read from synced/local state ONLY — the host-folded FEN each
/// <see cref="LocalGameController"/> already publishes — so that half works identically
/// on every client, needs no network of its own, and is real-time rather than polled.
/// <b>TV is the one source that isn't free</b>: it polls gamchess, so it only runs while
/// it is the FEATURED source on this client. Cycle to a table, or turn TV off, and the
/// polling stops and gamchess drops its upstream.</para>
///
/// <para><b>Per-client throughout, which is the existing pattern rather than a new
/// one.</b> <see cref="CycleFeatured"/> and the index are local, so two players at this
/// wall already saw different tables before TV existed. TV is simply one more entry in
/// that cycle — it does not get priority, so a player who wants lichess can sit on it
/// while a game runs at a table.</para>
///
/// <para><b>TV is never required.</b> Turn it off and the cycle is tables only, exactly
/// as it was. Kill gamchess and the same thing happens with a status line.</para>
/// </summary>
public sealed class SpectatorController : Component
{
	public static SpectatorController Instance { get; private set; }

	/// <summary>Position on the wall, or null/empty when nothing is live.</summary>
	public string Fen { get; private set; }
	public string LastMoveUci { get; private set; }

	/// <summary>The player's name ALONE — no title, no rating. The plaque composes those
	/// itself, because they sit on different lines.</summary>
	public string WhiteName { get; private set; } = "White";
	public string BlackName { get; private set; } = "Black";

	/// <summary>lichess title ("GM", "WFM", "BOT"), or null. Always null for an sbox
	/// table: Gambit has no titles.</summary>
	public string WhiteTitle { get; private set; }
	public string BlackTitle { get; private set; }

	/// <summary>lichess rating, or 0 when there isn't one. Always 0 for an sbox table —
	/// Gambit has no rating, which is why the plaque's second line can be clock-only.</summary>
	public int WhiteRating { get; private set; }
	public int BlackRating { get; private set; }

	/// <summary>Clock face for each seat, or null/empty when the featured table is
	/// untimed (or nothing is live). Already formatted — the wall panel just prints it.</summary>
	public string WhiteClock { get; private set; }
	public string BlackClock { get; private set; }

	/// <summary>Which seat's clock is running on the featured table, or null.</summary>
	public ChessSeat? TickingSeat { get; private set; }

	/// <summary>Menu name of the featured table's time control ("Blitz 3+2"), or null
	/// when nothing is live.</summary>
	public string TimeControlLabel { get; private set; }

	/// <summary>One-line label above the board ("FEATURED · Table 3").</summary>
	public string ChannelLabel { get; private set; } = "SPECTATE";

	/// <summary>Is what's on the wall right now a lichess TV game rather than a table?
	/// The panels need it because the two aren't the same claim — a table game is the
	/// host-folded FEN and genuinely real-time; TV is polled.</summary>
	public bool IsTvSource { get; private set; }

	/// <summary>Why there's nothing to show, or null.</summary>
	public string StatusText { get; private set; }

	/// <summary>"White wins — out of time", while the wall is holding on a game that
	/// just finished. Null the rest of the time.
	///
	/// <para>Its OWN property rather than a flavour of <see cref="StatusText"/>, because
	/// the two mean opposite things: StatusText is "there is nothing to show", and this
	/// is "what you are looking at is the end of a game". Folding the fanfare into
	/// StatusText is exactly how it came to be invisible — every renderer of StatusText
	/// gates on <c>!HasPosition</c>, and the fanfare's whole point is that a position IS
	/// up.</para></summary>
	public string FanfareText { get; private set; }

	public bool HasPosition => !string.IsNullOrEmpty( Fen );

	/// <summary>Full-move number the board is showing, from the FEN's last field
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

	// Which source we're on. Indexes into [tables…, TV] when TV is on, and into
	// [tables…] when it isn't.
	int _featuredIndex;

	/// <summary>The TV feed, built on first use — a player with TV off never creates
	/// one, and one that is never ticked never polls.</summary>
	LichessTvSource _tv;

	// TV used to stream only while a viewer was within range of the board, on the
	// reasoning that an unwatched wall shouldn't cost lichess a held stream. That gate
	// is GONE, deliberately: TV is on or off, and the client's setting decides. It was
	// three attempts at a number that never once behaved as its own doc claimed (1200
	// in an 800-unit room gated nothing; measuring from this component measured the room
	// centre because SpectatorWall lives on the LobbyRoom GO; measuring in 3D against a
	// board floating ~390 up made a third of the distance vertical) — and it bought a
	// wall that went blank when you stepped back from it.
	//
	// The cost it was guarding is still bounded, and by better things: TV polls only
	// while it is the FEATURED source on this client (cycle to a table and it stops), and
	// gamchess holds ONE upstream per channel however many watch it, dropping it once
	// nobody polls. An idle lobby with TV on costs lichess one stream per channel — which
	// is what "N clients cost lichess nothing" was always about.

	// ── The TV controls ──
	//
	// These live on the SPECTATOR BOARD (SpectatorScreen), not the settings board, and
	// that is the whole of the UI story: one board for TV, the one you're looking at.
	// The admin uses the same picker as everyone else — theirs just also moves the
	// lobby's suggestion.

	/// <summary>Does TV appear in the cycle at all? A local setting, default on, so
	/// only someone who turns it OFF has anything saved.</summary>
	public static bool TvEnabled => PlayerData.Current.LichessTvEnabled;

	/// <summary>The channel the lobby suggests. Blitz until an admin says otherwise.</summary>
	public static string SuggestedChannel =>
		LichessTv.Coerce( LobbyNetworkManager.Instance?.SuggestedTvChannel );

	/// <summary>Is this client taking the lobby's channel rather than its own pick?
	///
	/// <para><b>Always true for an admin</b>, and not as a special case: an admin's pick
	/// IS the lobby's channel, so "follow the lobby" is the only thing they can be doing.
	/// The screen hides the toggle for them rather than offer a control that means
	/// nothing.</para></summary>
	public static bool FollowingLobbyTv =>
		LobbyNetworkManager.LocalIsAdmin || PlayerData.Current.LichessTvFollowHost;

	/// <summary>Which channel this client watches: the lobby's, or an explicit local
	/// pick.
	///
	/// <para>The admin SUGGESTS, it doesn't dictate — a player who has chosen a channel
	/// keeps it when the admin changes theirs.</para></summary>
	public static string DesiredChannel
	{
		get
		{
			var d = PlayerData.Current;
			if ( !FollowingLobbyTv && LichessTv.IsValid( d.LichessTvChannel ) )
				return d.LichessTvChannel;
			return SuggestedChannel;
		}
	}

	/// <summary>Turn TV on or off for this client. Off drops it from the wall's cycle;
	/// the wall keeps mirroring tables, which was its job before TV existed.</summary>
	public static void SetTvEnabled( bool on ) => MutateData( d => d.LichessTvEnabled = on );

	/// <summary>Take the lobby's channel from now on. Not offered to an admin — they set
	/// it.</summary>
	public static void FollowLobbyTv() => MutateData( d => d.LichessTvFollowHost = true );

	/// <summary>Watch a channel.
	///
	/// <para><b>An admin's pick moves the whole lobby's suggestion</b> instead of setting
	/// a personal override — which is what makes one picker serve both jobs. Routed
	/// through the host and re-checked there: the admin may not be the network host on a
	/// dedi, and <c>LocalIsAdmin</c> is a UI hint, never authority. If it's refused, this
	/// client simply keeps watching the lobby's channel.</para></summary>
	public static void PickTvChannel( string channel )
	{
		channel = LichessTv.Coerce( channel );

		if ( LobbyNetworkManager.LocalIsAdmin )
		{
			LobbyNetworkManager.Instance?.RequestSetSuggestedTvChannel( channel );
			return;
		}

		MutateData( d =>
		{
			d.LichessTvFollowHost = false;
			d.LichessTvChannel = channel;
		} );
	}

	// PlayerData.Current may be the shared defaults object, which doesn't persist — so
	// mutate the same way the settings board does: load-or-new, change, save.
	static void MutateData( System.Action<PlayerData> change )
	{
		var d = PlayerData.Load() ?? new PlayerData();
		change( d );
		d.Save();
	}

	protected override void OnEnabled() => Instance = this;
	protected override void OnDisabled() { if ( Instance == this ) Instance = null; }

	protected override void OnUpdate() => UpdateFeatured();

	/// <summary>Step to the next source — live tables, then TV.</summary>
	public void CycleFeatured() => _featuredIndex++;

	void UpdateFeatured()
	{
		var live = CollectLiveTables();
		bool tvOn = TvEnabled;
		int sources = live.Count + ( tvOn ? 1 : 0 );

		if ( sources == 0 )
		{
			// TV off and no tables. Any source built before TV was switched off must stop
			// too, or it keeps polling for a channel that is no longer in the cycle.
			IsTvSource = false;
			_tv?.StopWatching();
			ClearPosition();
			ChannelLabel = "FEATURED";
			StatusText = "No live games at the tables right now.";
			return;
		}

		int idx = ( _featuredIndex % sources + sources ) % sources;

		// TV is the last entry, so it never displaces a real table at index 0 — but it
		// also gets no priority: you can sit on it while games run.
		if ( tvOn && idx == live.Count )
		{
			ShowTv( sources );
			return;
		}

		// A table is featured, so TV isn't — same as walking away, and for the same
		// reasons: stop polling (gamchess can drop the upstream) and drop the version.
		IsTvSource = false;
		_tv?.StopWatching();

		var t = live[idx];
		Fen = t.Fen;
		LastMoveUci = t.LastMove;
		WhiteName = t.White;
		BlackName = t.Black;
		// An sbox table has neither: Gambit has no titles and no rating.
		WhiteTitle = BlackTitle = null;
		WhiteRating = BlackRating = 0;
		WhiteClock = t.WhiteClock;
		BlackClock = t.BlackClock;
		TickingSeat = t.Ticking;
		TimeControlLabel = t.TcLabel;
		ChannelLabel = sources > 1
			? $"FEATURED · Table {t.Number} ({idx + 1}/{sources})"
			: $"FEATURED · Table {t.Number}";
		StatusText = null;
		// Cycling away from a fanfare mid-hold must not leave a lichess result floating
		// over somebody's table game. A table game's own end is the local controller's
		// business, not this one's.
		FanfareText = null;
	}

	void ShowTv( int sources )
	{
		IsTvSource = true;
		_tv ??= new LichessTvSource();
		_tv.SetChannel( DesiredChannel );

		var label = LichessTv.Label( _tv.Channel ) ?? "TV";
		ChannelLabel = sources > 1
			? $"LICHESS TV · {label} ({sources}/{sources})"
			: $"LICHESS TV · {label}";

		// Polling IS the watch signal gamchess ref-counts on. TV being the featured source
		// is the whole gate now — cycle to a table, or turn TV off, and this stops being
		// called, the polls stop, and gamchess drops its upstream after its idle TTL.
		_tv.Tick();

		if ( !_tv.HasPosition )
		{
			ClearPosition();
			StatusText = _tv.StatusText ?? "Waiting for lichess TV…";
			return;
		}

		Fen = _tv.Fen;
		LastMoveUci = _tv.LastMoveUci;
		WhiteName = _tv.WhiteName;
		BlackName = _tv.BlackName;
		WhiteTitle = _tv.WhiteTitle;
		BlackTitle = _tv.BlackTitle;
		WhiteRating = _tv.WhiteRating;
		BlackRating = _tv.BlackRating;
		// SECONDS, which is what Format takes — and already counted down locally from the
		// last frame (lichess only sends a clock on a move, so the raw value would sit
		// frozen through every think). Re-formatted per frame, which is what makes the
		// plaque tick. A TV game always has a clock: every lichess channel is timed.
		WhiteClock = TimeControl.Format( _tv.WhiteClock );
		BlackClock = TimeControl.Format( _tv.BlackClock );
		TickingSeat = _tv.TickingSeat;
		TimeControlLabel = label;

		// The game on the board has finished: say how it ended, and stop showing a clock
		// as running. lichess TV would already be showing the next game by now — stopping
		// on the result for a beat is the whole point.
		//
		// ShowingFinished, not InFanfare: the hold expires before the poll that replaces
		// the position lands, and for that gap the board is still showing the finished
		// game. Keying on the hold would drop the result line and restart the ticking
		// highlight while the dead game is still up.
		if ( _tv.ShowingFinished )
		{
			FanfareText = _tv.FanfareText;
			TickingSeat = null;
			StatusText = null;
			return;
		}
		FanfareText = null;
		StatusText = null;
	}

	/// <summary>One live table, as the wall needs it.</summary>
	sealed class LiveTable
	{
		public string Fen, LastMove, White, Black, Number, WhiteClock, BlackClock, TcLabel;
		public ChessSeat? Ticking;
	}

	/// <summary>Every sbox table currently showing a live game, read from the
	/// host-folded FEN and the host-run clocks — no token, no API, no poll.</summary>
	List<LiveTable> CollectLiveTables()
	{
		var list = new List<LiveTable>();
		foreach ( var st in Scene.GetAllComponents<ChessStation>() )
		{
			var lc = LocalGameController.For( st );
			if ( lc is not { Playing: true } || lc.Game == null ) continue;

			bool timed = !lc.Tc.IsUnlimited;
			list.Add( new LiveTable
			{
				Fen = lc.Game.Fen,
				LastMove = lc.Game.LastMoveUci,
				White = st.WhiteName ?? "White",
				Black = st.BlackName ?? "Black",
				Number = TableNumber( st ),
				WhiteClock = timed ? TimeControl.Format( lc.ClockFor( ChessSeat.White ) ) : null,
				BlackClock = timed ? TimeControl.Format( lc.ClockFor( ChessSeat.Black ) ) : null,
				Ticking = lc.TickingSeat,
				TcLabel = lc.Tc.Name,
			} );
		}
		return list;
	}

	void ClearPosition()
	{
		Fen = null;
		LastMoveUci = null;
		WhiteName = "White";
		BlackName = "Black";
		WhiteTitle = null;
		BlackTitle = null;
		WhiteRating = 0;
		BlackRating = 0;
		WhiteClock = null;
		BlackClock = null;
		TickingSeat = null;
		TimeControlLabel = null;
		// No position means nothing to hold a result over. A stale fanfare would float
		// a dead game's result across an empty board, or across a table game.
		FanfareText = null;
	}

	static string TableNumber( ChessStation st )
	{
		// Stations are named "ChessStation{i}" by ChessRing.
		var name = st.GameObject.Name;
		int i = name.Length;
		while ( i > 0 && char.IsDigit( name[i - 1] ) ) i--;
		return i < name.Length ? name[i..] : "?";
	}
}
