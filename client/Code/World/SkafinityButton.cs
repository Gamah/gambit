using Sandbox;
using Sandbox.UI;

namespace Gambit.World;

/// <summary>
/// Skafinity link copy helper — the third of the MORE board's outbound links, alongside
/// <see cref="DiscordButton"/> and <see cref="GamesButton"/>, and click-to-copy for the same
/// reason: there is no documented API to open a URL from game code.
/// </summary>
/// <remarks>
/// Skafinity is the library that writes the lobby's soundtrack (N opens its board, M mutes
/// it). It is a separate, open-source project that this game merely installs, so the credit
/// and the link belong on MORE with the other outbound links rather than buried in the music
/// board. Its own "just copied" clock, for the reason given on <see cref="GamesButton"/>.
/// </remarks>
public static class SkafinityButton
{
	/// <summary>The source repo. Shown verbatim as well as copied — never shorten it for
	/// display, or the board and the clipboard stop agreeing.</summary>
	public const string SkafinityUrl = "https://github.com/gamah/skafinity";

	/// <summary>Time since the link was last copied — LobbyOverlay shows brief feedback.</summary>
	public static RealTimeSince SinceCopied { get; private set; } = 999f;

	public static void Copy()
	{
		Clipboard.SetText( SkafinityUrl );
		SinceCopied = 0f;
	}
}
