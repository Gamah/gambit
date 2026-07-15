using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// Console commands for gamchess (issue #7 §7.6). These exist so the client half
/// is verifiable standalone — <c>gambit_gamchess_ping</c> proves reachability and
/// the D8 allowlist entry before any UI depends on them.
/// </summary>
public static class GamchessCommands
{
	/// <summary>Is gamchess up, and is the allowlist right? Bypasses the circuit
	/// breaker, so it always really tries.</summary>
	[ConCmd( "gambit_gamchess_ping" )]
	public static void Ping() => _ = DoPing();

	static async Task DoPing()
	{
		GamchessApi.ResetBreaker();
		Log.Info( $"[Gambit] pinging {GamchessApi.Base}/health …" );

		var res = await GamchessApi.Health();
		if ( res.Ok )
		{
			Log.Info( $"[Gambit]   ✓ {LichessApi.Truncate( res.Body, 200 )}" );
			return;
		}
		Log.Warning( $"[Gambit]   ✗ {res.Error}" );
		// Reading the failure (verified in-editor 2026-07-15):
		//   "SSL connection could not be established" → the request LEFT the sandbox
		//      and reached a TLS handshake, so the allowlist is fine. Caddy has no
		//      cert for this host — the vhost isn't configured or isn't up yet.
		//   a refusal before any connection → the HttpAllowList in gambit.sbproj is
		//      missing "https://chess.gamah.net/" (D8).
		//   any HTTP status at all → we reached gamchess; read the status.
		Log.Info( "[Gambit]   TLS/SSL error = allowlist OK, but no cert for that host (Caddy vhost down). " +
			$"Blocked before connecting = HttpAllowList is missing \"{GamchessApi.Base}/\" in gambit.sbproj (D8)." );
	}

	/// <summary>Prove the Facepunch → gamchess auth round-trip end to end: mints a
	/// token and registers a throwaway OAuth state under our verified SteamID. A 401
	/// here means Facepunch rejected us or echoed a different SteamId.</summary>
	[ConCmd( "gambit_gamchess_signin" )]
	public static void SignIn() => _ = DoSignIn();

	static async Task DoSignIn()
	{
		if ( !GamchessAuth.Available )
		{
			Log.Warning( "[Gambit] no Steam identity — gamchess features are off on this build." );
			return;
		}

		var (steamId, token) = await GamchessAuth.Credentials( forceRefresh: true );
		if ( string.IsNullOrEmpty( token ) )
		{
			Log.Warning( "[Gambit] couldn't mint a Facepunch auth token." );
			return;
		}
		// Redact: an FP token is a credential, short-lived or not.
		Log.Info( $"[Gambit] steam {steamId}, fp token {LichessApi.Redact( token )}" );

		// A throwaway state — this only proves the auth path, it starts no real
		// sign-in. 32+ chars of [A-Za-z0-9_-] or the server rejects it (the entropy
		// floor is a security control, not validation politeness).
		var state = LichessOAuth.RandomState();
		var res = await GamchessApi.LichessBegin( state );
		if ( res.Ok )
		{
			Log.Info( $"[Gambit]   ✓ auth accepted — {LichessApi.Truncate( res.Body, 200 )}" );
			return;
		}
		if ( res.Unauthorized )
		{
			Log.Warning( "[Gambit]   ✗ 401 — Facepunch rejected the token, or echoed a different SteamId." );
			return;
		}
		Log.Warning( $"[Gambit]   ✗ {res.Error} {LichessApi.Truncate( res.Body, 200 )}" );
	}

	/// <summary>List YOUR archived games. The archive is private — there's no way to
	/// ask for anyone else's, so this takes no SteamID.</summary>
	[ConCmd( "gambit_gamchess_games" )]
	public static void Games() => _ = DoGames();

	static async Task DoGames()
	{
		if ( !GamchessAuth.Available )
		{
			Log.Warning( "[Gambit] no Steam identity — the archive needs one." );
			return;
		}

		var res = await GamchessApi.ListGames();
		if ( !res.Ok )
		{
			Log.Warning( $"[Gambit] archive lookup failed: {res.Error}" );
			return;
		}
		Log.Info( $"[Gambit] your archive: {LichessApi.Truncate( res.Body, 1000 )}" );
	}

	/// <summary>Unlink this Steam account from its lichess account on gamchess. The
	/// local token is untouched — use <c>gambit_signout</c> for that.</summary>
	[ConCmd( "gambit_gamchess_unlink" )]
	public static void Unlink() => _ = DoUnlink();

	static async Task DoUnlink()
	{
		var res = await GamchessApi.DeleteLichessLink();
		if ( res.Ok ) Log.Info( $"[Gambit] unlinked: {LichessApi.Truncate( res.Body, 200 )}" );
		else Log.Warning( $"[Gambit] unlink failed: {res.Error}" );
	}
}
