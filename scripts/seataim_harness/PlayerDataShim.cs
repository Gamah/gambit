// The one thing SeatAim reads out of the real PlayerData. The real type pulls in
// FileSystem + System.Text.Json, neither of which exists off-engine; the flag it is asked
// for is a plain bool, so the harness supplies it directly.
namespace Gambit.Game;

public sealed class PlayerData
{
	public bool LookAimAtBoard { get; set; }

	public static PlayerData Current = new PlayerData();
	public static PlayerData Load() => Current;
}
