using System;
using System.Collections.Generic;
using System.Threading;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Lives on the Player GameObject next to the PlayerController.
/// Handles walking up to a chess table seat, pressing Use (E) to sit down,
/// and Escape / the Leave button to stand back up.
/// While seated, the PlayerController is disabled (no movement, no mouselook)
/// and this component drives the camera to the seat's anchor over the board.
/// </summary>
public sealed class LobbyPlayer : Component
{
	public static LobbyPlayer Local { get; private set; }

	/// <summary>How close (horizontally) a player must stand to a seat spot for
	/// "Press E" to offer it.</summary>
	[Property] public float InteractRange { get; set; } = 55f;

	/// <summary>Z below which the player has "fallen off the map": respawns them at
	/// their spawn point. The floor top is at Z=0, so this is the catch-volume a
	/// couple hundred units below the room.</summary>
	[Property] public float FallKillZ { get; set; } = -150f;

	Vector3 _spawnPos;

	/// <summary>This player's Gambit display name, synced by the owner so everyone
	/// can render it on the name tag (issue #51) — Steam name comes from the
	/// owning connection, but the display name only exists in the owner's
	/// local FileSystem.Data.</summary>
	[Sync] public string GambitName { get; set; }

	/// <summary>Lichess rating for the name tag as a string ("" when not signed in
	/// or unrated). A rating is public info, so unlike the token it's fine to sync.</summary>
	[Sync] public string GambitRating { get; set; }

	/// <summary>Name tag height above the player origin (the avatar is ~72 tall).</summary>
	[Property] public float NameTagHeight { get; set; } = 82f;

	/// <summary>WorldPanel scale of the name tag — tune in editor if mis-sized.</summary>
	[Property] public float NameTagScale { get; set; } = 6f;

	/// <summary>Station whose seat "Press E" would take, null if none in range.</summary>
	public ChessStation NearbyStation { get; private set; }

	/// <summary>The seat at <see cref="NearbyStation"/> that would be taken.</summary>
	public ChessSeat NearbySeat { get; private set; }

	/// <summary>Settings board in range that "Press E" would activate, null if none.</summary>
	public SettingsStation NearbyBoard { get; private set; }

	/// <summary>Info/dev-notes station in range that "Press E" would activate, null if none.</summary>
	public InfoStation NearbyInfo { get; private set; }

	/// <summary>Spectator wall the player is close enough to engage, if any.</summary>
	public SpectatorStation NearbySpectator { get; private set; }

	public bool Engaged => ChessStation.Active != null || SettingsStation.Active != null || InfoStation.Active != null || SpectatorStation.Active != null;

	/// <summary>True once the camera has finished blending to the seat anchor —
	/// engaged screens wait for this so the swoop down to the board stays visible.</summary>
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

	// Blend-out state: camera eases from the seat anchor back to the pose
	// captured at Engage; the controller stays disabled until it lands
	bool _leaving;
	TimeSince _leaveTime;
	Vector3 _leaveFromPos;
	Rotation _leaveFromRot;

	// Seated-body plant: where our avatar stood when we sat, restored on stand-up so
	// the camera/controller hand-back lands exactly where the blend-out targets (no snap).
	bool _movedForSeat;
	Vector3 _seatReturnPos;
	Rotation _seatReturnRot;

	// Deferred "join by link" (M4): when a pasted lichess URL assigns a side we
	// aren't currently on, we Disengage first and take the seat once the leave
	// blend finishes (processed in OnUpdate's roaming section).
	ChessStation _pendingJoinStation;
	ChessSeat? _pendingJoinSeat;

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
		EnsureGameHud();
		EnsureSplash();
		EnsureSpectatorScreen();

		// Re-validate any stored lichess token; a 401 clears it and re-prompts
		// (PLAN.md M3 gate: "401 → re-prompt"). No-op when signed out.
		Gambit.Api.LichessAuth.ValidateStoredToken();
	}

	/// <summary>Attach the seated-game HUD to the scene's ScreenPanel at runtime
	/// (local player only) — no scene rewire needed for M2, same self-heal spirit
	/// as LobbyRoom.EnsureChessRing.</summary>
	void EnsureGameHud()
	{
		foreach ( var screen in Scene.GetAllComponents<ScreenPanel>() )
		{
			if ( screen.Components.Get<Gambit.UI.GameHud>() == null )
				screen.GameObject.AddComponent<Gambit.UI.GameHud>();
			return; // first ScreenPanel is the scene UI root
		}
	}

	/// <summary>Attach the lichess sign-in modal to the scene ScreenPanel at
	/// runtime (local player only) — same self-heal as EnsureGameHud, so M3 needs
	/// no scene rewire.</summary>
	void EnsureSplash()
	{
		foreach ( var screen in Scene.GetAllComponents<ScreenPanel>() )
		{
			if ( screen.Components.Get<Gambit.UI.Screens.SplashScreen>() == null )
				screen.GameObject.AddComponent<Gambit.UI.Screens.SplashScreen>();
			return; // first ScreenPanel is the scene UI root
		}
	}

	/// <summary>Attach the spectator-wall channel picker to the scene ScreenPanel at
	/// runtime (M5) — same self-heal as EnsureGameHud, so no scene rewire is needed. The
	/// screen draws nothing until the player engages the SpectatorStation.</summary>
	void EnsureSpectatorScreen()
	{
		foreach ( var screen in Scene.GetAllComponents<ScreenPanel>() )
		{
			if ( screen.Components.Get<Gambit.UI.Screens.SpectatorScreen>() == null )
				screen.GameObject.AddComponent<Gambit.UI.Screens.SpectatorScreen>();
			return; // first ScreenPanel is the scene UI root
		}
	}

	/// <summary>Teleport back to spawn after falling off the map.
	/// PlayerController.Velocity is read-only; landing on the floor at spawn
	/// bleeds off the fall velocity anyway.</summary>
	void Respawn()
	{
		WorldPosition = _spawnPos;
	}

	/// <summary>Floating name tag over remote players (issue #51): Steam name with
	/// the Gambit display name underneath. Proxies only — the local player never
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
		tag.AddComponent<Gambit.UI.NameTagPanel>().Player = this;
	}

	protected override void OnUpdate()
	{
		if ( IsProxy ) return;

		// Fell off the map: only possible while roaming (the controller is off while
		// engaged), so catch it before the engage/leave handling below.
		if ( !Engaged && !_leaving && WorldPosition.z < FallKillZ )
			Respawn();

		// Publish the display name for everyone's name tags; it can appear or change
		// at any time. Load() is cached. DisplayName is the single source of truth — the
		// signed-in lichess account name, else the free-form anonymous name (PLAN.md M3).
		var data = Gambit.Game.PlayerData.Load();
		var uname = data?.DisplayName() ?? "";
		if ( GambitName != uname ) GambitName = uname;

		var rating = ( !string.IsNullOrEmpty( data?.LichessUsername ) && data.LichessRating > 0 )
			? data.LichessRating.ToString() : "";
		if ( GambitRating != rating ) GambitRating = rating;

		if ( _leaving )
		{
			UpdateLeaveCamera();
			return;
		}

		if ( Engaged )
		{
			// Escape or Back stands up / closes the wall board. (Start auto-sets
			// EscapePressed; the Back button is wired through the "Back" action.)
			// Standing up mid-game resigns, so that path is two-stage (RequestLeave).
			if ( Input.EscapePressed || Input.Pressed( "Back" ) )
			{
				Input.EscapePressed = false;
				RequestLeave();
				return;
			}

			// Only the chess seat drives the camera; wall boards leave it where it is.
			if ( ChessStation.Active != null )
				UpdateLockedCamera();
			return;
		}

		// Sign-in modal: free the mouse for clicking and lock movement, without
		// touching the camera — a screen-space overlay, same idea as the wall boards
		// (UseLookControls=false), plus UseInputControls=false so WASD can't walk the
		// avatar out from under the modal. Restored when it closes.
		if ( Gambit.UI.Screens.SplashScreen.IsOpen )
		{
			if ( _controller != null && !_splashLock )
			{
				_splashLock = true;
				_controller.UseLookControls = false;
				_controller.UseInputControls = false;
			}
			return;
		}
		if ( _splashLock )
		{
			_splashLock = false;
			if ( _controller != null )
			{
				_controller.UseLookControls = true;
				_controller.UseInputControls = true;
			}
		}

		// Chat typing: keep the controller off so WASD keystrokes don't walk the
		// avatar, and skip interaction handling until the box closes.
		if ( Gambit.UI.Screens.ChatPanel.IsOpen )
		{
			if ( _controller != null && _controller.Enabled )
			{
				_eyeFrom = _controller.EyeAngles; // capture before disabling so we can restore the view
				_controller.Enabled = false;
			}
			return;
		}
		// Re-enabling the controller resets its EyeAngles to yaw 0 (camera snaps to
		// world-forward); re-apply the captured angles like the engage-leave path does.
		if ( _controller != null && !_controller.Enabled )
		{
			_controller.Enabled = true;
			_controller.EyeAngles = _eyeFrom;
		}

		// Deferred join-by-link (M4): a pasted lichess URL asked us to take a
		// specific side. If we were seated elsewhere we've since Disengaged; now the
		// leave blend is done and we're roaming, so take the assigned seat and let
		// Engage swoop the camera to it.
		if ( _pendingJoinSeat is { } pendingSeat )
		{
			var station = _pendingJoinStation;
			_pendingJoinStation = null;
			_pendingJoinSeat = null;
			if ( station != null && !station.SeatTaken( pendingSeat ) )
				Engage( station, pendingSeat );
			return;
		}

		// Brand-new player (no name, never signed in): open the sign-in / name modal
		// first. The welcome board waits until it's closed (TryAutoShowInfo guards on it).
		if ( !_splashPopDone )
		{
			_splashPopDone = true;
			var pd = Gambit.Game.PlayerData.Load();
			if ( string.IsNullOrEmpty( pd?.LichessToken ) && string.IsNullOrEmpty( pd?.Username ) )
				Gambit.UI.Screens.SplashScreen.Open();
		}

		// First-ever load: pop the welcome/info board up automatically until the player
		// has dismissed it once (PlayerData.InfoPanelSeen).
		if ( !_infoPopDone )
			TryAutoShowInfo();

		FindNearbySeat();
		NearbyBoard = NearbyStation == null ? FindNearbyBoard() : null;
		NearbyInfo = NearbyStation == null && NearbyBoard == null ? FindNearbyInfo() : null;
		NearbySpectator = NearbyStation == null && NearbyBoard == null && NearbyInfo == null ? FindNearbySpectator() : null;

		if ( NearbyStation != null && Input.Pressed( "use" ) )
			Engage( NearbyStation, NearbySeat );
		else if ( NearbyBoard != null && Input.Pressed( "use" ) )
			EngageBoard( NearbyBoard );
		else if ( NearbyInfo != null && Input.Pressed( "use" ) )
			EngageInfo( NearbyInfo );
		else if ( NearbySpectator != null && Input.Pressed( "use" ) )
			EngageSpectator( NearbySpectator );
	}

	SpectatorStation FindNearbySpectator()
	{
		SpectatorStation best = null;
		float bestDist = float.MaxValue;

		foreach ( var station in Scene.GetAllComponents<SpectatorStation>() )
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

	InfoStation FindNearbyInfo()
	{
		InfoStation best = null;
		float bestDist = float.MaxValue;

		foreach ( var station in Scene.GetAllComponents<InfoStation>() )
		{
			// Don't prompt to read dev notes when there aren't any (the wall board hides too)
			if ( station.Kind == InfoStation.StationKind.DevNotes && !Gambit.UI.DevNotesPanel.HasNotes )
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
	bool _splashPopDone;
	bool _splashLock; // controller look/input suppressed while the sign-in modal is up

	/// <summary>Auto-open the welcome/info board the first time a player ever loads the
	/// lobby (until they dismiss it once — PlayerData.InfoPanelSeen). Retries each frame
	/// until the InfoStation exists (InfoWall builds it at runtime).</summary>
	void TryAutoShowInfo()
	{
		// Let the sign-in modal go first on a brand-new profile; it retries next frame.
		if ( Gambit.UI.Screens.SplashScreen.IsOpen ) return;

		if ( Gambit.Game.PlayerData.Load()?.InfoPanelSeen == true )
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
			Log.Warning( $"[Gambit] avatar dressing failed: {e.Message}" );
		}
	}

	/// <summary>Nearest free seat within InteractRange: each table offers two seat
	/// spots (ChessStation.SeatWorldPosition), and the closest free one wins. You
	/// take the side you actually walk up to — the lichess open game (M4) colours
	/// you by that side, so we no longer override the first sitter to White (the old
	/// PLAN D1 convention). White still moves first regardless of who sat first.</summary>
	void FindNearbySeat()
	{
		NearbyStation = null;
		float bestDist = InteractRange;

		foreach ( var station in Scene.GetAllComponents<ChessStation>() )
		{
			foreach ( var seat in new[] { ChessSeat.White, ChessSeat.Black } )
			{
				if ( station.SeatTaken( seat ) ) continue;

				var delta = station.SeatWorldPosition( seat ) - GameObject.WorldPosition;
				float dist = new Vector2( delta.x, delta.y ).Length;
				if ( dist < bestDist )
				{
					NearbyStation = station;
					NearbySeat = seat;
					bestDist = dist;
				}
			}
		}
	}

	// ── Two-stage leave (M2): standing up from a live game resigns it, so the
	// first Escape/Leave only arms the intent; a second within the window (or
	// with no live game) actually stands. GameHud/LobbyOverlay read LeaveArmed
	// to show "again to resign".

	RealTimeSince _leaveArm = 999f;
	const float LeaveConfirmWindow = 3f;

	/// <summary>An armed leave-confirm is pending (UI shows the resign warning).</summary>
	public bool LeaveArmed => _leaveArm < LeaveConfirmWindow;

	/// <summary>Escape / the Leave button while engaged. Immediate for wall
	/// boards, finished games and untouched boards; two-stage when it would
	/// forfeit a live game.</summary>
	public void RequestLeave()
	{
		var station = ChessStation.Active;
		var controller = Gambit.Game.LocalGameController.For( station );
		var lichess = Gambit.Game.LichessPlayController.For( station );

		// Standing up mid-game forfeits it — true for both the local two-seat game and
		// a live in-sbox lichess game, so both arm the two-stage confirm.
		bool localForfeits = controller is { Playing: true }
			&& controller.LocalSeat != null
			&& ( controller.Game?.MoveCount ?? 0 ) > 0;
		bool lichessForfeits = lichess is { Playing: true };
		bool forfeits = localForfeits || lichessForfeits;

		if ( forfeits && !LeaveArmed )
		{
			_leaveArm = 0f;
			return;
		}

		_leaveArm = 999f;
		if ( localForfeits )
			controller.ResignLocal();
		// Reset the lichess controller in every non-idle phase, not just Playing:
		// resign a live game, cancel a pending challenge/seek/open link, or clear the
		// game-over screen — so the board returns to "not playing" for the next sitter.
		lichess?.LeaveSeat();
		Gambit.Game.PuzzleController.For( station )?.LeaveSeat();
		Disengage();
	}

	public void Engage( ChessStation station, ChessSeat seat )
	{
		station.Enter( seat );
		if ( ChessStation.Active != station ) return; // someone else has it
		BeginEngage();
	}

	/// <summary>Take a specific side and swoop the camera there — the "join by link"
	/// path (M4), where lichess (not proximity) picks the colour. Uses the board the
	/// player is at / nearest to. If already seated on the other side of that board,
	/// leaves first and takes the assigned seat once the blend finishes
	/// (<see cref="_pendingJoinSeat"/>, handled in OnUpdate).</summary>
	public void JoinLichessSide( ChessSeat seat )
	{
		var station = ChessStation.Active ?? NearestStation();
		if ( station == null ) return;

		if ( ChessStation.Active == station && ChessStation.ActiveSeat == seat )
			return; // already on the assigned side

		if ( ChessStation.Active != null )
		{
			_pendingJoinStation = station;
			_pendingJoinSeat = seat;
			Disengage();
			return;
		}

		Engage( station, seat );
	}

	/// <summary>Nearest chess table by planar distance, ignoring seat occupancy —
	/// the board a "join by link" seats you at when you aren't already at one.</summary>
	ChessStation NearestStation()
	{
		ChessStation best = null;
		float bestDist = float.MaxValue;
		foreach ( var station in Scene.GetAllComponents<ChessStation>() )
		{
			var delta = station.WorldPosition - GameObject.WorldPosition;
			float dist = new Vector2( delta.x, delta.y ).Length;
			if ( dist < bestDist ) { best = station; bestDist = dist; }
		}
		return best;
	}

	/// <summary>Open a south-wall settings board. Its UI is a screen-space ScreenPanel,
	/// so the camera doesn't move (unlike the chess seats) — just free the mouse for the
	/// cursor by disabling look. No occupancy (boards are local-only).</summary>
	public void EngageBoard( SettingsStation board )
	{
		board.Enter();
		BeginBoardEngage();
	}

	/// <summary>Open an east-wall info / dev-notes board — screen-space UI, same as EngageBoard.</summary>
	public void EngageInfo( InfoStation station )
	{
		station.Enter();
		BeginBoardEngage();
	}

	/// <summary>Open the west-wall spectator board's channel picker — screen-space UI,
	/// same as EngageBoard (camera stays put; cursor freed).</summary>
	public void EngageSpectator( SpectatorStation station )
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

	bool BoardEngaged => SettingsStation.Active != null || InfoStation.Active != null || SpectatorStation.Active != null;

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
		// velocity left from walking into the table would coast frictionlessly forever.
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

		// Physically plant our avatar at our side of the board. We only ever see the
		// locked overhead camera with our own avatar hidden, so this is invisible to
		// *us* — but the transform is networked from us (the owner), so every OTHER
		// client's copy of our avatar snaps to the seat instead of standing wherever we
		// walked up. And because the body then stops moving, their PlayerController
		// derives zero speed and drops it out of the walk cycle into a plain idle — no
		// more sliding/strafing across the room (the parent-project bug). We keep our
		// own standing height and only slide horizontally to the seat, then face the
		// board so we read as sitting down to it.
		if ( ChessStation.Active is { } seatStation )
		{
			_seatReturnPos = WorldPosition;
			_seatReturnRot = WorldRotation;
			_movedForSeat = true;

			var seatPos = seatStation.SeatWorldPosition( ChessStation.ActiveSeat );
			seatPos.z = WorldPosition.z; // slide to the seat side only; keep our own height

			var boardFlat = seatStation.WorldPosition;
			boardFlat.z = seatPos.z;
			var toBoard = boardFlat - seatPos;

			WorldPosition = seatPos;
			if ( toBoard.Length > 0.01f )
				WorldRotation = Rotation.LookAt( toBoard, Vector3.Up );
		}

		// Hide our avatar so it doesn't stand between the locked camera and the
		// board — collected here (not OnStart) so dresser-spawned renderers are included
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
		if ( ChessStation.Active == null && BoardEngaged )
		{
			SettingsStation.Active?.Leave();
			InfoStation.Active?.Leave();
			SpectatorStation.Active?.Leave();
			if ( _controller != null )
				_controller.UseLookControls = true;
			return;
		}

		// Un-plant: put the body back where we stood when we sat, so the controller
		// hand-back below lands exactly where the camera blend-out targets (the pose
		// captured at Engage) instead of snapping from the seat spot. Remote clients see
		// us pop back to standing as we get up — the natural "stand up" moment.
		if ( _movedForSeat )
		{
			WorldPosition = _seatReturnPos;
			WorldRotation = _seatReturnRot;
			_movedForSeat = false;
		}

		ChessStation.Active?.Leave();
		SettingsStation.Active?.Leave();
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

	/// <summary>Ease the camera to the local player's seat anchor. The anchor is
	/// pre-aimed down at the board center by ChessRing, so no target math here.</summary>
	void UpdateLockedCamera()
	{
		var anchor = ChessStation.Active?.SeatAnchor( ChessStation.ActiveSeat );
		if ( anchor == null || _cameraObject == null ) return;

		float t = Math.Clamp( _engageTime / CamBlendTime, 0f, 1f );
		t = 1f - MathF.Pow( 1f - t, 3f ); // ease-out cubic, same curve as the leave blend

		_cameraObject.WorldPosition = Vector3.Lerp( _camFromPos, anchor.WorldPosition, t );
		_cameraObject.WorldRotation = Rotation.Slerp( _camFromRot, anchor.WorldRotation, t );
	}
}
