using System;
using Sandbox;

namespace Gambit.World;

/// <summary>The six chess piece types, in no particular order.</summary>
public enum ChessPieceType { Pawn, Knight, Bishop, Rook, Queen, King }

/// <summary>
/// Builds chess pieces (D5): first tries a real model at
/// models/chess/{type}.vmdl (the future Poly Haven import — a drop-in swap),
/// and falls back to a stack of tinted dev-box primitives matching the
/// codebase's all-procedural aesthetic. All dimensions are in base units and
/// multiplied by <c>scale</c>; a piece's local origin is the center of its
/// footprint at board-surface height (z=0), so callers just parent it and set
/// LocalPosition to the square center.
/// </summary>
public static class ChessSetBuilder
{
	// Piece tints: warm ivory vs near-black walnut. Both must read against both
	// square colors, so neither is pure white/black.
	public static readonly Color WhiteColor = new( 0.85f, 0.81f, 0.72f );
	public static readonly Color BlackColor = new( 0.09f, 0.07f, 0.06f );

	/// <summary>Standard back-rank piece order, queenside (file a) first.</summary>
	public static readonly ChessPieceType[] BackRank =
	{
		ChessPieceType.Rook, ChessPieceType.Knight, ChessPieceType.Bishop,
		ChessPieceType.Queen, ChessPieceType.King,
		ChessPieceType.Bishop, ChessPieceType.Knight, ChessPieceType.Rook,
	};

	/// <summary>Overall height of each fallback piece in base units — the model
	/// path scales its mesh to the same height so a future import keeps the
	/// board's proportions.</summary>
	public static float PieceHeight( ChessPieceType type ) => type switch
	{
		ChessPieceType.Pawn => 3.2f,
		ChessPieceType.Knight => 4.4f,
		ChessPieceType.Bishop => 4.8f,
		ChessPieceType.Rook => 4.0f,
		ChessPieceType.Queen => 5.6f,
		ChessPieceType.King => 6.4f,
		_ => 4f,
	};

	/// <summary>
	/// Build one piece under <paramref name="parent"/> and return its root GO.
	/// <paramref name="yaw"/> is the piece's facing (knights look at the enemy);
	/// pass the side's "toward the opponent" yaw.
	/// </summary>
	public static GameObject BuildPiece( GameObject parent, ChessPieceType type, bool white, float scale, float yaw = 0f )
	{
		var root = new GameObject( true, $"{( white ? "White" : "Black" )} {type}" );
		root.Parent = parent;
		root.LocalRotation = Rotation.FromYaw( yaw );

		var tint = white ? WhiteColor : BlackColor;

		// Drop-in real-model path (PLAN D5): import pieces as models/chess/*.vmdl
		// on the user's machine and the procedural fallback below never runs.
		var model = Model.Load( $"models/chess/{type.ToString().ToLowerInvariant()}.vmdl" );
		if ( model != null )
		{
			var renderer = root.AddComponent<ModelRenderer>();
			renderer.Model = model;
			renderer.Tint = tint;
			float h = model.Bounds.Size.z;
			if ( h > 0.01f )
				root.LocalScale = PieceHeight( type ) * scale / h;
			return root;
		}

		float s = scale;
		void Box( string name, Vector3 pos, Vector3 size, Rotation? rot = null ) =>
			AddBox( root, name, pos * s, size * s, tint, rot );

		switch ( type )
		{
			case ChessPieceType.Pawn:
				Box( "Base", new Vector3( 0, 0, 0.5f ), new Vector3( 1.9f, 1.9f, 1f ) );
				Box( "Head", new Vector3( 0, 0, 2.3f ), new Vector3( 1.2f, 1.2f, 1.8f ) );
				break;

			case ChessPieceType.Rook:
				Box( "Base", new Vector3( 0, 0, 0.45f ), new Vector3( 2.3f, 2.3f, 0.9f ) );
				Box( "Shaft", new Vector3( 0, 0, 2f ), new Vector3( 1.6f, 1.6f, 2.2f ) );
				Box( "Crown", new Vector3( 0, 0, 3.6f ), new Vector3( 2.3f, 2.3f, 0.8f ) );
				break;

			case ChessPieceType.Knight:
				Box( "Base", new Vector3( 0, 0, 0.45f ), new Vector3( 2.3f, 2.3f, 0.9f ) );
				// Neck leans toward the opponent (local +X = facing after yaw)
				Box( "Neck", new Vector3( -0.15f, 0, 2.2f ), new Vector3( 1.3f, 1.3f, 2.6f ),
					Rotation.FromPitch( -18f ) );
				Box( "Head", new Vector3( 0.75f, 0, 3.6f ), new Vector3( 2f, 1.2f, 1.1f ) );
				break;

			case ChessPieceType.Bishop:
				Box( "Base", new Vector3( 0, 0, 0.45f ), new Vector3( 2.3f, 2.3f, 0.9f ) );
				Box( "Shaft", new Vector3( 0, 0, 2.4f ), new Vector3( 1.3f, 1.3f, 3f ) );
				// 45°-rolled cube reads as the mitre point
				Box( "Mitre", new Vector3( 0, 0, 4.3f ), new Vector3( 1f, 1f, 1f ),
					Rotation.FromRoll( 45f ) );
				break;

			case ChessPieceType.Queen:
				Box( "Base", new Vector3( 0, 0, 0.5f ), new Vector3( 2.5f, 2.5f, 1f ) );
				Box( "Shaft", new Vector3( 0, 0, 2.7f ), new Vector3( 1.5f, 1.5f, 3.4f ) );
				Box( "Collar", new Vector3( 0, 0, 4.6f ), new Vector3( 2.1f, 2.1f, 0.5f ) );
				Box( "Crown", new Vector3( 0, 0, 5.2f ), new Vector3( 0.9f, 0.9f, 0.9f ),
					Rotation.FromRoll( 45f ) );
				break;

			case ChessPieceType.King:
				Box( "Base", new Vector3( 0, 0, 0.5f ), new Vector3( 2.5f, 2.5f, 1f ) );
				Box( "Shaft", new Vector3( 0, 0, 2.9f ), new Vector3( 1.6f, 1.6f, 3.8f ) );
				Box( "Collar", new Vector3( 0, 0, 5f ), new Vector3( 2.1f, 2.1f, 0.5f ) );
				Box( "CrossV", new Vector3( 0, 0, 5.9f ), new Vector3( 0.45f, 0.45f, 1.4f ) );
				Box( "CrossH", new Vector3( 0, 0, 6f ), new Vector3( 0.45f, 1.2f, 0.45f ) );
				break;
		}

		return root;
	}

	/// <summary>Same dev-box sizing trick as ChessRing.AddBox (box.vmdl is not 1×1×1),
	/// kept separate so the builder has no ring dependency.</summary>
	static void AddBox( GameObject parent, string name, Vector3 localPos, Vector3 size, Color tint, Rotation? localRot = null )
	{
		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null )
		{
			Log.Warning( "[Gambit] models/dev/box.vmdl failed to load — chess pieces have no visuals" );
			return;
		}

		var visual = new GameObject( true, name );
		visual.Parent = parent;
		visual.LocalPosition = localPos;
		visual.LocalRotation = localRot ?? Rotation.Identity;

		var modelSize = model.Bounds.Size;
		visual.LocalScale = new Vector3(
			size.x / modelSize.x,
			size.y / modelSize.y,
			size.z / modelSize.z );

		var renderer = visual.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = tint;
	}
}
