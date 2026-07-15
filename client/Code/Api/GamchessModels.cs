namespace Gambit.Api;

// gamchess JSON DTOs. Property names match the wire format exactly (snake_case),
// so System.Text.Json binds them without attributes or case-insensitive options —
// same trick as LichessModels.
//
// The contract these mirror is documented in the repo README ("gamchess API
// contract"). It is hand-mirrored with no codegen, so a change here is a change
// there — and both halves live in this repo, so it should be one commit.
//
// Note what has no DTO and never will: a lichess token. gamchess has no column
// for one and no endpoint that returns one.

/// <summary>Reply from <c>POST /api/v1/auth/lichess/begin</c>.</summary>
public sealed class GamchessBeginResponse
{
	/// <summary>The exact redirect_uri to hand lichess. Must be used byte-identically
	/// at authorize and at exchange, so we never rebuild it ourselves.</summary>
	public string redirect_uri { get; set; }
}

/// <summary>Reply from <c>GET /api/v1/auth/lichess/code</c>. 404 until the browser
/// has come back — that is the normal "not yet", not an error.</summary>
public sealed class GamchessCodeResponse
{
	/// <summary>The OAuth authorization code. Single-use, ~1 minute, and useless
	/// without the PKCE verifier that never left this machine.</summary>
	public string code { get; set; }
}

/// <summary>Reply from <c>PUT /api/v1/links/lichess</c>.</summary>
public sealed class GamchessLinkResponse
{
	public string lichess_username { get; set; }
}

/// <summary>An error body from any gamchess endpoint.</summary>
public sealed class GamchessError
{
	public string error { get; set; }
}
