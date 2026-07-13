using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rotaliate.Api;

// Shared, defensive flattening of a server-supplied 2D board into the row-major
// 100-cell array GameBoard expects. The grid comes straight off the network, so a
// malformed/hostile payload (wrong dimensions, ragged rows, nulls) must not index
// out of bounds and crash the client — validate the shape and fail with a clear
// message instead. (Security review C5.)
internal static class GridShape
{
	public const int Size = 10;
	public const int CellCount = Size * Size;

	public static int[] Flatten( int[][] grid )
	{
		if ( grid == null || grid.Length != Size )
			throw new FormatException( $"grid must have {Size} rows, got {grid?.Length.ToString() ?? "null"}" );

		var flat = new int[CellCount];
		for ( var r = 0; r < Size; r++ )
		{
			var row = grid[r];
			if ( row == null || row.Length != Size )
				throw new FormatException( $"grid row {r} must have {Size} cells, got {row?.Length.ToString() ?? "null"}" );
			for ( var c = 0; c < Size; c++ )
				flat[r * Size + c] = row[c];
		}
		return flat;
	}
}

// player_tag is the 8-hex-char public player identifier (server-computed hash of
// guid+username); it replaces the GUID everywhere other players can see it, and
// changes whenever the username changes — re-cache it after a rename.
public record PlayerResponse(
	[property: JsonPropertyName( "guid" )] string Guid,
	[property: JsonPropertyName( "username" )] string Username,
	[property: JsonPropertyName( "player_tag" )] string PlayerTag
);

public record CreatePlayerResponse(
	[property: JsonPropertyName( "guid" )] string Guid,
	[property: JsonPropertyName( "player_tag" )] string PlayerTag
);

// GET /players/by-steam?steam_id= (issue #71): recovers an existing account from
// its SteamID after the X-Steam-Token proves ownership. username may be null.
public record RecoverBySteamResponse(
	[property: JsonPropertyName( "guid" )] string Guid,
	[property: JsonPropertyName( "username" )] string Username,
	[property: JsonPropertyName( "player_tag" )] string PlayerTag,
	[property: JsonPropertyName( "steam_id" )] string SteamId
);

// steam_id (issue #68): SteamID64 as a 1–20 digit string (exceeds JS/double
// precision). Optional on POST /players and PUT /players/{guid}/steamid; Steam
// builds send it, web builds omit it (WhenWritingNull → empty body).
// token (issue #69): Facepunch auth token proving Steam ownership; required
// alongside steam_id, omitted (with steam_id) on web builds.
public record CreatePlayerRequest(
	[property: JsonPropertyName( "steam_id" ), JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )] string SteamId = null,
	[property: JsonPropertyName( "token" ), JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )] string Token = null
);

public record LinkSteamIdRequest(
	[property: JsonPropertyName( "steam_id" )] string SteamId,
	[property: JsonPropertyName( "token" )] string Token
);

// Server error envelope: {"error": "message"}
public record ErrorResponse(
	[property: JsonPropertyName( "error" )] string Error
);

// Seeds are JSON strings on the wire — int64 exceeds JS number precision,
// so the server never emits them as JSON numbers.
public record PuzzleResponse(
	[property: JsonPropertyName( "seed" )] string Seed,
	[property: JsonPropertyName( "puzzle_id" )] string PuzzleId,
	[property: JsonPropertyName( "grid" )] int[][] Grid,
	// In-memory server session for move streaming; empty if no X-Player-ID was sent.
	[property: JsonPropertyName( "session_id" )] string SessionId
)
{
	// Flatten the 2D grid to a 1D row-major array for GameBoard (shape-validated).
	public int[] FlatGrid() => GridShape.Flatten( Grid );
};

public record FreeplayRequest(
	[property: JsonPropertyName( "seed" )] string Seed
);

// Encoding: rotation = direction*81 + row*9 + col (0–161, dir 0=CW 1=CCW);
// selector reposition = 162 + row*9 + col (162–242). row/col = 2×2 top-left (0–8).
public record MoveRequest(
	[property: JsonPropertyName( "move" )] int Move
);

public record MoveResponse(
	[property: JsonPropertyName( "move_count" )] int MoveCount,
	[property: JsonPropertyName( "solved" )] bool Solved,
	[property: JsonPropertyName( "duration_ms" )] long DurationMs
);

public record LeaderboardEntry(
	[property: JsonPropertyName( "rank" )] int Rank,
	[property: JsonPropertyName( "username" )] string Username,
	// Display fallback for legacy rows whose player never set a name
	[property: JsonPropertyName( "player_tag" )] string PlayerTag,
	[property: JsonPropertyName( "duration_ms" )] long DurationMs,
	[property: JsonPropertyName( "total_moves" )] int TotalMoves,
	[property: JsonPropertyName( "session_id" )] string SessionId
);

public record SessionResponse(
	[property: JsonPropertyName( "session_id" )]  string SessionId,
	[property: JsonPropertyName( "puzzle_id" )]   string PuzzleId,
	[property: JsonPropertyName( "seed" )]        string Seed,
	[property: JsonPropertyName( "game_type_id" )] int   GameTypeId,
	[property: JsonPropertyName( "player_tag" )]  string PlayerTag,
	[property: JsonPropertyName( "username" )]    string Username,
	[property: JsonPropertyName( "duration_ms" )] long   DurationMs,
	[property: JsonPropertyName( "total_moves" )] int    TotalMoves,
	[property: JsonPropertyName( "completed_at" )] string CompletedAt
);

// ── Replay ──

public record ReplayMove(
	[property: JsonPropertyName( "row" )] int Row,
	[property: JsonPropertyName( "col" )] int Col,
	[property: JsonPropertyName( "direction" )] int Direction,
	// Selector reposition: counts as a move but doesn't rotate the board
	[property: JsonPropertyName( "selector" )] bool Selector,
	// Which player made the move (multiplayer); 0/absent for solo. Maps to
	// ReplayResponse.Players for the selector ring color.
	[property: JsonPropertyName( "player_number" )] int PlayerNumber,
	// Server arrival time, Unix ms; 0 if not recorded
	[property: JsonPropertyName( "played_at" )] long PlayedAt
);

// Multiplayer replay roster entry: which board colors each player_number owns.
public record ReplayPlayer(
	[property: JsonPropertyName( "player_number" )] int   PlayerNumber,
	[property: JsonPropertyName( "colors" )]        int[] Colors
);

public record ReplayResponse(
	[property: JsonPropertyName( "seed" )] string Seed,
	[property: JsonPropertyName( "moves" )] List<ReplayMove> Moves,
	// Present for multiplayer replays; null/absent for solo.
	[property: JsonPropertyName( "players" )] List<ReplayPlayer> Players
);

// ── Public profile (GET /api/v1/profile/{player_tag}) ──

// One completed game in a player's public profile. seed is a JSON string (int64).
// duration_ms/total_moves/rank may be null (abandoned game / not recorded).
// game_type ∈ daily|hourly|freeplay|2p|4p. Any type with a session_id can be watched
// on the giant spectator board (SpectatorBoard.StartReplay) — server #52 stores moves
// for all modes.
public record ProfileGame(
	[property: JsonPropertyName( "session_id" )]  string SessionId,
	[property: JsonPropertyName( "game_type" )]   string GameType,
	[property: JsonPropertyName( "seed" )]        string Seed,
	[property: JsonPropertyName( "duration_ms" )] long?  DurationMs,
	[property: JsonPropertyName( "total_moves" )] int?   TotalMoves,
	[property: JsonPropertyName( "completed_at" )] string CompletedAt,
	[property: JsonPropertyName( "rank" )]        int?   Rank
);

// Public, auth-free profile keyed by player_tag; username may be null (never set).
public record ProfileResponse(
	[property: JsonPropertyName( "player_tag" )] string            PlayerTag,
	[property: JsonPropertyName( "username" )]   string            Username,
	[property: JsonPropertyName( "games" )]      List<ProfileGame> Games
);

// Returned by /api/v1/daily/recent and /api/v1/hourly/recent (flat array)
public record RecentPuzzle(
	[property: JsonPropertyName( "puzzle_id" )] string PuzzleId,
	[property: JsonPropertyName( "active_from" )] string ActiveFrom
);

public record SetUsernameRequest(
	[property: JsonPropertyName( "username" )] string Username
);

// PUT /players/{guid}/username — tag is recomputed from the new name
public record SetUsernameResponse(
	[property: JsonPropertyName( "username" )] string Username,
	[property: JsonPropertyName( "player_tag" )] string PlayerTag
);

public record FeedbackRequest(
	[property: JsonPropertyName( "message" )] string Message,
	[property: JsonPropertyName( "email" )]   string Email = ""
);

// ── WebSocket multiplayer ──

// POST /api/v1/ws/ticket (X-Player-ID header) → a single-use, short-TTL ticket
// for the WS upgrade. The GUID is the player's secret credential, so it's proven
// once over HTTP here and never rides the WS URL (security review C3). ttl_ms is
// the validity window (~30s) — mint immediately before connecting.
public record WsTicketResponse(
	[property: JsonPropertyName( "ticket" )] string Ticket,
	[property: JsonPropertyName( "ttl_ms" )] long   TtlMs
);

public record WsEnvelope(
	[property: JsonPropertyName( "type" )]    string      Type,
	[property: JsonPropertyName( "payload" )] JsonElement Payload
);

public record LobbyPromptPayload(
	[property: JsonPropertyName( "max" )] int Max
);

public record LobbyCreatedPayload(
	[property: JsonPropertyName( "code" )]    string            Code,
	[property: JsonPropertyName( "count" )]   int               Count,
	[property: JsonPropertyName( "max" )]     int               Max,
	[property: JsonPropertyName( "public" )]  bool              Public,
	[property: JsonPropertyName( "players" )] List<LobbyPlayer> Players
);

// One public, joinable lobby from GET /api/v1/lobbies/open
public record OpenLobby(
	[property: JsonPropertyName( "code" )]       string Code,
	[property: JsonPropertyName( "mode" )]       int    Mode,
	[property: JsonPropertyName( "count" )]      int    Count,
	[property: JsonPropertyName( "max" )]        int    Max,
	[property: JsonPropertyName( "host" )]       string Host,
	[property: JsonPropertyName( "created_at" )] long   CreatedAt
);

public record LobbyPlayer(
	[property: JsonPropertyName( "player_tag" )] string PlayerTag,
	[property: JsonPropertyName( "username" )]   string Username
);

public record LobbyUpdatePayload(
	[property: JsonPropertyName( "code" )]    string            Code,
	[property: JsonPropertyName( "count" )]   int               Count,
	[property: JsonPropertyName( "max" )]     int               Max,
	[property: JsonPropertyName( "players" )] List<LobbyPlayer> Players
);

public record MpPlayerInfo(
	[property: JsonPropertyName( "player_tag" )] string PlayerTag,
	[property: JsonPropertyName( "username" )]  string Username,
	[property: JsonPropertyName( "color" )]     int    Color,
	[property: JsonPropertyName( "colors" )]    int[]  Colors
);

public record PlayerReadyPayload(
	[property: JsonPropertyName( "player_tag" )]   string PlayerTag,
	[property: JsonPropertyName( "username" )]     string Username,
	[property: JsonPropertyName( "ready_count" )]  int    ReadyCount,
	[property: JsonPropertyName( "player_count" )] int    PlayerCount
);

public record RoomReadyPayload(
	[property: JsonPropertyName( "room_id" )] string              RoomId,
	[property: JsonPropertyName( "seed" )]    string              Seed,
	[property: JsonPropertyName( "grid" )]    int[][]             Grid,
	[property: JsonPropertyName( "color" )]   int                 Color,
	[property: JsonPropertyName( "colors" )]  int[]               Colors,
	[property: JsonPropertyName( "players" )] List<MpPlayerInfo>  Players
)
{
	public int[] FlatGrid() => GridShape.Flatten( Grid );
}

public record MpSelectorInfo(
	[property: JsonPropertyName( "color" )] int Color,
	[property: JsonPropertyName( "row" )]   int Row,
	[property: JsonPropertyName( "col" )]   int Col
);

public record MpLastMoveInfo(
	[property: JsonPropertyName( "move" )]  int Move,  // encoded rotation, 0–161
	[property: JsonPropertyName( "color" )] int Color  // mover's primary color
);

public record StateSyncPayload(
	[property: JsonPropertyName( "grid" )]       int[][]                Grid,
	[property: JsonPropertyName( "move_count" )] int                    MoveCount,
	[property: JsonPropertyName( "counts" )]     Dictionary<string,int> Counts,
	[property: JsonPropertyName( "selectors" )]  List<MpSelectorInfo>   Selectors,
	[property: JsonPropertyName( "last_move" )]  MpLastMoveInfo         LastMove,
	[property: JsonPropertyName( "ts" )]         long                   Ts
)
{
	public int[] FlatGrid() => GridShape.Flatten( Grid );
}

public record MpGameOverPayload(
	[property: JsonPropertyName( "winner_tag" )]   string WinnerTag,
	[property: JsonPropertyName( "winner_color" )] int    WinnerColor,
	[property: JsonPropertyName( "duration_ms" )]  long   DurationMs
);

public record MpWinEntry(
	[property: JsonPropertyName( "player_tag" )] string PlayerTag,
	[property: JsonPropertyName( "username" )]  string Username,
	[property: JsonPropertyName( "wins" )]      int    Wins
);
