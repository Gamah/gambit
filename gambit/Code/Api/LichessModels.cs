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
