using System;
using System.Linq;
using Gambit.Api;
using Gambit.World;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// Cross-session matchmaking, client side (M19). A CLIENT-LOCAL coordinator: it holds
/// the browsable list, this player's own open advert, and the poll loop — none of it
/// networked, so it survives the scene teardown a <c>Networking.Connect</c> does (it is
/// static state in the assembly, not in the scene). See MATCHMAKING.md.
///
/// <para>Two flows, both driven from the table setup panel:</para>
/// <list type="bullet">
/// <item><b>Open (host only):</b> advertise this lobby. gamchess lists it; when someone
/// joins, this host seats both players on gamchess's RANDOM colour assignment and starts
/// the two-seat game.</item>
/// <item><b>Join:</b> pick an open game → <c>Networking.Connect</c> into the opener's
/// lobby → the opener's host seats you on your assigned side. You don't pick a colour.</item>
/// </list>
///
/// <para><b>"Join up" mode only in this build.</b> The 'relay' mode (both players stay in
/// their own lobbies, gamchess relays the game) has its whole backend built and tested,
/// but the client-side relay controller is not wired yet — a relay match is declined with
/// a legible message rather than half-joined. See MATCHMAKING.md's "what remains".</para>
/// </summary>
public static class Matchmaking
{
	// ── Client-local state the setup panel reads ──

	/// <summary>This player's own open advert id, or null. Set while waiting for a joiner.</summary>
	public static string MyMatchId { get; private set; }

	/// <summary>We have an advert up and are waiting for someone to join.</summary>
	public static bool Waiting => MyMatchId != null;

	/// <summary>Open games someone else has posted (join targets).</summary>
	public static MatchmakingApi.MatchItem[] OpenGames { get; private set; } = Array.Empty<MatchmakingApi.MatchItem>();

	/// <summary>A one-line status for the panel ("Waiting for an opponent…", an error…).</summary>
	public static string Status { get; private set; } = "";

	/// <summary>Anyone may advertise a game. (A 'join' game needs the opener to be the
	/// lobby host — the lobby_id is their host SteamId — but a 'relay' game needs no lobby
	/// at all, so posting is always available and the mode decides the rest.)</summary>
	public static bool CanOpen => true;

	/// <summary>How this player's next posted game runs: "relay" (both stay put, gamchess
	/// relays — the self-play / two-hosts path) or "join" (the joiner enters your lobby).
	/// Relay is the default because it needs no shared lobby and is the one that works when
	/// both sides are the same Steam account.</summary>
	public static string OpenMode { get; set; } = "relay";

	// ── Internals ──

	static RealTimeUntil _pollNext;   // opener: poll my own match
	static RealTimeUntil _listNext;   // browser: refresh the open list
	static bool _busy;                // an HTTP call is in flight
	static int _tcIndex = TimeControl.DefaultIndex; // the control the opener advertised
	static string _mode = "relay";                  // the mode of our own open advert

	// Set once a match seats us: auto-engage the seat our SteamId lands in. Survives
	// Networking.Connect (static state, not in the scene the connect tears down).
	static bool _awaitSeat;

	static ulong LocalSteam => Connection.Local?.SteamId ?? 0UL;
	static string LocalName => PlayerData.Load()?.DisplayName() ?? "Player";

	// ── Actions (from the setup panel) ──

	/// <summary>Advertise a game at the given control, in the current <see cref="OpenMode"/>.</summary>
	public static async void OpenGame( int tcIndex )
	{
		if ( !CanOpen || Waiting || _busy ) return;
		_tcIndex = TimeControl.IsValidIndex( tcIndex ) ? tcIndex : TimeControl.DefaultIndex;
		_mode = OpenMode == "join" ? "join" : "relay";
		_busy = true;
		Status = "Posting your game…";
		try
		{
			// 'join' hands out our lobby (host SteamId) as the connect target; 'relay'
			// needs no lobby, so it posts none.
			string lobby = _mode == "join" ? LocalSteam.ToString() : "";
			var res = await MatchmakingApi.Open( _mode, lobby,
				TimeControl.At( _tcIndex ).PgnSpec, LocalName );
			var ok = res.Ok ? GamchessApi.Deserialize<MatchmakingApi.OpenResponse>( res.Body ) : null;
			if ( ok?.Id is string id )
			{
				MyMatchId = id;
				Status = "Waiting for an opponent — sides are random.";
				_pollNext = 1f;
			}
			else
			{
				Status = res.Error ?? "Couldn't post the game.";
			}
		}
		finally { _busy = false; }
	}

	/// <summary>Withdraw our open advert.</summary>
	public static async void CancelOpen()
	{
		string id = MyMatchId;
		MyMatchId = null;
		Status = "";
		if ( id != null ) await MatchmakingApi.Cancel( id );
	}

	/// <summary>Refresh the open-games list (throttled by Tick).</summary>
	public static async void Refresh()
	{
		if ( _busy ) return;
		_busy = true;
		try
		{
			var res = await MatchmakingApi.List();
			var list = res.Ok ? GamchessApi.Deserialize<MatchmakingApi.MatchList>( res.Body ) : null;
			OpenGames = list?.Matches ?? Array.Empty<MatchmakingApi.MatchItem>();
		}
		finally { _busy = false; }
	}

	/// <summary>Join an open game: claim it, then connect into the opener's lobby. The
	/// host there seats us on the colour gamchess assigned — we don't choose.</summary>
	public static async void Join( string matchId )
	{
		if ( _busy || string.IsNullOrEmpty( matchId ) ) return;
		_busy = true;
		Status = "Joining…";
		try
		{
			var res = await MatchmakingApi.Join( matchId );
			if ( !res.Ok )
			{
				Status = res.Status == 409 ? "That game was just taken." : ( res.Error ?? "Couldn't join." );
				return;
			}
			var join = GamchessApi.Deserialize<MatchmakingApi.JoinResponse>( res.Body );
			if ( join == null ) { Status = "Couldn't join."; return; }

			if ( join.Mode == "relay" )
			{
				// Both players stay put; gamchess relays. Engage the relay controller on
				// the table we're sitting at (this is how you play yourself across hosts).
				if ( EngageRelay( join.GameId, join.YourColor ) )
					Status = "";
				else
					Status = "Sit at a table to play.";
				return;
			}

			// 'join' mode: enter the opener's lobby.
			if ( string.IsNullOrEmpty( join.LobbyId ) )
			{
				Status = "The game didn't return a lobby to join.";
				return;
			}

			// Arm the auto-seat: once the opener's host seats us, engage that seat. The
			// side is gamchess's (join.YourColor) but we needn't store it — we engage
			// whichever seat our SteamId lands in.
			_awaitSeat = true;

			// Leave our own lobby and join theirs. This tears down our scene and rebuilds
			// from their snapshot — the static state above rides through it.
			Networking.Connect( join.LobbyId );
		}
		finally { _busy = false; }
	}

	// ── Per-frame pump (called from LobbyPlayer.OnUpdate, local player only) ──

	public static void Tick()
	{
		if ( Waiting ) PollMyMatch();
		if ( _awaitSeat ) TryAutoEngage();
	}

	/// <summary>Opener: poll our advert. When someone joins, seat both players (we are the
	/// host) and start the game; the poll also heartbeats the advert so it isn't swept.</summary>
	static async void PollMyMatch()
	{
		if ( _busy || (float)_pollNext > 0f ) return;
		_pollNext = 2f;
		_busy = true;
		try
		{
			var res = await MatchmakingApi.Poll( MyMatchId );
			if ( res.Status == 404 ) { MyMatchId = null; Status = "Your game expired."; return; }
			var m = res.Ok ? GamchessApi.Deserialize<MatchmakingApi.MatchItem>( res.Body ) : null;
			if ( m == null ) return;

			if ( m.Status == "matched" && m.Mode == "join" )
			{
				MyMatchId = null; // stop polling; the game takes over
				Status = "Opponent found — starting.";
				SeatMatchAsHost( m );
			}
			else if ( m.Status == "matched" && m.Mode == "relay" )
			{
				MyMatchId = null;
				// The opener engages the relay game on their own table. game_id is set once
				// the joiner joined (the join created the relay game). your_color is ours.
				Status = EngageRelay( m.GameId, m.YourColor ) ? "" : "Sit at a table to play.";
			}
			else if ( m.Status == "closed" )
			{
				MyMatchId = null;
				Status = "";
			}
		}
		finally { _busy = false; }
	}

	/// <summary>Opener host: seat both players on gamchess's colours and arm our own
	/// auto-engage. The joiner may not have connected yet — the host reservation seats
	/// them on arrival (LobbyNetworkManager). Colours are gamchess's, applied regardless
	/// of who opened, which is the whole point: the opener can't self-assign White.</summary>
	static void SeatMatchAsHost( MatchmakingApi.MatchItem m )
	{
		if ( !Networking.IsHost || LobbyNetworkManager.Instance is not { } net ) return;
		if ( !ulong.TryParse( m.WhiteSteamID, out var white ) || !ulong.TryParse( m.BlackSteamID, out var black ) )
		{
			Status = "The match came back without a side assignment.";
			return;
		}

		// The reserved table is the one the opener advertised from — the setup panel is a
		// table's pre-game panel, so the opener is sitting at it. Force-seating both here
		// on gamchess's colours OVERRIDES the opener's walk-up side: that is the whole point
		// (they can't self-assign White by sitting first).
		if ( ChessStation.Active is not { } station )
		{
			Status = "Sit at a table to be matched.";
			return;
		}
		if ( !net.HostSeatMatch( white, black, _tcIndex, station ) )
		{
			Status = "That table isn't free for the match.";
			return;
		}

		// We are one of the two — engage our own assigned seat (found by occupancy).
		_awaitSeat = true;
	}

	/// <summary>Engage a gamchess relay game on the table we're sitting at. Both players do
	/// this and stay in their own lobbies — the game runs on gamchess. Returns false if
	/// we're not seated at a table (nowhere to render the game).</summary>
	static bool EngageRelay( string gameId, string colorWord )
	{
		if ( string.IsNullOrEmpty( gameId ) || ChessStation.Active is not { } station ) return false;
		if ( RelayGameController.For( station ) is not { } relay ) return false;
		relay.Engage( gameId, colorWord == "white", TimeControl.At( _tcIndex ).PgnSpec );
		return true;
	}

	/// <summary>Once our SteamId has been seated at a table (by us as host, or by the
	/// opener's host after we connected), move the camera into that seat. One-shot.</summary>
	static void TryAutoEngage()
	{
		var me = LocalSteam;
		if ( me == 0 || LobbyPlayer.Local is not { } player ) return;

		// Already engaged in a seat that is actually OURS — the match landed us home. Done.
		if ( ChessStation.Active is { } active && active.SeatSteamId( ChessStation.ActiveSeat ) == me )
		{
			_awaitSeat = false;
			return;
		}
		// Engaged in a seat that is NO LONGER ours — a random reassignment overwrote the
		// side we walked up to. Don't engage on top of it; the station's own reconciliation
		// stands us up first, and next frame the scan below re-seats us on the right side.
		if ( ChessStation.Active != null ) return;

		foreach ( var station in player.Scene.GetAllComponents<ChessStation>() )
		{
			if ( station.WhiteSteamId == me ) { player.Engage( station, ChessSeat.White ); _awaitSeat = false; return; }
			if ( station.BlackSteamId == me ) { player.Engage( station, ChessSeat.Black ); _awaitSeat = false; return; }
		}
	}

	/// <summary>Refresh the open list on a cadence while the panel wants it. The panel
	/// calls this each frame it's showing the browse view; we throttle to a poll rate.</summary>
	public static void WantList()
	{
		if ( (float)_listNext > 0f ) return;
		_listNext = 3f;
		Refresh();
	}
}
