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

/// <summary>Reply from <c>GET /api/v1/lichess</c> — is this player linked?
/// <para>Note what isn't here: the lichess token. It never crosses this seam in
/// either direction. The client authenticates to gamchess; gamchess acts on
/// lichess.</para></summary>
public sealed class LichessLink
{
	public bool linked { get; set; }
	public string lichess_id { get; set; }   // canonical lowercase — the identity
	public string username { get; set; }     // display casing — cosmetic
	public string link_url { get; set; }
}

/// <summary>One snapshot of a relayed lichess game, from
/// <c>GET /api/v1/lichess/play/{id}</c>.
///
/// <para><c>moves</c> is lichess's own full UCI list from the start position —
/// never a delta — which is why a dropped or duplicated poll costs nothing and
/// there is no reconciliation to get wrong. Replay it into a ChessGame.</para>
///
/// <para><c>version</c> is the long-poll cursor: pass it back as <c>since</c> and
/// gamchess holds the request until the state moves past it.</para>
///
/// <para>SteamIDs are STRINGS, as everywhere in this API: a SteamID64 is past
/// JavaScript's 2^53 and the web viewer reads the same contract.</para></summary>
public sealed class LichessPlayState
{
	/// <summary>"waiting" (the other seat hasn't asked yet) · "challenging" ·
	/// "live" · "over" · "failed".</summary>
	public string status { get; set; }
	public string error { get; set; }
	public ulong version { get; set; }

	public string game_id { get; set; }
	public string url { get; set; }

	public string white_steam_id { get; set; }
	public string black_steam_id { get; set; }
	public string white_name { get; set; }
	public string black_name { get; set; }

	public string moves { get; set; }

	// Milliseconds, straight from lichess. Rendered, never run locally — lichess
	// is the only authority on its own clocks, exactly as the host is on a local
	// table's.
	public long white_time_ms { get; set; }
	public long black_time_ms { get; set; }
	public long white_inc_ms { get; set; }
	public long black_inc_ms { get; set; }

	/// <summary>A game against a RANDOM lichess opponent rather than the player
	/// sitting opposite. The stranger has no SteamID, so one seat id is empty and
	/// <see cref="your_color"/> is the only way to know which side you have.</summary>
	public bool seek { get; set; }

	/// <summary>"white" | "black" | null — which side YOU play. Stamped per caller
	/// by gamchess, and the only per-caller field in an otherwise shared snapshot.
	/// <para>Authoritative over matching SteamIDs: for a seek there is no opponent
	/// SteamID to match, and for a paired game gamchess knows what it actually
	/// started.</para></summary>
	public string your_color { get; set; }

	/// <summary>lichess's own status: created/started/mate/resign/outoftime/…</summary>
	public string lichess_status { get; set; }
	public string winner { get; set; }        // "white" | "black" | null
	public bool finished { get; set; }
	public bool white_draw { get; set; }      // that side is offering a draw
	public bool black_draw { get; set; }
}
