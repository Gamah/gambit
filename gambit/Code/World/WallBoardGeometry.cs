using Sandbox;

namespace Gambit.World;

/// <summary>
/// One shared physical size for every lobby wall <b>display board</b> — the east-wall info
/// board (CenterInfoPanel), the south-wall settings boards (WallSettingsPanel), and the
/// spectator walk-up board (SpectatorInfoPanel) — so they all read as the same-size sign on
/// the wall. The values are the ones the user calibrated on the info board; change them here
/// once and every board moves together.
///
/// <para>All three boards leave their <see cref="WorldPanel"/> at the default PanelSize (so
/// they share the same intrinsic pixel space and font scale) and apply <see cref="BoardScale"/>
/// as their GO LocalScale, and all three lay out their content <c>height:auto</c> and
/// floor-anchor the quad (see CenterInfoPanel's OnUpdate). That combination — not the GO
/// scale alone — is what makes them match.</para>
/// </summary>
public static class WallBoardGeometry
{
	/// <summary>Aspect stretch applied to the default (square) WorldPanel rect: portrait, width
	/// along local Y, height along local Z. CenterInfoPanel's fonts are calibrated to this
	/// stretch, so the shared boards reuse the same font sizes.</summary>
	public static readonly Vector3 Stretch = new( 1f, 1.8f, 2.4f );

	/// <summary>Master multiplier on top of <see cref="Stretch"/> — the single size knob the
	/// user tuned on the east-wall info board.</summary>
	public const float Master = 2.2f;

	/// <summary>GO LocalScale for a wall display board.</summary>
	public static Vector3 BoardScale => Stretch * Master;
}
