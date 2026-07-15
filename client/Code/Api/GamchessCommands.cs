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
		Log.Info( "[Gambit]   a 0 status usually means the HttpAllowList is missing " +
			$"\"{GamchessApi.Base}/\" in gambit.sbproj (D8); anything else means the host is down." );
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

	/// <summary>List a player's archived games (defaults to you). Public endpoint —
	/// no auth needed, which is why it takes a SteamID.</summary>
	[ConCmd( "gambit_gamchess_games" )]
	public static void Games( string steamId = null ) => _ = DoGames( steamId );

	static async Task DoGames( string steamId )
	{
		ulong id = GamchessAuth.LocalSteamId;
		if ( !string.IsNullOrWhiteSpace( steamId ) && !ulong.TryParse( steamId.Trim(), out id ) )
		{
			Log.Warning( "[Gambit] that isn't a SteamID64." );
			return;
		}
		if ( id == 0 )
		{
			Log.Warning( "[Gambit] no SteamID to look up." );
			return;
		}

		var res = await GamchessApi.ListGames( id );
		if ( !res.Ok )
		{
			Log.Warning( $"[Gambit] archive lookup failed: {res.Error}" );
			return;
		}
		Log.Info( $"[Gambit] archive for {id}: {LichessApi.Truncate( res.Body, 1000 )}" );
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
