using System.Threading.Tasks;

namespace Gambit.Api;

/// <summary>
/// gamchess's lichess routes (HTTPFIX).
///
/// <para><b>Read the name carefully: this is not a lichess client.</b> Every call
/// here goes to gamchess. The lichess client is <see cref="Gambit.Api.Lichess"/>,
/// and the split is the custody decision made visible — gamchess does the three
/// things a client genuinely cannot, and nothing else.</para>
///
/// <list type="number">
/// <item><b>Holds the redirect URI.</b> lichess compares <c>redirect_uri</c>
/// byte-for-byte between authorize and token, and this client cannot listen on a
/// socket, so the browser has to come back to a server.</item>
/// <item><b>Shows the disclosure.</b> Consent belongs somewhere with a URL bar,
/// and what is being granted should be readable before lichess's own screen asks
/// for approval.</item>
/// <item><b>Is the directory.</b> Two seats need each other's lichess usernames
/// to challenge by name, and neither client may simply be told the other's.</item>
/// </list>
///
/// <para>What gamchess no longer does: hold a token, play a game, relay a move,
/// or revoke anything. <c>/api/v1/lichess/play</c>, <c>/seek</c>,
/// <c>/challenge</c>, <c>/open</c> and the play long poll are gone from both
/// halves.</para>
///
/// <para>Thin on purpose: a URL table over <see cref="GamchessApi"/>, so it
/// inherits the timeout, the circuit breaker and the token dance rather than
/// reimplementing them. <b>gamchess being down must never stop a lichess game in
/// progress</b> — the game stream is between this client and lichess, and
/// nothing here is on its path.</para>
/// </summary>
public static class LichessApi
{
	/// <summary>Where a player really turns Gambit off, if they don't trust us to.
	/// Worth naming in the UI: lichess's <c>/account/oauth/token</c> page does NOT
	/// list this grant (it shows personal tokens only), so someone looking there
	/// sees an empty list and concludes nothing is linked.</summary>
	public const string SecurityUrl = "https://lichess.org/account/security";

	/// <summary>The URL the in-game board copies to the clipboard. A constant with
	/// no secret in it, and safe precisely because of that — it is Steam-session
	/// gated, so whoever opens it links <i>their own</i> accounts.
	///
	/// <para><see cref="Lichess.LichessLink.LinkUrl"/> is the one to SHOW: the
	/// server returns its own copy when a flow starts, which is what keeps the test
	/// instance pointing at itself.</para></summary>
	public const string LinkUrl = GamchessApi.Base + "/lichess/link";

	// ── Linking ──

	/// <summary>Step ①: register a PKCE challenge before showing the link. gamchess
	/// never sees the verifier behind it.</summary>
	public static Task<GamchessApi.Result> LinkStart( string codeChallenge ) =>
		GamchessApi.SendAuthed( "/api/v1/lichess/link/start", "POST", GamchessApi.Json( new
		{
			code_challenge = codeChallenge,
		} ) );

	/// <summary>Step ④: has the browser come back yet? Answered from our
	/// authenticated SteamID and nothing else — there is no state to pass, which is
	/// what makes a state seen in a URL bar useless to anyone else.</summary>
	public static Task<GamchessApi.Result> LinkCollect() =>
		GamchessApi.SendAuthed( "/api/v1/lichess/link/collect", "POST", null );

	/// <summary>Step ⑤: record who we are on lichess.
	///
	/// <para><b>This is the one call in the codebase that sends the lichess token
	/// anywhere but lichess</b>, and it happens exactly once per link. gamchess
	/// calls <c>GET /api/account</c> with it and discards it — it does not store it,
	/// log it, or put it in an error string.</para>
	///
	/// <para>We could assert our own username (we can read <c>/api/account</c>
	/// ourselves) and deliberately do not: an asserted identity is a claim, and a
	/// claim would let anyone squat a real account's row and lock its owner out of
	/// ever linking. Same rule as gamchess trusting only the SteamId Facepunch
	/// echoes back.</para></summary>
	public static Task<GamchessApi.Result> Claim( string token ) =>
		GamchessApi.SendAuthed( "/api/v1/lichess/claim", "POST", GamchessApi.Json( new
		{
			token,
		} ) );

	/// <summary>Am I linked, as far as gamchess is concerned? Answers only about the
	/// caller — there is no way to ask about anyone else.
	///
	/// <para>Note what this is NOT for: "am I linked" is answerable locally now, from
	/// <see cref="Lichess.LichessTokenStore"/>, instantly and offline. This says
	/// whether gamchess AGREES, which is what the two-seat directory needs.</para></summary>
	public static Task<GamchessApi.Result> Status() =>
		GamchessApi.SendAuthed( "/api/v1/lichess", "GET", null );

	/// <summary>Make gamchess forget the link. <b>Half of unlinking</b> — the revoke
	/// is ours to do, because it must be signed by the token, which only we hold.
	/// <see cref="Lichess.LichessLink.Unlink"/> does both in the right order.</summary>
	public static Task<GamchessApi.Result> Unlink() =>
		GamchessApi.SendAuthed( "/api/v1/lichess", "DELETE", null );

	// ── The directory ──

	/// <summary>
	/// "I'm playing this table's game on lichess; who is opposite me?"
	///
	/// <para><b>BOTH seats must call this</b>, and the reason has CHANGED even
	/// though the rule has not. It used to be the consent story: gamchess held both
	/// players' tokens, so a one-sided start would have let any linked player drag
	/// any other into a real game from anywhere. That is gone — each client acts with
	/// its own token and can only ever commit itself, and a one-sided start now just
	/// leaves a challenge sitting in someone's notifications.</para>
	///
	/// <para>What it still does is DIRECTORY DISCLOSURE: gamchess must not hand out a
	/// player's lichess username to whoever asks, so it reveals the opposite seat's
	/// only once both seats have posted for the same <paramref name="clientGameId"/>,
	/// and only to those two.</para>
	///
	/// <para><paramref name="clientGameId"/> is the table's synced id — the rendezvous
	/// key both seats agree on. It is not a secret and carries no authority; the two
	/// authenticated calls do.</para>
	/// </summary>
	public static Task<GamchessApi.Result> Rendezvous( string clientGameId, ulong whiteSteamId,
		ulong blackSteamId ) =>
		GamchessApi.SendAuthed( "/api/v1/lichess/rendezvous", "POST", GamchessApi.Json( new
		{
			client_game_id = clientGameId,
			white_steam_id = whiteSteamId.ToString(),
			black_steam_id = blackSteamId.ToString(),
		} ) );
}

/// <summary>
/// Lichess TV, as far as the client is concerned (M9).
///
/// <para>Like <see cref="LichessApi"/>, every call goes to gamchess and none to
/// lichess — but for a different reason. The Board API needs custody of a token;
/// TV needs none at all (<c>/api/tv/{channel}/feed</c> is anonymous upstream). What
/// routes TV through gamchess is the fan-out: gamchess holds ONE stream per channel
/// and serves every watcher from it, so a hundred players on blitz cost lichess one
/// stream. That invariant is the deal, and clients hitting lichess directly would
/// break it.</para>
///
/// <para>gamchess's TV routes are session-gated like everything else. That is not
/// about cost — a session costs one local HMAC — it is so we don't become a free
/// unauthed relay for lichess's content, on the one IP whose limits every real
/// player shares and whose User-Agent names us.</para>
///
/// <para><b>Never required.</b> TV going down means the wall mirrors real tables,
/// exactly as it did before M9.</para>
/// </summary>
public static class LichessTvApi
{
	/// <summary>The base WebSocket URL for a channel's push stream (M18):
	/// <c>wss://…/api/v1/tv/{channel}</c>. The channel is escaped even though
	/// <see cref="Gambit.Game.LichessTv"/> only ever hands us a key off its own list —
	/// gamchess re-checks against its allowlist and 404s anything else at the
	/// handshake, and belt-and-braces on a value that becomes a URL costs nothing.
	///
	/// <para>The connection itself is owned by <see cref="Gambit.Game.LichessTvSource"/>,
	/// which holds the socket, applies each pushed snapshot, and reconnects on a drop —
	/// there is no request/response call to wrap here as the old long poll had.</para></summary>
	public static string ChannelSocketUrl( string channel ) =>
		$"{GamchessApi.WsBase}/api/v1/tv/{Uri.EscapeDataString( channel )}";

	/// <summary>What channels gamchess will actually serve. The client keeps its own
	/// list (<see cref="Gambit.Game.LichessTv"/>) so the settings board works offline;
	/// this exists to check the two agree.</summary>
	public static Task<GamchessApi.Result> Channels() =>
		GamchessApi.SendAuthed( "/api/v1/tv/channels", "GET", null );
}
