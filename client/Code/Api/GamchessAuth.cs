using System;
using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// Proves this player's Steam identity to gamchess (issue #7 §1).
///
/// <para><c>Sandbox.Services.Auth.GetToken</c> mints a short-lived Facepunch auth
/// token. We ship it to gamchess alongside our SteamID64; gamchess forwards both
/// to <c>public.facepunch.com/sbox/auth/token</c> and trusts <b>only the SteamId
/// Facepunch echoes back</b>. Our claimed SteamID is just an input to that
/// equality check — it authorises nothing on its own, which is what stops a valid
/// token for one account being used to act as another.</para>
///
/// <para>The service-name argument to GetToken is <b>cosmetic</b>: Facepunch
/// validates <c>{steamid, token}</c> without it. It's passed for clarity only.</para>
///
/// <para>This proves <i>Steam</i> identity, which is how gamchess keys an
/// archive to a player.</para>
///
/// <para><b>Never fatal.</b> GetToken returns null rather than throwing on a
/// non-Steam build, and every failure here degrades to "no gamchess", never to a
/// broken game.</para>
/// </summary>
public static class GamchessAuth
{
	/// <summary>Cosmetic — Facepunch ignores it (see the class remarks).</summary>
	const string ServiceName = "gamchess";

	/// <summary>How long we reuse a minted token before re-minting. FP token TTL is
	/// not documented (issue #7 §10.3 flags it as an open spike), so this is
	/// deliberately conservative: short enough that a stale token is rare, long
	/// enough that a sign-in poll isn't minting on every tick. A 401 re-mints
	/// immediately regardless, which is the real safety net.</summary>
	const float CacheSeconds = 120f;

	static string _token;
	static RealTimeUntil _tokenExpires;

	/// <summary>Is there a Steam identity to authenticate with at all? False on a
	/// non-Steam build, where every gamchess feature is simply off.</summary>
	public static bool Available => LocalSteamId != 0;

	/// <summary>This machine's SteamID64, or 0. A CLAIM as far as gamchess is
	/// concerned — never an identity until Facepunch echoes it back.
	/// <para>Read from <c>Connection.Local</c>, the same source the rest of the
	/// codebase uses (LobbyNetworkManager, ChessStation). Null-guarded because it
	/// isn't populated before the connection settles — see the deferred host spawn
	/// in CLAUDE.md.</para></summary>
	public static ulong LocalSteamId => Connection.Local?.SteamId ?? 0UL;

	/// <summary>
	/// Current (steamId, fpToken) for a gamchess call, minting if needed. Returns
	/// (null, null) when Steam isn't available or the mint failed — callers must
	/// treat that as "skip gamchess", never as an error worth surfacing mid-game.
	/// </summary>
	public static async Task<(string steamId, string token)> Credentials( bool forceRefresh = false )
	{
		var steamId = LocalSteamId;
		if ( steamId == 0 ) return (null, null);

		if ( !forceRefresh && !string.IsNullOrEmpty( _token ) && (float)_tokenExpires > 0f )
			return (steamId.ToString(), _token);

		var token = await Mint();
		if ( string.IsNullOrEmpty( token ) ) return (null, null);

		_token = token;
		_tokenExpires = CacheSeconds;
		return (steamId.ToString(), token);
	}

	/// <summary>Mint a fresh Facepunch auth token, or null. Documented to return
	/// null rather than throw off-Steam, but wrapped anyway — this sits on the path
	/// of a game ending, and an exception there would be far worse than no archive.</summary>
	static async Task<string> Mint()
	{
		try
		{
			var token = await Sandbox.Services.Auth.GetToken( ServiceName );
			if ( string.IsNullOrEmpty( token ) )
			{
				Log.Info( "[Gambit] no Facepunch auth token (not a Steam build?) — gamchess features off" );
				return null;
			}
			return token;
		}
		catch ( Exception e )
		{
			Log.Warning( "[Gambit] couldn't mint a Facepunch auth token — gamchess features off: " + e.Message );
			return null;
		}
	}

	/// <summary>Drop the cached token (sign-out, or an explicit retry). Drops the
	/// gamchess session with it — the session was minted from this token and proves
	/// the same identity, so forgetting one while keeping the other would leave the
	/// client authenticated by a credential it no longer holds the basis for.</summary>
	public static void Forget()
	{
		_token = null;
		_tokenExpires = 0f;
		GamchessApi.ForgetSession();
	}
}
