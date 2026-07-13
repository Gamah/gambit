using System.Collections.Generic;
using Sandbox;

namespace Rotaliate.World;

/// <summary>
/// Four box-primitive streetlights mounted at the top corners of the room walls
/// that pop on/off (no fade) around the orbiting sun's horizon crossing
/// (RoomLightOrbit). The toggle is directional with a margin: on the downstroke
/// they pop on a little BEFORE the sun reaches the horizon; on the upstroke they
/// pop off a little AFTER it has cleared the horizon. Each lamp carries a small
/// "bulb" sphere (a mini version of the sun) that glows while lit and dims to a
/// dark grey (still visible) when off.
/// Built in the editor-preview pattern (NotSaved GOs rebuilt in OnEnabled/
/// OnValidate). Lives on the Room GO.
/// </summary>
public sealed class Streetlights : Component, Component.ExecuteInEditor
{
	/// <summary>Small fixture height above the wall top.</summary>
	[Property] public float Height { get; set; } = 56f;

	/// <summary>Brightness of each lamp's PointLight — deliberately low.</summary>
	[Property] public float Brightness { get; set; } = 0.5f;

	/// <summary>Falloff radius — extends reach without raising brightness.</summary>
	[Property] public float Range { get; set; } = 3000f;

	/// <summary>How far in from each wall-top corner the poles sit, in world units.
	/// 0 = right at the wall edge.</summary>
	[Property] public float CornerInset { get; set; } = 0f;

	/// <summary>Diameter of the glowing bulb sphere.</summary>
	[Property] public float BulbDiameter { get; set; } = 22f;

	/// <summary>Bulb colour while lit. HDR (>1) reads as a glow with bloom.</summary>
	[Property] public Color BulbColor { get; set; } = new Color( 6f, 5f, 3f );

	/// <summary>Bulb colour while off — a dark grey so the sphere stays visible (not
	/// glowing) instead of vanishing.</summary>
	[Property] public Color BulbOffColor { get; set; } = new Color( 0.12f, 0.12f, 0.14f );

	/// <summary>Lamps are on while the sun's Z is below this multiple of the wall
	/// height (so they come on well before the sun reaches the walls and stay on
	/// past them).</summary>
	[Property] public float OnBelowWallFactor { get; set; } = 2f;

	LobbyRoom Room => Components.Get<LobbyRoom>();
	float RoomSize => Room?.RoomSize ?? 800f;
	float WallTop => Room?.WallHeight ?? 150f;

	GameObject _root;
	readonly List<(PointLight Lamp, ModelRenderer Bulb)> _fixtures = new();
	Vector2 _builtRoom = new( -1f, -1f );
	bool _lit = true;

	protected override void OnEnabled() => Rebuild();
	protected override void OnValidate() => Rebuild();
	public void RebuildPreview() => Rebuild();
	protected override void OnDisabled() => Clear();

	protected override void OnUpdate()
	{
		if ( _builtRoom != new Vector2( RoomSize, WallTop ) )
			Rebuild();

		var sun = RoomLightOrbit.Instance;
		if ( !sun.IsValid() ) return;

		float z = sun.SunZ;
		if ( float.IsNaN( z ) ) return;

		// On while the sun is below 120% of the wall height, off above it.
		bool night = z < WallTop * OnBelowWallFactor;
		if ( night == _lit ) return;
		_lit = night;
		foreach ( var (lamp, bulb) in _fixtures )
		{
			if ( lamp.IsValid() ) lamp.Enabled = night;
			// Bulb stays drawn either way; just dims to grey when off.
			if ( bulb.IsValid() ) bulb.Tint = night ? BulbColor : BulbOffColor;
		}
	}

	void Rebuild()
	{
		if ( !Active ) return;
		Clear();
		_builtRoom = new Vector2( RoomSize, WallTop );

		_root = new GameObject( true, "Streetlights" );
		_root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_root.Parent = GameObject;

		float c = RoomSize * 0.5f - CornerInset;
		float topZ = WallTop;
		BuildPole( "PoleNE", new Vector3( c, c, topZ ) );
		BuildPole( "PoleNW", new Vector3( -c, c, topZ ) );
		BuildPole( "PoleSE", new Vector3( c, -c, topZ ) );
		BuildPole( "PoleSW", new Vector3( -c, -c, topZ ) );
	}

	void BuildPole( string name, Vector3 basePos )
	{
		var pole = new GameObject( true, name );
		pole.Parent = _root;
		pole.LocalPosition = basePos;

		float h = Height;
		var bodyColor = new Color( 0.04f, 0.04f, 0.045f );
		AddBox( pole, "Pole", new Vector3( 0, 0, h * 0.5f ), new Vector3( 5, 5, h ), bodyColor );
		AddBox( pole, "Lamp", new Vector3( 0, 0, h + 5 ), new Vector3( 16, 16, 10 ), bodyColor );

		var lightGo = new GameObject( true, "Lamp" );
		lightGo.Parent = pole;
		lightGo.LocalPosition = new Vector3( 0, 0, h );
		var light = lightGo.AddComponent<PointLight>();
		float b = Brightness;
		// Warm white, matching the room light's tint ratio; kept dim on purpose.
		light.LightColor = new Color( b, b * 0.95f, b * 0.85f );
		// Radius extends the falloff distance without raising the brightness.
		light.Radius = Range;
		light.Shadows = true;
		light.Enabled = _lit;

		var bulb = AddBulb( pole, new Vector3( 0, 0, h ) );
		if ( bulb != null ) _fixtures.Add( (light, bulb) );
	}

	ModelRenderer AddBulb( GameObject parent, Vector3 localPos )
	{
		var model = Model.Load( "models/dev/sphere.vmdl" );
		if ( model == null ) return null;

		var bulb = new GameObject( true, "Bulb" );
		bulb.Parent = parent;
		bulb.LocalPosition = localPos;
		var m = model.Bounds.Size;
		bulb.WorldScale = new Vector3( BulbDiameter / m.x, BulbDiameter / m.y, BulbDiameter / m.z );

		var renderer = bulb.AddComponent<ModelRenderer>();
		renderer.Model = model;
		var mat = Material.Load( "materials/dev/primary_white_emissive.vmat" );
		if ( mat != null ) renderer.MaterialOverride = mat;
		// Always drawn; tint carries the on/off state (glow vs. dark grey).
		renderer.Tint = _lit ? BulbColor : BulbOffColor;
		return renderer;
	}

	static void AddBox( GameObject parent, string name, Vector3 localPos, Vector3 size, Color tint )
	{
		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null ) return;

		var visual = new GameObject( true, name );
		visual.Parent = parent;
		visual.LocalPosition = localPos;
		var m = model.Bounds.Size;
		visual.LocalScale = new Vector3( size.x / m.x, size.y / m.y, size.z / m.z );

		var renderer = visual.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = tint;
	}

	void Clear()
	{
		_fixtures.Clear();
		if ( _root.IsValid() )
			_root.Destroy();
		_root = null;
	}
}
