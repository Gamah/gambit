namespace Gambit.Api.Lichess;

// lichess's own wire shapes, as far as Gambit reads them. Property names match
// lichess's JSON exactly so System.Text.Json binds them without attributes.
//
// Ported from the server's internal/lichess/board.go, which read them off the
// live OpenAPI spec on 2026-07-15. Per the repo's re-derive rule these are facts
// about somebody else's API and can change without notice — re-read the spec
// before trusting any of them.
//
// TWO LICHESS STREAMS, TWO ENVELOPES, and confusing them is a real hazard:
// the Board API sends {"type":…} with its fields INLINE (below), while the TV
// feed sends {"t":…,"d":{…}} (see TvState, which gamchess still owns). Nothing
// here applies to TV.

/// <summary>One line of <c>/api/stream/event</c>. Type is gameStart, gameFinish,
/// challenge, challengeCanceled or challengeDeclined. Only the fields Gambit acts
/// on are modelled; lichess may add more and we ignore them.</summary>
public sealed class LichessEvent
{
	public string type { get; set; }
	public LichessEventGame game { get; set; }
	public LichessChallenge challenge { get; set; }
}

/// <summary>Rides gameStart/gameFinish.</summary>
public sealed class LichessEventGame
{
	public string gameId { get; set; }
	public string fullId { get; set; }
	public string fen { get; set; }
	public string color { get; set; }
	public string speed { get; set; }
}

/// <summary>Rides challenge/challengeCanceled/challengeDeclined. <b>The id doubles
/// as the GAME id once accepted</b> — same value, which is what lets a challenger
/// start streaming before the acceptance lands.</summary>
public sealed class LichessChallenge
{
	public string id { get; set; }
	public string status { get; set; }
	public string url { get; set; }
	public LichessChallengeUser challenger { get; set; }
	public LichessChallengeUser destUser { get; set; }
	public string speed { get; set; }
	public bool rated { get; set; }
	/// <summary>"in" = to us, "out" = from us.</summary>
	public string direction { get; set; }
}

public sealed class LichessChallengeUser
{
	public string id { get; set; }
	public string name { get; set; }
}

/// <summary>What <c>POST /api/challenge/open</c> returns.
///
/// <para><c>urlWhite</c>/<c>urlBlack</c> are the same game with a forced colour
/// (literally <c>url + "?color=white"</c>). We take one side and hand the OTHER
/// side's url to the browser opponent.</para></summary>
public sealed class LichessOpenChallenge
{
	public string id { get; set; }
	public string url { get; set; }
	public string urlWhite { get; set; }
	public string urlBlack { get; set; }
}

/// <summary>A <c>gameState</c> line: the whole game so far, never a delta.
///
/// <para><c>moves</c> is the FULL space-separated UCI list from the start position
/// every time, which is what lets the board be rebuilt rather than reconciled — a
/// dropped or duplicated line costs nothing. Times are MILLISECONDS (the TV feed
/// sends the same idea in seconds; two endpoints, two units).</para></summary>
public sealed class LichessGameState
{
	public string type { get; set; }
	public string moves { get; set; }
	public long wtime { get; set; }
	public long btime { get; set; }
	public long winc { get; set; }
	public long binc { get; set; }
	public string status { get; set; }
	/// <summary>"white" | "black" | null (no winner / unfinished).</summary>
	public string winner { get; set; }

	/// <summary>That side has a draw offer standing.
	///
	/// <para><b>lichess OMITS these when false</b> rather than sending false, so
	/// "absent" and "not offering" are the same thing — which is exactly what a
	/// bool's zero value already means. Do not "fix" this with nullable bools.</para>
	///
	/// <para>And a DECLINED draw is INVISIBLE here: lila's <c>Drawer.no</c> emits
	/// nothing to the Board stream. See <c>GameHud</c>'s "Lichess won't signal a
	/// decline" line — a lichess fact, not a custody one, and still true.</para></summary>
	public bool wdraw { get; set; }
	public bool bdraw { get; set; }

	/// <summary>Standing takeback proposals. Unlike a draw, a declined takeback IS
	/// pushed (<c>Takebacker.no</c> publishes), so these can be trusted to clear.</summary>
	public bool wtakeback { get; set; }
	public bool btakeback { get; set; }
}

/// <summary>The <c>gameFull</c> line — always the first line of a game stream, and
/// on a RECONNECT it carries the whole move list again, which is why dropping and
/// re-opening a stream loses nothing.</summary>
public sealed class LichessGameFull
{
	public string type { get; set; }
	public string id { get; set; }
	public string speed { get; set; }
	public bool rated { get; set; }
	public LichessGamePlayer white { get; set; }
	public LichessGamePlayer black { get; set; }
	public string initialFen { get; set; }
	public LichessClockSetup clock { get; set; }
	public LichessGameState state { get; set; }
}

public sealed class LichessGamePlayer
{
	public string id { get; set; }
	public string name { get; set; }
	/// <summary>Set for an AI opponent; absent for a human. Not something Gambit
	/// plays against on purpose, but it is what makes <c>name</c> null.</summary>
	public int aiLevel { get; set; }
}

/// <summary>Milliseconds, both.</summary>
public sealed class LichessClockSetup
{
	public long initial { get; set; }
	public long increment { get; set; }
}

/// <summary>What the token endpoint returns. <b>There is NO refresh token</b>:
/// lichess tokens are long-lived (<c>expires_in</c> ≈ 31536000, about a year) and
/// re-linking is the only renewal path. Nothing may be built to refresh.</summary>
public sealed class LichessTokenResponse
{
	public string access_token { get; set; }
	public string token_type { get; set; }
	public long expires_in { get; set; }
}

/// <summary>Enough of <c>GET /api/account</c> to know who we are.</summary>
public sealed class LichessAccount
{
	public string id { get; set; }
	public string username { get; set; }
}

/// <summary>Status helpers. lichess's status enum is open-ended, so the rule is
/// stated as "which ones are LIVE" rather than enumerating the terminal ones.</summary>
public static class LichessStatus
{
	/// <summary>"created" and "started" are the only live statuses — everything
	/// else (mate, resign, stalemate, timeout, outoftime, draw, aborted, cheat,
	/// noStart, unknownFinish, insufficientMaterialClaim, variantEnd) is terminal.</summary>
	public static bool Finished( string status ) => status switch
	{
		null or "" or "created" or "started" => false,
		_ => true,
	};
}
