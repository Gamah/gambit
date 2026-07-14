using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// Developer console commands for M3 — the import self-test plus sign-in helpers
/// the user runs in the s&amp;box editor. (The riskier D4 streaming spike lives
/// alone in <see cref="LichessTvSpike"/> so a whitelist rejection there can't take
/// these down.) Everything here uses only APIs already proven in the repo.
///
///   gambit_lichess_import_test — self-test the POST /api/import mechanism
///   gambit_signin [token]      — paste a token (or open the splash with no arg)
///   gambit_signout             — forget + revoke the stored token
///   gambit_whoami              — print the current lichess identity (redacted)
/// </summary>
public static class LichessSpikes
{
	// ── Import mechanism self-test (isolates HTTP from our PGN builder) ──

	/// <summary>POST a tiny hand-written, definitely-valid PGN to lichess and log
	/// the full response. If this works but a real game import doesn't, the fault
	/// is our PGN output, not the HTTP path (PLAN.md M2 carry-in).</summary>
	[ConCmd( "gambit_lichess_import_test" )]
	public static void ImportTest() => _ = RunImportTest();

	static async Task RunImportTest()
	{
		const string pgn =
			"[Event \"Gambit import self-test\"]\n" +
			"[Site \"Terry's Gambit\"]\n" +
			"[Result \"1-0\"]\n\n" +
			"1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

		Log.Info( "[Gambit] import self-test → POST https://lichess.org/api/import (known-good PGN)" );
		var res = await LichessApi.ImportPgn( pgn );
		Log.Info( $"[Gambit]   HTTP {res.Status}, ok={res.Ok}" );
		if ( !string.IsNullOrEmpty( res.Error ) ) Log.Warning( $"[Gambit]   error: {res.Error}" );
		Log.Info( $"[Gambit]   body: {LichessApi.Truncate( res.Body, 300 )}" );

		var url = LichessApi.Deserialize<LichessImport>( res.Body )?.url;
		if ( !string.IsNullOrEmpty( url ) )
			Log.Info( $"[Gambit]   ✓ imported OK: {url}  (HTTP path + allowlist are good)" );
		else
			Log.Error( "[Gambit]   ✗ no url — a 4xx means the POST body/allowlist is wrong; a 2xx with no url means the reply shape changed." );
	}

	// ── Sign-in helpers ──

	/// <summary>Paste a lip_ token to sign in, or run with no argument to open the
	/// splash screen. Prefer the splash — a token on the command line lands in the
	/// console history in the clear.</summary>
	[ConCmd( "gambit_signin" )]
	public static void SignIn( string token = null )
	{
		if ( string.IsNullOrWhiteSpace( token ) )
		{
			Gambit.UI.Screens.SplashScreen.Open();
			return;
		}
		_ = DoSignIn( token );
	}

	static async Task DoSignIn( string token )
	{
		var (ok, error) = await LichessAuth.SignInWithToken( token );
		if ( ok ) Log.Info( $"[Gambit] signed in as {LichessAuth.Username}" );
		else Log.Warning( $"[Gambit] sign-in failed: {error}" );
	}

	/// <summary>Begin the OAuth code-paste flow from the console: logs the authorize
	/// URL. Open it, authorize, then run <c>gambit_oauth_complete</c> with the URL
	/// your browser lands on (it won't load — the code is in the address bar).</summary>
	[ConCmd( "gambit_oauth" )]
	public static void OAuthStart()
	{
		var url = LichessOAuth.Start();
		Log.Info( "[Gambit] OAuth: open this URL, click Authorize, then run" );
		Log.Info( "[Gambit]   gambit_oauth_complete <the-address-bar-url-you-land-on>" );
		Log.Info( url );
	}

	[ConCmd( "gambit_oauth_complete" )]
	public static void OAuthComplete( string redirect ) => _ = DoOAuth( redirect );

	static async Task DoOAuth( string redirect )
	{
		var (ok, error) = await LichessOAuth.Complete( redirect );
		if ( ok ) Log.Info( $"[Gambit] OAuth signed in as {LichessAuth.Username}" );
		else Log.Warning( $"[Gambit] OAuth failed: {error}" );
	}

	[ConCmd( "gambit_signout" )]
	public static void SignOut() => LichessAuth.SignOut();

	[ConCmd( "gambit_whoami" )]
	public static void WhoAmI()
	{
		if ( LichessAuth.SignedIn )
			Log.Info( $"[Gambit] signed in to lichess as {LichessAuth.Username} (token {LichessApi.Redact( LichessAuth.Token )})" );
		else
			Log.Info( "[Gambit] not signed in to lichess" );
	}

	// ── M4: open-game link flow ──

	/// <summary>Self-test POST /api/challenge/open (unrated Rapid 10+0) and log the
	/// raw reply so the URL shape can be confirmed in-editor. If this returns
	/// urlWhite/urlBlack, the in-game "Create Rapid 10+0 game" button will too.</summary>
	[ConCmd( "gambit_open_challenge" )]
	public static void OpenChallenge() => _ = RunOpenChallenge();

	static async Task RunOpenChallenge()
	{
		Log.Info( "[Gambit] open-challenge test → POST https://lichess.org/api/challenge/open (unrated Rapid 10+0)" );
		var res = await LichessApi.CreateOpenChallenge( 600, 0, "Terry's Gambit test" );
		Log.Info( $"[Gambit]   HTTP {res.Status}, ok={res.Ok}" );
		if ( !string.IsNullOrEmpty( res.Error ) ) Log.Warning( $"[Gambit]   error: {res.Error}" );
		Log.Info( $"[Gambit]   body: {LichessApi.Truncate( res.Body, 400 )}" );

		var oc = LichessApi.Deserialize<LichessOpenChallenge>( res.Body );
		if ( !string.IsNullOrEmpty( oc?.urlWhite ) )
		{
			Log.Info( $"[Gambit]   ✓ speed={oc.speed}  white={oc.urlWhite}  black={oc.urlBlack}" );
			Log.Info( "[Gambit]   open either URL in a browser to play that side." );
		}
		else
		{
			Log.Error( "[Gambit]   ✗ no urlWhite — a 4xx means the body/allowlist is wrong; a 2xx with no urls means the reply shape changed (check body above)." );
		}
	}

	/// <summary>Join a lichess game by link from the console: reads the colour the
	/// URL pins (?color=white|black) and swoops the local player's camera to that
	/// side of the nearest board — the same routing the in-game paste field uses.</summary>
	[ConCmd( "gambit_join" )]
	public static void Join( string url )
	{
		var seat = Gambit.Game.LichessGameController.SeatFromUrl( url );
		if ( seat is not { } s )
		{
			Log.Warning( "[Gambit] couldn't read a side from that link — use a ?color=white or ?color=black URL." );
			return;
		}
		Log.Info( $"[Gambit] joining as {s} — moving camera to that seat." );
		Gambit.World.LobbyPlayer.Local?.JoinLichessSide( s );
	}

	// ── M4: in-sbox play (poll account/playing) ──

	/// <summary>Challenge a lichess user to a Rapid 10+0 game played on the board
	/// you're seated at. They accept on lichess.org; the game then streams onto the
	/// sbox board via polling. Sit down first.</summary>
	[ConCmd( "gambit_challenge" )]
	public static void Challenge( string username )
	{
		var pc = Gambit.Game.LichessPlayController.For( Gambit.World.ChessStation.Active );
		if ( pc == null ) { Log.Warning( "[Gambit] sit at a board first, then: gambit_challenge <lichess-username>" ); return; }
		pc.ChallengeUser( username );
	}

	/// <summary>Head-to-head (#3): challenge the signed-in lichess player sitting across
	/// this board — their client auto-accepts, so neither of you leaves sbox. Both must be
	/// signed in and seated on opposite sides; the guaranteed-working twin of the HUD's
	/// "Play … (head-to-head)" button.</summary>
	[ConCmd( "gambit_challenge_seated" )]
	public static void ChallengeSeated()
	{
		var pc = Gambit.Game.LichessPlayController.For( Gambit.World.ChessStation.Active );
		if ( pc == null ) { Log.Warning( "[Gambit] sit at a board first (across a signed-in player), then: gambit_challenge_seated" ); return; }
		pc.ChallengeSeatedOpponent();
	}

	/// <summary>Play Stockfish (level 1–8, default 3) on the board you're seated at —
	/// zero-setup way to test the play loop.</summary>
	[ConCmd( "gambit_challenge_ai" )]
	public static void ChallengeAi( int level = 3 )
	{
		var pc = Gambit.Game.LichessPlayController.For( Gambit.World.ChessStation.Active );
		if ( pc == null ) { Log.Warning( "[Gambit] sit at a board first, then: gambit_challenge_ai [level]" ); return; }
		pc.ChallengeAi( level );
	}

	/// <summary>Quick match: seek a random lichess opponent at Rapid 10+0 on the board
	/// you're seated at (the M4 gate item). Pass <c>casual</c> for an unrated seek;
	/// anything else (or nothing) seeks rated. Sit down first, then wait for the pairing.</summary>
	[ConCmd( "gambit_seek" )]
	public static void Seek( string mode = "rated" )
	{
		var pc = Gambit.Game.LichessPlayController.For( Gambit.World.ChessStation.Active );
		if ( pc == null ) { Log.Warning( "[Gambit] sit at a board first, then: gambit_seek [rated|casual]" ); return; }
		bool rated = !string.Equals( mode, "casual", System.StringComparison.OrdinalIgnoreCase );
		Log.Info( $"[Gambit] seeking a {( rated ? "rated" : "casual" )} Rapid 10+0 opponent…" );
		pc.QuickSeek( rated );
	}

	/// <summary>Force the in-sbox lichess controller on the board you're at back to
	/// "not playing" — resigns a live game, cancels a pending challenge/seek/open link,
	/// or clears the game-over screen. The same reset standing up performs; handy to
	/// un-stick a board during testing.</summary>
	[ConCmd( "gambit_play_reset" )]
	public static void PlayReset()
	{
		var pc = Gambit.Game.LichessPlayController.For( Gambit.World.ChessStation.Active );
		if ( pc == null ) { Log.Warning( "[Gambit] sit at a board first, then: gambit_play_reset" ); return; }
		pc.LeaveSeat();
		Log.Info( "[Gambit] lichess play state reset to idle." );
	}

	/// <summary>Create an open game vs an anonymous browser and sit in on it in sbox
	/// on the side you're seated at — we self-seat via the API (accept?color=), so you
	/// just share the opponent link. Sit down first; the HUD then shows the link.</summary>
	[ConCmd( "gambit_play_open" )]
	public static void PlayOpen()
	{
		var pc = Gambit.Game.LichessPlayController.For( Gambit.World.ChessStation.Active );
		if ( pc == null ) { Log.Warning( "[Gambit] sit at a board first, then: gambit_play_open" ); return; }
		pc.PlayOpenGame();
	}
}
