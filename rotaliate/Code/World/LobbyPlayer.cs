using System;
using System.Collections.Generic;
using System.Threading;
using Rotaliate.UI.Screens;
using Sandbox;

namespace Rotaliate.World;

/// <summary>
/// Lives on the Player GameObject next to the PlayerController.
/// Handles walking up to an ArcadeStation, pressing Use (E) to lock into it,
/// and Escape / the Leave button to get back out.
/// While locked in, the PlayerController is disabled (no movement, no mouselook)
/// and this component drives the camera to the station's anchor.
/// </summary>
public sealed class LobbyPlayer : Component
{
	public static LobbyPlayer Local { get; private set; }

	[Property] public float InteractRange { get; set; } = 90f;

	/// <summary>Z below which the player has "fallen off the map": respawns them at
	/// their spawn point and grants the adventurer achievement. The floor top is at
	/// Z=0, so this is the catch-volume a couple hundred units below the room.</summary>
	[Property] public float FallKillZ { get; set; } = -150f;

	Vector3 _spawnPos;

	/// <summary>This player's Rotaliate username, synced by the owner so everyone
	/// can render it on the name tag (issue #51) — Steam name comes from the
	/// owning connection, but the Rotaliate identity only exists in the owner's
	/// local FileSystem.Data.</summary>
	[Sync] public string RotaliateName { get; set; }

	/// <summary>Name tag height above the player origin (the avatar is ~72 tall).</summary>
	[Property] public float NameTagHeight { get; set; } = 82f;

	/// <summary>WorldPanel scale of the name tag — tune in editor if mis-sized.</summary>
	[Property] public float NameTagScale { get; set; } = 6f;

	/// <summary>Station in range that "Press E" would activate, null if none.</summary>
	public ArcadeStation NearbyStation { get; private set; }

	/// <summary>Settings board in range that "Press E" would activate, null if none.</summary>
	public SettingsStation NearbyBoard { get; private set; }

	/// <summary>Leaderboard station in range that "Press E" would activate, null if none.</summary>
	public LeaderboardStation NearbyLeaderboard { get; private set; }

	/// <summary>Info/dev-notes station in range that "Press E" would activate, null if none.</summary>
	public InfoStation NearbyInfo { get; private set; }

	public bool Engaged => ArcadeStation.Active != null || SettingsStation.Active != null || LeaderboardStation.Active != null || InfoStation.Active != null;

	/// <summary>True once the camera has finished blending to the station anchor —
	/// the game screens wait for this so the swoop to the wall screen stays visible.</summary>
	public bool CameraSettled => Engaged && _engageTime >= CamBlendTime;

	PlayerController _controller;
	Rigidbody _rigidbody;
	GameObject _cameraObject;
	SkinnedModelRenderer _bodyRenderer;

	// All avatar renderers hidden at Engage (body, eyes, hair, clothing — the
	// dresser spawns each as its own SkinnedModelRenderer), restored at Disengage.
	readonly List<ModelRenderer> _hiddenRenderers = new();

	Vector3 _camFromPos;
	Rotation _camFromRot;
	Angles _eyeFrom; // controller look angles at engage, restored on leave so it doesn't snap to yaw 0
	TimeSince _engageTime;
	const float CamBlendTime = 0.35f;

	// Blend-out state: camera eases from the station anchor back to the pose
	// captured at Engage; the controller stays disabled until it lands
	bool _leaving;
	TimeSince _leaveTime;
	Vector3 _leaveFromPos;
	Rotation _leaveFromRot;

	protected override void OnDestroy()
	{
		_dressCts?.Cancel();
		if ( Local == this ) Local = null;
	}

	protected override void OnStart()
	{
		_controller = Components.Get<PlayerController>();
		_rigidbody = Components.Get<Rigidbody>();
		_bodyRenderer = GameObject.GetComponentInChildren<SkinnedModelRenderer>();
		var camera = GameObject.GetComponentInChildren<CameraComponent>();
		_cameraObject = camera?.GameObject;

		DressFromAvatar();

		if ( IsProxy )
		{
			// Someone else's avatar — it must not bring a camera into our scene
			_cameraObject?.Destroy();
			_cameraObject = null;
			CreateNameTag();
			return;
		}

		Local = this;
		_spawnPos = WorldPosition;
	}

	/// <summary>Teleport back to spawn after falling off the map and record the death
	/// (the deaths stat drives the adventurer achievement). PlayerController.Velocity is
	/// read-only; landing on the floor at spawn bleeds off the fall velocity anyway.</summary>
	void Respawn()
	{
		WorldPosition = _spawnPos;
		Rotaliate.Game.PlayerStats.Increment( Rotaliate.Game.PlayerStats.Deaths );
	}

	/// <summary>Floating name tag over remote players (issue #51): Steam name with
	/// the Rotaliate username underneath. Proxies only — the local player never
	/// sees their own.</summary>
	void CreateNameTag()
	{
		var tag = new GameObject( true, "NameTag" );
		tag.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		tag.Parent = GameObject; // destroyed with the avatar
		tag.LocalPosition = new Vector3( 0, 0, NameTagHeight );
		tag.LocalScale = NameTagScale;
		// Billboard at the actual camera (WorldPanel.LookAtCamera), not at the local
		// body like the wall boards do. Facing the body left the tag yaw-tilted by the
		// third-person camera offset (~256u behind the avatar), which read as the text
		// sitting back in depth behind the player.
		tag.AddComponent<WorldPanel>().LookAtCamera = true;
		tag.AddComponent<Rotaliate.UI.NameTagPanel>().Player = this;
	}

	protected override void OnUpdate()
	{
		if ( IsProxy ) return;

		// Fell off the map: only possible while roaming (the controller is off while
		// engaged), so catch it before the engage/leave handling below.
		if ( !Engaged && !_leaving && WorldPosition.z < FallKillZ )
			Respawn();

		// Publish the Rotaliate username for everyone's name tags; it can appear
		// (enrollment) or change (Profile rename) at any time. Load() is cached.
		var uname = Rotaliate.Game.PlayerData.Load()?.Username ?? "";
		if ( RotaliateName != uname ) RotaliateName = uname;

		if ( _leaving )
		{
			UpdateLeaveCamera();
			return;
		}

		if ( Engaged )
		{
			// Escape is two-stage: in play/replay (or on the results overlay) it
			// returns to the main menu screen; on the menu screens it leaves the
			// station as before. The Settings rebind listener still consumes
			// Escape itself.
			// Start (SwitchRightMenu) auto-sets EscapePressed; the Back button
			// (SwitchLeftMenu) is wired through the "Back" action to match.
			if ( (Input.EscapePressed || Input.Pressed( "Back" )) && !ModePickerScreen.IsRebinding )
			{
				Input.EscapePressed = false;

				var game = Rotaliate.Game.GameController.Instance;
				var mp   = Rotaliate.Game.MultiplayerController.Instance;
				if ( game != null && game.State != Rotaliate.Game.GameState.Idle )
					game.ReturnToMenu();
				else if ( mp != null && (mp.State == Rotaliate.Game.MpState.Playing || mp.State == Rotaliate.Game.MpState.GameOver) )
					mp.ReturnToMenu();
				else
					Disengage();
				return;
			}

			// Only the cabinet drives the camera; wall boards leave it where it is.
			if ( ArcadeStation.Active != null )
				UpdateLockedCamera();
			return;
		}

		// Chat typing: keep the controller off so WASD keystrokes don't walk the
		// avatar, and skip interaction handling until the box closes.
		if ( Rotaliate.UI.Screens.ChatPanel.IsOpen )
		{
			if ( _controller != null && _controller.Enabled )
			{
				_eyeFrom = _controller.EyeAngles; // capture before disabling so we can restore the view
				_controller.Enabled = false;
			}
			return;
		}
		// Re-enabling the controller resets its EyeAngles to yaw 0 (camera snaps to
		// world-forward — the info-board wall); re-apply the captured angles like the
		// engage-leave path does.
		if ( _controller != null && !_controller.Enabled )
		{
			_controller.Enabled = true;
			_controller.EyeAngles = _eyeFrom;
		}

		// First-ever load: pop the welcome/info board up automatically until the player
		// has dismissed it once (PlayerData.InfoPanelSeen).
		if ( !_infoPopDone )
			TryAutoShowInfo();

		NearbyStation = FindNearbyStation();
		NearbyBoard = NearbyStation == null ? FindNearbyBoard() : null;
		NearbyLeaderboard = NearbyStation == null && NearbyBoard == null ? FindNearbyLeaderboard() : null;
		NearbyInfo = NearbyStation == null && NearbyBoard == null && NearbyLeaderboard == null ? FindNearbyInfo() : null;

		// E stops a playing spectator replay — on its own when nothing's in reach, or as
		// a side effect of engaging anything below (the same press still engages).
		if ( SpectatorBoard.Replaying && Input.Pressed( "use" ) )
			SpectatorBoard.StopReplay();

		if ( NearbyStation != null && Input.Pressed( "use" ) )
			Engage( NearbyStation );
		else if ( NearbyBoard != null && Input.Pressed( "use" ) )
			EngageBoard( NearbyBoard );
		else if ( NearbyLeaderboard != null && Input.Pressed( "use" ) )
			EngageLeaderboard( NearbyLeaderboard );
		else if ( NearbyInfo != null && Input.Pressed( "use" ) )
			EngageInfo( NearbyInfo );
	}

	SettingsStation FindNearbyBoard()
	{
		SettingsStation best = null;
		float bestDist = float.MaxValue;

		foreach ( var board in Scene.GetAllComponents<SettingsStation>() )
		{
			// Nothing for a non-admin to edit on the host board
			if ( board.Host && !LobbyNetworkManager.LocalIsAdmin ) continue;

			// Horizontal distance — the boards hang at wall-center height
			var delta = board.WorldPosition - GameObject.WorldPosition;
			float dist = new Vector2( delta.x, delta.y ).Length;
			if ( dist < board.InteractRange && dist < bestDist )
			{
				best = board;
				bestDist = dist;
			}
		}

		return best;
	}

	LeaderboardStation FindNearbyLeaderboard()
	{
		LeaderboardStation best = null;
		float bestDist = float.MaxValue;

		foreach ( var station in Scene.GetAllComponents<LeaderboardStation>() )
		{
			var delta = station.WorldPosition - GameObject.WorldPosition;
			float dist = new Vector2( delta.x, delta.y ).Length;
			if ( dist < station.InteractRange && dist < bestDist )
			{
				best = station;
				bestDist = dist;
			}
		}

		return best;
	}

	InfoStation FindNearbyInfo()
	{
		InfoStation best = null;
		float bestDist = float.MaxValue;

		foreach ( var station in Scene.GetAllComponents<InfoStation>() )
		{
			// Don't prompt to read dev notes when there aren't any (the wall board hides too)
			if ( station.Kind == InfoStation.StationKind.DevNotes && !Rotaliate.UI.DevNotesPanel.HasNotes )
				continue;

			var delta = station.WorldPosition - GameObject.WorldPosition;
			float dist = new Vector2( delta.x, delta.y ).Length;
			if ( dist < station.InteractRange && dist < bestDist )
			{
				best = station;
				bestDist = dist;
			}
		}

		return best;
	}

	bool _infoPopDone;

	/// <summary>Auto-open the welcome/info board the first time a player ever loads the
	/// lobby (until they dismiss it once — PlayerData.InfoPanelSeen). Retries each frame
	/// until the InfoStation exists (InfoWall builds it at runtime).</summary>
	void TryAutoShowInfo()
	{
		if ( Rotaliate.Game.PlayerData.Load()?.InfoPanelSeen == true )
		{
			_infoPopDone = true;
			return;
		}

		InfoStation info = null;
		foreach ( var station in Scene.GetAllComponents<InfoStation>() )
			if ( station.Kind == InfoStation.StationKind.Info ) { info = station; break; }
		if ( info == null ) return; // not built yet — try again next frame

		_infoPopDone = true;
		EngageInfo( info );
	}

	CancellationTokenSource _dressCts;

	/// <summary>
	/// Apply the owning player's Steam avatar clothing to the citizen body.
	/// Clothing isn't networked state, so every client dresses each avatar locally.
	/// The local player uses CreateFromLocalUser — it reads the full avatar including
	/// Steam-inventory cosmetics, which the "avatar" user-data JSON can miss.
	/// Proxies have only the owner connection's user data to go on.
	///
	/// ApplyAsync (not the synchronous Apply) is required: workshop / Steam-inventory
	/// cosmetics (hats, chains, …) aren't on disk locally, and the engine's Apply
	/// "doesn't download missing clothing" — it silently drops them. ApplyAsync
	/// downloads each missing item's package then re-applies, matching the engine's
	/// own Dresser component.
	/// </summary>
	async void DressFromAvatar()
	{
		if ( _bodyRenderer == null ) return;

		ClothingContainer clothing = null;
		if ( !IsProxy )
		{
			clothing = ClothingContainer.CreateFromLocalUser();
		}
		else
		{
			// The avatar JSON carries another player's cosmetic loadout; don't log it
			// per spawn — it's noise and minor privacy chatter, not a debugging aid
			// (security review C6).
			var avatarJson = Network.Owner?.GetUserData( "avatar" );
			if ( !string.IsNullOrEmpty( avatarJson ) )
				clothing = ClothingContainer.CreateFromJson( avatarJson );
		}

		if ( clothing == null ) return;

		_dressCts?.Cancel();
		_dressCts = new CancellationTokenSource();
		try
		{
			await clothing.ApplyAsync( _bodyRenderer, _dressCts.Token );
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception e )
		{
			Log.Warning( $"[Rotaliate] avatar dressing failed: {e.Message}" );
		}
	}

	ArcadeStation FindNearbyStation()
	{
		ArcadeStation best = null;
		float bestDist = InteractRange;

		foreach ( var station in Scene.GetAllComponents<ArcadeStation>() )
		{
			if ( station.Occupied ) continue;

			float dist = station.GameObject.WorldPosition.Distance( GameObject.WorldPosition );
			if ( dist < bestDist )
			{
				best = station;
				bestDist = dist;
			}
		}

		return best;
	}

	public void Engage( ArcadeStation station )
	{
		station.Enter();
		if ( ArcadeStation.Active != station ) return; // someone else has it
		BeginEngage();
	}

	/// <summary>Open a south-wall settings board. Its UI is a screen-space ScreenPanel,
	/// so the camera doesn't move (unlike the cabinets) — just free the mouse for the
	/// cursor by disabling look. No occupancy (boards are local-only).</summary>
	public void EngageBoard( SettingsStation board )
	{
		board.Enter();
		BeginBoardEngage();

		// Achievements for opening each settings panel.
		Rotaliate.Game.Achievements.Unlock( board.Kind switch
		{
			SettingsStation.StationKind.Host  => Rotaliate.Game.Achievements.DiscordMod,
			SettingsStation.StationKind.Music => Rotaliate.Game.Achievements.Dj,
			_                                 => Rotaliate.Game.Achievements.Comfy,
		} );
	}

	/// <summary>Open a north-wall leaderboard pair — screen-space UI, same as EngageBoard.</summary>
	public void EngageLeaderboard( LeaderboardStation station )
	{
		station.Enter();
		BeginBoardEngage();
	}

	/// <summary>Open an east-wall info / dev-notes board — screen-space UI, same as EngageBoard.</summary>
	public void EngageInfo( InfoStation station )
	{
		station.Enter();
		BeginBoardEngage();
	}

	/// <summary>Wall boards draw their UI directly on the screen, so leave the camera
	/// alone and only disable look controls so the cursor can drive the panel.</summary>
	void BeginBoardEngage()
	{
		if ( _controller != null )
			_controller.UseLookControls = false;
	}

	bool BoardEngaged => SettingsStation.Active != null || LeaderboardStation.Active != null || InfoStation.Active != null;

	void BeginEngage()
	{
		if ( _cameraObject != null )
		{
			_camFromPos = _cameraObject.WorldPosition;
			_camFromRot = _cameraObject.WorldRotation;
		}
		_engageTime = 0;

		if ( _controller != null )
		{
			_eyeFrom = _controller.EyeAngles; // capture before disabling so we can restore it on leave
			_controller.Enabled = false;
		}

		// Stop the movement rigidbody dead. The PlayerController is the only thing that
		// brakes it, and with the controller now disabled (and LinearDamping = 0) any
		// velocity left from walking into the cabinet would coast frictionlessly forever.
		// We don't see it (camera locked, body hidden) but our networked position keeps
		// drifting, so everyone else watches our avatar slide across the room.
		if ( _rigidbody != null )
		{
			_rigidbody.Velocity = Vector3.Zero;
			_rigidbody.AngularVelocity = Vector3.Zero;
		}

		// The PlayerController's animator stops updating the body the moment the
		// controller is disabled, so the last walk value keeps looping the locomotion
		// animgraph in place. Stomp the movement parameters to idle ourselves — nothing
		// overwrites them while disabled, so the body settles to a stand.
		if ( _bodyRenderer != null )
		{
			_bodyRenderer.Set( "move_groundspeed", 0f );
			_bodyRenderer.Set( "move_x", 0f );
			_bodyRenderer.Set( "move_y", 0f );
			_bodyRenderer.Set( "move_z", 0f );
		}

		// Hide our avatar so it doesn't stand between the locked camera and the
		// screen — collected here (not OnStart) so dresser-spawned renderers are included
		_hiddenRenderers.Clear();
		foreach ( var r in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( !r.Enabled ) continue;
			r.Enabled = false;
			_hiddenRenderers.Add( r );
		}
	}

	public void Disengage()
	{
		// Wall boards never moved the camera — just re-enable look and close the panel.
		if ( ArcadeStation.Active == null && BoardEngaged )
		{
			SettingsStation.Active?.Leave();
			LeaderboardStation.Active?.Leave();
			InfoStation.Active?.Leave();
			if ( _controller != null )
				_controller.UseLookControls = true;
			return;
		}

		CameraBackoff = 0f;
		_cameraRise = 0f;
		ArcadeStation.Active?.Leave();
		SettingsStation.Active?.Leave();
		LeaderboardStation.Active?.Leave();
		InfoStation.Active?.Leave();

		foreach ( var r in _hiddenRenderers )
		{
			if ( r.IsValid() )
				r.Enabled = true;
		}
		_hiddenRenderers.Clear();

		// Reverse of the engage blend: ease the camera from the anchor back to
		// where it was when we engaged, then hand control back to the controller
		if ( _cameraObject != null )
		{
			_leaveFromPos = _cameraObject.WorldPosition;
			_leaveFromRot = _cameraObject.WorldRotation;
			_leaveTime = 0;
			_leaving = true;
		}
		else
		{
			RestoreController();
		}
	}

	void UpdateLeaveCamera()
	{
		float t = Math.Clamp( _leaveTime / CamBlendTime, 0f, 1f );
		t = 1f - MathF.Pow( 1f - t, 3f );

		_cameraObject.WorldPosition = Vector3.Lerp( _leaveFromPos, _camFromPos, t );
		_cameraObject.WorldRotation = Rotation.Slerp( _leaveFromRot, _camFromRot, t );

		if ( _leaveTime >= CamBlendTime )
		{
			_leaving = false;
			RestoreController();
		}
	}

	/// <summary>Hand the camera back to the PlayerController without a snap. The
	/// controller owns the camera (UseCameraControls) and re-derives it from its own
	/// EyeAngles, which it resets on re-enable (the view snapped to yaw 0). Re-apply the
	/// angles captured at engage — after Enabled = true, so OnEnabled can't clobber them.</summary>
	void RestoreController()
	{
		if ( _controller == null ) return;
		_controller.Enabled = true;
		_controller.EyeAngles = _eyeFrom;
	}

	/// <summary>Current play-mode camera pull-back from the anchor, smoothed.
	/// ArcadeRing.ScreenFractionRect adds it so the UI rect tracks the camera.</summary>
	public static float CameraBackoff { get; private set; }

	float _cameraRise; // play-mode camera lift, smoothed alongside CameraBackoff

	static bool BoardIsOut()
	{
		var game = Rotaliate.Game.GameController.Instance;
		if ( game != null && (game.State == Rotaliate.Game.GameState.Playing || game.State == Rotaliate.Game.GameState.Complete) )
			return true;
		var mp = Rotaliate.Game.MultiplayerController.Instance;
		return mp != null && (mp.State == Rotaliate.Game.MpState.Playing || mp.State == Rotaliate.Game.MpState.GameOver);
	}

	void UpdateLockedCamera()
	{
		var anchor = ArcadeStation.Active?.CameraAnchor;
		if ( anchor == null || _cameraObject == null ) return;

		// Back the camera up and lift it a bit while the cube board is out of the
		// cabinet, tilting down so it keeps aiming at the board center
		var ring = ArcadeRing.Instance;
		bool boardOut = BoardIsOut();
		float blend = Math.Clamp( Time.Delta * 6f, 0f, 1f );
		var data = Rotaliate.Game.PlayerData.Load();
		float want = boardOut
			? (ring?.PlayCameraBackoff ?? 10f) * Rotaliate.Game.PlayerData.ClampCameraScale( data?.CameraBackoffScale ?? 1f )
			: 0f;
		CameraBackoff = MathX.Lerp( CameraBackoff, want, blend );
		float wantRise = boardOut
			? (ring?.PlayCameraRise ?? 8f) * Rotaliate.Game.PlayerData.ClampCameraScale( data?.CameraRiseScale ?? 1f )
			: 0f;
		_cameraRise = MathX.Lerp( _cameraRise, wantRise, blend );

		var anchorPos = anchor.WorldPosition - anchor.WorldRotation.Forward * CameraBackoff
			+ Vector3.Up * _cameraRise;
		// Aiming at the screen center keeps the tilted view (and the UI rect trig,
		// which assumes a centered screen) consistent with the square-on case
		var lookTarget = anchor.WorldPosition + anchor.WorldRotation.Forward * (ring?.CameraDistance ?? 75f);
		var anchorRot = Rotation.LookAt( lookTarget - anchorPos, Vector3.Up );

		float t = Math.Clamp( _engageTime / CamBlendTime, 0f, 1f );
		t = 1f - MathF.Pow( 1f - t, 3f ); // ease-out cubic, same curve as board rotation

		_cameraObject.WorldPosition = Vector3.Lerp( _camFromPos, anchorPos, t );
		_cameraObject.WorldRotation = Rotation.Slerp( _camFromRot, anchorRot, t );
	}
}
