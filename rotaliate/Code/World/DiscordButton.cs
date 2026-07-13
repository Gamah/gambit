using Sandbox;
using Sandbox.UI;

namespace Rotaliate.World;

/// <summary>
/// Discord invite copy helper. There is no documented API to open a URL in-game, but
/// Clipboard.SetText works (proven in ModePickerScreen), so the Discord link is
/// click-to-copy instead — the clickable link lives in InfoScreen (the east-wall info
/// board's engage-flow viewer). LobbyOverlay shows brief "copied" feedback via
/// <see cref="SinceCopied"/>.
/// </summary>
public static class DiscordButton
{
	public const string InviteCode = "GG8HWUfFpD";
	public const string InviteUrl = "https://discord.gg/" + InviteCode;

	/// <summary>Time since the invite was last copied — LobbyOverlay shows brief feedback.</summary>
	public static RealTimeSince SinceCopied { get; private set; } = 999f;

	public static void Copy()
	{
		Clipboard.SetText( InviteUrl );
		SinceCopied = 0f;
	}
}
