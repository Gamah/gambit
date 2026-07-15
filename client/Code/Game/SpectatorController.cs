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
/// <b>TV is the one source that isn't free</b>: it polls gamchess, so it only runs
/// while someone is actually looking at it.</para>
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

	/// <summary>Why there's nothing to show, or null.</summary>
	public string StatusText { get; private set; }

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

	/// <summary>How close the viewer must be for TV to actually stream.
	///
	/// <para><b>This is what makes TV lazy, and lazy is the whole premise.</b> Polling is
	/// the watch signal gamchess ref-counts on, so without a gate every client in a lobby
	/// long-polls forever and gamchess holds a lichess stream open for a wall nobody is
	/// looking at — exactly the cost this design exists to avoid. Mirroring tables is
	/// free and stays ungated; only TV pays.</para>
	///
	/// <para><b>It must be smaller than the room or it gates nothing.</b> The scene's
	/// <c>RoomSize</c> is 800 (the code default of 240 is overridden — check the scene,
	/// never the default), so the interior spans ±400 and no player can be more than
	/// ~870 from the wall. A first draft used 1200 and was therefore unconditionally
	/// true everywhere in the lobby: it looked like a gate and gated nothing.</para>
	///
	/// <para>500 is measured HORIZONTALLY (see <see cref="ViewerPresent"/>) against a
	/// wall at y≈369, so it fires from the room centre (369) to about y=-131 — the near
	/// half of the room. That matches the intent ("walk up to the wall and there's a
	/// live game on it") without needing to stand underneath it.</para></summary>
	[Property] public float TvWatchRange { get; set; } = 500f;

	/// <summary>World position TV proximity is measured against — set by
	/// <see cref="SpectatorWall"/> to the floating board's centre.
	///
	/// <para><b>Not this component's own position.</b> SpectatorWall lives on the
	/// LobbyRoom GO (that's where it reads RoomSize from), so this component sits at the
	/// ROOM CENTRE, nowhere near the wall — measuring against it would gate on "is the
	/// player in the room", which is not the question.</para></summary>
	public Vector3 WatchAnchor { get; set; }

	/// <summary>Is anyone here to see it?
	///
	/// <para>Measured from the CAMERA rather than the pawn: the camera is literally what's
	/// looking, and it's also what's true while engaged at a station, where the pawn stays
	/// put.</para>
	///
	/// <para><b>HORIZONTALLY</b>, which is the same thing <c>LobbyPlayer</c>'s walk-up
	/// ranges do and is not a detail. This board deliberately floats high above the wall —
	/// with the tilt, its centre is ~390 up, so a straight 3D distance from a camera at
	/// eye height (~64) starts at 326 before the player has moved at all. That vertical
	/// term would dominate the range, make the number mean something other than what it
	/// reads as, and shift every time anyone tuned <c>BoardCellSize</c>,
	/// <c>ClearAboveWall</c> or <c>TiltDegrees</c>. "How far away is the viewer" is a
	/// floor-plan question; answer it on the floor plan.</para></summary>
	bool ViewerPresent
	{
		get
		{
			// Engaged at the wall always counts, however far the camera ends up sitting.
			if ( SpectatorStation.Active != null ) return true;
			var cam = Scene?.Camera;
			if ( cam == null ) return false;
			var delta = cam.WorldPosition - WatchAnchor;
			return new Vector2( delta.x, delta.y ).Length <= TvWatchRange;
		}
	}

	/// <summary>Does TV appear in the cycle at all? A local setting, default on, so
	/// only someone who turns it OFF has anything saved.</summary>
	public static bool TvEnabled => PlayerData.Current.LichessTvEnabled;

	/// <summary>Which channel this client watches: the host's suggestion by default,
	/// or an explicit local pick.
	///
	/// <para>The host SUGGESTS, it doesn't dictate — a player who has chosen a channel
	/// keeps it when the admin changes theirs.</para></summary>
	public static string DesiredChannel
	{
		get
		{
			var d = PlayerData.Current;
			if ( !d.LichessTvFollowHost && LichessTv.IsValid( d.LichessTvChannel ) )
				return d.LichessTvChannel;
			return LichessTv.Coerce( LobbyNetworkManager.Instance?.SuggestedTvChannel );
		}
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
	}

	void ShowTv( int sources )
	{
		_tv ??= new LichessTvSource();
		_tv.SetChannel( DesiredChannel );

		var label = LichessTv.Label( _tv.Channel ) ?? "TV";
		ChannelLabel = sources > 1
			? $"LICHESS TV · {label} ({sources}/{sources})"
			: $"LICHESS TV · {label}";

		// Ticking is what tells gamchess someone is watching — stop and it drops the
		// upstream after its idle TTL. So gate it on someone actually being here:
		// otherwise every client in a lobby holds a lichess stream open forever for a
		// wall nobody is looking at.
		if ( !ViewerPresent )
		{
			// Show NOTHING rather than the last position we happened to have: the feed is
			// stopped, so it's frozen, and a frozen game on a board this size reads as a
			// live one from across the room. StopWatching also resets the poll version, so
			// coming back asks for "whatever is on now".
			_tv.StopWatching();
			ClearPosition();
			StatusText = $"Walk up to watch lichess TV ({label}).";
			return;
		}

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
		// TV clocks arrive in SECONDS, which is what Format takes. A TV game always has
		// a clock — lichess's channels are all timed — so there's no untimed branch here.
		WhiteClock = TimeControl.Format( _tv.WhiteClock );
		BlackClock = TimeControl.Format( _tv.BlackClock );
		TickingSeat = _tv.TickingSeat;
		TimeControlLabel = label;
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
