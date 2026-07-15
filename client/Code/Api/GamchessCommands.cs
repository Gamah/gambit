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
			Log.Info( $"[Gambit]   ✓ {GamchessApi.Truncate( res.Body, 200 )}" );
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
	/// token and asks gamchess who we are. A 401 means Facepunch rejected us or
	/// echoed a different SteamId.</summary>
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
		Log.Info( $"[Gambit] steam {steamId}, fp token {GamchessApi.Redact( token )}" );

		// Any authed endpoint proves the round-trip; the archive is the cheapest.
		var res = await GamchessApi.ListGames( 1 );
		if ( res.Ok )
		{
			Log.Info( $"[Gambit]   ✓ auth accepted — {GamchessApi.Truncate( res.Body, 200 )}" );
			return;
		}
		if ( res.Unauthorized )
		{
			Log.Warning( "[Gambit]   ✗ 401 — Facepunch rejected the token, or echoed a different SteamId." );
			return;
		}
		Log.Warning( $"[Gambit]   ✗ {res.Error} {GamchessApi.Truncate( res.Body, 200 )}" );
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
		Log.Info( $"[Gambit] your archive: {GamchessApi.Truncate( res.Body, 1000 )}" );
	}

	/// <summary>Am I linked to lichess? Prints the link, and the URL to fix it if
	/// not. The archive rule applies here too: this can only ever answer about YOU
	/// — there is no SteamID to pass.</summary>
	[ConCmd( "gambit_lichess" )]
	public static void Lichess() => _ = DoLichess();

	static async Task DoLichess()
	{
		if ( !GamchessAuth.Available )
		{
			Log.Warning( "[Gambit] no Steam identity — lichess linking needs one." );
			return;
		}

		var res = await LichessApi.Status();
		if ( !res.Ok )
		{
			Log.Warning( $"[Gambit] lichess status failed: {res.Error}" );
			return;
		}

		var link = GamchessApi.Deserialize<LichessLink>( res.Body );
		if ( link == null )
		{
			Log.Warning( $"[Gambit] unreadable reply: {GamchessApi.Truncate( res.Body, 200 )}" );
			return;
		}

		if ( link.linked )
		{
			Log.Info( $"[Gambit] lichess: linked as {link.username} ({link.lichess_id})" );
			Log.Info( $"[Gambit]   unlink in-game at the east wall board, or revoke at {LichessApi.SecurityUrl}" );
			Log.Info( "[Gambit]   NOTE: changing your lichess password does NOT unlink — only a revoke does." );
			return;
		}

		Log.Info( "[Gambit] lichess: not linked." );
		Log.Info( $"[Gambit]   link at: {LichessApi.LinkUrl}" );
	}

	/// <summary>Unlink from lichess: gamchess revokes the token, then forgets it.
	/// Best-effort revoke — the row goes either way, so if lichess was down when
	/// you did this, revoke it yourself at lichess.org/account/security.</summary>
	[ConCmd( "gambit_lichess_unlink" )]
	public static void LichessUnlink() => _ = DoLichessUnlink();

	static async Task DoLichessUnlink()
	{
		if ( !GamchessAuth.Available )
		{
			Log.Warning( "[Gambit] no Steam identity — nothing to unlink." );
			return;
		}

		await LichessLinkState.Unlink();
		Log.Info( LichessLinkState.Linked
			? "[Gambit] unlink failed — still linked. Is gamchess up?"
			: "[Gambit] lichess: unlinked." );
	}

	/// <summary>What is the TV wall actually doing? Prints the whole chain in one line
	/// each, because "nothing is showing" has now twice been diagnosed by guesswork and
	/// once wrongly — the useful question is which link is dead, and none of it is
	/// visible from outside.</summary>
	[ConCmd( "gambit_tv" )]
	public static void TvStatus() => _ = DoTvStatus();

	static async Task DoTvStatus()
	{
		Log.Info( $"[Gambit] TV enabled (this client): {Gambit.Game.SpectatorController.TvEnabled}" );
		Log.Info( $"[Gambit] channel: {Gambit.Game.SpectatorController.DesiredChannel}"
			+ $" (lobby suggests {Gambit.Game.SpectatorController.SuggestedChannel},"
			+ $" following: {Gambit.Game.SpectatorController.FollowingLobbyTv})" );

		var c = Gambit.Game.SpectatorController.Instance;
		if ( c == null )
		{
			Log.Warning( "[Gambit] no SpectatorController — the wall didn't build." );
			return;
		}
		Log.Info( $"[Gambit] wall: {c.ChannelLabel} · tv-source={c.IsTvSource}"
			+ $" · position={c.HasPosition} · fanfare={c.FanfareText ?? "(none)"}" );

		// The raw state, which is the thing that actually decides whether a fanfare can
		// ever happen: no last_status here means gamchess can't tell us how games end.
		var res = await LichessTvApi.PollChannel( Gambit.Game.SpectatorController.DesiredChannel, 0 );
		if ( !res.Ok )
		{
			Log.Warning( $"[Gambit] gamchess TV poll failed: {res.Error ?? res.Status.ToString()}"
				+ ( res.NotFound ? " — this gamchess has no TV routes (deploy the server half)." : "" ) );
			return;
		}
		var st = GamchessApi.Deserialize<TvState>( res.Body );
		if ( st == null )
		{
			Log.Warning( "[Gambit] gamchess TV returned something unreadable." );
			return;
		}
		Log.Info( $"[Gambit] gamchess: v{st.version} game={st.game_id} {st.white_name} vs {st.black_name}" );
		Log.Info( st.last_game_id == null
			? "[Gambit] gamchess reports NO last-game result — either no game has ended yet, "
				+ "or this gamchess predates the fanfare (deploy the server half)."
			: $"[Gambit] gamchess last game: {st.last_game_id} status={st.last_status} winner={st.last_winner}" );
	}
}
