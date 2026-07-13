using System.Collections.Generic;

namespace Gambit.Api;

// lichess JSON DTOs. Property names are lowercased to match the wire format
// exactly, so System.Text.Json binds them without needing attributes or
// case-insensitive options (same trick as LocalGameController.ImportResponse).

/// <summary>Reply from <c>GET /api/account</c> — the signed-in user's profile.</summary>
public sealed class LichessAccount
{
	public string id { get; set; }
	public string username { get; set; }
	public string title { get; set; }          // "GM", "BOT", … or null
	public Dictionary<string, LichessPerf> perfs { get; set; }
}

/// <summary>One rating bucket inside <see cref="LichessAccount.perfs"/>.</summary>
public sealed class LichessPerf
{
	public int rating { get; set; }
	public int games { get; set; }
	public bool prov { get; set; }              // provisional (few games played)
}

/// <summary>Reply from <c>POST /api/import</c> — the imported game's location.</summary>
public sealed class LichessImport
{
	public string id { get; set; }
	public string url { get; set; }
}

/// <summary>Reply from <c>POST /api/challenge/open</c> — an open-ended challenge
/// anyone can join by opening a URL (M4). The fields are top-level (this endpoint
/// does not wrap them under a <c>challenge</c> key, unlike the direct-challenge
/// endpoints). <c>urlWhite</c>/<c>urlBlack</c> each pin a colour: whoever opens
/// that URL plays that side, so they double as our side-assignment source. These
/// URLs are public (no token) — safe to <c>[Sync]</c> across the lobby.</summary>
public sealed class LichessOpenChallenge
{
	public string id { get; set; }
	public string url { get; set; }
	public string speed { get; set; }        // "rapid" for 10+0
	public string urlWhite { get; set; }
	public string urlBlack { get; set; }
}

/// <summary>Reply from <c>POST /api/token</c> — the OAuth code exchange.</summary>
public sealed class LichessTokenResponse
{
	public string token_type { get; set; }   // "Bearer"
	public string access_token { get; set; }
	public int expires_in { get; set; }
}
