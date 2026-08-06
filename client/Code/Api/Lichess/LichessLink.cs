using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api.Lichess;

/// <summary>
/// Linking a lichess account, client side (HTTPFIX).
///
/// <para><b>The client holds the PKCE verifier and does the token exchange
/// itself.</b> gamchess mints the state, shows the disclosure page, and parks the
/// code that comes back — and can do nothing with it, because a code without its
/// verifier is worthless. That is the whole custody change in one sentence: the
/// secret never exists anywhere but this machine.</para>
///
/// <para><b>The player still opens <c>/lichess/link</c>, a CONSTANT, and never the
/// raw authorize URL.</b> This is the call most likely to be got wrong, because
/// showing lichess's URL directly looks simpler. The constant is safe precisely
/// because it carries no secret: it is Steam-session gated, so whoever opens it
/// links THEIR OWN accounts, and handing it to a friend just links the friend. A
/// raw authorize URL is bound to YOUR state and YOUR SteamID — a friend who
/// opened it would consent on THEIR lichess account and YOU would end up holding
/// a grant on it. Strictly worse than anything the old design could do.</para>
///
/// <para>Five steps: register a challenge, open the page, consent, collect the
/// parked code, exchange it and claim the identity.</para>
/// </summary>
public static class LichessLink
{
	/// <summary>How often to ask gamchess whether the browser has come back. The
	/// player is alt-tabbed completing an OAuth flow, so this decides how long
	/// "linked!" takes to appear.</summary>
	const float PollSeconds = 2f;

	/// <summary>Give up on an unfinished flow. Matches gamchess's own slot TTL —
	/// if they disagree the server wins and we'd poll a slot that no longer
	/// exists, which is harmless but pointless.</summary>
	const float FlowTimeoutSeconds = 600f;

	/// <summary>What the board should say right now. Null when nothing is in
	/// flight.</summary>
	public static string Status { get; private set; }

	/// <summary>Why the last attempt failed, for the board. Null when nothing is
	/// wrong.</summary>
	public static string Error { get; private set; }

	/// <summary>A link is in progress — the board shows the URL and waits.</summary>
	public static bool InProgress { get; private set; }

	/// <summary>The URL to copy. Filled in once a flow starts; a constant, and
	/// there is deliberately nothing secret in it.</summary>
	public static string LinkUrl { get; private set; } = GamchessApi.Base + "/lichess/link";

	// The verifier for the flow in progress. IN MEMORY ONLY and for the length of
	// one link: it is the one value that must never reach gamchess, and there is
	// no reason for it to outlive the exchange.
	static Pkce _pkce;
	static RealTimeUntil _nextPoll;
	static RealTimeUntil _flowExpires;
	static bool _busy;

	/// <summary>
	/// Start a link. Safe to call again while one is running — it does nothing.
	/// </summary>
	public static void Begin()
	{
		if ( InProgress || _busy ) return;
		if ( !GamchessAuth.Available )
		{
			Error = "Linking needs Steam.";
			return;
		}
		_busy = true;
		Error = null;
		Status = "Starting…";
		_ = BeginAsync();
	}

	static async Task BeginAsync()
	{
		// Mint the pair BEFORE telling anyone anything. gamchess gets the
		// challenge; the verifier stays here.
		_pkce = Pkce.New();

		var res = await LichessApi.LinkStart( _pkce.Challenge );
		_busy = false;

		if ( !res.Ok )
		{
			_pkce = default;
			Status = null;
			Error = ReadError( res, "Couldn't start the link." );
			return;
		}

		var start = GamchessApi.Deserialize<LichessLinkStart>( res.Body );
		if ( start == null || string.IsNullOrEmpty( start.state ) )
		{
			_pkce = default;
			Status = null;
			Error = "gamchess didn't start the link.";
			return;
		}

		if ( !string.IsNullOrEmpty( start.link_url ) ) LinkUrl = start.link_url;

		InProgress = true;
		_flowExpires = FlowTimeoutSeconds;
		_nextPoll = PollSeconds;
		Status = "Open the link in a browser and approve it on lichess.";
	}

	/// <summary>Call every frame while the lichess board is being looked at. Polls
	/// for the parked code, then finishes the link.
	///
	/// <para>Two guards, and the second is the <c>TryArchive</c> lesson:
	/// <see cref="_busy"/> is claimed BEFORE the await, or this fires a request
	/// every frame until the first one returns.</para></summary>
	public static void Poll()
	{
		if ( !InProgress || _busy ) return;

		if ( (float)_flowExpires <= 0f )
		{
			Cancel();
			Error = "That link expired. Try again.";
			return;
		}
		if ( (float)_nextPoll > 0f ) return;

		_busy = true;
		_nextPoll = PollSeconds;
		_ = PollAsync();
	}

	static async Task PollAsync()
	{
		var res = await LichessApi.LinkCollect();
		if ( !res.Ok )
		{
			_busy = false;
			return;   // transient; the next poll tries again
		}

		var slot = GamchessApi.Deserialize<LichessLinkCollect>( res.Body );
		if ( slot == null || slot.status == "waiting" )
		{
			_busy = false;
			return;
		}
		if ( slot.status != "ready" || string.IsNullOrEmpty( slot.code ) )
		{
			// "none" — the slot expired, or somebody started a newer flow (gamchess
			// keeps only the newest per SteamID).
			_busy = false;
			Cancel();
			Error = "That link expired. Try again.";
			return;
		}

		await Finish( slot );
		_busy = false;
	}

	static async Task Finish( LichessLinkCollect slot )
	{
		Status = "Finishing…";

		// ⑤ Exchange at lichess, with OUR verifier and THE SERVER'S redirect_uri.
		//
		// The redirect_uri must be byte-identical to the one the authorize call
		// used, and it comes back from gamchess rather than being hardcoded here:
		// a hardcoded copy would silently break the test instance, which must point
		// at itself. Same reason the server derives it once from PUBLIC_BASE_URL.
		var form = new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = slot.code,
			["code_verifier"] = _pkce.Verifier,
			["redirect_uri"] = slot.redirect_uri,
			["client_id"] = slot.client_id,
		};

		var res = await LichessClient.Send( "/api/token", "POST", null, form );
		if ( !res.Ok )
		{
			Cancel();
			Error = "Lichess wouldn't complete the link: " + res.Reason;
			return;
		}

		var tok = LichessClient.Parse<LichessTokenResponse>( res.Body );
		if ( string.IsNullOrEmpty( tok?.access_token ) )
		{
			Cancel();
			Error = "Lichess returned no token.";
			return;
		}

		// The verifier has done its job. Drop it before anything else can go wrong
		// with it in scope.
		_pkce = default;

		// Tell gamchess WHO we are — by handing it the token once, not by asserting
		// a username. It asks lichess and believes lichess. An asserted id would let
		// anyone squat a real account's row and lock its owner out of ever linking.
		//
		// This is the one moment the token leaves this machine. It is not stored
		// there, not logged, and not put in an error string.
		var claim = await LichessApi.Claim( tok.access_token );
		if ( !claim.Ok )
		{
			// The token is REAL and works — gamchess just won't record it. Say so
			// honestly rather than silently keeping a link the directory doesn't
			// know about: without a gamchess row, the two-seat flow can't find an
			// opponent's username.
			Cancel();
			Error = ReadError( claim, "Couldn't finish the link." )
				+ " The lichess approval worked — try linking again.";
			return;
		}

		var link = GamchessApi.Deserialize<LichessLinkStatus>( claim.Body );
		if ( link == null || string.IsNullOrEmpty( link.username ) )
		{
			Cancel();
			Error = "Couldn't finish the link.";
			return;
		}

		LichessTokenStore.Save( tok.access_token, link.lichess_id, link.username, LichessScopes.All );

		InProgress = false;
		Status = null;
		Error = null;
		LichessLinkState.AdoptLink();
		Log.Info( $"[Gambit] linked lichess account {link.username}" );
	}

	/// <summary>Abandon a link in progress. Does not touch a link already made.</summary>
	public static void Cancel()
	{
		_pkce = default;
		InProgress = false;
		Status = null;
	}

	/// <summary>
	/// Unlink: revoke at lichess, then forget locally, then tell gamchess.
	///
	/// <para><b>The order is the whole point, and it is finally correct rather than
	/// best-effort.</b> <c>DELETE /api/token</c> is signed BY the token being
	/// revoked — verified against the live spec 2026-08-05, "Revokes the access
	/// token sent as Bearer for this request", 204 — so only this machine can do
	/// it, and only while it still has the token. Revoke FIRST: a token we have
	/// already deleted can never be revoked by anyone but the player, on
	/// <c>/account/security</c>.</para>
	///
	/// <para>The revoke is best-effort and the delete is not: a player who pressed
	/// unlink must end up unlinked. A failed revoke leaves a token that dies on its
	/// own in about a year, which is exactly why the copy names
	/// <c>/account/security</c> as the real off switch — and why it names that page
	/// rather than <c>/account/oauth/token</c>, which lists personal tokens only
	/// and would show an empty list.</para>
	/// </summary>
	public static async Task Unlink()
	{
		string token = LichessTokenStore.Token;
		if ( !string.IsNullOrEmpty( token ) )
		{
			// lichess treats a 401 as "already dead", which is the outcome we
			// wanted, so nothing here distinguishes it from success.
			var res = await LichessClient.Send( "/api/token", "DELETE", token );
			if ( !res.Ok && !res.Unauthorized )
				Log.Info( $"[Gambit] couldn't revoke the lichess token ({res.Status}) — deleting it anyway" );
		}

		LichessTokenStore.Forget();
		LichessEventStream.Reset();
		await LichessApi.Unlink();
		LichessLinkState.Forget();
	}

	static string ReadError( GamchessApi.Result res, string fallback )
	{
		var body = GamchessApi.Deserialize<GamchessError>( res.Body );
		return !string.IsNullOrEmpty( body?.error ) ? body.error : res.Error ?? fallback;
	}
}

/// <summary>Reply from <c>POST /api/v1/lichess/link/start</c>.</summary>
public sealed class LichessLinkStart
{
	public string state { get; set; }
	/// <summary>Built server-side so the redirect_uri stays byte-exact. <b>The
	/// client must not open this</b> — see <see cref="LichessLink"/>.</summary>
	public string authorize_url { get; set; }
	public string redirect_uri { get; set; }
	public string link_url { get; set; }
}

/// <summary>Reply from <c>POST /api/v1/lichess/link/collect</c>.</summary>
public sealed class LichessLinkCollect
{
	/// <summary>"none" · "waiting" · "ready".</summary>
	public string status { get; set; }
	public string code { get; set; }
	/// <summary>Use THIS at the token endpoint, never a hardcoded one.</summary>
	public string redirect_uri { get; set; }
	public string client_id { get; set; }
}
