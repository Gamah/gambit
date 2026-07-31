using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Gambit.Api;

/// <summary>
/// Typed gamchess calls for cross-session matchmaking (M19), mirroring
/// <see cref="LichessApi"/>: each method is one <see cref="GamchessApi.SendAuthed"/>,
/// every response a small DTO parsed by <see cref="GamchessApi.Deserialize{T}"/>.
///
/// <para>Two flows share the directory. A <b>join</b> match ends with the joiner
/// calling <c>Networking.Connect(lobby_id)</c> to enter the opener's lobby and play
/// the ordinary two-seat game — gamchess is done once paired. A <b>relay</b> match
/// spins up a gamchess-authoritative live game (<see cref="RelayGet"/> /
/// <see cref="RelayMove"/> / <see cref="RelayAction"/>), the one game type that
/// cannot run without gamchess.</para>
///
/// <para>Everything here degrades the same way the rest of gamchess does: a call
/// returns a <see cref="GamchessApi.Result"/> that never throws, and a failure
/// means "matchmaking's offline", never a broken game.</para>
/// </summary>
public static class MatchmakingApi
{
	// ── Directory ──

	/// <summary>Advertise an open game. <paramref name="lobbyId"/> is the local
	/// host's SteamId (join mode only — the connect target); ignored for relay.</summary>
	public static Task<GamchessApi.Result> Open( string mode, string lobbyId, string timeControl, string openerName ) =>
		GamchessApi.SendAuthed( "/api/v1/matchmaking", "POST", GamchessApi.Json( new
		{
			mode,
			lobby_id = lobbyId ?? "",
			time_control = timeControl,
			opener_name = openerName ?? "",
		} ) );

	/// <summary>The open games you could join (never your own).</summary>
	public static Task<GamchessApi.Result> List() =>
		GamchessApi.SendAuthed( "/api/v1/matchmaking", "GET", null );

	/// <summary>Poll one match — the opener waits on this to learn someone joined
	/// (and their colour, and a relay game_id). Doubles as the opener's heartbeat.</summary>
	public static Task<GamchessApi.Result> Poll( string id ) =>
		GamchessApi.SendAuthed( $"/api/v1/matchmaking/{id}", "GET", null );

	/// <summary>Claim an open game. gamchess coin-flips the colour, so the response's
	/// <c>your_color</c> is authoritative — the opener does not get White by default.</summary>
	public static Task<GamchessApi.Result> Join( string id ) =>
		GamchessApi.SendAuthed( $"/api/v1/matchmaking/{id}/join", "POST", null );

	/// <summary>Cancel your own open advert.</summary>
	public static Task<GamchessApi.Result> Cancel( string id ) =>
		GamchessApi.SendAuthed( $"/api/v1/matchmaking/{id}", "DELETE", null );

	// ── Relay game (relay mode) ──

	/// <summary>Poll a relay game's state, moves from ply <paramref name="since"/> on.</summary>
	public static Task<GamchessApi.Result> RelayGet( string id, int since ) =>
		GamchessApi.SendAuthed( $"/api/v1/relaygame/{id}?since={since}", "GET", null );

	/// <summary>Play a move. <paramref name="over"/> (with result/reason) is set when
	/// the mover's own rules say the move ended the game — gamchess trusts it, exactly
	/// as the two-seat host trusts a NetChessMove.</summary>
	public static Task<GamchessApi.Result> RelayMove( string id, string uci, string fen,
		bool over = false, string result = null, string reason = null ) =>
		GamchessApi.SendAuthed( $"/api/v1/relaygame/{id}/move", "POST", GamchessApi.Json( new
		{
			uci,
			fen,
			over,
			result = result ?? "",
			reason = reason ?? "",
		} ) );

	/// <summary>Resign / abort / offer, accept or decline a draw.</summary>
	public static Task<GamchessApi.Result> RelayAction( string id, string action ) =>
		GamchessApi.SendAuthed( $"/api/v1/relaygame/{id}/{action}", "POST", null );

	// ── Response DTOs (snake_case on the wire, mirrored by hand — CLAUDE.md) ──

	public sealed class OpenResponse
	{
		[JsonPropertyName( "id" )] public string Id { get; set; }
	}

	public sealed class MatchItem
	{
		[JsonPropertyName( "id" )] public string Id { get; set; }
		[JsonPropertyName( "opener_name" )] public string OpenerName { get; set; }
		[JsonPropertyName( "mode" )] public string Mode { get; set; }
		[JsonPropertyName( "time_control" )] public string TimeControl { get; set; }
		[JsonPropertyName( "status" )] public string Status { get; set; }
		[JsonPropertyName( "created_at" )] public string CreatedAt { get; set; }
		// Present only on a participant's poll of a matched row: their colour, the relay
		// game_id, and — for the opener host, which seats both — the assigned SteamIDs.
		[JsonPropertyName( "your_color" )] public string YourColor { get; set; }
		[JsonPropertyName( "game_id" )] public string GameId { get; set; }
		[JsonPropertyName( "white_steam_id" )] public string WhiteSteamID { get; set; }
		[JsonPropertyName( "black_steam_id" )] public string BlackSteamID { get; set; }
		[JsonPropertyName( "opponent_name" )] public string OpponentName { get; set; }
	}

	public sealed class MatchList
	{
		[JsonPropertyName( "matches" )] public MatchItem[] Matches { get; set; }
	}

	public sealed class JoinResponse
	{
		[JsonPropertyName( "mode" )] public string Mode { get; set; }
		[JsonPropertyName( "your_color" )] public string YourColor { get; set; }
		[JsonPropertyName( "lobby_id" )] public string LobbyId { get; set; } // join mode
		[JsonPropertyName( "game_id" )] public string GameId { get; set; }   // relay mode
	}

	/// <summary>A relay game's live state. Clocks are already ticked to "now" server-side;
	/// the client runs the ticking side down locally between polls and snaps on each one
	/// (the house rule — never read HIGH). Moves are the tail from the requested cursor.</summary>
	public sealed class RelayState
	{
		[JsonPropertyName( "id" )] public string Id { get; set; }
		[JsonPropertyName( "white_steam_id" )] public string WhiteSteamId { get; set; }
		[JsonPropertyName( "black_steam_id" )] public string BlackSteamId { get; set; }
		[JsonPropertyName( "time_control" )] public string TimeControl { get; set; }
		[JsonPropertyName( "ply" )] public int Ply { get; set; }
		[JsonPropertyName( "moves" )] public string[] Moves { get; set; }
		[JsonPropertyName( "fen" )] public string Fen { get; set; }
		[JsonPropertyName( "turn" )] public string Turn { get; set; }
		[JsonPropertyName( "white_ms" )] public long WhiteMs { get; set; }
		[JsonPropertyName( "black_ms" )] public long BlackMs { get; set; }
		[JsonPropertyName( "untimed" )] public bool Untimed { get; set; }
		[JsonPropertyName( "status" )] public string Status { get; set; }
		[JsonPropertyName( "reason" )] public string Reason { get; set; }
		[JsonPropertyName( "draw_offer" )] public string DrawOffer { get; set; }

		public bool Finished => Status is not null and not "live";
	}
}
