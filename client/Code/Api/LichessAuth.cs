using System.Threading.Tasks;
using Sandbox;
using Gambit.Game;

namespace Gambit.Api;

/// <summary>
/// lichess sign-in (PLAN.md D3). Ships the guaranteed path — <b>token paste</b>:
/// the player creates a scoped personal token on lichess.org and pastes the
/// <c>lip_…</c> string into the splash screen; we validate it with
/// <c>GET /api/account</c> and store it in <see cref="PlayerData"/> (local
/// FileSystem JSON, never networked, never logged unredacted).
///
/// <para><b>Why not the full PKCE/browser flow yet?</b> The OAuth Authorization
/// Code + PKCE dance needs two things s&amp;box's sandbox does not give game code:
/// a way to open a system browser (no whitelisted URL/overlay API — CLAUDE.md),
/// and a loopback HTTP listener to catch the redirect (<c>System.Net.HttpListener</c>
/// / raw sockets are outside the API whitelist, so even referencing them is an
/// SB1000 compile error — that <i>is</i> the answer to PLAN.md's D3 "spike 2").
/// D3 always designated token-paste the shippable path; PKCE stays deferred until
/// (if ever) a whitelisted loopback/browser API exists. The scoped token-create
/// URL below pre-fills exactly the scopes we need so the paste flow is one click
/// plus one paste.</para>
/// </summary>
public static class LichessAuth
{
	/// <summary>Pre-filled lichess token-create page: exactly the Board API scopes
	/// Gambit needs (D3). Copy-to-clipboard from the splash — we can't open it for
	/// the user, but the whole page is prepared once they paste the URL.</summary>
	public const string TokenCreateUrl =
		"https://lichess.org/account/oauth/token/create" +
		"?scopes[]=board:play&scopes[]=challenge:read&scopes[]=challenge:write&scopes[]=puzzle:read" +
		"&description=Terry's+Gambit";

	/// <summary>Is there a stored lichess token right now?</summary>
	public static bool SignedIn => !string.IsNullOrEmpty( PlayerData.Load()?.LichessToken );

	/// <summary>The signed-in lichess account name, or empty.</summary>
	public static string Username => PlayerData.Load()?.LichessUsername ?? "";

	/// <summary>The stored token (secret — callers pass it only to
	/// <see cref="LichessApi"/>, never log or network it).</summary>
	public static string Token => PlayerData.Load()?.LichessToken ?? "";

	/// <summary>Representative rating for the signed-in account, 0 if unknown.</summary>
	public static int Rating => PlayerData.Load()?.LichessRating ?? 0;

	/// <summary>Validate a pasted token and, on success, persist it plus the
	/// account name. Returns (ok, error-for-the-user).</summary>
	public static async Task<(bool ok, string error)> SignInWithToken( string token )
	{
		token = token?.Trim();
		if ( string.IsNullOrEmpty( token ) )
			return (false, "Paste your lichess token first.");
		// Personal API tokens are lip_… ; a stray full URL paste is the usual slip.
		if ( token.Contains( ' ' ) || token.Contains( '/' ) )
			return (false, "That doesn't look like a token — paste just the lip_… string.");

		var res = await LichessApi.GetAccount( token );
		if ( res.Unauthorized )
			return (false, "lichess rejected that token (401). Create a fresh one and try again.");
		if ( !res.Ok )
			return (false, res.Error ?? "Couldn't validate the token — try again.");

		var acct = LichessApi.Deserialize<LichessAccount>( res.Body );
		if ( acct == null || string.IsNullOrEmpty( acct.username ) )
			return (false, "lichess sent an unexpected reply.");

		var data = PlayerData.Load() ?? new PlayerData();
		data.LichessToken = token;
		data.LichessUsername = acct.username;
		data.LichessRating = PickRating( acct );
		data.Save();

		Log.Info( $"[Gambit] signed in to lichess as {acct.username} (token {LichessApi.Redact( token )})" );
		return (true, null);
	}

	/// <summary>A single rating for the name tag: rapid first (our main time
	/// control), then classical, blitz, bullet. 0 if none/unrated.</summary>
	static int PickRating( LichessAccount acct )
	{
		if ( acct?.perfs == null ) return 0;
		foreach ( var key in new[] { "rapid", "classical", "blitz", "bullet" } )
			if ( acct.perfs.TryGetValue( key, out var p ) && p != null && p.rating > 0 )
				return p.rating;
		return 0;
	}

	/// <summary>Forget the local token (and best-effort revoke it on lichess).</summary>
	public static async void SignOut()
	{
		var data = PlayerData.Load();
		var token = data?.LichessToken;

		if ( !string.IsNullOrEmpty( token ) )
		{
			// Revoke server-side too, but never block logout on it.
			var res = await LichessApi.DeleteToken( token );
			if ( !res.Ok )
				Log.Info( $"[Gambit] token revoke returned {res.Status} — clearing locally anyway" );
		}

		// Reload in case Save() elsewhere replaced the cache during the await.
		data = PlayerData.Load();
		if ( data != null )
		{
			data.LichessToken = "";
			data.LichessUsername = "";
			data.Save();
		}
		Log.Info( "[Gambit] signed out of lichess" );
	}

	/// <summary>Handle a 401 from any authenticated call: the token is dead
	/// (lichess tokens don't refresh — D3), so clear it and re-prompt. Safe to
	/// call from any request path.</summary>
	public static void OnUnauthorized()
	{
		var data = PlayerData.Load();
		if ( data == null || string.IsNullOrEmpty( data.LichessToken ) ) return;

		data.LichessToken = "";
		data.LichessUsername = "";
		data.Save();
		Log.Warning( "[Gambit] lichess token was rejected (401) — cleared; please sign in again" );

		Gambit.UI.Screens.SplashScreen.Open();
	}

	/// <summary>On startup, quietly confirm a stored token still works; a 401
	/// clears it and re-prompts (PLAN.md M3 gate: "401 → re-prompt"). No-op when
	/// signed out. Fire-and-forget.</summary>
	public static async void ValidateStoredToken()
	{
		var token = PlayerData.Load()?.LichessToken;
		if ( string.IsNullOrEmpty( token ) ) return;

		var res = await LichessApi.GetAccount( token );
		if ( res.Unauthorized )
		{
			OnUnauthorized();
			return;
		}
		if ( !res.Ok ) return; // transient (offline, rate-limited) — keep the token

		// Refresh the cached account name/rating in case they changed on lichess.
		var acct = LichessApi.Deserialize<LichessAccount>( res.Body );
		if ( acct != null && !string.IsNullOrEmpty( acct.username ) )
		{
			var data = PlayerData.Load();
			if ( data != null )
			{
				int rating = PickRating( acct );
				if ( data.LichessUsername != acct.username || data.LichessRating != rating )
				{
					data.LichessUsername = acct.username;
					data.LichessRating = rating;
					data.Save();
				}
			}
		}
	}
}
