using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.VR;

namespace Gambit.World;

/// <summary>
/// The controllers in the room, and the beam that points with them.
///
/// <para><b>Two hands, one pointer.</b> Both controllers are drawn as grip-sized primitives, and
/// the beam and the panel pointer belong to whichever hand last pulled its trigger — see
/// <see cref="VrInput.Pointing"/>. Two live pointers would need a priority rule for one hover
/// state (the board's highlighted square, a panel's <c>:hover</c>), and the answer to "which one
/// wins" is "the one you just used".</para>
///
/// <para><b>There is no device model to draw.</b> <c>VRController.GetModel</c> is a stub: it
/// returns <c>Model.Cube</c> — <c>models/dev/box.vmdl</c> — for every device, and never null. Drawn
/// unscaled at the grip pose, that is a 50-unit cube centred on the palm with the player's head
/// inside it, so every face is a backface, nothing renders and nothing is occluded either: the
/// room looks perfectly normal and the hands are simply absent. Primitives sized in world units
/// are the honest alternative until there are hands worth animating.</para>
///
/// <para><b>The grip pose carries the model, the aim pose carries the pointer.</b> They are
/// different poses on the same controller: grip is the palm, aim is the direction the device
/// points, and OpenXR runtimes do not define them as the same thing. Drawing the model at the aim
/// pose would hold it at an odd angle; aiming from the grip pose would miss low.</para>
///
/// <para><b>Panels are pointed at through the engine's own <c>WorldInput</c></b>, which casts its
/// GameObject's forward and, in VR, reads one named hand's trigger as a left click — so the HUD
/// quad and the tabletop plate need nothing new, as long as that named hand is kept in step with
/// the pointer (see <see cref="PlacePointer"/>). It must be the only one in the scene: the ray it
/// feeds the UI is a global, so a second one (the camera's, on the desktop) would fight it frame by
/// frame. That is why <see cref="LobbyPlayer"/> leaves the camera's off in VR.</para>
///
/// <para><b>Placed by <see cref="VrRig"/>, not by its own update</b> — the poses come off the
/// playspace anchor, so whatever sets the anchor has to be the thing that places anything derived
/// from it, or the hands trail the body by a frame while walking.</para>
/// </summary>
public sealed class VrHands : Component
{
	public static VrHands Instance { get; private set; }

	/// <summary>Draw the primitive grips even while the citizen's own hands are on the
	/// controllers (<c>gambit_vr_grips</c>). Off: a box inside the palm is a second answer to
	/// where the hand is.</summary>
	public static bool ForceGrips { get; private set; }

	/// <summary>Bring the primitive grips back on top of the citizen's hands.</summary>
	[ConCmd( "gambit_vr_grips" )]
	public static void ToggleGrips()
	{
		ForceGrips = !ForceGrips;
		Log.Info( $"[Gambit] vr: box grips {(ForceGrips ? "FORCED ON" : "off while IK hands drive")}" );
	}

	/// <summary>How far the beam reaches when it hits nothing, in world units.</summary>
	[Property] public float Reach { get; set; } = 160f;

	/// <summary>Beam thickness in world units.</summary>
	[Property] public float BeamWidth { get; set; } = 1.2f;

	/// <summary>HDR values (&gt;1) read as a glow with bloom, like the sun sphere's colour.</summary>
	[Property] public Color BeamColor { get; set; } = new Color( 1.4f, 2.6f, 3.4f );

	/// <summary>Grip size in world units — 1 unit is an inch, so this is about 5 x 5 x 13 cm, a
	/// chunky controller grip.</summary>
	static readonly Vector3 GripSize = new( 2f, 2f, 5f );

	/// <summary>Pointer tip size: small, bright, and at the aim pose rather than the grip, so which
	/// hand is pointing is visible even when the beam is pointing away from the eye.</summary>
	static readonly Vector3 TipSize = new( 1.2f, 1.2f, 1.2f );

	GameObject _left;
	GameObject _right;
	ModelRenderer _leftModel;
	ModelRenderer _rightModel;
	ModelRenderer _tip;

	GameObject _pointer;
	ModelRenderer _beam;
	WorldInput _input;

	/// <summary>The dev box's own size, so everything built from it can be scaled to world units
	/// without loading the model again per frame.</summary>
	Vector3 _boxSize = new( 50f, 50f, 50f );

	/// <summary>Why there are no hands, when there are no hands. A component that throws in
	/// <c>OnStart</c> leaves no trace a headset can see: the objects are simply absent, which looks
	/// exactly like never having been asked for. The state board reads this.</summary>
	public static string LastError { get; private set; }

	/// <summary>What this component built, readable whether or not it exists — "no component" and
	/// "a component that built nothing" are different faults with the same symptom.</summary>
	public static string Status()
	{
		if ( Instance == null ) return $"hands=NONE{(LastError == null ? "" : $" err: {LastError}")}";

		return Instance.StatusLine() + (LastError == null ? "" : $" err: {LastError}");
	}

	protected override void OnStart()
	{
		Instance = this;

		try
		{
			Build();
		}
		catch ( Exception e )
		{
			LastError = e.Message;
			Log.Warning( e, "[Gambit] vr: hands failed to build" );
		}
	}

	void Build()
	{
		_left = MakeHand( "VR Hand Left", out _leftModel );
		_right = MakeHand( "VR Hand Right", out _rightModel );

		_pointer = MakeLocalObject( "VR Pointer" );
		_tip = AddBox( _pointer, "Tip", TipSize, new Color( 2.4f, 3.2f, 4f ) );
		_input = _pointer.AddComponent<WorldInput>();

		// The beam is a stretched box, not a LineRenderer. Everything that renders in this scene is
		// a tinted dev box normalised by its bounds, including the grips and the tip above, and a
		// beam built any other way is a second rendering path to be wrong in — which it was, twice
		// over, while a box of the same dimensions could not be.
		_beam = AddBox( _pointer, "Beam", new Vector3( 1f, BeamWidth, BeamWidth ), BeamColor );
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
		model = AddBox( go, "Grip", GripSize, new Color( 0.08f, 0.08f, 0.09f ) );
		return go;
	}

	/// <summary>A box of a given world size, the way every primitive in this scene is built: the
	/// dev box normalised by its own bounds, tinted, on a child object so the hand's own transform
	/// stays the controller pose and nothing has to undo a scale to read it.</summary>
	ModelRenderer AddBox( GameObject parent, string name, Vector3 size, Color tint )
	{
		var model = Model.Load( "models/dev/box.vmdl" );
		if ( model == null ) return null;

		var go = new GameObject( true, name ) { Parent = parent };
		_boxSize = model.Bounds.Size;
		go.LocalScale = new Vector3( size.x / _boxSize.x, size.y / _boxSize.y, size.z / _boxSize.z );

		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = tint;
		return renderer;
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

	// Granular enough for a verdict, so nothing has to parse StatusLine's string to find out
	// which step failed.
	public static bool Built => Instance?._left.IsValid() ?? false;
	public static int Placed => Instance?._placed ?? 0;
	public static bool Drawn => (Instance?._leftModel.IsValid() ?? false)
		&& Instance._leftModel.Enabled && Instance._leftModel.Model != null;
	public static bool BeamOn => (Instance?._beam.IsValid() ?? false) && Instance._beam.Enabled;

	/// <summary>The beam is over something a click would land on.
	///
	/// <para><b>What makes this a usable answer is which panels it counts.</b>
	/// <c>WorldPanelInput.Tick</c> only ray-tests roots whose <c>ChildrenWantMouseInput</c> is
	/// set, and only sets <c>Hovered</c> from one that takes the mouse — so a display board (the
	/// wall signs, the seated board, every panel built <c>pointer-events: none</c>) is not
	/// hovered however squarely the beam crosses it. This is "the beam is on a button", not "the
	/// beam is on a panel".</para>
	///
	/// <para>It is last frame's: <c>WorldInput</c> writes it in its own update, after
	/// <see cref="VrInput"/>'s. For deciding what a press means that is the right frame — a
	/// hover held long enough to aim a trigger at was already there.</para></summary>
	public static bool PointerHovering =>
		(Instance?._input.IsValid() ?? false) && Instance._input.Enabled
			&& Instance._input.Hovered != null;

	/// <summary>Put both hands and the pointer where the controllers are, this frame. Called by
	/// <see cref="VrRig"/> once the playspace anchor is set.</summary>
	public void Place()
	{
		try
		{
			PlaceAll();
		}
		catch ( Exception e )
		{
			LastError = e.Message;
		}
	}

	void PlaceAll()
	{
		_placed++;

		var left = Input.VR.LeftHand;
		var right = Input.VR.RightHand;

		// A controller that stopped reporting holds its last pose, and a grip left floating in the
		// room is worse than no grip: it says "tracking" when nothing is.
		//
		// And a grip is only drawn while there is no HAND there: seated, the citizen's own arms
		// are on the controllers (LobbyPlayer.ApplyVrHandIk) and a box inside the palm is a
		// second answer to where the hand is. The pointer and its beam are a separate object and
		// stay either way — they are what the player aims with.
		// ForceGrips is the way back from inside the headset: if the citizen's arms are not where
		// its hands should be — an IK chain that didn't take, a wrist offset still a guess — the
		// alternative to a wrong hand must not be NO hand.
		bool grips = ForceGrips || !LobbyPlayer.VrHandsDriving;
		PlaceHand( _left, left.Transform, left.Active && grips );
		PlaceHand( _right, right.Transform, right.Active && grips );

		PlacePointer();

		// A frame that got all the way here had nothing to report; leaving a stale message on the
		// board would pin it at THREW over one transient throw.
		LastError = null;
	}

	static void PlaceHand( GameObject go, Transform pose, bool show )
	{
		if ( !go.IsValid() ) return;

		go.Enabled = show;
		if ( !show ) return;

		go.WorldPosition = pose.Position;
		go.WorldRotation = pose.Rotation;
	}

	void PlacePointer()
	{
		if ( !_pointer.IsValid() ) return;

		// No tracked hand: the beam goes away and the pointer stops feeding the UI a ray it is
		// no longer aiming.
		if ( VrInput.AimRay is not { } ray )
		{
			if ( _beam.IsValid() ) _beam.Enabled = false;
			if ( _tip.IsValid() ) _tip.Enabled = false;
			if ( _input.IsValid() ) _input.Enabled = false;
			return;
		}

		if ( _input.IsValid() )
		{
			_input.Enabled = true;

			// WHICH TRIGGER CLICKS IS A SEPARATE SETTING FROM WHERE THE RAY POINTS, and it defaults
			// to Left. WorldInput casts this object's forward, which follows the pointing hand, but
			// takes the VR click off Input.VR.<VRHandSource>.Trigger. Left unset, a right-handed
			// player hovers a button and nothing ever presses it. Nothing else can produce that
			// symptom: a world-panel hit is ray-to-plane maths over the root panels
			// (WorldPanelInput.Tick), so no model, collider or beam geometry can occlude a click.
			_input.VRHandSource = VrInput.Pointing == VrInput.Hand.Left
				? VRHand.HandSources.Left
				: VRHand.HandSources.Right;
		}

		if ( _tip.IsValid() ) _tip.Enabled = true;

		_pointer.WorldPosition = ray.Position;
		_pointer.WorldRotation = Rotation.LookAt( ray.Forward );

		// The beam ends where the room does. Pieces, cells and panels have no colliders (the board
		// pick is plane maths, by design), so this lands on the table or the floor behind them
		// rather than on the square — the square's own highlight is the reticle, exactly as it is
		// for the desktop's look aim.
		var trace = Scene.Trace.Ray( ray, Reach )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.Run();

		// Clamped: a ray that starts inside a collider (an arm through the table cabinet) traces
		// zero, and two identical points are a line with no extent — drawn, enabled, invisible.
		float length = MathF.Max( trace.Hit ? trace.Distance : Reach, 6f );

		if ( !_beam.IsValid() ) return;

		_beam.Enabled = true;
		_beam.Tint = BeamColor;

		// The box's own +X is the aim direction (the pointer object looks along the ray), so it is
		// stretched along X and pushed half its length forward to start at the hand.
		var box = _beam.GameObject;
		box.LocalScale = new Vector3( length / _boxSize.x, BeamWidth / _boxSize.y, BeamWidth / _boxSize.z );
		box.LocalPosition = new Vector3( length * 0.5f, 0f, 0f );
	}
}
