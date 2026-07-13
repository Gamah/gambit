using System;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Orbits a named room light (default "RoomLight") around <see cref="OrbitCenter"/>
/// in the X-Z (vertical) plane for a day-night cycle: the light rides over the top
/// of the room ("day"), then swings down under the floor ("night") on the far side
/// of the orbit. The orbit axis is the room's Y axis, chosen so the light passes
/// directly beneath the room at the low point. Radius and starting phase are taken
/// from the light's authored position relative to the centre, so moving it in the
/// scene just works.
///
/// Lives on the Room GO and drives the separate RoomLight GameObject by name. It
/// moves that GO (carrying the emissive sun sphere along the orbit) and, since the
/// light is a DirectionalLight, aims it from the sun toward the room centre — sun
/// rays with global shadows, instead of a point light orbiting outside the walls
/// and leaking straight through them. Brightness fades by height (FadeByHeight) so
/// it dims to nothing at dusk; the corner streetlights own the night. Editor-safe
/// (ExecuteInEditor).
/// </summary>
public sealed class RoomLightOrbit : Component, Component.ExecuteInEditor
{
	/// <summary>Scene GameObject name of the light to orbit.</summary>
	[Property] public string LightName { get; set; } = "RoomLight";

	/// <summary>Seconds for one full day-night revolution. The single knob for the
	/// day/night cycle length.</summary>
	[Property] public float PeriodSeconds { get; set; } = 300f;

	/// <summary>World-space point the light orbits (room centre).</summary>
	[Property] public Vector3 OrbitCenter { get; set; } = Vector3.Zero;

	/// <summary>Draw a glowing sphere at the light to make it look like a sun.</summary>
	[Property] public bool ShowSun { get; set; } = true;

	/// <summary>Diameter of the sun sphere, in world units.</summary>
	[Property] public float SunDiameter { get; set; } = 220f;

	/// <summary>Sun colour. HDR values (>1) read as a glow with bloom.</summary>
	[Property] public Color SunColor { get; set; } = new Color( 5f, 3.8f, 2f );

	/// <summary>Floor-relative height at/below which the sun counts as "set" (night).</summary>
	[Property] public float HorizonZ { get; set; } = 0f;

	/// <summary>Fade the light's brightness by the sun's height instead of snapping it
	/// off: full at the top of the orbit, scaling linearly to 0 at the floor and
	/// staying off below it (negative Z ignored). Avoids the underground point light
	/// leaking up through the floor / walls — the streetlights own the night.</summary>
	[Property] public bool FadeByHeight { get; set; } = true;

	public static RoomLightOrbit Instance { get; private set; }

	/// <summary>True while the sun is above the horizon (daytime).</summary>
	public bool SunAboveHorizon => _lightGo.IsValid() && _lightGo.WorldPosition.z > HorizonZ;

	/// <summary>Current world Z of the sun, or NaN if the light isn't found yet.</summary>
	public float SunZ => _lightGo.IsValid() ? _lightGo.WorldPosition.z : float.NaN;

	Light _light;
	GameObject _lightGo;
	GameObject _sun;
	float _radius;
	float _phase;   // starting angle measured from +Z, toward +X
	float _y;       // height held constant (orbit lives in the X-Z plane)
	float _t;
	bool _orbitInit;
	Color _baseColor;       // authored full-brightness colour of the light
	bool _playMode;
	float _userMul = 1f;    // world-settings brightness slider
	int _appliedVersion = -1;

	protected override void OnEnabled()
	{
		Instance = this;
		_t = 0f;
		_orbitInit = false;
	}

	protected override void OnStart() => _playMode = true; // play mode only

	protected override void OnDisabled()
	{
		if ( _sun.IsValid() ) _sun.Destroy();
		_sun = null;
		_light = null;
		_lightGo = null;
	}

	/// <summary>Find and cache the light by name. Cached so it survives being disabled
	/// at night (Components.Get only returns ENABLED components — the source of the
	/// "turns off once, never back on" bug).</summary>
	bool EnsureLight()
	{
		if ( _lightGo.IsValid() ) return true;
		foreach ( var light in Scene.GetAllComponents<Light>() )
		{
			if ( light.GameObject.Name != LightName ) continue;
			_light = light;
			_lightGo = light.GameObject;
			return true;
		}
		return false;
	}

	void BuildSun()
	{
		if ( _sun.IsValid() ) _sun.Destroy();
		_sun = null;
		if ( !ShowSun || !_lightGo.IsValid() ) return;

		var model = Model.Load( "models/dev/sphere.vmdl" );
		if ( model == null )
		{
			Log.Warning( "[Gambit] models/dev/sphere.vmdl failed to load — no sun visual" );
			return;
		}

		_sun = new GameObject( true, "Sun" );
		_sun.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_sun.Parent = _lightGo; // tracks the light's transform automatically
		_sun.LocalPosition = Vector3.Zero;
		var b = model.Bounds.Size;
		_sun.WorldScale = new Vector3( SunDiameter / b.x, SunDiameter / b.y, SunDiameter / b.z );

		var renderer = _sun.AddComponent<ModelRenderer>();
		renderer.Model = model;
		// The sun sphere sits right at the orbiting directional light, so with default
		// shadow casting it projects its own silhouette as a big circular shadow into the
		// middle of the room. It's emissive set dressing — render it, but cast no shadow.
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		// Emissive material so the sphere is self-lit (a plain lit material renders
		// black: the point light sits at the sphere's centre, so its surface faces
		// away from the only light source). Tint multiplies the emission.
		var mat = Material.Load( "materials/dev/primary_white_emissive.vmat" );
		if ( mat != null ) renderer.MaterialOverride = mat;
		renderer.Tint = SunColor;
	}

	protected override void OnUpdate()
	{
		if ( !EnsureLight() ) return;

		if ( !_orbitInit )
		{
			var off = _lightGo.WorldPosition - OrbitCenter;
			_radius = MathF.Sqrt( off.x * off.x + off.z * off.z );
			_phase = MathF.Atan2( off.x, off.z );
			_y = _lightGo.WorldPosition.y;
			_baseColor = _light.IsValid() ? _light.LightColor : Color.White;
			BuildSun();
			_orbitInit = true;
		}

		_t += Time.Delta;
		if ( PeriodSeconds <= 0f ) return;

		float a = _phase + MathF.Tau * (_t / PeriodSeconds);
		var pos = new Vector3(
			OrbitCenter.x + _radius * MathF.Sin( a ),
			_y,
			OrbitCenter.z + _radius * MathF.Cos( a ) );
		_lightGo.WorldPosition = pos; // carries the sun sphere along the orbit

		// Aim the (directional) light from the sun toward the room centre, so it casts
		// parallel sun rays with global shadows — a point light orbiting outside the
		// walls leaks straight through them.
		var dir = OrbitCenter - pos;
		if ( dir.LengthSquared > 0.001f )
			_lightGo.WorldRotation = Rotation.LookAt( dir.Normal );

		ApplyBrightness();
	}

	/// <summary>Light brightness = authored base × world-settings slider × height fade
	/// (0 at/below the floor, 1 at the top of the orbit). Single writer of the light's
	/// colour, so SettingsWall no longer touches it.</summary>
	void ApplyBrightness()
	{
		if ( !_light.IsValid() ) return;

		// Pick up the world-settings brightness slider (play mode only).
		if ( _playMode && Gambit.UI.SettingsModel.SettingsVersion != _appliedVersion )
		{
			var data = Gambit.Game.PlayerData.Load();
			_userMul = Gambit.Game.PlayerData.ClampLightScale( data?.WorldLightBrightness ?? 1f );
			_appliedVersion = Gambit.UI.SettingsModel.SettingsVersion;
		}

		float fade = FadeByHeight ? Math.Clamp( SunZ / _radius, 0f, 1f ) : 1f;
		float f = _userMul * fade;
		_light.LightColor = new Color( _baseColor.r * f, _baseColor.g * f, _baseColor.b * f, _baseColor.a );
	}
}
