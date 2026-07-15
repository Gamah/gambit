namespace Gambit.Api;

// gamchess JSON DTOs. Property names match the wire format exactly (snake_case),
// so System.Text.Json binds them without attributes or case-insensitive options.
//
// The contract these mirror is documented in the repo README ("gamchess API
// contract"). It is hand-mirrored with no codegen, so a change here is a change
// there — and both halves live in this repo, so it should be one commit.

/// <summary>One archived game, from <c>GET /api/v1/games</c> or
/// <c>GET /api/v1/games/{id}</c>.
/// <para>SteamIDs are STRINGS on the wire: a SteamID64 (~7.6e16) is past
/// JavaScript's 2^53, so a bare number would be corrupted by the web viewer.</para></summary>
public sealed class GamchessGame
{
	public string id { get; set; }
	public string client_game_id { get; set; }
	public string pgn { get; set; }
	public string white_steam_id { get; set; }
	public string black_steam_id { get; set; }
	public string result { get; set; }
	public string played_at { get; set; }
	public string submitted_by { get; set; }
}

/// <summary>Reply from <c>GET /api/v1/games</c>.</summary>
public sealed class GamchessGameList
{
	public GamchessGame[] games { get; set; }
}

/// <summary>An error body from any gamchess endpoint.</summary>
public sealed class GamchessError
{
	public string error { get; set; }
}
