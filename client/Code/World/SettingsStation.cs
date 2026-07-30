using Sandbox;

namespace Gambit.World;

/// <summary>
/// Engage target for a south-wall settings board (issue #49). Walking up and pressing
/// E opens the interactive SettingsScreen as a screen-space ScreenPanel — the camera
/// stays put (unlike the cabinets); LobbyPlayer just disables look controls so the
/// cursor is free. Purely local: no occupancy or networking. Created by SettingsWall
/// next to each WallSettingsPanel.
/// </summary>
public sealed class SettingsStation : Component
{
	// Music used to be a third kind here, with its own south-wall board. It isn't a place
	// you walk to any more — the soundtrack is N (board) / M (mute) from anywhere, driven
	// by Gambit.UI.MusicScreen.
	public enum StationKind { World, Host }

	/// <summary>The board the local player is currently locked onto, if any.</summary>
	public static new SettingsStation Active { get; private set; }

	/// <summary>Which board this is.</summary>
	[Property] public StationKind Kind { get; set; }

	/// <summary>True for the host-settings board. (Back-compat shim over Kind.)</summary>
	public bool Host
	{
		get => Kind == StationKind.Host;
		set => Kind = value ? StationKind.Host : StationKind.World;
	}

	/// <summary>Horizontal walk-up range for the "Press E" prompt.</summary>
	[Property] public float InteractRange { get; set; } = 130f;

	public void Enter() => Active = this;

	public void Leave()
	{
		if ( Active == this ) Active = null;
	}

	protected override void OnDestroy()
	{
		// Board rebuilt under an engaged player (room resize) — back out cleanly
		if ( Active == this )
			LobbyPlayer.Local?.Disengage();
	}
}
