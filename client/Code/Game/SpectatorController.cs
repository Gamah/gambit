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
/// <para>This used to carry two more sources, lichess TV and watch-by-id, plus the
/// clocks/ratings/titles/replay-buffering machinery they needed. All of it went when
/// lichess did; the wall's own source never used any of it (a table relay carries a
/// position and two names, nothing else — the old code cleared the player metadata
/// for exactly this source). TV comes back if lichess does, or when gamchess grows a
/// feed of its own.</para>
/// </summary>
public sealed class SpectatorController : Component
{
	public static SpectatorController Instance { get; private set; }

	/// <summary>Position on the wall, or null/empty when nothing is live.</summary>
	public string Fen { get; private set; }
	public string LastMoveUci { get; private set; }
	public string WhiteName { get; private set; } = "White";
	public string BlackName { get; private set; } = "Black";

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
		Fen = t.fen;
		LastMoveUci = t.lastMove;
		WhiteName = t.white;
		BlackName = t.black;
		ChannelLabel = live.Count > 1
			? $"FEATURED · Table {t.number} ({idx + 1}/{live.Count})"
			: $"FEATURED · Table {t.number}";
		StatusText = null;
	}

	/// <summary>Every sbox table currently showing a live game, read from the
	/// host-folded FEN — no token, no API, no poll.</summary>
	List<(string fen, string lastMove, string white, string black, string number)> CollectLiveTables()
	{
		var list = new List<(string, string, string, string, string)>();
		foreach ( var st in Scene.GetAllComponents<ChessStation>() )
		{
			var lc = LocalGameController.For( st );
			if ( lc is { Playing: true } && lc.Game != null )
			{
				list.Add( (lc.Game.Fen, lc.Game.LastMoveUci,
					st.WhiteName ?? "White", st.BlackName ?? "Black", TableNumber( st )) );
			}
		}
		return list;
	}

	void ClearPosition()
	{
		Fen = null;
		LastMoveUci = null;
		WhiteName = "White";
		BlackName = "Black";
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
