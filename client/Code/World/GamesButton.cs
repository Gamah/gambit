using Sandbox;
using Sandbox.UI;

namespace Gambit.World;

/// <summary>
/// "Our other games" link copy helper — the sibling of <see cref="DiscordButton"/>, and for
/// the same reason: there is no documented API to open a URL or the Steam overlay from game
/// code, but <c>Clipboard.SetText</c> works, so every outbound link in this game is
/// click-to-copy. Both live on the MORE board (south wall, where the music board used to be);
/// <see cref="SinceCopied"/> drives the same brief LobbyOverlay confirmation the invite does.
/// </summary>
/// <remarks>
/// A separate static rather than a second pair of members on <see cref="DiscordButton"/>: the
/// two have independent "just copied" clocks, and a shared one would flash the wrong
/// confirmation for whichever button was not pressed. Same shape as <c>LichessButton</c>,
/// which already carries two of them for the same reason.
/// </remarks>
public static class GamesButton
{
	/// <summary>The publisher page, filtered to games. Shown verbatim as well as copied — a
	/// player who cannot open a link has to be able to read the whole thing off the board, so
	/// this must never become a display string that differs from what lands on the clipboard.</summary>
	public const string GamesUrl = "https://sbox.game/gamah/~packages?type=game";

	/// <summary>Time since the link was last copied — LobbyOverlay shows brief feedback.</summary>
	public static RealTimeSince SinceCopied { get; private set; } = 999f;

	public static void Copy()
	{
		Clipboard.SetText( GamesUrl );
		SinceCopied = 0f;
	}
}
