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

	/// <summary>The link the seated player hands to their browser opponent (the
	/// OPPOSITE colour's url of an open-challenge game). Empty for every other flow.
	/// See <see cref="LichessApi.OpenLink"/> and the server's runOpen.</summary>
	public string share_url { get; set; }

	public string white_steam_id { get; set; }
	public string black_steam_id { get; set; }
	public string white_name { get; set; }
	public string black_name { get; set; }

	public string moves { get; set; }

	// Milliseconds, straight from lichess. lichess is the only authority on its own
	// clocks — but it only SENDS one when a move happens, so the client runs the
	// side-to-move's value down locally between moves and snaps back to these on the
	// next state (see LichessGameController's countdown). The two staleness fields
	// below are what let it do that without reading HIGH.
	public long white_time_ms { get; set; }
	public long black_time_ms { get; set; }
	public long white_inc_ms { get; set; }
	public long black_inc_ms { get; set; }

	/// <summary>How long ago gamchess received these clocks from lichess, and how long
	/// it held our request — both in ms, both computed at send. The client subtracts
	/// this frame's staleness (age + measured network, where network = round trip −
	/// hold) before running a clock down, so a stale frame never reads HIGHER than the
	/// time actually left. Identical machinery and reasoning to <see cref="TvState"/>'s
	/// two fields. 0 (omitted) from an older gamchess ⇒ no correction ⇒ the old
	/// frozen-between-moves behaviour, never a broken clock.</summary>
	public long clock_age_ms { get; set; }
	public long hold_ms { get; set; }

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
	public bool white_takeback { get; set; }  // that side is proposing a takeback
	public bool black_takeback { get; set; }
}

/// <summary>The game session bearer, from <c>POST /api/v1/session</c> (M9).
/// <para>Traded for a Facepunch token once, then presented on every later request:
/// gamchess verifies it with a local HMAC and no I/O, where an FP token costs a live
/// Facepunch round-trip <i>per request</i>. Held in memory only — see
/// <see cref="GamchessApi.ForgetSession"/>.</para></summary>
public sealed class SessionResponse
{
	/// <summary>Prefixed <c>gcs_</c> so gamchess can tell it from an FP token
	/// without a second header.</summary>
	public string token { get; set; }

	/// <summary>Unix seconds. Advisory — the client re-mints on a 401 rather than
	/// watching the clock, because the two clocks may disagree.</summary>
	public long expires_at { get; set; }
}

/// <summary>The featured game on a lichess TV channel — one full, self-contained
/// snapshot pushed over the TV WebSocket (M18), <c>wss://…/api/v1/tv/{channel}</c>.
///
/// <para>Every message is the whole state, not a delta: latest-wins, no cursor. The
/// pre-M18 long poll's <c>version</c> and <c>hold_ms</c> fields are gone with it.</para>
///
/// <para><b>Clocks are SECONDS here</b>, not the milliseconds
/// <see cref="LichessPlayState"/> uses for the same idea: two lichess endpoints, two
/// units. Seconds is what <c>TimeControl.Format</c> takes, so nothing converts.</para>
///
/// <para>Nobody in a TV game is a Gambit player. There are no SteamIDs and no seats —
/// this is a spectate-only feed and none of it may ever be treated as a caller.</para></summary>
public sealed class TvState
{
	public string channel { get; set; }

	/// <summary>Human channel name ("Blitz"), so the client needn't keep its own copy
	/// in sync with the server's.</summary>
	public string label { get; set; }

	/// <summary>Why the channel has nothing right now (lichess backing off, stream
	/// dropped). Never fatal — the wall keeps mirroring real tables regardless.</summary>
	public string error { get; set; }

	public string game_id { get; set; }
	public string url { get; set; }

	public string fen { get; set; }
	public string last_move_uci { get; set; }

	public string white_name { get; set; }
	public string black_name { get; set; }
	public string white_title { get; set; }
	public string black_title { get; set; }
	public int white_rating { get; set; }
	public int black_rating { get; set; }

	/// <summary>Seconds. See the class remarks.</summary>
	public int white_clock { get; set; }
	public int black_clock { get; set; }

	/// <summary>"white" | "black" | null — whose clock is running, derived by gamchess
	/// from the FEN's side-to-move.</summary>
	public string ticking_seat { get; set; }

	/// <summary>How long ago gamchess RECEIVED these clocks from lichess, in ms (M18).
	///
	/// <para>Milliseconds of age, not a timestamp, and that is the point: we do not
	/// share a wall clock with gamchess, so an absolute stamp would be corrected by
	/// whatever our machine's clock skew happens to be — including upwards, which is
	/// the one direction a live clock may never be wrong in. A duration survives
	/// skew.</para>
	///
	/// <para><b>~0 on a live push</b> — a change wakes the writer and it sends at once,
	/// so a moving game's steady-state frames are fresh and the whole-second floor
	/// absorbs the sub-second transport latency for free. It is the CONNECT/replay path
	/// that this covers: a client that tunes in mid-think is handed the stored frame,
	/// already this-many-ms stale, and subtracts <c>age_ms</c> so it reads LOW not high.
	/// 0 (omitted) means "no correction". Replaces the pre-M18 <c>clock_age_ms</c> +
	/// <c>hold_ms</c> + round-trip apparatus with this single field.</para></summary>
	public long age_ms { get; set; }

	// How the PREVIOUS featured game ended.
	//
	// <para>The TV feed says nothing about a game ending — it just swaps to the next
	// one — so gamchess fetches these from the game export when it notices the swap.
	// They describe the game you were probably still watching, NOT the one in
	// <see cref="fen"/>.</para>

	/// <summary>The game that just ended. The client matches this against the game it
	/// is currently showing: if they're the same, that's "your game finished, here's
	/// how", and it's the cue for the fanfare.</summary>
	public string last_game_id { get; set; }

	/// <summary>lichess's own status: mate, resign, outoftime, stalemate, draw,
	/// timeout, aborted, variantEnd… Empty when the fetch failed, which costs the
	/// fanfare its reason and nothing else.</summary>
	public string last_status { get; set; }

	/// <summary>"white" | "black" | null. <b>Null means a DRAW</b> — lichess omits the
	/// field rather than sending a third value, so it's an answer, not a gap.</summary>
	public string last_winner { get; set; }

	public string last_white_name { get; set; }
	public string last_black_name { get; set; }
}

/// <summary>What <c>GET /api/v1/tv/channels</c> returns.</summary>
public sealed class TvChannelsResponse
{
	public string @default { get; set; }
	public TvChannelInfo[] channels { get; set; }
}

public sealed class TvChannelInfo
{
	public string key { get; set; }
	public string label { get; set; }
}
