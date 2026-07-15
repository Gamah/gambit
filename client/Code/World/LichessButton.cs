using Gambit.Api;
using Sandbox;
using Sandbox.UI;

namespace Gambit.World;

/// <summary>
/// Click-to-copy for the lichess link URL, and for lichess's own Security page.
///
/// <para>Copy, not open: there is no documented API to open a URL or the Steam
/// overlay from in-game (CLAUDE.md), so every link Gambit offers is
/// click-to-copy. Same shape as <see cref="DiscordButton"/>, which proved the
/// pattern.</para>
///
/// <para>The link URL is a CONSTANT with no secret in it, which is what makes
/// copying it safe: it is Steam-session gated, so whoever pastes it links
/// <i>their own</i> accounts. Handing it to a friend just links the friend.</para>
/// </summary>
public static class LichessButton
{
	/// <summary>Time since the link URL was last copied — the screen shows brief
	/// "✓ copied" feedback off this, as LobbyOverlay does for Discord.</summary>
	public static RealTimeSince SinceCopied { get; private set; } = 999f;

	/// <summary>Time since lichess's Security URL was last copied.</summary>
	public static RealTimeSince SinceSecurityCopied { get; private set; } = 999f;

	public static void Copy()
	{
		Clipboard.SetText( LichessApi.LinkUrl );
		SinceCopied = 0f;
	}

	/// <summary>Copy lichess's Security page URL — the place a player revokes us
	/// without needing to trust our unlink button. Worth its own affordance: it is
	/// the honest answer to "how do I turn this off if I don't trust you?", and
	/// lichess's /account/oauth/token page will NOT show this grant.</summary>
	public static void CopySecurity()
	{
		Clipboard.SetText( LichessApi.SecurityUrl );
		SinceSecurityCopied = 0f;
	}
}
