using System;
using System.Threading.Tasks;
using Gambit.Game;

namespace Gambit.Api;

/// <summary>
/// Lichess, as far as the client is concerned (M8).
///
/// <para><b>Every call here goes to gamchess, never to lichess.</b> The client
/// holds no lichess token and speaks no lichess protocol: it authenticates to
/// gamchess with the Facepunch token exactly as the archive does, and gamchess
/// acts on lichess with the token it stores. That is the whole shape of the
/// custody decision — see CLAUDE.md.</para>
///
/// <para>Why it has to be that way: playing a lichess game means holding a
/// long-lived ndjson stream open, and this client cannot read a stream at all
/// (<c>Sandbox.Http</c> buffers the whole body before returning, and
/// <c>HttpCompletionOption</c> is off the API whitelist). Whoever reads the
/// stream must hold the token.</para>
///
/// <para>Thin on purpose: this is a URL table over <see cref="GamchessApi"/>, so
/// it inherits the 8s timeout, the shared circuit breaker, and the re-mint-once
/// -on-401 rule rather than reimplementing any of them. <b>Lichess being down,
/// or unlinked, or refusing, must never be something that stops local chess</b> —
/// the same rule gamchess itself lives under.</para>
/// </summary>
public static class LichessApi
{
	/// <summary>The URL the in-game board copies to the clipboard.
	///
	/// <para>A constant with no secret in it, and safe precisely because of that:
	/// it is Steam-session gated, so whoever opens it links <i>their own</i>
	/// accounts. Handing it to a friend just links the friend.</para></summary>
	public const string LinkUrl = GamchessApi.Base + "/lichess/link";

	/// <summary>Where a player really turns Gambit off, if they don't trust us to.
	/// Worth naming in the UI: lichess's <c>/account/oauth/token</c> page does NOT
	/// list this grant (it shows personal tokens only), so someone looking there
	/// sees an empty list and concludes nothing is linked.</summary>
	public const string SecurityUrl = "https://lichess.org/account/security";

	// ── Linking ──

	/// <summary>Am I linked? Answers only about the caller — there is no way to ask
	/// about anyone else, and no SteamID parameter to pass.</summary>
	public static Task<GamchessApi.Result> Status() =>
		GamchessApi.SendAuthed( "/api/v1/lichess", "GET", null );

	/// <summary>Unlink: gamchess revokes the token at lichess, then forgets it.</summary>
	public static Task<GamchessApi.Result> Unlink() =>
		GamchessApi.SendAuthed( "/api/v1/lichess", "DELETE", null );

	// ── Playing ──

	/// <summary>
	/// "I want this table's game played on lichess."
	///
	/// <para><b>BOTH seats must call this</b>, each from their own client with their
	/// own Facepunch token, before gamchess will issue a challenge. That is not a
	/// formality: gamchess holds a token for every linked player, so if one seat
	/// could start a game alone, any linked player could drag any other into a
	/// lichess game at will. Two independently-authenticated intents are what make
	/// it consent.</para>
	///
	/// <para><paramref name="clientGameId"/> is the table's synced id — the
	/// rendezvous key both seats agree on. It is not a secret and carries no
	/// authority; the FP tokens do.</para>
	/// </summary>
	public static Task<GamchessApi.Result> Play( string clientGameId, ulong whiteSteamId,
		ulong blackSteamId, TimeControl tc ) =>
		GamchessApi.SendAuthed( "/api/v1/lichess/play", "POST", GamchessApi.Json( new
		{
			client_game_id = clientGameId,
			white_steam_id = whiteSteamId.ToString(),
			black_steam_id = blackSteamId.ToString(),
			limit_seconds = tc.InitialSeconds,
			increment_seconds = tc.IncrementSeconds,
			unlimited = tc.IsUnlimited,
		} ) );

	/// <summary>
	/// Find a RANDOM lichess opponent (a lobby seek).
	///
	/// <para>Unlike <see cref="Play"/> this needs only ONE caller: you are spending
	/// your own grant to play a stranger who opts in on lichess's side by their own
	/// choice, so there is nobody to get consent from. It works from a table you're
	/// sitting at alone — the opponent isn't in this lobby at all.</para>
	///
	/// <para><b>Rapid or slower only.</b> lichess's lobby refuses blitz and faster
	/// (a stricter floor than a direct challenge's), so the fast presets can only be
	/// played against the person opposite you.</para>
	///
	/// <para><b>Scarce.</b> lichess allows ~5 lobby seeks a minute PER IP, and all
	/// of Terry's Gambit is one IP — so this budget is shared by every player alive.
	/// gamchess self-limits and explains rather than spending it on a 429. Don't
	/// retry on failure; show the reason.</para>
	///
	/// <para><paramref name="timeMinutes"/> is MINUTES — lichess's unit for a seek,
	/// where a challenge takes seconds. The asymmetry is theirs.</para>
	/// </summary>
	public static Task<GamchessApi.Result> Seek( string clientGameId, float timeMinutes,
		int incrementSeconds, bool rated, string ratingRange = null, string color = null ) =>
		GamchessApi.SendAuthed( "/api/v1/lichess/seek", "POST", GamchessApi.Json( new
		{
			client_game_id = clientGameId,
			time_minutes = timeMinutes,
			increment_seconds = incrementSeconds,
			rated,
			rating_range = ratingRange ?? "",
			color = color ?? "",
		} ) );

	/// <summary>Withdraw a seek, or drop a pairing that hasn't started.
	///
	/// <para>Not politeness — the held connection IS the seek, so this is what
	/// actually removes it from lichess's lobby. A player who walks away without
	/// this stays pairable and gets dropped into a game nobody is sitting at.</para></summary>
	public static Task<GamchessApi.Result> Cancel( string clientGameId ) =>
		GamchessApi.SendAuthed(
			$"/api/v1/lichess/play/{Uri.EscapeDataString( clientGameId )}", "DELETE", null );

	/// <summary>
	/// The game-state transport: a long poll. gamchess holds this open for ~5s
	/// waiting for the state to pass <paramref name="since"/>, then answers.
	///
	/// <para>A poll rather than a WebSocket. s&amp;box <i>can</i> speak WebSocket —
	/// <c>Sandbox.WebSocket</c> streams fine — but gamchess would need a Go WS
	/// library, and that repo can't take a new dependency (the machine that writes
	/// it has neither Go nor Docker to regenerate go.sum). A long poll suits a
	/// client whose HTTP buffers whole bodies anyway, and costs one round trip of
	/// latency, which blitz can afford.</para>
	///
	/// <para>The 5s hold is deliberately under <see cref="GamchessApi.Timeout"/>:
	/// a hold longer than that would look like a timeout to us and trip the
	/// breaker on every poll.</para>
	/// </summary>
	public static Task<GamchessApi.Result> PollState( string clientGameId, ulong since ) =>
		GamchessApi.SendAuthed(
			$"/api/v1/lichess/play/{Uri.EscapeDataString( clientGameId )}?since={since}", "GET", null );

	/// <summary>Play a move on lichess. gamchess uses OUR token — a seat can only
	/// ever act for itself.</summary>
	public static Task<GamchessApi.Result> Move( string clientGameId, string uci ) =>
		Act( clientGameId, "move", new { uci } );

	public static Task<GamchessApi.Result> Resign( string clientGameId ) =>
		Act( clientGameId, "resign", null );

	/// <summary>Offer a draw, or accept one that's been offered — lichess treats
	/// both as the same call.</summary>
	public static Task<GamchessApi.Result> OfferDraw( string clientGameId ) =>
		Act( clientGameId, "draw", null );

	public static Task<GamchessApi.Result> DeclineDraw( string clientGameId ) =>
		Act( clientGameId, "draw-decline", null );

	/// <summary>Abort — only legal before both sides have moved; lichess refuses
	/// otherwise and says so.</summary>
	public static Task<GamchessApi.Result> Abort( string clientGameId ) =>
		Act( clientGameId, "abort", null );

	static Task<GamchessApi.Result> Act( string clientGameId, string action, object body ) =>
		GamchessApi.SendAuthed(
			$"/api/v1/lichess/play/{Uri.EscapeDataString( clientGameId )}/{action}", "POST",
			body == null ? null : GamchessApi.Json( body ) );
}
