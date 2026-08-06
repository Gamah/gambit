using System;
using System.Text.Json;
using Sandbox;

namespace Gambit.Api.Lichess;

/// <summary>
/// The player's lichess token, on this PC (HTTPFIX).
///
/// <para><b>No prompt, no toggle, verbose copy instead.</b> The owner's call: a
/// consent dialog for "may we save the key you just created" is a decision the
/// player has no basis to make, so the disclosure page and the info board say
/// plainly where the key lives and what deleting the game's data does. Asking
/// would have been theatre.</para>
///
/// <para><b>The risk was re-derived, not assumed</b> (it replaces the old,
/// never-closed "rogue lobby host" spike). Joining an EDITOR host or a
/// local-project dedicated server compiles and loads that host's source into your
/// process before the scene loads, and whitelisted C# is enough to read
/// <c>FileSystem.Data</c> and POST it anywhere. But a host running a PUBLISHED
/// package sends no code at all, and memory-only bought little regardless: anyone
/// playing on lichess is linked <i>that session</i>, and injected code shares the
/// process. <c>FileSystem.Data</c> is per-package under the org root.</para>
///
/// <para><b>Its OWN file, never <c>player.json</c>.</b> <c>PlayerData</c> is a
/// static shared object serialized whole on every settings write — a token riding
/// inside it would eventually land in a log or a crash dump, and every slider
/// nudge would rewrite it. A separate file also makes "forget my token" a single
/// delete.</para>
///
/// <para>This is NOT the rule that governs gamchess credentials.
/// <c>GamchessApi._session</c> and <c>GamchessAuth._token</c> stay memory-only:
/// a gamchess session is stateless and unrevokable, and a lichess token is
/// revokable by its owner on <c>/account/security</c> at any time. Different
/// credentials, different reasoning — do not "make them consistent".</para>
/// </summary>
public static class LichessTokenStore
{
	const string Path = "lichess.json";

	sealed class Stored
	{
		public string token { get; set; }
		/// <summary>Canonical lowercase lichess id — the identity.</summary>
		public string lichess_id { get; set; }
		/// <summary>Display casing. Cosmetic, and what the boards show.</summary>
		public string username { get; set; }
		/// <summary>What lichess actually granted, space-separated. Recorded so a
		/// scope change can be DETECTED rather than discovered when a call 403s:
		/// tokens are long-lived and have no refresh, so a widened scope set means
		/// a re-link, and this is how we know to ask for one.</summary>
		public string scopes { get; set; }
	}

	static Stored _cache;
	static bool _loaded;

	static void Load()
	{
		if ( _loaded ) return;
		_loaded = true;
		try
		{
			if ( !FileSystem.Data.FileExists( Path ) ) return;
			_cache = JsonSerializer.Deserialize<Stored>( FileSystem.Data.ReadAllText( Path ) );
		}
		catch ( Exception e )
		{
			// A corrupt file is "not linked", never a crash. Same discipline as
			// everything else that touches the network here: fail to the state where
			// chess still works.
			Log.Warning( $"[Gambit] couldn't read the lichess link: {e.Message}" );
			_cache = null;
		}
	}

	/// <summary>Is this machine linked? Answerable locally, instantly, and
	/// offline — which is why <see cref="LichessLinkState"/> no longer polls
	/// gamchess to find out.</summary>
	public static bool Linked
	{
		get { Load(); return !string.IsNullOrEmpty( _cache?.token ); }
	}

	/// <summary>The token, or null.
	///
	/// <para>Every caller is inside <c>Code/Api/Lichess/</c>. It must never be
	/// logged, never put in an error string, and never sent anywhere except
	/// lichess.org and — exactly once, at link — gamchess's claim endpoint.
	/// lichess records a <c>clientOrigin</c> per token, so abuse of a leaked Gambit
	/// token is attributable to Gambit, and their lever is killing the whole app on
	/// that origin.</para></summary>
	public static string Token
	{
		get { Load(); return _cache?.token; }
	}

	/// <summary>The linked account's display name, or null.</summary>
	public static string Username
	{
		get { Load(); return _cache?.username; }
	}

	/// <summary>The linked account's canonical id, or null.</summary>
	public static string LichessId
	{
		get { Load(); return _cache?.lichess_id; }
	}

	/// <summary>True when the stored grant predates the scope set this build asks
	/// for, so the player should be told to re-link rather than left to discover it
	/// when a feature 403s.
	///
	/// <para>Conservative on purpose: an UNKNOWN scope string (an older file that
	/// never recorded one) is NOT reported as stale. A false "please link again"
	/// costs a real re-link and a real consent screen for nothing.</para></summary>
	public static bool ScopesAreStale
	{
		get
		{
			Load();
			if ( string.IsNullOrEmpty( _cache?.scopes ) ) return false;
			foreach ( var want in LichessScopes.All.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
				if ( !HasScope( want ) ) return true;
			return false;
		}
	}

	/// <summary>Does the stored grant include this scope? An unrecorded scope
	/// string answers true — see <see cref="ScopesAreStale"/>.</summary>
	public static bool HasScope( string scope )
	{
		Load();
		if ( string.IsNullOrEmpty( _cache?.scopes ) ) return true;
		foreach ( var s in _cache.scopes.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
			if ( s == scope ) return true;
		return false;
	}

	/// <summary>Save a fresh link. Called once, at the end of the link flow.</summary>
	public static void Save( string token, string lichessId, string username, string scopes )
	{
		_cache = new Stored { token = token, lichess_id = lichessId, username = username, scopes = scopes };
		_loaded = true;
		try
		{
			FileSystem.Data.WriteAllText( Path, JsonSerializer.Serialize( _cache ) );
		}
		catch ( Exception e )
		{
			// The token still works this session — it is in memory. Say so rather
			// than pretend the link is durable.
			Log.Warning( $"[Gambit] couldn't save the lichess link (it will be lost on exit): {e.Message}" );
		}
	}

	/// <summary>Forget the token on this machine.
	///
	/// <para><b>This is HALF of unlinking, and the other half is not optional.</b>
	/// Deleting the file does not revoke anything: the grant stays live at lichess
	/// for up to a year. <see cref="LichessLink.Unlink"/> revokes FIRST and then
	/// calls this, and the copy tells players that deleting the game's data does
	/// only what this does.</para></summary>
	public static void Forget()
	{
		_cache = null;
		_loaded = true;
		try
		{
			if ( FileSystem.Data.FileExists( Path ) ) FileSystem.Data.DeleteFile( Path );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit] couldn't delete the lichess link file: {e.Message}" );
		}
	}

	/// <summary>Re-read from disk on the next access. For the console command and
	/// for tests; nothing in normal play needs it.</summary>
	public static void Invalidate() => _loaded = false;
}

/// <summary>The scopes Gambit asks lichess for.
///
/// <para><b>Kept in step with the server's <c>lichess.Scopes</c> by hand</b>, like
/// every other part of this contract — but the SERVER's copy is the one that ends
/// up in the authorize URL, so if they ever disagree the server wins and this is
/// only used to notice a stale grant.</para>
///
/// <para>The set widened at HTTPFIX because that branch forced a re-link on
/// everyone anyway, which is the one moment in the project's life when widening
/// costs nothing extra. It is not licence to widen again casually — a scope
/// change means every linked player re-links, since lichess tokens have no
/// refresh.</para>
///
/// <para><b><c>web:mobile</c> and <c>web:polygon</c> stay out</b>, and not for
/// risk reasons: their own descriptions are "Official Lichess mobile app" and
/// "Take Take Take". Taking one would be claiming first-party status to bypass a
/// gate lichess put on third-party board clients deliberately. That is the door
/// blitz seeks and quick pairing are behind, and PLAN No. 13 records the decision
/// not to walk through it.</para></summary>
public static class LichessScopes
{
	/// <summary>Plays games: seek, both streams, move, resign, draw, takeback,
	/// abort. A single all-or-nothing grant with no read-only subset.</summary>
	public const string BoardPlay = "board:play";

	public const string PuzzleRead = "puzzle:read";
	public const string PuzzleWrite = "puzzle:write";
	public const string FollowRead = "follow:read";

	public const string All = BoardPlay + " " + PuzzleRead + " " + PuzzleWrite + " "
		+ FollowRead;
}
