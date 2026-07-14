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

/// <summary>Reply from <c>POST /api/challenge/{user}</c> — the created challenge
/// (id/url for reference; the game is identified later via account/playing).
/// <c>POST /api/challenge/ai</c> reuses the same shape but is already a live game,
/// so <c>id</c> is the game id.</summary>
public sealed class LichessChallenge
{
	public string id { get; set; }
	public string url { get; set; }
	public string speed { get; set; }
}

/// <summary>Reply from <c>GET /api/account/playing</c> — the poll payload driving
/// in-sbox play (PLAN.md M4). Only ongoing games are listed, so a game vanishing
/// from here is how we detect it ended.</summary>
public sealed class LichessNowPlaying
{
	public List<NowPlayingGame> nowPlaying { get; set; }
}

public sealed class NowPlayingGame
{
	public string gameId { get; set; }
	public string fullId { get; set; }
	public string color { get; set; }        // "white" / "black" — the signed-in player's side
	public string fen { get; set; }          // current position (may be placement-only)
	public string lastMove { get; set; }     // UCI of the last move, or ""
	public bool isMyTurn { get; set; }
	public int secondsLeft { get; set; }
	public NowPlayingOpponent opponent { get; set; }
}

public sealed class NowPlayingOpponent
{
	public string username { get; set; }
	public int? rating { get; set; }
	public int? ai { get; set; }             // Stockfish level when the opponent is the AI
}

/// <summary>Subset of <c>GET /game/export/{id}</c> (JSON) — read a finished game's
/// outcome, since account/playing has already dropped it.</summary>
public sealed class LichessGameStatus
{
	public string id { get; set; }
	public string status { get; set; }       // "mate","resign","outoftime","draw","stalemate",…
	public string winner { get; set; }       // "white"/"black"/null
}

// ── Puzzles (M5) ──

/// <summary>Reply from <c>GET /api/puzzle/{daily|next|id}</c>. The puzzle position is
/// the source <see cref="game"/>'s PGN played out to <c>puzzle.initialPly</c> half-moves;
/// from there <c>puzzle.solution</c> is the UCI line, opponent-move-first (lichess
/// convention: the position is one ply before the solver's turn, so solution[0] is the
/// opponent's setup move, solution[1] the solver's first move, and so on).</summary>
public sealed class LichessPuzzleResponse
{
	public PuzzleGame game { get; set; }
	public PuzzleData puzzle { get; set; }
}

public sealed class PuzzleGame
{
	public string id { get; set; }
	public string pgn { get; set; }          // space-separated movetext (no headers)
	public string clock { get; set; }
	public List<PuzzlePlayer> players { get; set; }
}

public sealed class PuzzlePlayer
{
	public string name { get; set; }
	public string color { get; set; }        // "white" / "black"
	public int? rating { get; set; }
}

public sealed class PuzzleData
{
	public string id { get; set; }
	public int rating { get; set; }
	public int initialPly { get; set; }
	public List<string> solution { get; set; }   // UCI moves, opponent-first
	public List<string> themes { get; set; }
}

// ── TV (M5) ──

/// <summary>One channel of <c>GET /api/tv/channels</c> — the current featured game
/// on that channel and its top player. The reply is a JSON object keyed by channel
/// name ("bot","blitz","rapid",…), so deserialize the whole thing as
/// <c>Dictionary&lt;string, LichessTvChannel&gt;</c>.</summary>
public sealed class LichessTvChannel
{
	public LichessTvUser user { get; set; }
	public int rating { get; set; }
	public string gameId { get; set; }
	public string color { get; set; }        // colour the featured user plays
}

public sealed class LichessTvUser
{
	public string id { get; set; }
	public string name { get; set; }
	public string title { get; set; }        // "GM", "BOT", … or null
}

/// <summary>Reply from <c>POST /api/token</c> — the OAuth code exchange.</summary>
public sealed class LichessTokenResponse
{
	public string token_type { get; set; }   // "Bearer"
	public string access_token { get; set; }
	public int expires_in { get; set; }
}
