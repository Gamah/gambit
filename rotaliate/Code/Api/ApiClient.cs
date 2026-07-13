using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Rotaliate.Game;
using Sandbox;

namespace Rotaliate.Api;

public static class ApiClient
{
	public const string ProdUrl = "https://rotaliate.io";
	public const string TestUrl = "https://test.rotaliate.io";

	/// <summary>Backend the client talks to. Mutable (not const — const inlines at
	/// compile time and can't be overridden) so the lobby host can point every client
	/// at a chosen instance via <see cref="Rotaliate.World.LobbyNetworkManager.TargetUrl"/>.</summary>
	public static string BaseUrl { get; set; } = ProdUrl;

	static string Url( string path ) => BaseUrl + path;

	static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	static T Deserialize<T>( string json ) => JsonSerializer.Deserialize<T>( json, JsonOpts );

	// Error bodies aren't always JSON (e.g. a plain "Missing token" on 401), so
	// tolerate parse failures instead of masking the real status with a JSON error.
	static T TryDeserialize<T>( string json ) where T : class
	{
		try { return Deserialize<T>( json ); }
		catch { return null; }
	}

	// X-Player-ID identifies the player on every request; solo sessions are keyed to it.
	static Dictionary<string, string> Headers()
	{
		var guid = PlayerData.Load()?.Guid;
		if ( string.IsNullOrEmpty( guid ) ) return null;
		return new Dictionary<string, string> { ["X-Player-ID"] = guid };
	}

	static async Task<string> GetString( string path )
	{
		var resp = await Http.RequestAsync( Url( path ), "GET", null, Headers() );
		return await resp.Content.ReadAsStringAsync();
	}

	static async Task<string> PostString( string path, object body )
	{
		var resp = await Http.RequestAsync( Url( path ), "POST", Http.CreateJsonContent( body ), Headers() );
		return await resp.Content.ReadAsStringAsync();
	}

	public static async Task<CreatePlayerResponse> CreatePlayer()
	{
		// Include the local SteamID64 + auth token so a brand-new account is linked on
		// creation (issue #68/#69). A SteamID is only sent with a token to prove
		// ownership — if we can't mint one (web/non-Steam), drop the SteamID and
		// create a plain account, exactly as web clients do (body empty).
		var steamId = LocalSteamId();
		var token = steamId != null ? await AuthToken() : null;
		if ( string.IsNullOrEmpty( token ) ) steamId = null;

		var resp = await SendCreatePlayer( steamId, token );
		// 401 = token bad/expired: regenerate once and retry (Steam builds only).
		if ( (int)resp.StatusCode == 401 && steamId != null )
			resp = await SendCreatePlayer( steamId, await AuthToken() );

		var json = await resp.Content.ReadAsStringAsync();
		if ( !resp.IsSuccessStatusCode )
		{
			var err = TryDeserialize<ErrorResponse>( json );
			throw new Exception( string.IsNullOrEmpty( err?.Error ) ? $"HTTP {(int)resp.StatusCode}" : err.Error );
		}
		// A SteamID sent with the create request is linked server-side, so record it and
		// skip a redundant link (+token) on the first cabinet entry (issue #71).
		if ( steamId != null ) MarkSteamLinked( steamId );
		return Deserialize<CreatePlayerResponse>( json );
	}

	static async Task<System.Net.Http.HttpResponseMessage> SendCreatePlayer( string steamId, string token )
	{
		return await Http.RequestAsync(
			Url( "/api/v1/players" ), "POST",
			Http.CreateJsonContent( new CreatePlayerRequest( steamId, token ) ), Headers() );
	}

	/// <summary>Recover an existing account's GUID from the local SteamID (issue #71),
	/// for a fresh device with a Steam login but no stored GUID. Sends a Facepunch auth
	/// token (X-Steam-Token header) proving ownership; the server only hands back the
	/// GUID — a credential — to a client that proves it owns the SteamID. Returns the
	/// account on 200; null on 404 (no linked account → caller creates one) or when no
	/// SteamID/token is available (web/non-Steam). Retries once with a fresh token on
	/// 401 (expired/invalid).</summary>
	public static async Task<RecoverBySteamResponse> RecoverBySteamId()
	{
		var steamId = LocalSteamId();
		if ( steamId == null ) return null;
		var token = await AuthToken();
		if ( string.IsNullOrEmpty( token ) ) return null; // can't prove ownership

		var resp = await SendRecoverBySteamId( steamId, token );
		// 401 = token bad/expired: regenerate once and retry.
		if ( (int)resp.StatusCode == 401 )
			resp = await SendRecoverBySteamId( steamId, await AuthToken() );

		if ( (int)resp.StatusCode == 404 ) return null; // no account linked yet
		if ( !resp.IsSuccessStatusCode )
		{
			Log.Warning( $"[Rotaliate] SteamID recovery failed: HTTP {(int)resp.StatusCode}" );
			return null;
		}
		return TryDeserialize<RecoverBySteamResponse>( await resp.Content.ReadAsStringAsync() );
	}

	static async Task<System.Net.Http.HttpResponseMessage> SendRecoverBySteamId( string steamId, string token )
	{
		// SteamID is a query param (a /by-steam/{id} path collided with /players/{guid}/history
		// in Go's mux); the token rides in the header, never the URL.
		var headers = new Dictionary<string, string> { ["X-Steam-Token"] = token };
		return await Http.RequestAsync(
			Url( $"/api/v1/players/by-steam?steam_id={steamId}" ), "GET", null, headers );
	}

	/// <summary>The local player's SteamID64 as a string, or null on non-Steam/web
	/// builds. Sent as a string — it exceeds JS/double precision (issue #68).</summary>
	static string LocalSteamId()
	{
		ulong id = Connection.Local?.SteamId ?? 0UL;
		return id != 0 ? id.ToString() : null;
	}

	/// <summary>Facepunch auth token proving Steam ownership (issue #69). The service
	/// name tracks the selected backend (prod = "rotaliate", test = "rotaliate-test")
	/// for client-side organisation only — Facepunch validates {steamid, token}
	/// without it, so a name mismatch never rejects. Returns null (never throws) when
	/// no token can be minted — e.g. web/non-Steam — so callers fall back to a plain,
	/// SteamID-less request.</summary>
	static async Task<string> AuthToken()
	{
		var service = BaseUrl == TestUrl ? "rotaliate-test" : "rotaliate";
		try { return await Sandbox.Services.Auth.GetToken( service ); }
		catch ( Exception e )
		{
			Log.Warning( $"[Rotaliate] auth token unavailable: {e.Message}" );
			return null;
		}
	}

	/// <summary>Attach/update the local SteamID64 on an account that already exists
	/// (issue #68/#69). No-op on non-Steam builds. Sends a Facepunch auth token;
	/// retries once with a fresh token on 401 (expired/invalid). 422 = bad id,
	/// 404 = unknown GUID, 409 = id already linked to another account — all logged,
	/// not thrown (linking is best-effort; 409 leaves the local GUID as-is).</summary>
	public static async Task LinkSteamId( string guid )
	{
		var steamId = LocalSteamId();
		if ( steamId == null ) return;

		// Already linked this SteamID to this account — skip; no token needed (issue #71).
		// Recovery/creation that links the SteamID server-side records it, so the common
		// "GUID present, already linked" cabinet entry mints no Facepunch token at all.
		if ( PlayerData.Load()?.LinkedSteamId == steamId ) return;

		var token = await AuthToken();
		if ( string.IsNullOrEmpty( token ) ) return; // can't prove ownership — skip

		var resp = await SendLinkSteamId( guid, steamId, token );
		// 401 = token bad/expired: regenerate once and retry.
		if ( (int)resp.StatusCode == 401 )
			resp = await SendLinkSteamId( guid, steamId, await AuthToken() );
		if ( !resp.IsSuccessStatusCode )
		{
			Log.Warning( $"[Rotaliate] SteamID link failed: HTTP {(int)resp.StatusCode}" );
			return;
		}
		MarkSteamLinked( steamId );
	}

	/// <summary>Record that the local SteamID is linked to the current account so future
	/// cabinet entries skip the link + token mint (issue #71).</summary>
	static void MarkSteamLinked( string steamId )
	{
		if ( string.IsNullOrEmpty( steamId ) ) return;
		var data = PlayerData.Load();
		if ( data == null || data.LinkedSteamId == steamId ) return;
		data.LinkedSteamId = steamId;
		data.Save();
	}

	static async Task<System.Net.Http.HttpResponseMessage> SendLinkSteamId( string guid, string steamId, string token )
	{
		return await Http.RequestAsync(
			Url( $"/api/v1/players/{guid}/steamid" ), "PUT",
			Http.CreateJsonContent( new LinkSteamIdRequest( steamId, token ) ), Headers() );
	}

	public static async Task<PlayerResponse> GetPlayer( string guid )
	{
		return Deserialize<PlayerResponse>( await GetString( $"/api/v1/players/{guid}" ) );
	}

	/// <summary>True if the GUID exists server-side, false on a definite 404.
	/// Any other failure throws — never treat a network error as "player gone".</summary>
	public static async Task<bool> PlayerExists( string guid )
	{
		var resp = await Http.RequestAsync( Url( $"/api/v1/players/{guid}" ), "GET", null, Headers() );
		if ( (int)resp.StatusCode == 404 ) return false;
		if ( !resp.IsSuccessStatusCode ) throw new Exception( $"HTTP {(int)resp.StatusCode}" );
		return true;
	}

	/// <summary>Throws with the server's message on rejection (422 = name fails
	/// validation: 3–20 chars, alphanumeric/underscore, no disallowed words;
	/// 409 = name already taken). Returns the recomputed player_tag on success.</summary>
	public static async Task<SetUsernameResponse> SetUsername( string guid, string username )
	{
		var body = Http.CreateJsonContent( new SetUsernameRequest( username ) );
		var resp = await Http.RequestAsync( Url( $"/api/v1/players/{guid}/username" ), "PUT", body, Headers() );
		var json = await resp.Content.ReadAsStringAsync();
		if ( !resp.IsSuccessStatusCode )
		{
			var err = Deserialize<ErrorResponse>( json );
			throw new Exception( string.IsNullOrEmpty( err?.Error ) ? $"HTTP {(int)resp.StatusCode}" : err.Error );
		}
		return Deserialize<SetUsernameResponse>( json );
	}

	public static async Task<PuzzleResponse> GetDailyPuzzle()
	{
		return Deserialize<PuzzleResponse>( await GetString( "/api/v1/puzzle/daily" ) );
	}

	public static async Task<PuzzleResponse> GetHourlyPuzzle()
	{
		return Deserialize<PuzzleResponse>( await GetString( "/api/v1/puzzle/hourly" ) );
	}

	public static async Task<PuzzleResponse> GetFreeplay( string seed = "0" )
	{
		var json = await PostString( "/api/v1/puzzle/freeplay", new FreeplayRequest( seed ) );
		return Deserialize<PuzzleResponse>( json );
	}

	/// <summary>
	/// Stream one move to the server-side session. Moves must be sent serially in play
	/// order — callers chain on the previous send. Throws MoveRejectedException on a
	/// non-success status (404 session expired, 422 invalid move, 429 rate limited).
	/// </summary>
	public static async Task<MoveResponse> SendMove( string sessionId, int move )
	{
		var resp = await Http.RequestAsync(
			Url( $"/api/v1/sessions/{sessionId}/moves" ), "POST",
			Http.CreateJsonContent( new MoveRequest( move ) ), Headers() );

		if ( !resp.IsSuccessStatusCode )
			throw new MoveRejectedException( (int)resp.StatusCode );

		var json = await resp.Content.ReadAsStringAsync();
		return Deserialize<MoveResponse>( json );
	}

	public static async Task<SessionResponse> GetSession( string sessionId )
	{
		return Deserialize<SessionResponse>( await GetString( $"/api/v1/sessions/{sessionId}" ) );
	}

	public static async Task<ReplayResponse> GetReplay( string sessionId )
	{
		return Deserialize<ReplayResponse>( await GetString( $"/api/v1/sessions/{sessionId}/replay" ) );
	}

	/// <summary>Public player profile (no auth) keyed by the 8-hex player_tag — never
	/// the GUID. Throws on 404 (unknown tag) / 422 (malformed tag); the caller surfaces
	/// the failure as "profile unavailable".</summary>
	public static async Task<ProfileResponse> GetProfile( string playerTag )
	{
		var resp = await Http.RequestAsync( Url( $"/api/v1/profile/{Uri.EscapeDataString( playerTag )}" ), "GET", null, Headers() );
		if ( !resp.IsSuccessStatusCode )
			throw new Exception( $"HTTP {(int)resp.StatusCode}" );
		return Deserialize<ProfileResponse>( await resp.Content.ReadAsStringAsync() );
	}

	/// <summary>
	/// Regenerate a board from a seed for replay playback. Sends no X-Player-ID,
	/// so the server doesn't open a move-stream session for it.
	/// </summary>
	public static async Task<PuzzleResponse> RegenerateGrid( string seed )
	{
		var resp = await Http.RequestAsync(
			Url( "/api/v1/puzzle/freeplay" ), "POST",
			Http.CreateJsonContent( new FreeplayRequest( seed ) ) );
		return Deserialize<PuzzleResponse>( await resp.Content.ReadAsStringAsync() );
	}

	public static async Task<List<LeaderboardEntry>> GetDailyLeaderboard( string metric, string puzzleId = null )
	{
		var path = $"/api/v1/leaderboard/daily/{metric}";
		if ( puzzleId != null ) path += $"?puzzle_id={Uri.EscapeDataString( puzzleId )}";
		return Deserialize<List<LeaderboardEntry>>( await GetString( path ) );
	}

	public static async Task<List<LeaderboardEntry>> GetHourlyLeaderboard( string metric, string puzzleId = null )
	{
		var path = $"/api/v1/leaderboard/hourly/{metric}";
		if ( puzzleId != null ) path += $"?puzzle_id={Uri.EscapeDataString( puzzleId )}";
		return Deserialize<List<LeaderboardEntry>>( await GetString( path ) );
	}

	public static async Task<List<MpWinEntry>> GetMultiplayerLeaderboard( int size )
	{
		return Deserialize<List<MpWinEntry>>( await GetString( $"/api/v1/leaderboard/multiplayer/{size}" ) );
	}

	// Public match browser — open lobbies with a free slot. mode = 2 or 4.
	public static async Task<List<OpenLobby>> GetOpenLobbies( int mode )
	{
		return Deserialize<List<OpenLobby>>( await GetString( $"/api/v1/lobbies/open?mode={mode}" ) );
	}

	public static async Task<List<RecentPuzzle>> GetRecentDaily()
	{
		return Deserialize<List<RecentPuzzle>>( await GetString( "/api/v1/daily/recent" ) );
	}

	public static async Task<List<RecentPuzzle>> GetRecentHourly()
	{
		return Deserialize<List<RecentPuzzle>>( await GetString( "/api/v1/hourly/recent" ) );
	}

	/// <summary>Mint a single-use, short-TTL WebSocket ticket (security review C3).
	/// The GUID rides the X-Player-ID header (via <see cref="Headers"/>) and is proven
	/// once here, so the WS upgrade carries only ?ticket= — never the GUID, which would
	/// leak into proxy/access logs. Mint immediately before connecting; the ticket
	/// expires after ttl_ms (~30s). Throws on failure (e.g. 400 = missing X-Player-ID)
	/// so the caller fails the connect cleanly rather than falling back to ?player_id=.</summary>
	public static async Task<WsTicketResponse> GetWsTicket()
	{
		var resp = await Http.RequestAsync( Url( "/api/v1/ws/ticket" ), "POST", null, Headers() );
		if ( !resp.IsSuccessStatusCode )
			throw new Exception( $"ws ticket request failed: HTTP {(int)resp.StatusCode}" );
		var json = await resp.Content.ReadAsStringAsync();
		var ticket = Deserialize<WsTicketResponse>( json );
		if ( ticket == null || string.IsNullOrEmpty( ticket.Ticket ) )
			throw new Exception( "ws ticket response missing ticket" );
		return ticket;
	}

	public static async Task SubmitFeedback( string message, string playerGuid = null )
	{
		// TODO: replace steamId email placeholder with a real email field once the UI has one
		ulong steamId = Connection.Local?.SteamId ?? 0UL;
		var email   = steamId != 0 ? $"{steamId}@steam" : "";
		await PostString( "/api/v1/feedback", new FeedbackRequest( message, email ) );
	}
}

public sealed class MoveRejectedException : Exception
{
	public int StatusCode { get; }

	public MoveRejectedException( int statusCode )
		: base( $"move rejected: HTTP {statusCode}" )
	{
		StatusCode = statusCode;
	}
}
