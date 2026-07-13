using Sandbox;

namespace Gambit.World;

/// <summary>
/// Single writer for a table's overhead SpotLight color: applies the persisted
/// user brightness multiplier (issue #49) on top of the ring's scene-tuned
/// MarqueeBrightness. Added to the light GO by ChessRing.BuildChessTable; not
/// ExecuteInEditor, so the editor preview keeps the static color ChessRing set
/// and the settings only apply in play.
/// </summary>
public sealed class MarqueeGlow : Component
{
	SpotLight _light;

	// Settings cache, shared across all table lights and refreshed when the settings
	// version bumps. The tint is hardcoded pure white; only brightness is tunable.
	static int _version = -1;
	static float _scale = 1f;

	protected override void OnUpdate()
	{
		_light ??= Components.Get<SpotLight>();
		if ( _light == null ) return;

		if ( _version != Gambit.UI.SettingsModel.SettingsVersion )
			RefreshSettings();

		float b = ( ChessRing.Instance?.MarqueeBrightness ?? 3.3f ) * _scale;
		_light.LightColor = new Color( b, b, b );
	}

	static void RefreshSettings()
	{
		_version = Gambit.UI.SettingsModel.SettingsVersion;
		var data = Gambit.Game.PlayerData.Load();
		_scale = Gambit.Game.PlayerData.ClampLightScale( data?.MarqueeLightBrightness ?? 1f );
	}
}
