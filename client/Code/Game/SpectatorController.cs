using System.Collections.Generic;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// Drives the west-wall spectator board: mirrors a live game happening at an sbox
/// table onto the wall.
///
/// <para>Reads synced/local state ONLY — the host-folded FEN each
/// <see cref="LocalGameController"/> already publishes — so it works identically on
/// every client, needs no network of its own, and is real-time rather than polled.</para>
///
/// <para>The wall has one source, and it is deliberately thin: a table relay carries a
/// position and two names, nothing else. A broadcast/TV source would need
/// clocks/ratings/titles/replay-buffering machinery that none of this has.</para>
/// </summary>
public sealed class SpectatorController : Component
{
	public static SpectatorController Instance { get; private set; }

	/// <summary>Position on the wall, or null/empty when nothing is live.</summary>
	public string Fen { get; private set; }
	public string LastMoveUci { get; private set; }
	public string WhiteName { get; private set; } = "White";
	public string BlackName { get; private set; } = "Black";

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

	// Which live table we're on, when more than one is going.
	int _featuredIndex;

	protected override void OnEnabled() => Instance = this;
	protected override void OnDisabled() { if ( Instance == this ) Instance = null; }

	protected override void OnUpdate() => UpdateFeatured();

	/// <summary>Step to the next live table — the wall's only control now.</summary>
	public void CycleFeatured() => _featuredIndex++;

	void UpdateFeatured()
	{
		var live = CollectLiveTables();
		if ( live.Count == 0 )
		{
			ClearPosition();
			ChannelLabel = "FEATURED";
			StatusText = "No live games at the tables right now.";
			return;
		}

		int idx = ( _featuredIndex % live.Count + live.Count ) % live.Count;
		var t = live[idx];
		Fen = t.Fen;
		LastMoveUci = t.LastMove;
		WhiteName = t.White;
		BlackName = t.Black;
		WhiteClock = t.WhiteClock;
		BlackClock = t.BlackClock;
		TickingSeat = t.Ticking;
		TimeControlLabel = t.TcLabel;
		ChannelLabel = live.Count > 1
			? $"FEATURED · Table {t.Number} ({idx + 1}/{live.Count})"
			: $"FEATURED · Table {t.Number}";
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
