using Gambit.Api;
using Gambit.World;
using Sandbox;
using Sandbox.UI; // Clipboard

namespace Gambit.Game;

/// <summary>
/// Hosts an <b>open lichess game</b> at a table (PLAN.md M4, link-flow variant).
/// One instance per station, added by ChessRing beside <see cref="ChessStation"/>
/// and <see cref="LocalGameController"/>, replicating with the network-spawned
/// station GO.
///
/// <para>Why this exists without streaming: incremental HTTP streaming is
/// unavailable under s&amp;box <c>Http</c> (PLAN.md D4 — <c>RequestAsync</c>
/// buffers the whole body; confirmed in-editor), and raw sockets/TLS are
/// off-whitelist, so real-time Board API play (moves landing on the board) is not
/// yet possible in-sandbox. This mode instead uses <c>POST /api/challenge/open</c>
/// (a single short request, no stream): the seated player generates an unrated
/// Rapid 10+0 game and gets two colour-pinned URLs. The chess itself is played on
/// lichess.org in a browser; sbox is the meeting point that assigns a side, hands
/// over the link, and routes the camera. Live moves-on-the-board play waits on a
/// streaming primitive (a hosted wss relay bridging lichess ndjson — deferred).</para>
///
/// <para>The open-challenge URLs are public (created without a token), so unlike
/// the lichess bearer token they are safe to <c>[Sync]</c> across the lobby — that
/// is how a second client sitting at the other seat, or a late spectator, sees the
/// same game.</para>
/// </summary>
public sealed class LichessGameController : Component
{
	/// <summary>Occupancy/seat source for this table. Set by ChessRing at build.</summary>
	[Property] public ChessStation Station { get; set; }

	/// <summary>The controller living beside the given station, or null.</summary>
	public static LichessGameController For( ChessStation station ) =>
		station?.Components.Get<LichessGameController>();

	// ── Host-authoritative synced state (public URLs — no secret) ──

	/// <summary>lichess challenge id, empty when no open game is live here.</summary>
	[Sync( SyncFlags.FromHost )] public string ChallengeId { get; set; }

	/// <summary>Colour-pinned play URLs from lichess (whoever opens one plays that
	/// side). Empty until a game is created.</summary>
	[Sync( SyncFlags.FromHost )] public string UrlWhite { get; set; }
	[Sync( SyncFlags.FromHost )] public string UrlBlack { get; set; }

	/// <summary>An open lichess game exists at this table right now.</summary>
	public bool HasOpenGame => !string.IsNullOrEmpty( ChallengeId );

	// ── Local UI state (creating client only) ──

	/// <summary>True while the create POST is in flight (HUD greys the button).</summary>
	public bool Creating { get; private set; }

	/// <summary>Last create failure, for the HUD. Null when none.</summary>
	public string Error => _error;
	string _error;

	/// <summary>Time since a URL was copied — brief HUD feedback (DiscordButton pattern).</summary>
	public RealTimeSince SinceCopied { get; private set; } = 999f;

	/// <summary>The colour-pinned play URL for a given seat.</summary>
	public string PlayUrlFor( ChessSeat seat ) =>
		seat == ChessSeat.White ? UrlWhite : UrlBlack;

	protected override void OnUpdate()
	{
		// Recycle the board once everyone has left: clear a stale challenge so the
		// next person to sit starts fresh. The lichess game itself lives on
		// lichess.org regardless — this only resets sbox's display.
		if ( Networking.IsHost && HasOpenGame && Station != null
			&& Station.WhiteSteamId == 0 && Station.BlackSteamId == 0 )
			ClearFields();
	}

	// ── Create ──

	/// <summary>Seated player creates the open game (HUD button). Unauthenticated →
	/// always unrated. Rapid 10+0. The POST runs on this client; the resulting
	/// public URLs are handed to the host to fold into the synced fields.</summary>
	public async void CreateOpenGame()
	{
		if ( HasOpenGame || Creating ) return;

		Creating = true;
		_error = null;
		try
		{
			var res = await LichessApi.CreateOpenChallenge( 600, 0, "Terry's Gambit" );
			if ( !res.Ok )
			{
				_error = res.Error ?? "lichess wouldn't create the game";
				Log.Warning( $"[Gambit] open challenge failed ({res.Status}): {LichessApi.Truncate( res.Body, 200 )}" );
				return;
			}

			var oc = LichessApi.Deserialize<LichessOpenChallenge>( res.Body );
			if ( oc == null || string.IsNullOrEmpty( oc.id )
				|| string.IsNullOrEmpty( oc.urlWhite ) || string.IsNullOrEmpty( oc.urlBlack ) )
			{
				_error = "lichess sent an unexpected reply";
				Log.Warning( $"[Gambit] open challenge reply missing urls: {LichessApi.Truncate( res.Body, 200 )}" );
				return;
			}

			HostSetChallenge( oc.id, oc.urlWhite, oc.urlBlack );
			Log.Info( $"[Gambit] open lichess game created: {oc.url}" );
		}
		finally
		{
			Creating = false;
		}
	}

	/// <summary>Creator → host: publish the (public) open-challenge URLs. First
	/// creator wins, so a race between the two seats can't clobber a live game.</summary>
	[Rpc.Host]
	void HostSetChallenge( string id, string urlWhite, string urlBlack )
	{
		if ( HasOpenGame ) return;
		ChallengeId = id;
		UrlWhite = urlWhite;
		UrlBlack = urlBlack;
	}

	/// <summary>Drop the current open game so the board can host another (HUD
	/// "New link", or auto-recycle when the table empties).</summary>
	public void ClearOpenGame()
	{
		if ( HasOpenGame ) HostClear();
	}

	[Rpc.Host]
	void HostClear() => ClearFields();

	/// <summary>Host-side field reset. Only the host mutates these <c>FromHost</c>
	/// syncs — reached inline from the auto-recycle path and via HostClear's RPC.</summary>
	void ClearFields()
	{
		ChallengeId = null;
		UrlWhite = null;
		UrlBlack = null;
	}

	// ── Copy ──

	/// <summary>Copy a play URL — no in-game browser-open API exists (CLAUDE.md),
	/// so click-to-copy like the Discord invite / PGN import link.</summary>
	public void CopyUrl( string url )
	{
		if ( string.IsNullOrEmpty( url ) ) return;
		Clipboard.SetText( url );
		SinceCopied = 0f;
	}

	// ── Join by link ──

	/// <summary>Which side a lichess challenge/game URL assigns, read from its
	/// <c>?color=white|black</c> query (the shape lichess returns for open-challenge
	/// URLs). Null if the URL pins no colour (e.g. the neutral game URL) — lichess
	/// only decides the side when such a link is opened, so sbox can't know it.</summary>
	public static ChessSeat? SeatFromUrl( string url )
	{
		if ( string.IsNullOrWhiteSpace( url ) ) return null;
		var u = url.ToLowerInvariant();
		if ( u.Contains( "color=white" ) ) return ChessSeat.White;
		if ( u.Contains( "color=black" ) ) return ChessSeat.Black;
		return null;
	}
}
