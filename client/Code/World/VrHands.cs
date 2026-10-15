using System.Collections.Generic;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// The controllers in the room, and the beam that points with them.
///
/// <para><b>Two hands, one pointer.</b> Both controllers are drawn, with the model the runtime
/// reports for the device in hand (<c>VRController.GetModel</c>), so there is no art to keep in
/// step with whatever headset is plugged in. The beam and the panel pointer belong to whichever
/// hand last pulled its trigger — <see cref="VrInput.Pointing"/> — because two live pointers would
/// need a priority rule for one hover state (the board's highlighted square, a panel's
/// <c>:hover</c>) and the answer to "which one wins" is "the one you just used".</para>
///
/// <para><b>The grip pose carries the model, the aim pose carries the pointer.</b> They are
/// different poses on the same controller: grip is the palm, aim is the direction the device
/// points, and OpenXR runtimes do not define them as the same thing. Drawing the model at the aim
/// pose would hold it at an odd angle; aiming from the grip pose would miss low.</para>
///
/// <para><b>Panels are pointed at through the engine's own <c>WorldInput</c></b>, which casts its
/// GameObject's forward and, in VR, reads the trigger as a left click — so the HUD quad and the
/// tabletop plate need nothing new. It must be the only one in the scene: the ray it feeds the UI
/// is a global, so a second one (the camera's, on the desktop) would fight it frame by frame. That
/// is why <see cref="LobbyPlayer"/> leaves the camera's off in VR.</para>
///
/// <para><b>Placed by <see cref="VrRig"/>, not by its own update</b> — the poses come off the
/// playspace anchor, so whatever sets the anchor has to be the thing that places anything derived
/// from it, or the hands trail the body by a frame while walking.</para>
/// </summary>
public sealed class VrHands : Component
{
	public static VrHands Instance { get; private set; }

	/// <summary>How far the beam reaches when it hits nothing, in world units.</summary>
	[Property] public float Reach { get; set; } = 160f;

	/// <summary>Beam thickness in world units.</summary>
	[Property] public float BeamWidth { get; set; } = 1.2f;

	/// <summary>HDR values (&gt;1) read as a glow with bloom, like the sun sphere's colour.</summary>
	[Property] public Color BeamColor { get; set; } = new Color( 1.4f, 2.6f, 3.4f );

	/// <summary>Scale for the stand-in box, when the runtime has no model for the device. A dev box
	/// is 50 units across, which is a crate in the hand rather than a controller.</summary>
	const float StandInScale = 0.12f;

	GameObject _left;
	GameObject _right;
	ModelRenderer _leftModel;
	ModelRenderer _rightModel;

	GameObject _pointer;
	LineRenderer _beam;
	WorldInput _input;

	protected override void OnStart()
	{
		Instance = this;

		_left = MakeHand( "VR Hand Left", out _leftModel );
		_right = MakeHand( "VR Hand Right", out _rightModel );

		_pointer = MakeLocalObject( "VR Pointer" );
		_input = _pointer.AddComponent<WorldInput>();

		_beam = _pointer.AddComponent<LineRenderer>();
		_beam.UseVectorPoints = true;
		_beam.Color = BeamColor;
		_beam.Width = BeamWidth;
		_beam.Face = SceneLineObject.FaceMode.Camera;

		// A beam that casts shadows is both wrong-looking and a shadow map we would be paying for
		// every frame it moves.
		_beam.CastShadows = false;
	}

	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;

		_left?.Destroy();
		_right?.Destroy();
		_pointer?.Destroy();
	}

	GameObject MakeHand( string name, out ModelRenderer model )
	{
		var go = MakeLocalObject( name );
		model = go.AddComponent<ModelRenderer>();
		return go;
	}

	/// <summary>A client-local object at the scene root. Not a child of the avatar: the avatar is
	/// networked, so a child of it rides the snapshot — the rule the tabletop plate is built
	/// around too — and these poses are world-space anyway.</summary>
	GameObject MakeLocalObject( string name )
	{
		var go = new GameObject( true, name )
		{
			Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked
		};
		go.NetworkMode = NetworkMode.Never;
		return go;
	}

	/// <summary>What this component actually built and placed, for the status board — "the hands
	/// exist" and "the hands are where the controllers are" are different claims, and only one of
	/// them is visible when nothing is drawn.</summary>
	public string StatusLine()
	{
		string models = $"{(_leftModel.IsValid() && _leftModel.Model != null ? "L" : "l")}"
			+ $"{(_rightModel.IsValid() && _rightModel.Model != null ? "R" : "r")}";

		return $"hands: objs={_left.IsValid()}/{_right.IsValid()} drawn={models}"
			+ $" placed={_placed} beam={(_beam.IsValid() && _beam.Enabled)}";
	}

	int _placed;

	/// <summary>Put both hands and the pointer where the controllers are, this frame. Called by
	/// <see cref="VrRig"/> once the playspace anchor is set.</summary>
	public void Place()
	{
		_placed++;

		var left = Input.VR.LeftHand;
		var right = Input.VR.RightHand;

		PlaceHand( _left, _leftModel, left.Transform, left.GetModel() );
		PlaceHand( _right, _rightModel, right.Transform, right.GetModel() );

		PlacePointer();
	}

	void PlaceHand( GameObject go, ModelRenderer renderer, Transform pose, Model model )
	{
		if ( !go.IsValid() ) return;

		go.WorldPosition = pose.Position;
		go.WorldRotation = pose.Rotation;

		if ( !renderer.IsValid() ) return;

		// A renderer with no model draws nothing at all, and the runtime reports none until the
		// device has finished waking up — so there is always something in hand, and the real model
		// replaces the stand-in box whenever it turns up.
		if ( model != null )
		{
			if ( renderer.Model != model )
			{
				renderer.Model = model;
				go.WorldScale = 1f;
			}

			return;
		}

		if ( renderer.Model == null )
		{
			renderer.Model = Model.Load( "models/dev/box.vmdl" );
			go.WorldScale = StandInScale;
		}
	}

	void PlacePointer()
	{
		if ( !_pointer.IsValid() ) return;

		// No tracked hand: the beam goes away and the pointer stops feeding the UI a ray it is
		// no longer aiming.
		if ( VrInput.AimRay is not { } ray )
		{
			if ( _beam.IsValid() ) _beam.Enabled = false;
			if ( _input.IsValid() ) _input.Enabled = false;
			return;
		}

		if ( _input.IsValid() ) _input.Enabled = true;

		_pointer.WorldPosition = ray.Position;
		_pointer.WorldRotation = Rotation.LookAt( ray.Forward );

		// The beam ends where the room does. Pieces, cells and panels have no colliders (the board
		// pick is plane maths, by design), so this lands on the table or the floor behind them
		// rather than on the square — the square's own highlight is the reticle, exactly as it is
		// for the desktop's look aim.
		var trace = Scene.Trace.Ray( ray, Reach )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.Run();

		float length = trace.Hit ? trace.Distance : Reach;

		if ( !_beam.IsValid() ) return;

		_beam.Enabled = true;
		_beam.Color = BeamColor;
		_beam.Width = BeamWidth;
		_beam.VectorPoints = new List<Vector3> { Vector3.Zero, Vector3.Forward * length };
	}
}
