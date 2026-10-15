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
/// <c>Game.IsRunningInVR</c>, so nothing VR has to be authored into the scene and the desktop
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
	/// <summary>True while a local VR rig is driving the view. Read by
	/// <see cref="LobbyPlayer"/> to keep our own avatar out of our own headset.</summary>
	public static bool Active { get; private set; }

	static VrRig _instance;

	/// <summary>The camera this drives — the local player's, handed over by
	/// <see cref="LobbyPlayer"/>, which is the one thing that knows which that is.</summary>
	public GameObject CameraObject { get; set; }

	LobbyPlayer _player;
	PlayerController _controller;

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
	}

	protected override void OnDestroy()
	{
		if ( _instance == this )
		{
			_instance = null;
			Active = false;
		}
	}

	protected override void OnUpdate() => Drive();
	protected override void OnPreRender() => Drive();

	void Drive()
	{
		if ( !Game.IsRunningInVR ) return;

		UpdateAnchor();

		// Input.VR.Head is already anchor-to-world, so this IS the pose the anchor just defined.
		var head = Input.VR.Head;
		if ( CameraObject.IsValid() )
		{
			CameraObject.WorldPosition = head.Position;
			CameraObject.WorldRotation = head.Rotation;
		}
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
