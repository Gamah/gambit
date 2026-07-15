using Sandbox;

namespace Gambit.World;

/// <summary>
/// Engage target for an east-wall info board (the TERRY'S GAMBIT info panel and the dev-notes
/// panel). Walking up and pressing E opens the interactive InfoScreen as a screen-space
/// ScreenPanel — the camera stays put (like the leaderboard stations); LobbyPlayer just
/// disables look controls so the cursor is free (the Discord link is click-to-copy).
/// Purely local: no occupancy or networking. Created by InfoWall for each board.
/// </summary>
public sealed class InfoStation : Component
{
	public enum StationKind { Info, DevNotes }

	/// <summary>The station the local player is currently locked onto, if any.</summary>
	public static new InfoStation Active { get; private set; }

	/// <summary>Which board this station fronts.</summary>
	[Property] public StationKind Kind { get; set; }

	/// <summary>Horizontal walk-up range for the "Press E" prompt.</summary>
	[Property] public float InteractRange { get; set; } = 130f;

	public void Enter() => Active = this;

	public void Leave()
	{
		if ( Active == this ) Active = null;
		// Closing the welcome/info board counts as "seen" — the lobby stops auto-popping it.
		if ( Kind == StationKind.Info )
			Gambit.Game.PlayerData.MarkInfoPanelSeen();
	}

	protected override void OnDestroy()
	{
		if ( Active == this )
			LobbyPlayer.Local?.Disengage();
	}
}
