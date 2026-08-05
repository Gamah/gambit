using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Gambit.Game;

namespace Gambit.Api.Lichess;

/// <summary>
/// The Board API: the half of lichess that actually plays a game (HTTPFIX).
///
/// <para>The C# port of the server's <c>internal/lichess/board.go</c>, which is
/// deleted. Every call here goes through <see cref="LichessClient"/>, which is
/// what guarantees the User-Agent and the 429 etiquette — nothing may build a
/// request to lichess any other way.</para>
///
/// <para><b>The traps below are lichess's, not ours, and every one of them has
/// already cost this project something.</b> They are carried over verbatim in
/// spirit from the Go, because they were re-derived from lila and the OpenAPI
/// spec rather than recalled, and the recalled versions were wrong.</para>
/// </summary>
public static class LichessBoard
{
	static string Tok => LichessTokenStore.Token;

	// ── Playing a game ──

	/// <summary>Play a UCI move. <paramref name="offeringDraw"/> rides along rather
	/// than needing a second call — lichess's own shape for "move and offer a
	/// draw".</summary>
	public static Task<LichessClient.Result> Move( string gameId, string uci, bool offeringDraw = false )
	{
		string p = $"/api/board/game/{Esc( gameId )}/move/{Esc( uci )}";
		if ( offeringDraw ) p += "?offeringDraw=true";
		return LichessClient.Send( p, "POST", Tok );
	}

	public static Task<LichessClient.Result> Resign( string gameId ) =>
		LichessClient.Send( $"/api/board/game/{Esc( gameId )}/resign", "POST", Tok );

	/// <summary>Offer a draw, accept one already offered, or decline.
	///
	/// <para><b>A 200 HERE MEANS NOTHING.</b> lila's <c>setDraw</c> returns Unit and
	/// the controller wraps it in <c>fuccess</c>, so every call answers
	/// <c>200 {"ok":true}</c> — including one it dropped on the floor. lichess
	/// silently refuses an offer before ply 2, a second offer within 20 ply of your
	/// last, and one against an AI. The documented 400 never fires. <b>The only
	/// truth is the standing offer on the NEXT gameState.</b> Nothing upstream may
	/// report that an offer landed.</para>
	///
	/// <para>Offering and accepting are the same call; the path segment is parsed by
	/// lila's <c>Form.trueish</c> (<c>1|true|True|on|yes</c>), so a decline is "any
	/// non-truthy word" rather than a <c>no</c> keyword.</para></summary>
	public static Task<LichessClient.Result> Draw( string gameId, bool accept ) =>
		LichessClient.Send( $"/api/board/game/{Esc( gameId )}/draw/{( accept ? "yes" : "no" )}", "POST", Tok );

	/// <summary>Propose a takeback, accept one already proposed, or decline. One
	/// endpoint for all three, exactly as with a draw.
	///
	/// <para>Same "a 200 means nothing" rule — lichess refuses a takeback before
	/// both players have moved by IGNORING the call. The difference from a draw is
	/// on the DECLINE: <c>Takebacker.no</c> publishes, so a declined takeback DOES
	/// clear on the next gameState, where a declined draw pushes nothing at all.
	/// That asymmetry is why the HUD may honestly say "waiting for your opponent"
	/// for a takeback and must not for a draw.</para></summary>
	public static Task<LichessClient.Result> Takeback( string gameId, bool accept ) =>
		LichessClient.Send( $"/api/board/game/{Esc( gameId )}/takeback/{( accept ? "yes" : "no" )}", "POST", Tok );

	/// <summary>Abort a game that hasn't left the opening. Only legal before both
	/// sides have moved; lichess 400s otherwise and says so.</summary>
	public static Task<LichessClient.Result> Abort( string gameId ) =>
		LichessClient.Send( $"/api/board/game/{Esc( gameId )}/abort", "POST", Tok );

	/// <summary>The game stream. The first line is always <c>gameFull</c>;
	/// subsequent <c>gameState</c> lines each carry the complete move list, so a
	/// reconnect loses nothing.</summary>
	public static LichessStream StreamGame( string gameId ) =>
		new( $"/api/board/game/stream/{Esc( gameId )}", () => LichessTokenStore.Token );

	// ── Challenges ──

	/// <summary>Accept an incoming challenge by id. <c>board:play</c> covers this —
	/// the spec lists challenge:write/bot:play/board:play as ALTERNATIVES, any one
	/// of which is enough.</summary>
	public static Task<LichessClient.Result> AcceptChallenge( string challengeId, string color = null )
	{
		string p = $"/api/challenge/{Esc( challengeId )}/accept";
		// `color` is documented as "only valid if this is an open challenge".
		if ( color is "white" or "black" ) p += "?color=" + color;
		return LichessClient.Send( p, "POST", Tok );
	}

	public static Task<LichessClient.Result> DeclineChallenge( string challengeId, string reason = null ) =>
		LichessClient.Send( $"/api/challenge/{Esc( challengeId )}/decline", "POST", Tok,
			string.IsNullOrEmpty( reason ) ? null : new Dictionary<string, string> { ["reason"] = reason } );

	/// <summary>Withdraw a challenge we issued.
	///
	/// <para><b>Not optional politeness.</b> Closing a keep-alive stream does NOT
	/// withdraw a challenge — lila only stops a 15s ping, and the challenge then
	/// goes Offline and stays acceptable for HOURS (a later ping revives it
	/// outright). Read from lila, not the OpenAPI doc, which says the opposite.
	/// Without an explicit cancel, a stranger could accept hours after the player
	/// stood up and walked away, starting a real game on their account at a board
	/// nobody is sitting at.</para></summary>
	public static Task<LichessClient.Result> CancelChallenge( string challengeId ) =>
		LichessClient.Send( $"/api/challenge/{Esc( challengeId )}/cancel", "POST", Tok );

	/// <summary>
	/// Challenge a NAMED lichess user directly. Gambit's primary flow.
	///
	/// <para>It reaches BLITZ where a seek cannot (lila gates challenges at
	/// <c>speed &gt;= Blitz</c> and seeks at <c>&gt;= Rapid</c> — two functions,
	/// same name, different files, different answers), and Gambit's default table
	/// is Blitz 3+0. It also spends the per-user challenge budget rather than the
	/// 5/min lobby one.</para>
	///
	/// <para><b>We do NOT use <c>keepAliveStream</c>.</b> Its only benefit is
	/// lichess's 15s ping, its trap has already bitten once (see
	/// <see cref="CancelChallenge"/>), and an explicit cancel on a plain buffered
	/// challenge is strictly simpler. Not porting it removes an entire third stream
	/// shape from the client. The cost is real and bounded: a real-time challenge
	/// expires ~20s after it was last seen, which is fine for the paired flow
	/// (the opposite seat accepts in well under a second) and is why the
	/// challenge-a-stranger-by-name flow tells the player it is short-lived.</para>
	/// </summary>
	public static async Task<(LichessChallenge challenge, LichessClient.Result res)>
		ChallengeUser( string username, TimeControl tc, bool rated, string color )
	{
		if ( !ValidUsername( username ) )
			return (null, new LichessClient.Result { Error = $"\"{username}\" isn't a lichess username." });

		var form = ClockForm( tc, rated, color );
		if ( form == null )
			return (null, new LichessClient.Result { Error = LichessTable.WhyNot( tc ) ?? "That clock isn't one lichess takes." });

		var res = await LichessClient.Send( $"/api/challenge/{Esc( username )}", "POST", Tok, form );
		if ( !res.Ok ) return (null, res);

		var ch = LichessClient.Parse<LichessChallenge>( res.Body );
		if ( string.IsNullOrEmpty( ch?.id ) )
			return (null, new LichessClient.Result { Error = "Lichess didn't return a challenge id." });
		return (ch, res);
	}

	/// <summary>
	/// Mint an OPEN challenge — the shareable link an anonymous browser opponent
	/// joins.
	///
	/// <para><b>Created ANONYMOUSLY, and this is the one call that must present no
	/// token.</b> <c>POST /api/challenge/open</c> is <c>security: []</c>, and a
	/// <c>board:play</c> token 403s it ("Missing scope: challenge:write") — unlike
	/// <see cref="ChallengeUser"/> and <see cref="AcceptChallenge"/>, which
	/// <c>board:play</c> does satisfy. That asymmetry is what made the 403 look
	/// contradictory the first time.</para>
	///
	/// <para><b>This is only HALF the flow.</b> On its own an open challenge is
	/// anon-vs-anon and the creator is not a participant. The seated player then
	/// ACCEPTS it with their own token (<see cref="AcceptChallenge"/> with a
	/// colour), which seats their real account; the OPPOSITE colour's url is what
	/// gets handed out. <b>Skipping the accept is the bug that shipped in M8</b> —
	/// the creator's seat stayed empty and the game never started.</para>
	/// </summary>
	public static async Task<(LichessOpenChallenge open, LichessClient.Result res)>
		OpenChallenge( TimeControl tc, bool rated )
	{
		// Only the clock DOMAIN matters here — an open challenge is web-joinable,
		// so there is no board-compat speed floor on the creation itself. Our side
		// relays through the Board API, though, so the caller still gates on
		// LichessTable.CanMirror.
		var form = new Dictionary<string, string> { ["rated"] = rated ? "true" : "false" };
		if ( !tc.IsUnlimited )
		{
			if ( !ValidClockLimit( tc.InitialSeconds ) )
				return (null, new LichessClient.Result { Error = $"Lichess won't take a {tc.InitialSeconds}s clock." });
			form["clock.limit"] = tc.InitialSeconds.ToString( CultureInfo.InvariantCulture );
			form["clock.increment"] = tc.IncrementSeconds.ToString( CultureInfo.InvariantCulture );
		}

		// null token — see the remarks. NOT an oversight, and the one call site
		// where an Authorization header is a bug.
		var res = await LichessClient.Send( "/api/challenge/open", "POST", null, form );
		if ( !res.Ok ) return (null, res);

		var open = LichessClient.Parse<LichessOpenChallenge>( res.Body );
		if ( string.IsNullOrEmpty( open?.id ) || string.IsNullOrEmpty( open.url ) )
			return (null, new LichessClient.Result { Error = "Lichess didn't return a link." });
		return (open, res);
	}

	// ── Seeks ──

	/// <summary>
	/// A real-time lobby seek. <b>The returned stream IS the seek</b> — lichess
	/// cancels it the moment we hang up, deliberately, so that a client which dies
	/// doesn't get paired into a game nobody will play.
	///
	/// <para>The stream carries NO information, not even the game id: it is a
	/// sequence of empty lines whose only job is to stay open. lichess's own
	/// instruction is to have an event stream open FIRST and learn about the game
	/// from <c>gameStart</c> there — which is why the seek flow needs
	/// <see cref="LichessEventStream"/> and the paired flow does not.</para>
	///
	/// <para><b>NOTE THE UNITS.</b> A seek's <c>time</c> is MINUTES;
	/// a challenge's <c>clock.limit</c> is SECONDS. The asymmetry is lichess's,
	/// and it is an easy way to ask for a ten-second game while meaning ten
	/// minutes.</para>
	///
	/// <para><b>Send NO ratingRange.</b> Omitting it does not mean "pair me with
	/// anyone" — for a real-time hook lila discards a default range and centres a
	/// Gaussian band on the seeker's REAL rating. It knows their rating and we do
	/// not, so empty is the STRONGEST value available and anything we computed
	/// would be worse-informed. (Asking for a genuinely open pool would mean
	/// sending <c>400-2899</c> to dodge lila's own ±500 "no preference" check.
	/// Don't: it games an implementation detail to get worse pairings.)</para>
	///
	/// <para>Returns null and fills <paramref name="error"/> when the seek can't be
	/// started at all — including when our own 5/min self-limit refuses it, which
	/// is a refusal the player must see rather than a failure to retry.</para>
	/// </summary>
	public static LichessStream Seek( TimeControl tc, bool rated, string color, out string error )
	{
		if ( !LichessTable.CanSeek( tc ) )
		{
			error = LichessTable.WhySeekNot( tc );
			return null;
		}
		if ( !LichessEtiquette.TakeSeekSlot( out error ) ) return null;

		var form = new Dictionary<string, string>
		{
			// MINUTES. See the remarks.
			["time"] = LichessTable.SeekTimeMinutes( tc ).ToString( "0.###", CultureInfo.InvariantCulture ),
			["increment"] = tc.IncrementSeconds.ToString( CultureInfo.InvariantCulture ),
			["rated"] = rated ? "true" : "false",
		};
		if ( color is "white" or "black" ) form["color"] = color;

		error = null;
		return new LichessStream( "/api/board/seek", () => LichessTokenStore.Token, "POST", form );
	}

	// ── Shared validation ──

	/// <summary>Build a challenge's clock form, or null when the control can't be
	/// challenged with.
	///
	/// <para><b>Omitting BOTH clock fields is how you ask for an unlimited game.</b>
	/// Sending <c>clock.limit=0&amp;clock.increment=0</c> asks for a 0+0 clock,
	/// which is a real thing to ask for and a rejected one.</para></summary>
	static Dictionary<string, string> ClockForm( TimeControl tc, bool rated, string color )
	{
		if ( !LichessTable.CanMirror( tc ) ) return null;

		var form = new Dictionary<string, string> { ["rated"] = rated ? "true" : "false" };
		if ( !tc.IsUnlimited )
		{
			if ( !ValidClockLimit( tc.InitialSeconds ) ) return null;
			if ( tc.IncrementSeconds is < 0 or > 60 ) return null;
			form["clock.limit"] = tc.InitialSeconds.ToString( CultureInfo.InvariantCulture );
			form["clock.increment"] = tc.IncrementSeconds.ToString( CultureInfo.InvariantCulture );
		}
		if ( color is "white" or "black" ) form["color"] = color;
		return form;
	}

	/// <summary>lichess's documented <c>clock.limit</c> domain: 0, 15, 30, 45, 60,
	/// 90, or any multiple of 60 up to 10800 (3 hours). Not a smooth range — a
	/// 100-second clock is a 400.</summary>
	public static bool ValidClockLimit( int seconds ) => seconds switch
	{
		0 or 15 or 30 or 45 or 60 or 90 => true,
		_ => seconds > 0 && seconds <= 10800 && seconds % 60 == 0,
	};

	/// <summary>Could this be a lichess username? lila's own rule: 2-30 chars of
	/// letters, digits, underscore and hyphen, starting with a letter or digit.
	///
	/// <para>A GATE, not decoration: the value is typed by a player and becomes a
	/// URL path segment. Escaping already stops it forging a path, so this is about
	/// not spending a challenge on something that cannot work.</para></summary>
	public static bool ValidUsername( string s )
	{
		if ( s is not { Length: >= 2 and <= 30 } ) return false;
		for ( int i = 0; i < s.Length; i++ )
		{
			char c = s[i];
			if ( char.IsAsciiLetterOrDigit( c ) ) continue;
			if ( ( c == '_' || c == '-' ) && i > 0 ) continue;
			return false;
		}
		return true;
	}

	static string Esc( string s ) => Uri.EscapeDataString( s ?? "" );
}
