using System.Collections.Generic;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Builds the lobby room geometry at runtime: a floor and four walls, no ceiling.
/// Generated objects are flagged NotSaved so editor preview never leaks into the scene file.
/// Constraints (learned the hard way):
/// - non-uniform Scale in scene JSON is silently misread, so geometry is built in C#
/// - a BoxCollider on a GameObject with non-uniform scale freezes the physics engine,
///   so colliders live on uniformly-scaled parents with BoxCollider.Scale set explicitly,
///   and the visual ModelRenderer lives on a non-uniformly-scaled child with no collider
/// </summary>
public sealed class LobbyRoom : Component, Component.ExecuteInEditor
{
	[Property] public Color WallColor { get; set; } = new Color( 0.26f, 0.26f, 0.26f );

	/// <summary>Interior floor size in units (1 unit ≈ 1 inch; 240 ≈ a 20ft bedroom).</summary>
	[Property] public float RoomSize { get; set; } = 240f;
	[Property] public float WallHeight { get; set; } = 80f;
	[Property] public float Thickness { get; set; } = 10f;

	readonly List<GameObject> _spawned = new();

	protected override void OnEnabled()
	{
		Rebuild();
		EnsureChessRing();
	}

	/// <summary>Self-heal for the M1 rename: loading a scene saved with the old
	/// ArcadeRing drops it as a missing component, leaving the room with no ring at
	/// all ("BOARDS — 0"). The ring belongs on this GO (ChessRing.RingRadius reads
	/// LobbyRoom.RoomSize from it), so create it here if it's absent — in the editor
	/// this dirties the scene and saving persists it; in play it just works.</summary>
	void EnsureChessRing()
	{
		if ( Components.Get<ChessRing>( FindMode.EverythingInSelf ) == null )
			GameObject.AddComponent<ChessRing>();
	}

	// Fires on editor property changes and after deserialization (scene load),
	// so the room regenerates in the editor without entering play mode
	protected override void OnValidate() => Rebuild();

	/// <summary>Re-run the build after a code hotload (Editor/HotloadRebuild.cs) —
	/// without this, geometry-code changes show stale output until a scene reload.</summary>
	public void RebuildPreview() => Rebuild();

	void Rebuild()
	{
		if ( !Active ) return;
		Clear();

		float half = RoomSize * 0.5f;
		float wallZ = WallHeight * 0.5f;
		float outer = RoomSize + Thickness * 2f;

		// Floor top sits at Z=0
		BuildBox( "Floor", new Vector3( 0, 0, -Thickness * 0.5f ), new Vector3( outer, outer, Thickness ) );

		BuildBox( "WallEast", new Vector3( half + Thickness * 0.5f, 0, wallZ ), new Vector3( Thickness, outer, WallHeight ) );
		BuildBox( "WallWest", new Vector3( -half - Thickness * 0.5f, 0, wallZ ), new Vector3( Thickness, outer, WallHeight ) );
		BuildBox( "WallNorth", new Vector3( 0, half + Thickness * 0.5f, wallZ ), new Vector3( outer, Thickness, WallHeight ) );
		BuildBox( "WallSouth", new Vector3( 0, -half - Thickness * 0.5f, wallZ ), new Vector3( outer, Thickness, WallHeight ) );
	}

	protected override void OnDisabled() => Clear();

	void BuildBox( string name, Vector3 localPos, Vector3 size )
	{
		// Collider GO: uniform scale, explicit BoxCollider.Scale, and NO scaled
		// descendants — physics aggregates colliders across a GO's hierarchy, so the
		// non-uniformly scaled visual must live entirely outside this object
		var colliderGo = new GameObject( true, name );
		colliderGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		colliderGo.Parent = GameObject;
		colliderGo.LocalPosition = localPos;

		var collider = colliderGo.AddComponent<BoxCollider>();
		collider.Scale = size;
		_spawned.Add( colliderGo );

		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null )
		{
			Log.Warning( "[Gambit] models/dev/box.vmdl failed to load — lobby room has colliders but no visuals" );
			return;
		}

		// Visual GO: sibling of the collider at the same position, scaled to match
		var visual = new GameObject( true, $"{name}_Visual" );
		visual.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		visual.Parent = GameObject;
		visual.LocalPosition = localPos;

		var modelSize = model.Bounds.Size;
		visual.LocalScale = new Vector3(
			size.x / modelSize.x,
			size.y / modelSize.y,
			size.z / modelSize.z );

		var renderer = visual.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = WallColor;

		_spawned.Add( visual );
	}

	void Clear()
	{
		foreach ( var go in _spawned )
		{
			if ( go.IsValid() )
				go.Destroy();
		}
		_spawned.Clear();
	}
}
