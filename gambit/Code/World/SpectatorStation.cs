using Sandbox;

namespace Gambit.World;

/// <summary>
/// Engage target for the spectator wall (PLAN.md M5). Walking up and pressing E opens
/// the interactive SpectatorScreen as a screen-space ScreenPanel — the camera stays put
/// (like the info/settings boards); LobbyPlayer just frees the cursor so the channel
/// buttons work. Purely local: no occupancy or networking. Created by SpectatorWall.
/// </summary>
public sealed class SpectatorStation : Component
{
	/// <summary>The station the local player is currently locked onto, if any.</summary>
	public static new SpectatorStation Active { get; private set; }

	/// <summary>Horizontal walk-up range for the "Press E" prompt.</summary>
	[Property] public float InteractRange { get; set; } = 130f;

	public void Enter() => Active = this;

	public void Leave()
	{
		if ( Active == this ) Active = null;
	}

	protected override void OnDestroy()
	{
		// Board rebuilt under an engaged player (room resize) — back out cleanly.
		if ( Active == this )
			LobbyPlayer.Local?.Disengage();
	}
}
