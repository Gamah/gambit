using Sandbox;

namespace Rotaliate.World;

/// <summary>
/// Engage target for a north-wall leaderboard pair (issue #53). Walking up and pressing
/// E opens the interactive LeaderboardScreen as a screen-space ScreenPanel — the camera
/// stays put (unlike the cabinets); LobbyPlayer just disables look controls so the
/// cursor is free. Purely local: no occupancy or networking. Created by LeaderboardWall
/// for each of the three pairs.
/// </summary>
public sealed class LeaderboardStation : Component
{
	/// <summary>The station the local player is currently locked onto, if any.</summary>
	public static new LeaderboardStation Active { get; private set; }

	/// <summary>Which pair of boards this station covers (0=Daily, 1=Hourly, 2=Multi).</summary>
	[Property] public int PairIndex { get; set; }

	/// <summary>Horizontal walk-up range for the "Press E" prompt.</summary>
	[Property] public float InteractRange { get; set; } = 130f;

	public void Enter() => Active = this;

	public void Leave()
	{
		if ( Active == this ) Active = null;
	}

	protected override void OnDestroy()
	{
		if ( Active == this )
			LobbyPlayer.Local?.Disengage();
	}
}
