using Sandbox;

namespace Rotaliate.World;

/// <summary>
/// Single writer for a cabinet's marquee SpotLight color: applies the persisted
/// user tint and brightness multiplier (issue #49) on top of the ring's scene-tuned
/// MarqueeBrightness, modulated by <see cref="Duck"/>. CubeBoardView no longer
/// writes the light directly — it smooths Duck toward ArcadeRing.MarqueeDuck while
/// a cube board is out (#48) and back to 1 when the slot empties. Added to the
/// light GO by ArcadeRing.BuildCabinet; not ExecuteInEditor, so the editor preview
/// keeps the static color ArcadeRing set and the settings only apply in play.
/// </summary>
public sealed class MarqueeGlow : Component
{
	/// <summary>Brightness modulation 0..1, driven (pre-smoothed) by CubeBoardView.</summary>
	public float Duck { get; set; } = 1f;

	SpotLight _light;

	// Settings cache, shared across all marquees and refreshed when the settings
	// version bumps. The marquee tint is hardcoded pure white; only brightness is tunable.
	static int _version = -1;
	static readonly Color _tint = Color.White;
	static float _scale = 1f;

	protected override void OnUpdate()
	{
		_light ??= Components.Get<SpotLight>();
		if ( _light == null ) return;

		if ( _version != Rotaliate.UI.SettingsModel.SettingsVersion )
			RefreshSettings();

		float b = ( ArcadeRing.Instance?.MarqueeBrightness ?? 3.3f ) * _scale * Duck;
		_light.LightColor = new Color( _tint.r * b, _tint.g * b, _tint.b * b );
	}

	static void RefreshSettings()
	{
		_version = Rotaliate.UI.SettingsModel.SettingsVersion;
		var data = Rotaliate.Game.PlayerData.Load();
		_scale = Rotaliate.Game.PlayerData.ClampLightScale( data?.MarqueeLightBrightness ?? 1f );
	}
}
