using System;
using Sandbox;
using Sandbox.VR;

namespace Gambit.World;

/// <summary>
/// Puts a headset on the desktop rig without changing the controls: the playspace anchor rides
/// the player's feet while roaming and the chess seat while seated, and the camera renders stereo
/// from the HMD pose. Nothing here reads a VR controller — mouse and keyboard still drive
/// movement, the cursor and every panel.
///
/// <para>Added at runtime by <see cref="LobbyPlayer"/> on the local player when
/// <c>Sandbox.Game.IsRunningInVR</c> (plain <c>Game</c> here is the <c>Gambit.Game</c> namespace),
/// so nothing VR has to be authored into the scene and the desktop
/// path is untouched when no headset is present.</para>
///
/// <para><b>The anchor is a FLOOR origin, never the eye.</b> <c>Input.VR.Head</c> is the headset
/// pose inside the playspace, so an anchor placed at the eye point would stack the player's real
/// height on top of it. Placing it at the floor (roaming) or at the seat's eye point minus the
/// head offset captured at sit-down (seated) is what keeps positional tracking live: leaning in
/// and standing up are real movement, not a camera that only rotates.</para>
///
/// <para><b>The camera is written in <c>OnPreRender</c>, which is what makes it the view.</b> A
/// camera moved after the view composes "stomps the composition and renders from its raw values"
/// (<c>CameraComponent.UpdateSceneCameraTransform</c>), and composition runs after Update and
/// before PreRender. So this beats <c>PlayerController.ModifyCamera</c> and
/// <see cref="LobbyPlayer.UpdateLockedCamera"/> without either of them having to know about VR —
/// and the engine's own <c>VRTrackedObject</c> is not used, so the anchor is always updated
/// before the pose that reads it, rather than depending on component order.</para>
///
/// <para><b>Seated is always the 3D seat anchor.</b> The 2D nadir anchor looks straight down,
/// where a yaw decomposition is the Euler singularity and means nothing; there is no playspace
/// orientation to derive from it. 2D play mode is a desktop view.</para>
/// </summary>
public sealed class VrRig : Component
{
	/// <summary>The yaw the player's BODY faces: the controller's eye angles, which the headset
	/// never writes to. Head rotation moves the playspace anchor and nothing else, so this and
	/// where the player is looking are two different directions — which is the whole reason
	/// <see cref="VrInput"/> has to rotate the walk axis between them.</summary>
	public static float BodyYaw => _instance?._controller?.EyeAngles.yaw ?? 0f;

	/// <summary>True while a local VR rig is driving the view. Read by
	/// <see cref="LobbyPlayer"/> to keep our own avatar out of our own headset.</summary>
	public static bool Active { get; private set; }

	static VrRig _instance;

	/// <summary>The camera this drives — the local player's, handed over by
	/// <see cref="LobbyPlayer"/>, which is the one thing that knows which that is.</summary>
	public GameObject CameraObject { get; set; }

	LobbyPlayer _player;
	PlayerController _controller;
	CameraComponent _companion;
	GameObject _board;

	/// <summary>The state board, off until asked for (<c>gambit_vr_debug</c>). It is an instrument,
	/// not furniture: it hangs in front of the player and covers the room it is reporting on.</summary>
	static bool DebugBoard;

	/// <summary>Where the state board hangs, in world units.</summary>
	static float BoardDistance = 170f;
	static float BoardHeight = 20f;

	// The headset pose inside the playspace at sit-down, used to land the player's real seated
	// pose on the seat's eye point: position so the board is in front of them wherever their
	// chair is, yaw so facing forward in that chair faces the board.
	Vector3 _seatHead;
	float _seatHeadYaw;
	bool _seatCaptured;
	GameObject _seatAnchor;

	protected override void OnStart()
	{
		_player = Components.Get<LobbyPlayer>();
		_controller = Components.Get<PlayerController>();
		_instance = this;
		Active = true;

		// First person, and both eyes. A camera left at StereoTargetEye.None renders only to the
		// companion window: the headset shows nothing at all.
		if ( _controller.IsValid() ) _controller.ThirdPerson = false;
		if ( CameraObject.IsValid() && CameraObject.Components.Get<CameraComponent>() is { } cam )
			cam.TargetEye = StereoTargetEye.Both;

		EnsureCompanion();
	}

	/// <summary>A flat camera for the monitor, following the headset.
	///
	/// <para><b>Without it the screen is black.</b> A camera whose <c>TargetEye</c> is not
	/// <c>None</c> takes <c>SceneCamera.RenderStereo</c>, which renders both eyes for the
	/// compositor and never touches the swap chain — so the one camera that can show the headset's
	/// view to the room cannot be the one driving the headset. This is what makes the spike
	/// watchable by anyone not wearing it, including over a screen share.</para>
	///
	/// <para>Never <c>Scene.Camera</c>: that is the main camera first and the lowest
	/// <c>Priority</c> after (<c>Scene.UpdateMainCamera</c>), so this one is not main and sits far
	/// behind. <c>Scene.Camera</c> has to stay the player's — <c>WorldInput</c>'s cursor ray,
	/// <c>ScreenPixelToRay</c> and the board's own picking all read it.</para></summary>
	void EnsureCompanion()
	{
		if ( _companion.IsValid() || !CompanionWanted ) return;

		var go = new GameObject( true, "VR Companion" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked
		};

		_companion = go.AddComponent<CameraComponent>();
		_companion.IsMainCamera = false;
		_companion.Priority = 16;
		_companion.TargetEye = StereoTargetEye.None;

		// The authored camera owns how the lobby looks (FOV 75, the clear colour behind the
		// skybox); a default camera next to it would show a differently framed, differently
		// cleared room and read as a bug in the scene.
		if ( CameraObject.Components.Get<CameraComponent>() is { } main )
		{
			_companion.FieldOfView = main.FieldOfView;
			_companion.FovAxis = main.FovAxis;
			_companion.ZNear = main.ZNear;
			_companion.ZFar = main.ZFar;
			_companion.ClearFlags = main.ClearFlags;
			_companion.BackgroundColor = main.BackgroundColor;
		}

		// Our own body is drawn for everyone else, and in VR the monitor is looking out of our
		// head: the same exclusion the controller puts on the first-person camera.
		_companion.RenderExcludeTags.Add( "viewer" );
	}

	protected override void OnDestroy()
	{
		if ( _instance == this )
		{
			_instance = null;
			Active = false;
		}
	}

	/// <summary>Why the rig is not doing what it should, when it isn't. An exception thrown per
	/// frame here is invisible in a headset, and the state board is the only place that can say
	/// so.</summary>
	public static string LastError { get; private set; }

	protected override void OnUpdate() => Guard();
	protected override void OnPreRender() => Guard();

	void Guard()
	{
		try
		{
			Drive();
		}
		catch ( Exception e )
		{
			LastError = e.Message;
		}
	}

	void Drive()
	{
		if ( !Sandbox.Game.IsRunningInVR ) return;

		// Built here rather than once at spawn: LobbyPlayer.OnStart runs once per spawn, so a VR
		// component added by a code hotload into a running session is never asked for and fails
		// silently — the same trap as a GameObjectSystem added mid-session (docs/engine.md).
		if ( VrHands.Instance == null ) Components.GetOrCreate<VrHands>();

		UpdateAnchor();

		// Input.VR.Head is already anchor-to-world, so this IS the pose the anchor just defined.
		var head = Input.VR.Head;
		if ( CameraObject.IsValid() )
		{
			CameraObject.WorldPosition = head.Position;
			CameraObject.WorldRotation = head.Rotation;
		}

		if ( _companion.IsValid() )
		{
			_companion.WorldPosition = head.Position;
			_companion.WorldRotation = head.Rotation;
		}

		// Everything else that rides the playspace is placed from here, in this order, because
		// every one of those poses is the anchor's: a component placing itself in its own update
		// would read whichever anchor the frame happened to have when it ran.
		VrHands.Instance?.Place();
		PlaceDebugBoard();

		// The pointer is a physical object now. The cursor has to be out of the way for that: a
		// visible cursor sends WorldInput down its camera-ray branch (and the engine zeroes
		// AnalogLook while one is up), so the hand would stop pointing the moment a panel with a
		// button appeared. Written every frame rather than once, because it is a global that the
		// seat's aim toggle and the music screen both set for their own reasons.
		Mouse.Visibility = MouseVisibility.Hidden;
	}

	void UpdateAnchor()
	{
		var station = ChessStation.Active;
		var seat = station?.SeatAnchor( ChessStation.ActiveSeat );

		if ( seat.IsValid() )
		{
			SeatAnchor( seat );
			return;
		}

		_seatCaptured = false;
		_seatAnchor = null;

		// Engaged at a wall board: the desktop camera flies to the board, but a VR player walked
		// up to it and is standing there. Leave the playspace where it is — moving it would take
		// the room out from under them to show them a screen panel they can already read.
		if ( _player.IsValid() && _player.Engaged ) return;

		// Roaming: the playspace sits on the player's feet, turned by the mouse. Head yaw rides
		// on top of the controller's, which is the point of fixing the headset to the existing
		// controller: WASD still goes where the mouse is pointing.
		float yaw = _controller.IsValid() ? _controller.EyeAngles.yaw : WorldRotation.Angles().yaw;
		Input.VR.Anchor = new Transform( WorldPosition, Rotation.FromYaw( yaw ) );
	}

	void SeatAnchor( GameObject seat )
	{
		// The seat anchor's forward is pitched down at the board; only its yaw is a playspace
		// orientation. A degenerate forward (a straight-down anchor) keeps the last yaw rather
		// than snapping the room to zero.
		var fwd = seat.WorldRotation.Forward.WithZ( 0f );
		float yaw = fwd.LengthSquared > 0.0001f ? fwd.EulerAngles.yaw : Input.VR.Anchor.Rotation.Angles().yaw;
		var rot = Rotation.FromYaw( yaw );

		// Capture once per seat, so what follows is real movement rather than a view that
		// re-centres under the player every frame.
		if ( !_seatCaptured || _seatAnchor != seat )
		{
			var local = Input.VR.Anchor.ToLocal( Input.VR.Head );
			_seatHead = local.Position;
			_seatHeadYaw = local.Rotation.Angles().yaw;
			_seatCaptured = true;
			_seatAnchor = seat;
		}

		// Undo the captured head yaw, then place the floor origin so that captured head position
		// lands on the seat's eye point.
		rot *= Rotation.FromYaw( -_seatHeadYaw );
		Input.VR.Anchor = new Transform( seat.WorldPosition - rot * _seatHead, rot );
	}

	/// <summary>Whether the monitor gets a flat view of what the headset sees. On by default:
	/// without it the screen is black while someone is in the headset.</summary>
	static bool CompanionWanted = true;

	/// <summary>Toggle the monitor's flat camera. It renders the scene a second time, so this is
	/// the switch to reach for when the headset's frame rate is the thing being measured.</summary>
	[ConCmd( "gambit_vr_companion" )]
	public static void ToggleCompanion()
	{
		CompanionWanted = !CompanionWanted;

		if ( _instance == null )
		{
			Log.Info( $"[Gambit] vr: companion camera {(CompanionWanted ? "ON" : "OFF")} (no rig yet)" );
			return;
		}

		if ( CompanionWanted )
		{
			_instance.EnsureCompanion();
		}
		else if ( _instance._companion.IsValid() )
		{
			_instance._companion.GameObject.Destroy();
			_instance._companion = null;
		}

		Log.Info( $"[Gambit] vr: companion camera {(CompanionWanted ? "ON" : "OFF")}" );
	}

	/// <summary>Where the player is looking, as a body rather than as a head: a point to hang
	/// something in front of, and a horizontal direction to hang it along. Seated, that is the seat
	/// anchor's yaw (the anchor itself is pitched steeply down at the board); roaming, it is the
	/// controller's eye angles — NOT the headset's, so that a glance does not drag the world's
	/// furniture around with it.
	///
	/// <para>One place decides this, because two things hang off it (the HUD quad and the debug
	/// board) and a panel that disagreed with the other about where "in front" is would look like a
	/// bug in whichever one you noticed second.</para>
	///
	/// <para><paramref name="flattenSeated"/> false keeps the seat anchor's own PITCH, which is the
	/// whole seated view direction rather than the body's: the anchor is aimed at the board centre,
	/// so a quad hung along it lands centred on the board the same way the desktop screen does.
	/// Anything hung at a fixed height in the room wants the flattened direction instead, which is
	/// why this is a choice and not a change.</para></summary>
	public static bool ViewAnchor( PlayerController controller, GameObject camera,
		out Vector3 from, out Vector3 forward, bool flattenSeated = true )
	{
		from = default;
		forward = default;

		var station = ChessStation.Active;
		var seat = station?.SeatAnchor( ChessStation.ActiveSeat );

		if ( seat.IsValid() )
		{
			from = seat.WorldPosition;
			forward = seat.WorldRotation.Forward;

			if ( !flattenSeated )
			{
				if ( forward.LengthSquared < 0.0001f ) return false;
				forward = forward.Normal;
				return true;
			}
		}
		else if ( controller.IsValid() && camera.IsValid() )
		{
			from = controller.WorldPosition.WithZ( camera.WorldPosition.z );
			forward = controller.EyeAngles.ToRotation().Forward;
		}
		else
		{
			return false;
		}

		forward = forward.WithZ( 0f );
		if ( forward.LengthSquared < 0.0001f ) return false;

		forward = forward.Normal;
		return true;
	}

	/// <summary>The rig's state, on a board in the room. A headset has no console in it, and every
	/// VR fact worth knowing is only true while one is being worn.
	///
	/// <para><b>It is built here, by the component whose own symptom proves it runs.</b> A tracked
	/// view means this code is executing; so if the board appears, every line on it can be
	/// believed, including the lines that say the input system or the hands are missing. Hosting it
	/// on either of those would make it silent in exactly the case it exists for.</para></summary>
	void PlaceDebugBoard()
	{
		if ( !DebugBoard )
		{
			_board?.Destroy();
			_board = null;
			return;
		}

		if ( !_board.IsValid() )
		{
			_board = new GameObject( true, "VR Debug Board" )
			{
				Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked
			};
			_board.NetworkMode = NetworkMode.Never;

			// The wall boards' size, by the repo's own contract: default PanelSize, content
			// height:auto, BoardScale as the GO scale. Nothing here invents a scale.
			_board.LocalScale = WallBoardGeometry.BoardScale;
			_board.AddComponent<WorldPanel>();
			_board.AddComponent<Gambit.UI.VrStatusPanel>();
		}

		if ( !ViewAnchor( _controller, CameraObject, out var from, out var forward ) ) return;

		_board.WorldPosition = from + forward * BoardDistance + Vector3.Up * BoardHeight;
		_board.WorldRotation = Rotation.LookAt( -forward, Vector3.Up );
	}

	/// <summary>Show or hide the rig's state board.</summary>
	[ConCmd( "gambit_vr_debug" )]
	public static void ToggleDebugBoard()
	{
		DebugBoard = !DebugBoard;
		Log.Info( $"[Gambit] vr: debug board {(DebugBoard ? "ON" : "OFF")}" );
	}

	/// <summary>Re-capture the seated reference pose: sit comfortably, face the board, run this.
	/// Only meaningful while seated — roaming has nothing to centre.</summary>
	[ConCmd( "gambit_vr_recentre" )]
	public static void Recentre()
	{
		if ( _instance == null )
		{
			Log.Info( "[Gambit] vr: no rig (not running in VR?)" );
			return;
		}

		_instance._seatCaptured = false;
		Log.Info( "[Gambit] vr: seated pose will re-capture this frame" );
	}
}
