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
		EnsureSpectatorScreen();
		EnsureVoiceScreen();

	}

	/// <summary>The scene-authored UI ScreenPanel (the "UI" GameObject) — the self-attach target
	/// for the HUD / spectator / voice screens below. It skips runtime-built ScreenPanels: the
	/// client-local music HUD (LocalMusicSystem) spawns its OWN ScreenPanel on a NotSaved GO, and
	/// these screens must never land on it. The old code just took the first ScreenPanel and
	/// trusted "first is the scene root" — true only by luck of enumeration order once a second
	/// ScreenPanel exists. Filtering on NotSaved makes it true by construction.</summary>
	ScreenPanel SceneUiScreen()
	{
		foreach ( var screen in Scene.GetAllComponents<ScreenPanel>() )
		{
			if ( screen.GameObject.Flags.Contains( GameObjectFlags.NotSaved ) )
				continue; // a runtime client-local panel (e.g. LocalMusic), not the scene root
			return screen;
		}
		return null;
	}

	/// <summary>Attach the seated-game HUD to the scene's ScreenPanel at runtime
	/// (local player only) — no scene rewire needed for M2, same self-heal spirit
	/// as LobbyRoom.EnsureChessRing.</summary>
	void EnsureGameHud()
	{
		var screen = SceneUiScreen();
		if ( screen == null ) return;
		if ( screen.Components.Get<Gambit.UI.GameHud>() == null )
			screen.GameObject.AddComponent<Gambit.UI.GameHud>();
	}

	/// <summary>Attach the spectator-wall channel picker to the scene ScreenPanel at
	/// runtime (M5) — same self-heal as EnsureGameHud, so no scene rewire is needed. The
	/// screen draws nothing until the player engages the SpectatorStation.</summary>
	void EnsureSpectatorScreen()
	{
		var screen = SceneUiScreen();
		if ( screen == null ) return;
		if ( screen.Components.Get<Gambit.UI.Screens.SpectatorScreen>() == null )
			screen.GameObject.AddComponent<Gambit.UI.Screens.SpectatorScreen>();
	}

	/// <summary>Attach the proximity-voice driver + HUD (M12) to the scene ScreenPanel at runtime
	/// (local player only) — same self-heal as EnsureGameHud, so no scene rewire is needed. VoiceScreen
	/// is the keyboard driver (G toggles voice, B the mute roster); VoicePanel is its chip/roster HUD.
	/// Both are strictly client-local (the mute/enabled state lives in per-user cookies), so parenting
	/// them off the ScreenPanel — not off a networked object — is what keeps them from riding anyone's
	/// snapshot (see the HUD-parenting rule in CLAUDE.md).</summary>
	void EnsureVoiceScreen()
	{
		var screen = SceneUiScreen();
		if ( screen == null ) return;
		if ( screen.Components.Get<VoiceScreen>() == null )
			screen.GameObject.AddComponent<VoiceScreen>();
		if ( screen.Components.Get<Gambit.UI.Screens.VoicePanel>() == null )
			screen.GameObject.AddComponent<Gambit.UI.Screens.VoicePanel>();
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
		// Hiding this tag in 2D is the panel's own job now (NameTagPanel gates on
		// ChessStation.LocalNadir), alongside the station occupancy sign — one rule, both panels.
	}

	protected override void OnUpdate()
	{
		UpdateSeatedAt();

		if ( IsProxy )
		{
			RestoreProxyVisibility();
			UpdateProxySeating();
			return;
		}

		// Fell off the map: only possible while roaming (the controller is off while
		// engaged), so catch it before the engage/leave handling below.
		if ( !Engaged && !_leaving && WorldPosition.z < FallKillZ )
			Respawn();

		// Publish the display name for everyone's name tags. Load() is cached.
		// DisplayName is the single source of truth — the Steam persona name.
		var data = Gambit.Game.PlayerData.Load();
		var uname = data?.DisplayName() ?? "";
		if ( GambitName != uname ) GambitName = uname;


		if ( _leaving )
		{
			UpdateLeaveCamera();
			return;
		}

		if ( Engaged )
		{
			// Escape or Back stands up / closes the wall board. (Start auto-sets
			// EscapePressed; the Back button is wired through the "Back" action.)
			// Standing up mid-game NO LONGER resigns (M17) — RequestLeave just gets you
			// up to roam, keeping the seat and the live game; resign is its own button.
			// The info/welcome board also closes on E (its own hint says "E or Esc").
			if ( Input.EscapePressed || Input.Pressed( "Back" )
				|| ( InfoStation.Active != null && Input.Pressed( "use" ) ) )
			{
				Input.EscapePressed = false;
				RequestLeave();
				return;
			}

			// Keep our OWN avatar trimmed while seated, re-asserted every frame — a seat
			// switch re-plants Terry in front of the orbited camera, and the engage-time
			// snapshot can miss a late/re-enabled renderer or a cosmetic that finished
			// downloading after we sat (ApplyAsync is async, and workshop items really do
			// land seconds late). Local only (this whole method returns early for a proxy),
			// so remote/networked Terries stay drawn — M13 depends on seeing the other
			// player. Chess seats only; wall boards leave you standing and visible.
			if ( ChessStation.Active != null )
			{
				HideLocalAvatar();
				ApplySitPose();
			}

			// Only the chess seat drives the camera; wall boards leave it where it is.
			// A seat switch schwoops the avatar (UpdateSeatSwitch) and the child camera
			// rides along — it takes priority until it lands, then the locked camera holds.
			if ( _switching )
				UpdateSeatSwitch();
			else if ( ChessStation.Active != null )
				UpdateLockedCamera();
			return;
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

	/// <summary>Which station and seat this avatar occupies, or null. Derived from the
	/// <c>[Sync(FromHost)]</c> occupancy, so it is true on EVERY client for EVERY player —
	/// which is the whole authority story for the seated pose: nothing about it is
	/// networked, because everything it needs already is.</summary>
	public (ChessStation Station, ChessSeat Seat)? SeatedAt { get; private set; }

	/// <summary>Recompute <see cref="SeatedAt"/>. Cheap enough to do per player per frame:
	/// it is a walk of ~8 stations, the same order as FindNearbySeat, which already does
	/// this every frame over stations × seats.</summary>
	void UpdateSeatedAt()
	{
		// Our OWN seat comes from ChessStation.Active, not from the [Sync] fields: that
		// covers the optimistic-claim window between pressing E and the host's occupancy
		// landing, which is exactly the moment we sit down.
		if ( !IsProxy && ChessStation.Active is { } active )
		{
			SeatedAt = (active, ChessStation.ActiveSeat);
			return;
		}

		ulong id = Network.Owner?.SteamId ?? 0;
		if ( id != 0 )
		{
			foreach ( var s in Scene.GetAllComponents<ChessStation>() )
			{
				if ( s.WhiteSteamId == id ) { SeatedAt = (s, ChessSeat.White); return; }
				if ( s.BlackSteamId == id ) { SeatedAt = (s, ChessSeat.Black); return; }
			}
		}

		SeatedAt = null;
	}

	bool _proxySeated;

	/// <summary>
	/// Sit a REMOTE player's citizen down (M13) — and stop their own PlayerController
	/// standing it straight back up.
	///
	/// <para><b>This is the milestone's real difficulty, and it is not a maybe.</b>
	/// <c>MoveMode.OnUpdateAnimatorState</c> — the engine's default move mode — opens with
	/// <c>renderer.Set( "sit", 0 )</c>. A proxy's PlayerController is ENABLED (this method's
	/// own OnUpdate early-returns before we could disable it, and its Renderer self-heals in
	/// PlayerController.Elements.cs, so it really is animating), which means it runs
	/// UpdateAnimation → Mode.UpdateAnimator → OnUpdateAnimatorState EVERY FRAME and stomps
	/// `sit` back to 0. Whatever we write, a remote terry stands up again the same frame. It
	/// also writes Renderer.WorldRotation from EyeAngles in OnRotateRenderBody, which would
	/// fight the seated facing.</para>
	///
	/// <para><b>UseAnimatorControls, not Enabled.</b> The surgical cut: PlayerController's
	/// call site is <c>if (UseAnimatorControls &amp;&amp; Renderer.IsValid())
	/// UpdateAnimation(Renderer)</c>, so clearing it silences BOTH the sit stomp and the body
	/// rotation while leaving the rest of the controller — physics, collision, the networked
	/// transform we actually want — completely alone. Disabling the whole controller would
	/// also work and would be a much bigger hammer, and it is not needed.</para>
	///
	/// <para>It self-heals if it ever leaks through a snapshot (it is a serialised
	/// [Property] on a NetworkMode.Snapshot object, like BodyGroups): this runs every frame
	/// on every client and restores it the moment the player stands.</para>
	/// </summary>
	void UpdateProxySeating()
	{
		bool seated = SeatedAt != null && ChessRing.Instance is not { TerrySeated: false };

		// Written only when wrong, so a standing proxy costs one comparison.
		if ( _controller != null && _controller.UseAnimatorControls == seated )
			_controller.UseAnimatorControls = !seated;

		if ( seated )
			ApplySitPose();
		else if ( _proxySeated )
		{
			// Only on the transition: once the animator is back on, the controller writes
			// sit = 0 itself every frame and this would just be saying it twice.
			ClearSitPose();
			ClearHandPose();
		}

		_proxySeated = seated;
	}

	/// <summary>
	/// Force a REMOTE player's avatar back to fully drawn, every frame (M13).
	///
	/// <para><b>This exists because of the snapshot trap, and it will bite without it.</b>
	/// <c>GameObject.Serialize</c> serialises Tags, and <c>ModelRenderer.BodyGroups</c> is a
	/// plain serialised [Property]. Per CLAUDE.md's issue-#12 rule, a joining client
	/// REBUILDS every <c>NetworkMode.Snapshot</c> object from the host's LIVE state — and
	/// the PlayerTemplate's Body child is NetworkMode 2. So if the HOST is sitting at a
	/// table with its head bodygroup off and a "viewer" tag on, a joiner rebuilds the host's
	/// avatar HEADLESS and nothing ever corrects it, because this method's own early-return
	/// is what stops it. Same mechanism as the music board rendering open and unstyled;
	/// different victim.</para>
	///
	/// <para><b>Only the BODYGROUP actually needs us, and that is worth being precise
	/// about.</b> The engine already handles the tag: <c>UpdateBodyVisibility()</c>
	/// (PlayerController.Animation.cs) ends in <c>if (IsProxy) viewer = false;</c> then
	/// <c>(Renderer?.GameObject ?? GameObject).Tags.Set("viewer", viewer)</c> — and it runs
	/// on proxies, because we only ever disable our OWN controller. So a leaked "viewer"
	/// tag is scrubbed one frame later by the engine itself. Nothing scrubs BodyGroups.
	/// The tag line below is kept anyway as belt-and-braces (the engine's pass is gated on
	/// UseCameraControls and a live Scene.Camera) and is written to the same GameObject the
	/// engine picks, so the two cannot disagree.</para>
	///
	/// <para>Deliberately unconditional: it WRITES two values that are almost always
	/// already correct, and the alternative — tracking whether this particular proxy might
	/// have arrived tainted — is state that can itself be wrong. A proxy's clothing is left
	/// alone: those GOs are created locally by each client's own ApplyAsync and are in
	/// nobody's snapshot, which is exactly why TrimSeatedAvatar prefers them.</para>
	/// </summary>
	void RestoreProxyVisibility()
	{
		// Never our own doing — but the host's live state may have ridden the snapshot here.
		var bodyGo = _bodyRenderer?.GameObject ?? GameObject;
		if ( bodyGo.IsValid() )
			bodyGo.Tags.Set( ViewerTag, false );

		_bodyRenderer?.SetBodyGroup( HeadGroup, BodyGroupOn );
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

	/// <summary>Auto-open the welcome/info board the first time a player ever loads the
	/// lobby (until they dismiss it once — PlayerData.InfoPanelSeen). Retries each frame
	/// until the InfoStation exists (InfoWall builds it at runtime).</summary>
	void TryAutoShowInfo()
	{
		// Let the sign-in modal go first on a brand-new profile; it retries next frame.

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
	/// spots (ChessStation.SeatWorldPosition), and the closest free one wins.
	///
	/// <para>You take the side you actually walk up to — the board is already oriented
	/// with that side toward you, so seating you anywhere else would put you behind the
	/// wrong pieces. That is why we no longer override the first sitter to White (the old
	/// CLAUDE.md D1 convention). White still moves first regardless of who sat first.</para>
	///
	/// <para>This sentence is what the info boards' "you play the colour you walked up to"
	/// is checked against, so keep it true.</para></summary>
	void FindNearbySeat()
	{
		NearbyStation = null;
		float bestDist = InteractRange;

		ulong mine = Connection.Local?.SteamId ?? 0;
		foreach ( var station in Scene.GetAllComponents<ChessStation>() )
		{
			foreach ( var seat in new[] { ChessSeat.White, ChessSeat.Black } )
			{
				// A seat someone else holds is skipped; my OWN seat (kept while roaming
				// mid-game) stays offered so I can walk back and sit down to resume.
				if ( station.SeatTaken( seat ) && station.SeatSteamId( seat ) != mine ) continue;

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

	// ── Standing up vs resigning (decoupled M17) ──
	//
	// Standing up NO LONGER resigns. You can get up and walk around while your seat
	// stays claimed and your game stays live — your clock runs on lichess exactly as
	// a real game does, and gamchess keeps it alive as long as your client is polling
	// (its abandonment sweep only fires if the client actually drops). Resign is its
	// own explicit, confirmed button on the seated HUD (see Resign()).
	//
	// The two-stage confirm now belongs to RESIGN alone: a first press arms, a second
	// within the window commits. GameHud reads LeaveArmed for that button's label.

	RealTimeSince _leaveArm = 999f;
	const float LeaveConfirmWindow = 3f;

	/// <summary>A resign-confirm is armed (the Resign button shows "Sure?").</summary>
	public bool LeaveArmed => _leaveArm < LeaveConfirmWindow;

	/// <summary>Escape / the Leave button while engaged. Immediate for wall
	/// boards, finished games and untouched boards; two-stage when it would
	/// forfeit a live game.</summary>
	/// <summary>Stand up (Escape / Back). This NO LONGER resigns — decoupled in M17.
	///
	/// <para>Standing up from a LIVE game just gets you out of the seat to roam: the seat
	/// stays claimed and the relay keeps polling, so the game stays live on lichess and
	/// your clock runs while you're away. Walk back and sit at the same seat to resume;
	/// resign is its own explicit button (<see cref="Resign"/>). This is what lets
	/// gamchess "let lichess handle it" — a still-connected player just risks flagging,
	/// and only a truly-gone client is resigned by the abandonment sweep.</para>
	///
	/// <para>Anything that ISN'T a live game is a full leave: an idle or waiting seat, a
	/// finished game, or a wall board. A pending seek/challenge is withdrawn on the way
	/// out (its held connection or an explicit /cancel), and a finished lichess game is
	/// dismissed so its server-side slot doesn't linger.</para></summary>
	public void RequestLeave()
	{
		var station = ChessStation.Active;
		var controller = Gambit.Game.LocalGameController.For( station );
		var lichess = Gambit.Game.LichessGameController.For( station );

		bool liveGame = ( lichess is { Engaged: true, Playing: true } && lichess.LocalSeat != null )
			|| ( controller is { Playing: true } && controller.LocalSeat != null
				&& ( controller.Game?.MoveCount ?? 0 ) > 0 );

		// Live game: get up and roam, keeping the seat and the game. No resign, no
		// Clear() — the controller stays Engaged and polling so the game stays live and
		// the abandonment sweep doesn't treat us as gone.
		if ( liveGame )
		{
			Disengage( keepSeat: true );
			return;
		}

		// Not a live game — a clean leave that releases the seat. Withdraw a pending
		// seek/challenge (its held connection IS the seek; a challenge needs an explicit
		// /cancel or it stays acceptable for hours), and dismiss a finished game so its
		// server-side slot and event stream don't linger to the 10-min sweep.
		if ( lichess is { AwaitingOpponent: true } )
			lichess.CancelWaiting();
		if ( lichess is { State: { finished: true } } )
			lichess.DismissFinished();
		lichess?.Clear();

		Disengage( keepSeat: false );
	}

	/// <summary>The table where the local player has a LIVE game they've stepped away from
	/// — seated (occupancy still ours) at a board whose game is running, while our camera
	/// is not at that board. Null if none. Drives the roaming reminder, so you don't forget
	/// a game (and a ticking clock) you walked away from. Covers a relayed lichess game and
	/// a local two-seat game alike.</summary>
	public ChessStation RoamingLiveGame()
	{
		ulong mine = Connection.Local?.SteamId ?? 0;
		if ( mine == 0 ) return null;

		foreach ( var s in Scene.GetAllComponents<ChessStation>() )
		{
			if ( ChessStation.Active == s ) continue;                 // we're AT this board
			if ( s.WhiteSteamId != mine && s.BlackSteamId != mine ) continue;  // not our seat

			var lichess = Gambit.Game.LichessGameController.For( s );
			var local = Gambit.Game.LocalGameController.For( s );
			if ( ( lichess is { Engaged: true, Playing: true } ) || ( local is { Playing: true } ) )
				return s;
		}
		return null;
	}

	/// <summary>The armed premove UCI at station <paramref name="s"/>, or null. Read off
	/// the same IBoardGame seam the board does, so it covers a lichess or local game.
	/// Surfaced in the roaming reminder: standing up keeps a premove armed (it lives on the
	/// controller, not the seat), and showing it is how you know it survived walking away.</summary>
	public string PremoveAt( ChessStation s )
	{
		if ( s == null ) return null;
		var src = Gambit.Game.BoardGame.Source(
			Gambit.Game.LocalGameController.For( s ), Gambit.Game.LichessGameController.For( s ) );
		return src?.PremoveUci is { Length: >= 4 } u ? u : null;
	}

	/// <summary>The table where the local player already has a live LICHESS game, other
	/// than <paramref name="except"/> — or null. Gates a SECOND lichess game: lichess does
	/// not document permission to play concurrent games through the Board API, so we relay
	/// only the first and any further table plays locally (SetupPanel hides the lichess
	/// options; the relay refuses server-side as a backstop). Only a lichess game counts —
	/// you can have local two-seat games at as many tables as you like.</summary>
	public ChessStation LichessGameElsewhere( ChessStation except )
	{
		foreach ( var s in Scene.GetAllComponents<ChessStation>() )
		{
			if ( s == except ) continue;
			if ( Gambit.Game.LichessGameController.For( s ) is { Engaged: true, Playing: true } )
				return s;
		}
		return null;
	}

	/// <summary>Resign the live game at the seat we're engaged at — the explicit,
	/// two-stage button on the seated HUD, unrelated to standing up. A first call arms
	/// the confirm (the button shows "Sure?"), a second within the window commits: it
	/// resigns on lichess (or ends the local game), then stands up and releases the seat.
	/// No-op if there is no live game to resign.</summary>
	public void Resign()
	{
		var station = ChessStation.Active;
		var controller = Gambit.Game.LocalGameController.For( station );
		var lichess = Gambit.Game.LichessGameController.For( station );

		// A lichess game is the one that counts — it is on the player's real record, so
		// it must be resigned THERE. The local controller can't (its ChessGame never
		// advances during a lichess game), so lichess takes precedence.
		bool lichessForfeits = lichess is { Engaged: true, Playing: true } && lichess.LocalSeat != null;
		bool localForfeits = !lichessForfeits
			&& controller is { Playing: true }
			&& controller.LocalSeat != null
			&& ( controller.Game?.MoveCount ?? 0 ) > 0;
		if ( !lichessForfeits && !localForfeits ) return;

		// Two-stage: first press arms, a second within the window commits.
		if ( !LeaveArmed )
		{
			_leaveArm = 0f;
			return;
		}
		_leaveArm = 999f;

		if ( lichessForfeits )
			lichess.ResignLocal();
		else
			controller.ResignLocal();

		// ResignLocal keeps polling to show the result; we've chosen to leave, so drop
		// it and release the seat. The local table resets as the seat empties.
		lichess?.Clear();
		Disengage( keepSeat: false );
	}

	public void Engage( ChessStation station, ChessSeat seat )
	{
		station.Enter( seat );
		if ( ChessStation.Active != station ) return; // someone else has it
		BeginEngage();
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

	/// <summary>Open the north-wall spectator board's channel picker — screen-space UI,
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
		_switching = false; // a fresh engage cancels any in-flight seat-switch schwoop
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

		SetSeatedPhysics( false );

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

		// Physically plant our avatar ON THE CHAIR at our side of the board. The transform
		// is networked from us (the owner), so every OTHER client's copy of our avatar
		// lands on the seat instead of standing wherever we walked up. And because the
		// body then stops moving, their PlayerController derives zero speed and drops it
		// out of the walk cycle into a plain idle — no more sliding/strafing across the
		// room (the parent-project bug).
		//
		// M13: this used to plant at SeatWorldPosition — the WALK-UP spot — keeping our
		// own standing z, so a "seated" player was a citizen standing bolt upright against
		// the table edge, and had been since the fork. Nobody noticed because
		// HideLocalAvatar meant you never saw yourself and the person opposite you was
		// standing the whole time. SeatSitWorldPosition is the chair; the two are
		// different questions.
		if ( ChessStation.Active is { } seatStation )
		{
			_seatReturnPos = WorldPosition;
			_seatReturnRot = WorldRotation;
			_movedForSeat = true;

			PlantOnSeat( seatStation, ChessStation.ActiveSeat );
		}

		// Hide our own avatar so it doesn't stand between the locked camera and the board.
		// Clear first (a fresh engage): Disengage clears on the way out too, so SwitchSeat
		// can safely APPEND to this set without wiping what BeginEngage already hid.
		_hiddenRenderers.Clear();
		HideLocalAvatar();
	}

	/// <summary>
	/// Keep our own seated avatar out of our own camera (never a remote/proxy one — those
	/// stay visible, which M13 leans on).
	///
	/// <para><b>Since M13 this trims rather than deletes.</b> The whole point of the
	/// milestone is that you can see your own forearms reach onto the board, so the body
	/// stays drawn and only the things that would fill the frame come off. What makes that
	/// affordable is arithmetic, not a mechanism: at the default anchor the frame's bottom
	/// edge lands at x ≈ −29.5, essentially the tabletop's edge — so the shot is already
	/// forearms-and-hands and nothing else, for free. Only the head is marginal (out by
	/// ~0.4, well inside the error bars on a guessed sit pose), so it is the one trim
	/// worth making.</para>
	///
	/// <para><b>Arms-only cannot be done any other way, and this is the record of it.</b>
	/// The citizen has no arms mesh: <c>citizen_bodygrouplist.vmdl_prefab</c> defines
	/// exactly five groups — Head, Chest, Legs, Hands, Feet — and the arms live inside
	/// <c>Torso_LOD0..3</c>, i.e. Chest. So <c>SetBodyGroup("Chest", 1)</c> takes the arms
	/// with it, and bone-zeroing can't help either (the arms hang off the spine chain, so
	/// collapsing the torso collapses them). As SeatEyeBlend descends, more torso enters
	/// frame and there is NOTHING to remove it with. That is the limit, not a TODO.</para>
	///
	/// <para>Collected live rather than at OnStart so dresser-spawned clothing renderers
	/// are caught, and re-runnable: a seat switch re-plants the body in front of the newly
	/// orbited camera. Already-handled renderers are skipped, so a per-frame call is safe.</para>
	/// </summary>
	void HideLocalAvatar()
	{
		// Hide the WHOLE body when the kill switch reverts to the pre-M13 world (TerrySeated
		// false), OR when 2D play mode suppresses seated bodies (M16) — they are noise under the
		// top-down camera. Same mechanism, same Disengage undo, for both.
		bool hideAll = ChessRing.Instance is { TerrySeated: false } || SeatedTerry.ForceHidden;
		if ( hideAll )
		{
			HideEveryRenderer();
			return;
		}

		// A 3D mode: if we had hidden everything (a live switch OUT of 2D, or the kill switch
		// flipped back on while seated), bring those renderers back before trimming — otherwise
		// the body stays invisible until we stand and re-sit. HideLocalAvatar runs every seated
		// frame, so this makes the mode switch take effect in place.
		ShowHiddenRenderers();
		TrimSeatedAvatar();
	}

	/// <summary>Re-enable every renderer <see cref="HideEveryRenderer"/> turned off and clear the
	/// list. Shared by Disengage (standing up) and the live 2D→3D switch in
	/// <see cref="HideLocalAvatar"/> — both undo the wholesale hide the same way.</summary>
	void ShowHiddenRenderers()
	{
		foreach ( var r in _hiddenRenderers )
		{
			if ( r.IsValid() )
				r.Enabled = true;
		}
		_hiddenRenderers.Clear();
	}

	/// <summary>The pre-M13 behaviour, kept behind ChessRing.TerrySeated: don't draw our
	/// avatar at all while seated.
	///
	/// <para>Non-negotiable, per git history. 0f68c91 — "Don't draw the local avatar while
	/// seated (fixes Terry filling the camera after a seat switch)". A standing avatar
	/// planted at the walk-up spot puts its head's front face around x = −27, well inside
	/// the frame's bottom edge at −30.1. If the seated rig is wrong in the editor, this is
	/// the switch that makes the game playable again.</para></summary>
	void HideEveryRenderer()
	{
		foreach ( var r in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( !r.Enabled ) continue;
			r.Enabled = false;
			// Contains-guard so a per-frame call can't grow the re-enable list without
			// bound if something ever flip-flops a renderer back on.
			if ( !_hiddenRenderers.Contains( r ) )
				_hiddenRenderers.Add( r );
		}
	}

	/// <summary>
	/// Take the head and our cosmetics out of OUR camera only, leaving the body — and the
	/// arms on it — drawn.
	///
	/// <para><b>Tag the GameObjects, never the class.</b> The engine already does the hard
	/// part: <c>ClothingContainer.Dressing</c> puts a <c>"clothing"</c> tag on every
	/// spawned cosmetic GO. It would be one line to
	/// <c>RenderExcludeTags.Add("clothing")</c> — and it would strip the OPPONENT's clothes
	/// too, because the exclude list is per-camera, not per-object. Remote viewers must keep
	/// seeing our swag; we tag our own GOs instead.</para>
	///
	/// <para><c>"viewer"</c> is the engine's own first-person tag
	/// (PlayerController.Camera.cs adds it to RenderExcludeTags). We add it explicitly
	/// anyway: while seated the controller is DISABLED, so its ModifyCamera isn't running
	/// to re-add it for us.</para>
	///
	/// <para><b>Per GameObject, with no inheritance to children.</b> The engine's check is
	/// <c>if (RenderExcludeTags.HasAny(c.GameObject.Tags)) continue;</c> — one GameObject at
	/// a time. That the engine tags each clothing GO separately rather than relying on Body
	/// is the corroboration, not an accident.</para>
	/// </summary>
	void TrimSeatedAvatar()
	{
		// Has(), not Contains(): ITagSet's API is Has/Set/Add — Contains would only bind
		// through LINQ's IEnumerable extension, which isn't imported here and would be an
		// O(n) walk of a HashSet-backed set if it were.
		var cam = Scene?.Camera;
		if ( cam != null && !cam.RenderExcludeTags.Has( ViewerTag ) )
			cam.RenderExcludeTags.Add( ViewerTag );

		// Cosmetics: created LOCALLY by DressFromAvatar's ApplyAsync on each client, so
		// they are in nobody's network snapshot and tagging them cannot leak. Prefer them
		// for exactly that reason — see the bodygroup below, which does not have this
		// luxury.
		foreach ( var r in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( r == _bodyRenderer ) continue;                     // the body IS the point
			if ( !r.GameObject.Tags.Has( ClothingTag ) ) continue;  // not a cosmetic
			r.GameObject.Tags.Set( ViewerTag, true );
			if ( !_viewerTagged.Contains( r.GameObject ) )
				_viewerTagged.Add( r.GameObject );
		}

		// The head. SetBodyGroup(string, INT) — the string overload silently matches
		// nothing on four of the five groups, because only Head's choices carry
		// name = "on"/"off"; the other four BodyGroupChoice nodes have no name field at
		// all. It would work here and fail the moment it was copied to Chest.
		_bodyRenderer?.SetBodyGroup( HeadGroup, BodyGroupOff );
		_headHidden = true;
	}

	// The engine's own first-person exclude tag, and the tag its dresser puts on every
	// cosmetic GameObject (ClothingContainer.Dressing).
	const string ViewerTag = "viewer";
	const string ClothingTag = "clothing";
	const string HeadGroup = "Head";
	const int BodyGroupOn = 0;
	const int BodyGroupOff = 1;

	readonly List<GameObject> _viewerTagged = new();
	bool _headHidden;

	/// <summary>
	/// Sit our own citizen down, by writing the animgraph parameters ourselves.
	///
	/// <para><b>Why not the engine's chair.</b> BaseChair / SitMoveMode need a LIVE
	/// PlayerController and ours is disabled the moment we sit — that is the whole seat
	/// design. So we do what <c>BaseChair.UpdatePlayerAnimator</c> does anyway, which is
	/// exactly this list of Set() calls. <b>And not CitizenAnimationHelper either</b>: it is
	/// a thin Target.Set(...) wrapper that doesn't expose the finger parameter M13 needs,
	/// and it would fight PlayerController while roaming. Writing _bodyRenderer directly is
	/// the API BeginEngage already uses two methods up.</para>
	///
	/// <para><b>Every frame, not once at engage.</b> Nothing else writes these while the
	/// controller is off, but a hotload, a re-dress or a seat switch can all land between
	/// engaging and standing, and a pose that is asserted once is a pose that quietly
	/// reverts. Same reasoning as re-asserting the trim above.</para>
	///
	/// <para><b>"sit" is an int, not a bool, and "b_sit" DOES NOT EXIST.</b> citizen.vanmgrph
	/// declares <c>sit</c> as a CEnumAnimParameter — not_sitting, sitting_01..03,
	/// sitting_ground_01..04 — so 1 is BaseChair's AnimatorSitPose.Chair1 (≡ the helper's
	/// SittingStyle.Chair). There is no <c>b_sit</c> parameter anywhere in the graph: the
	/// only thing in all of sbox-public that writes it is CitizenAnimationHelper.Sitting,
	/// and BaseChair — the engine's own working chair — never touches it. Setting it is a
	/// silent no-op, which is the worst kind of wrong.</para>
	/// </summary>
	void ApplySitPose()
	{
		var r = _bodyRenderer;
		if ( r == null ) return;

		// The kill switch reverts the POSE too, not just our own view of it — see
		// PlantOnSeat. Remote players see a standing citizen at the walk-up spot, exactly
		// as they have since the fork.
		if ( ChessRing.Instance is { TerrySeated: false } )
		{
			ClearSitPose();
			return;
		}

		// M14 cross-cutting spike: SitPoseChair (1 = sitting_01) is the shipped default;
		// SeatedHandSpikes.SitPose lets the editor flip to sitting_02 live to see whether it
		// leans the shoulders over the table for free. Read every frame, so a console flip
		// takes effect next frame on this sitter and every proxy.
		r.Set( "sit", SeatedHandSpikes.SitPoseClamped );

		// INCHES, hard-clamped to ±12 by the graph. The pose carries its own seat height
		// above the avatar's origin (which is at the FEET — see SeatSitWorldPosition); this
		// only trims it. citizen_sit.vsubgrph's own comment: "30 units at the source, 12
		// after scaling to inches."
		r.Set( "sit_offset_height", ChessRing.Instance?.SitOffsetHeight ?? 0f );

		// The same grounding stomps BaseChair makes, for the same reason: with the
		// controller off nothing else writes them, and a stale airborne/ducked flag would
		// fight the sit pose.
		r.Set( "b_grounded", true );
		r.Set( "b_climbing", false );
		r.Set( "b_swim", false );
		r.Set( "duck", false );

		// Re-assert the seated facing onto the body renderer whenever the animator is FROZEN
		// (UseAnimatorControls off — a seated PROXY). This is the actual fix for "seated terries
		// face random directions", and the M13 assumption it corrects is worth stating.
		//
		// M13 froze the proxy body (UseAnimatorControls = false) on the theory that a frozen
		// renderer would INHERIT the parent GameObject's board-facing yaw (PlantOnSeat). It does
		// not. PlayerController.OnRotateRenderBody — the only writer of renderer.WorldRotation —
		// writes an ABSOLUTE WORLD rotation of FromYaw(EyeAngles.yaw), overwriting any inheritance;
		// freezing merely stops it UPDATING, it doesn't reset the child's local rotation. So the
		// body stays pinned at whatever world yaw it last held: the owner's EyeAngles, which are
		// [Sync]'d to every proxy and — because the owner DISABLES its controller when it sits
		// (BeginEngage) — are frozen at the ROAMING look direction from the instant E was pressed,
		// not the board. It only ever looked right because you're usually facing the table when you
		// sit; approach from an angle and the terry faces that angle instead. Nothing on the parent
		// transform or the snapshot enters into it — this is live, on every already-joined client.
		//
		// So point the body at the board directly, from the seat geometry, every frame. Only while
		// frozen: an OWNER at a chess seat also has its controller disabled, but its own reach view
		// was tuned against that near-board eye yaw, so leave that path untouched.
		if ( _controller is { UseAnimatorControls: false }
			&& SeatedAt is { } s && SeatFacing( s.Station, s.Seat ) is { } face )
			r.WorldRotation = face;
	}

	/// <summary>Stand the citizen back up. Pairs with <see cref="ApplySitPose"/>; the
	/// controller takes the rest back over the moment it is re-enabled.</summary>
	void ClearSitPose()
	{
		_bodyRenderer?.Set( "sit", SitPoseStanding );
		_bodyRenderer?.Set( "sit_offset_height", 0f );
	}

	/// <summary>citizen.vanmgrph's <c>sit</c> enum: 0 = not_sitting, 1 = sitting_01.
	/// BaseChair spells these AnimatorSitPose.None / .Chair1, which are the better
	/// names for identical values.</summary>
	const int SitPoseStanding = 0;
	const int SitPoseChair = 1;

	/// <summary>
	/// Put this seated citizen's working hand where <see cref="Gambit.Chess.TerryPose"/>
	/// says it goes. Called by <see cref="SeatedTerry"/> for every seat on every client.
	///
	/// <para><b>IK needs no GameObjects.</b> <c>SetIk(name, Transform)</c> takes a Transform
	/// directly; CitizenAnimationHelper's IkLeftHand/IkRightHand GameObject properties are
	/// editor sugar over exactly this call. So the target is lerped in code and handed
	/// straight over — no scene objects to spawn, parent, or clean up.</para>
	///
	/// <para><b>SetIk wants a WORLD transform</b> and converts internally
	/// (<c>tx = WorldTransform.ToLocal(tx)</c>). Handing it a local one silently puts the
	/// hand somewhere near the floor of the room.</para>
	///
	/// <para><b>Right hand works, left hand idles.</b> Chess is played one-handed.</para>
	/// </summary>
	public void ApplyHandPose( ChessStation station, ChessSeat seat, Gambit.Chess.HandPose pose,
		GameObject gesturePiece = null )
	{
		var r = _bodyRenderer;
		var ring = ChessRing.Instance;
		if ( r == null || ring == null || station == null ) return;
		if ( ring is { TerrySeated: false } ) return;

		// M14: the hands are a SPIKE, off by default — the shipped world is bodies only. The
		// bodies stay on their own gate (TerrySeated) above; this is the independent hand switch.
		if ( !SeatedHandSpikes.HandsOn )
		{
			if ( _reachSpikeApplied ) { r.ClearPhysicsBones(); _reachSpikeApplied = false; }
			ReleaseRiseIk( r );
			_riseApplied = Vector3.Zero;
			_leanApplied = Vector3.Zero;
			_pitchApplied = Vector3.Zero;
			return;
		}

		// Where the hand would rest with nothing to do: elbows on the table, in the X
		// margin. CLAUDE.md calls both X margins "kept clear — they are the seat cameras'
		// sightlines", and this spends one of them ON PURPOSE: the hands ARE the sightline's
		// subject. The board frame's half-extent is 21.75 and the tabletop's is 30, so 26
		// lands squarely in the 8.25-wide margin — on the table, clear of the board.
		float side = seat == ChessSeat.White ? -1f : +1f;
		var idle = new Vector3( side * ring.HandIdleX, side * ring.HandIdleY, ring.HandIdleZ );

		_pendingLean = 0f;
		_risePlan = null; // re-planned (or not) below; a frame with no plan eases the rise out
		Vector3 target = idle;
		if ( pose.OnBoard && pose.Weight > 0f )
		{
			// TerryPose deals in square INDICES and scalars precisely so it can be run on a
			// host with no engine. This is the half that needs Sandbox: turning them into a
			// place. Square index is rank*8+file, matching ChessBoardView.SquareUnderCursor.
			var from = SquareLocal( ring, pose.FromSquare );

			// ToTray: the hand is taking a captured piece off the board. TerryPose can't
			// know where a tray is — it may not know about geometry at all — so it says
			// "the tray" and this decides which. The VICTIM's, because that is where
			// ChessBoardView actually puts the piece, and a hand walking it somewhere else
			// would be a second answer to a settled question. The victim is whatever this
			// seat isn't.
			// …and only PART of the way: see HandDiscardReach. The victim's tray is across
			// the board, well past where an arm goes, and an unreachable IK target doesn't
			// fail politely — it straightens the arm and drags the shoulder after it.
			var to = pose.ToTray
				? Vector3.Lerp( from, ring.TrayHandLocalPosition( white: seat == ChessSeat.Black ),
					ring.HandDiscardReach )
				: SquareLocal( ring, pose.ToSquare );

			// ── The wrist is a CHILD of the PIECE (owner decision, 2026-07-19). ──
			// When the view supplied the performed piece's live GameObject, the board-space
			// target IS its position — the square/Travel math below is only the fallback
			// for a missing/destroyed piece (promotion, resync, probe poses). The piece
			// holds its origin through the approach and slides on its own clock; deriving
			// the hand from it every frame is what makes desync impossible rather than
			// merely tuned away — the one-clock rule.
			//
			// Height comes from THE PIECE'S OWN TOP (bounds), not a board-surface constant:
			// GraspHeight (10, "brush the king's 9.6") measured from the board put the
			// wrist a whole pawn above a pawn — a gap the deleted carry used to mask by
			// lifting the piece up to the hand. Bounds track the slide arc for free; the
			// max() guards a degenerate bounds read back to the base position.
			Vector3 on;
			if ( gesturePiece.IsValid() )
			{
				var p = gesturePiece.WorldPosition;
				float topZ = MathF.Max( gesturePiece.GetBounds().Maxs.z, p.z );
				on = station.WorldTransform.PointToLocal( new Vector3( p.x, p.y, topZ ) )
					+ Vector3.Up * ( SeatedHandSpikes.GraspClearance + ring.HandLift );
			}
			else
			{
				on = Vector3.Lerp( from, to, pose.Travel ) + Vector3.Up * ( pose.Height + ring.HandLift );
			}

			// ── Out-of-reach handling: Approach A (default) vs the cut M13 sphere clamp ──
			//
			// The seated arm is ~20u and the shoulder sits far back (gambit_terry: shoulder at
			// x −44.6, arm 19.9), so most of a 34-deep board is beyond it. A target past the arm
			// doesn't fail politely — the IK straightens and drags the shoulder after it.
			//
			// M14 Approach A (SeatedHandSpikes.UseSphereClamp == false): a square outside the
			// honest reach band just IDLES the hand — no reach animated — and ChessBoardView's
			// piece-slide finishes far moves as it has since M11. The taste call the doc asks for
			// is exactly this: does "touches near pieces, rests for far ones" read as playing?
			//
			// The old M13 sphere clamp is kept behind the lever ONLY to compare the two failure
			// modes live. It collapses the far ranks onto ~rank 2 (the frozen-hand trap the doc
			// documents) — flip gambit_terry_clamp to see it.
			//
			// ── M14 second attempt: the half-rise (default) ──
			// The planner owns the WHOLE reach story: the proven M14 lean first, then rising the
			// pelvis off the chair toward the target, bounded by the legs (planted feet that may
			// step) instead of the chair. Everything it returns is applied in
			// ApplyRiseOverrides; here we only take the clamped hand target — which equals the
			// true target for the 51/64 squares the harness proves honestly reachable, and sits
			// on the risen reach sphere (never straining) for the far corners the piece-slide
			// still finishes. Geometry: Code/Chess/HalfRise.cs, proven in the dotnet harness.
			if ( SeatedHandSpikes.HalfRiseOn && PlanRise( station, seat, on ) is { } risePlan )
			{
				_risePlan = risePlan;
				_pendingLean = 0f; // the plan carries its own lean; the M14 path must not double it
				target = Vector3.Lerp( idle, FromPlanner( risePlan.Hand, seat ), pose.Weight );
			}
			else if ( SeatedHandSpikes.NaturalLean && ShoulderLocal( station ) is { } sh0 )
			{
				// ── Reach like a person: lean in only as far as needed, then reach ──
				//
				// A seated player reaches a far piece by leaning from the waist, not by growing the
				// arm. Lean the torso toward the target by the shortfall (capped at MaxLean) —
				// ApplyReachSpikes turns _pendingLean into the actual bone override that moves the
				// shoulder forward. Then clamp the reach to the LEANED envelope so the arm never
				// straightens or drags (the thing that read as broken). ChessBoardView's piece-slide
				// covers any residual for the farthest squares — which is just sliding a piece you
				// can't quite place. Near squares: deficit ≤ 0, no lean, upright, exact reach.
				float reach = ring.HandReach;
				float deficit = ( on - sh0 ).Length - reach;
				_pendingLean = deficit > 0f ? Math.Min( deficit, SeatedHandSpikes.MaxLean ) : 0f;

				float fwd = seat == ChessSeat.White ? +1f : -1f;
				var leaned = sh0 + new Vector3( fwd * _pendingLean, 0f, 0f );
				var reachOut = on - leaned;
				if ( reachOut.Length > reach )
				{
					// Same rule as the half-rise planner's step 5: clamp AT THE TARGET'S
					// HEIGHT (slice the sphere at its Z, spend the shortfall horizontally),
					// never onto the raw sphere — a high shoulder puts that point visibly
					// ABOVE the board and the hand reads as flying up for far ranks.
					float dz = reachOut.z;
					float budgetSq = reach * reach - dz * dz;
					float budget = budgetSq <= 1f ? 1f : MathF.Sqrt( budgetSq );
					var hdir = reachOut.WithZ( 0f );
					hdir = hdir.Length < 1e-4f ? new Vector3( fwd, 0f, 0f ) : hdir.Normal;
					on = new Vector3( leaned.x + hdir.x * budget, leaned.y + hdir.y * budget, on.z );
				}
				target = Vector3.Lerp( idle, on, pose.Weight );
			}
			else if ( SeatedHandSpikes.UseSphereClamp )
			{
				// Comparison lever: the cut M13 static sphere clamp (no lean — collapses far ranks).
				if ( ShoulderLocal( station ) is { } shoulder )
				{
					var reachOut = on - shoulder;
					if ( reachOut.Length > ring.HandReach )
						on = shoulder + reachOut.Normal * ring.HandReach;
				}
				target = Vector3.Lerp( idle, on, pose.Weight );
			}
			else if ( ring.SquareReachable( seat, pose.FromSquare ) )
			{
				target = Vector3.Lerp( idle, on, pose.Weight );
			}
			else
			{
				target = idle; // isolated Approach A: out of band → rest
			}
		}

		// ── Reach the target ON A DEADLINE during a gesture; ease at a rate otherwise ──
		//
		// A gesture's stages are a fixed budget (owner, 2026-07-19): the hand must be over
		// the piece when Reaching ends, at the destination when Carrying ends — however far
		// the reach. A RATE (1 − e^(−k·dt)) can't promise that: it lags in proportion to
		// distance, which is exactly the reported bug — on far reaches the timeline and the
		// piece holds expired while the hand was still commuting, both pieces finished on
		// their own slides, and the hand visibly "caught up" afterwards. So while a move is
		// animating, each frame covers dt / time-left of the remaining gap: arrival lands
		// exactly on the phase deadline, and when no time remains it degrades to the snap
		// the owner accepts. PhaseRemaining is timeline-units; wall seconds divide out the
		// rush and the speed slider — the same factors Advance scaled dt up by.
		//
		// Off a gesture (the fade back to rest) the old gentle rate remains — relaxing is
		// the one motion with no deadline. HandChaseRate no longer drives gestures.
		if ( _handLocal is not { } current )
		{
			_handLocal = target;                       // first frame: be there
		}
		else if ( pose.Animating && gesturePiece.IsValid()
			&& pose.Phase is not Gambit.Chess.HandPhase.Reaching )
		{
			// GLUED: past the approach, the wrist is the piece's child — no easing, no
			// deadline, no lag to tear open. Continuous by construction: the deadline
			// below put the hand exactly here as Reaching ended, and the piece's own
			// position is continuous (it holds, then slides). Reach limits still apply
			// downstream — a hand that can't have the piece shadows it from as close as
			// the arm gets, at piece height.
			_handLocal = target;
		}
		else if ( pose.Animating )
		{
			float speed = Gambit.Chess.TerryPose.SpeedScale <= 0f ? 1f : Gambit.Chess.TerryPose.SpeedScale;
			float remain = pose.PhaseRemaining / ( ( 1f + pose.Rush ) * speed );
			_handLocal = Vector3.Lerp( current, target,
				Math.Clamp( Time.Delta / MathF.Max( remain, Time.Delta ), 0f, 1f ) );
		}
		else
		{
			// The return to rest — quick (owner: "accelerate from the piece much faster").
			// A dedicated rate, not the old HoverChaseRate slider: hover is dead, and the
			// scene-serialized slider value would silently override any new default here.
			_handLocal = Vector3.Lerp( current, target,
				1f - MathF.Exp( -ReturnChaseRate * Time.Delta ) );
		}

		// Fingers down over the board, reaching ALONG THE ARM — the yaw follows the
		// shoulder→target bearing rather than the fixed seat-forward it used to be. The
		// fixed yaw is what cocked the wrist into a claw on any reach that wasn't dead
		// ahead: a deeply leaned shoulder reaches diagonally, and a hand forced to keep
		// pointing at the opponent from a diagonal forearm hyper-flexes at the wrist.
		//
		// PITCH follows the bearing too now (banner jank #2). It used to be a FIXED
		// ring.HandPitch (60°) nose-down, which on a far reach — where the forearm flattens
		// toward horizontal — forced the wrist to hyper-flex to keep pointing that far down.
		// So the effective pitch = the forearm's own declination below horizontal + a grasp
		// curl (SeatedHandSpikes.WristDrop), capped at ring.HandPitch. A steep near reach
		// keeps the full nose-down; a flat far reach relaxes toward the curl alone. Setting
		// WristDrop to the cap restores the old fixed-pitch behaviour exactly.
		float handYaw = seat == ChessSeat.White ? 0f : 180f;
		float handPitch = ring.HandPitch;
		if ( ShoulderLocal( station ) is { } shForYaw )
		{
			var bearing = _handLocal.Value - shForYaw;
			float horiz = bearing.WithZ( 0f ).Length;
			if ( horiz > 1f )
				handYaw = MathF.Atan2( bearing.y, bearing.x ) * ( 180f / MathF.PI );

			// Declination: how far below horizontal the arm points at the target (0 when the
			// hand is at or above shoulder height, positive reaching down). max(0,·) so a
			// slight reach-up never pitches the hand UPward past the curl.
			float decl = MathF.Atan2( -bearing.z, MathF.Max( horiz, 0.001f ) ) * ( 180f / MathF.PI );
			handPitch = MathF.Min( ring.HandPitch, MathF.Max( decl, 0f ) + SeatedHandSpikes.WristDrop );
		}
		// Roll swings the elbow OUT of the torso — the t-rex fix. The hand IK is fed a full
		// rotation, so the elbow pole follows this barrel-twist; 0 traps the arm in a vertical
		// plane and the elbow just drops. See SeatedHandSpikes.HandRoll (sign unverified).
		var rot = station.WorldRotation * Rotation.From( handPitch, handYaw, SeatedHandSpikes.HandRoll );

		// The IK aims a BONE, and the bone is the WRIST — so a target dropped straight on a
		// square puts the fingers past it and the piece under the palm. Pull the wrist back
		// along the hand's own axes so the grip lands where the player is looking.
		// gambit_terry's ruler prints hand_R, which is exactly this bone: tune against it.
		var world = station.WorldTransform.PointToWorld( _handLocal.Value )
			+ rot * ring.HandGripOffset;

		LastHandTarget = _handLocal.Value;
		// The wrist's ACTUAL target (grasp point + grip offset), station-local — so a reach
		// readout compares the bone against what it was really aimed at, not the grasp point
		// it sits deliberately back from. Without this a perfect reach reads as ~4 units off.
		LastHandIkTarget = station.WorldTransform.PointToLocal( world );

		// RIGHT HAND ONLY. Chess is played one-handed, and that is the rule.
		//
		// Worth knowing what it costs, because the measurement is unambiguous and someone
		// will otherwise re-file it as an aiming bug: a1 sits at y +17.1 while White's right
		// shoulder is near y −6, so reaching it is ~33 units — 23 of them pure sideways —
		// against an arm of roughly 24. The far rank is ~60 and nothing reaches it. On the
		// far side of the board the arm runs out and the hand stops short, pointing the
		// right way from as close as it gets.
		//
		// That is a fact about a 39-unit board in front of a 72-unit citizen, not something
		// a constant here can fix. gambit_terry's reach readout prints the ask beside the
		// achieved bone exactly so it stays visible as what it is.
		r.Set( "holdtype", HoldTypeItem );
		r.Set( "holdtype_handedness", HandednessRight );
		r.Set( "holdtype_pose_hand", pose.FingerClose );

		// The gesture's arrival deadline, in wall seconds — the SAME value the hand's own reach
		// lerp uses (PhaseRemaining, un-scaled by rush and the speed slider). RiseChaseK reads it
		// so the pelvis rise and foot pins land with the hand instead of lagging it. −1 off a
		// gesture, where the rise eases out on a rate.
		if ( pose.Animating )
		{
			float sp = Gambit.Chess.TerryPose.SpeedScale <= 0f ? 1f : Gambit.Chess.TerryPose.SpeedScale;
			_gestureRemain = pose.PhaseRemaining / ( ( 1f + pose.Rush ) * sp );
		}
		else
			_gestureRemain = -1f;

		if ( SeatedHandSpikes.HalfRiseOn )
		{
			// The half-rise path: ease + apply the pelvis/lean overrides and the foot/brace
			// IK, then PRE-COMPENSATE the right hand's target. The animgraph IK solves
			// BEFORE bone overrides apply, and the arm's chain rides the pelvis + spine
			// subtree — so an override translating that subtree by Δ carries the solved
			// hand by Δ too. Aiming at (true − Δ) is what lands the hand ON the true
			// target after the override. Δ is the APPLIED (eased) value, not the plan's,
			// so the compensation can never outrun the bones.
			ApplyRiseOverrides( r, station, seat );
			var trueAskWorld = world;
			world -= _riseApplied + _leanApplied + _pitchApplied;

			// ── The closed-loop hand servo ──
			// The doctor's pipeline dump proved every modelled stage exact: the pelvis and
			// spine overrides land to the decimal, and the animation-domain IK NAILS its
			// compensated target — yet the FINAL hand still lands ~5u off the true ask.
			// Some post-override native stage (procedural/twist bones, by elimination)
			// warps the last hop, and it is not readable from any API we have. So: don't
			// model it, MEASURE it — integrate last frame's final-hand-vs-true-ask error
			// into the target. Gated on the ask being stable (a moving target's error is
			// chase lag, not warp), clamped, decayed when the correction stops earning.
			// This also absorbs whatever the torso YAW does to the arm-root geometry,
			// which is what lets the yaw ship without exact modelling.
			if ( SeatedHandSpikes.ServoOn )
			{
				float sk = 1f - MathF.Exp( -ServoRate * Time.Delta );
				bool boneOk = r.TryGetBoneTransform( "hand_R", out var handNow );
				if ( _servoTrueAsk is { } prevAsk && boneOk )
				{
					var err = prevAsk - handNow.Position;

					// HORIZONTAL channel — gated on the ask being stable: a moving
					// target's horizontal error is chase lag, not warp, and winding
					// toward it would overshoot every gesture.
					if ( ( prevAsk - trueAskWorld ).Length < 2f && err.Length < 20f )
					{
						_handServo += err.WithZ( 0f ) * sk;
						if ( _handServo.Length > ServoClamp )
							_handServo = _handServo.Normal * ServoClamp;
					}
					else
					{
						_handServo = Vector3.Lerp( _handServo, Vector3.Zero, sk );
					}

					// VERTICAL channel — on whenever the ask's Z is genuinely locked
					// (owner report #3: far ranks still floated DURING moves). Through
					// Lifting/Carrying/Dropping — and at rest — the ask's Z is a
					// constant, so vertical error is native warp by definition even while
					// the ask sweeps horizontally; the stability gate that protects the
					// horizontal channel was exactly what left that warp uncorrected
					// mid-move. But during REACHING the Z is still ramping up from the
					// table, so vertical error there is chase lag — integrating it wound
					// the servo into a visible wobble (the jitter report): decay instead.
					// Softer rate than the horizontal channel plus a small deadband, so
					// the one-frame-delayed bone read can't hunt; same wild-read guard.
					float zk = 1f - MathF.Exp( -ServoZRate * Time.Delta );
					if ( pose.Phase is not Gambit.Chess.HandPhase.Reaching )
					{
						if ( MathF.Abs( err.z ) is < 20f and > 0.3f )
							_servoZ = Math.Clamp( _servoZ + err.z * zk, -ServoClamp, ServoClamp );
					}
					else
					{
						_servoZ *= 1f - zk;
					}
				}
				else
				{
					_handServo = Vector3.Lerp( _handServo, Vector3.Zero, sk );
					_servoZ *= 1f - sk;
				}
				_servoTrueAsk = trueAskWorld;
				world += _handServo + Vector3.Up * _servoZ;
			}

			// One-shot pipeline dump, second half: what was ACTUALLY applied and where the
			// bones ACTUALLY are (animation vs final). Splits "planner under-asked" from
			// "bones under-moved" from "solver missed the compensated target".
			if ( SeatedHandSpikes.RiseDebug )
			{
				Log.Info( "── rise debug: the skeleton ──" );
				Log.Info( $"   applied: rise=({_riseApplied.x:0.0},{_riseApplied.y:0.0},{_riseApplied.z:0.0})"
					+ $" |{_riseApplied.Length:0.0}|  lean=|{_leanApplied.Length:0.0}|"
					+ $"  feetIk={_riseFeetIk}  braceIk={_riseBraceIk}" );
				DumpBone( r, station, "pelvis" );
				DumpBone( r, station, SeatedHandSpikes.NaturalLeanBone );
				DumpBone( r, station, "arm_upper_R" );
				DumpBone( r, station, "hand_R" );
				Log.Info( $"   right-hand IK asked (station-local, post-compensation): "
					+ $"{Fmt( station.WorldTransform.PointToLocal( world ) )}  true ask: "
					+ $"{( LastHandIkTarget is { } ask ? Fmt( ask ) : "-" )}" );
				Log.Info( "   → 'final' should equal 'anim + applied' for pelvis and the lean bone; if it doesn't, the"
					+ " override isn't landing. hand_R final should equal the TRUE ask; short = the solver/compensation." );
				SeatedHandSpikes.RiseDebug = false;
			}
		}
		else
		{
			// Half-rise just switched off (a lever, or a sweep phase): release its foot/brace
			// IK and eased state HERE, not only in the lever command — the sweep flips
			// HalfRiseOn between phases without ClearHandPose, and pinned feet must never
			// leak into a seated phase's measurement.
			if ( _riseFeetIk || _riseBraceIk || _riseApplied != Vector3.Zero
				|| _leanApplied != Vector3.Zero || _pitchApplied != Vector3.Zero )
			{
				ReleaseRiseIk( r );
				_riseApplied = Vector3.Zero;
				_leanApplied = Vector3.Zero;
				_pitchApplied = Vector3.Zero;
			}

			// M14 Approach B/C: bend the skeleton BEFORE the IK solves, so the measurement reads
			// whether the two-bone IK re-derives against the changed shoulder. No-op unless a lever
			// is pulled; self-clears when they are all neutral.
			ApplyReachSpikes( r, station, seat );
		}

		// ── The final, solver-domain reach clamp — Z-LOCKED (owner report #2, 2026-07-19). ──
		// The planner clamps its ask against the PLAN's fully-risen shoulder, but the bones only
		// EASE toward the rise (RiseChaseK — now the gesture deadline, which shrinks this transient
		// but doesn't erase it: the rise still ramps over a phase's first frames) — so mid-move the
		// ask can sit outside the arm the solver actually has this frame, and the engine's own two-bone IK clamps the
		// wrist onto its REAL reach sphere along the shoulder→ask ray. That ray leaves a high
		// shoulder, so the wrist rode UP it: the "bumping against some sphere" float. The
		// engine's clamp is the one clamp we cannot re-aim — so never let it engage: pull the
		// ask inside the CURRENT animation-pose arm ourselves, slicing the sphere at the ask's
		// own Z (the piece height the timeline now locks), spending any transient shortfall
		// horizontally. As the body rises the budget grows and the hand extends outward — at
		// piece height the whole way. The servo keeps truing the residual native warp on top.
		if ( ShoulderLocal( station ) is { } shLocal )
		{
			var shWorld = station.WorldTransform.PointToWorld( shLocal );
			float arm = ( _measuredArm > 0f ? _measuredArm : FallbackArm ) - SeatedHandSpikes.ReachMargin;
			var outWorld = world - shWorld;
			if ( outWorld.Length > arm )
			{
				float dz = outWorld.z;
				float budgetSq = arm * arm - dz * dz;
				float budget = budgetSq <= 1f ? 1f : MathF.Sqrt( budgetSq );
				var flat = outWorld.WithZ( 0f );
				if ( flat.Length > 1e-4f )
					world = new Vector3( shWorld.x + flat.Normal.x * budget,
						shWorld.y + flat.Normal.y * budget, world.z );
			}
		}

		r.SetIk( IkRight, new Transform( world, rot ) );
	}

	/// <summary>Whether we currently hold a bone override (natural lean / spike lean / arm-scale),
	/// so the off-paths know to clear it exactly once rather than every frame.</summary>
	bool _reachSpikeApplied;

	/// <summary>How far the natural lean wants the torso forward THIS frame (world units), computed
	/// in <see cref="ApplyHandPose"/> from the reach shortfall and applied in
	/// <see cref="ApplyReachSpikes"/>. 0 = upright (target within reach, or lean off).</summary>
	float _pendingLean;

	/// <summary>
	/// M14 reach spikes, applied per frame just before the hand IK — <b>scaffolding, deleted with
	/// <see cref="SeatedHandSpikes"/> in the cleanup pass.</b>
	///
	/// <para><b>Approach B (lean):</b> override a bone (default <c>spine_2</c>, or
	/// <c>arm_upper_R</c> to test the IK root directly) forward toward the board each frame via
	/// <c>SetBoneTransform</c> → <c>SetBoneOverride</c>. The gating unknown the doc names — does the
	/// two-bone IK re-solve against this "final" override or the animator's original pose — is what
	/// the editor answers. We read the bone's world transform, shove it forward in the station's
	/// frame, and hand it back in model space (<c>WorldTransform.ToLocal</c>, exactly as
	/// <c>SetIk</c> does internally).</para>
	///
	/// <para><b>Approach C (arm scale):</b> best-effort — there is no runtime bone-scale API, so we
	/// set <c>Transform.Scale</c> on an override of the arm bones and let the editor say whether the
	/// native IK honours it as extra segment length. Almost certainly a no-op; run to prove it.</para>
	///
	/// <para><b>Clear-then-set every frame</b> is the pattern the engine's own physics-bone
	/// consumers use (<c>PhysicsBones.Update</c>): the override is persistent native state, so a
	/// stale one outlives its lever otherwise. The seated citizen is never ragdolled, so wiping the
	/// shared physics-bone slot costs nothing.</para>
	/// </summary>
	void ApplyReachSpikes( SkinnedModelRenderer r, ChessStation station, ChessSeat seat )
	{
		// The natural lean (default) drives the bone from _pendingLean; the manual Approach-B lean
		// lever is only live when the natural path is off, so the two never fight over the bone.
		bool natural = SeatedHandSpikes.NaturalLean && _pendingLean > 0.01f;
		bool manualLean = !SeatedHandSpikes.NaturalLean
			&& SeatedHandSpikes.LeanOn && SeatedHandSpikes.LeanForward != 0f;
		bool scale = SeatedHandSpikes.ArmScale != 1f;

		if ( !natural && !manualLean && !scale )
		{
			if ( _reachSpikeApplied ) { r.ClearPhysicsBones(); _reachSpikeApplied = false; }
			return;
		}

		if ( r.Model is not { } model ) return;

		r.ClearPhysicsBones();
		_reachSpikeApplied = true;

		// READ THE ANIMATION POSE, not the final one. TryGetBoneTransform returns the FINAL
		// transform — which already includes LAST frame's override — so offsetting/scaling that
		// compounds every frame and the bone flies off to thousands of units (it did: the first
		// sweep read 712 → 1702 → 3e14). TryGetBoneTransformAnimation is the post-animation,
		// pre-override pose, so a fixed offset/scale on it is stable frame to frame.
		if ( natural || manualLean )
		{
			string boneName = natural ? SeatedHandSpikes.NaturalLeanBone : SeatedHandSpikes.LeanBone;
			float amount = natural ? _pendingLean : SeatedHandSpikes.LeanForward;
			var bone = model.Bones.GetBone( boneName );
			if ( bone is not null && r.TryGetBoneTransformAnimation( bone, out var world ) )
			{
				// "Forward" = toward the board = station +X for White (who sits at −X), −X for Black.
				float fwd = seat == ChessSeat.White ? +1f : -1f;
				world.Position += station.WorldRotation * new Vector3( fwd * amount, 0f, 0f );
				r.SetBoneTransform( bone, r.WorldTransform.ToLocal( world ) );
			}
		}

		if ( scale )
		{
			// The IK chain is arm_upper_R → arm_lower_R1 → hand_R1; scale the two proximal segments.
			foreach ( var name in ArmScaleBones )
			{
				var b = model.Bones.GetBone( name );
				if ( b is null || !r.TryGetBoneTransformAnimation( b, out var w ) ) continue;
				w.Scale *= SeatedHandSpikes.ArmScale;
				r.SetBoneTransform( b, r.WorldTransform.ToLocal( w ) );
			}
		}
	}

	static readonly string[] ArmScaleBones = { "arm_upper_R", "arm_lower_R1" };

	// ───────────────────────────── M14: the half-rise runtime ─────────────────────────────

	/// <summary>This frame's plan from <see cref="Gambit.Chess.HalfRise"/> (planner frame:
	/// station-local, White-seat orientation), or null when the hand is idle / the target is
	/// seated-reachable with no rise. Written by ApplyHandPose, consumed by
	/// <see cref="ApplyRiseOverrides"/> in the same call.</summary>
	Gambit.Chess.RisePlan? _risePlan;

	/// <summary>The pelvis translation ACTUALLY applied this frame (world space, eased toward
	/// the plan) — also exactly what the IK pre-compensation subtracts, which is why it is
	/// stored rather than recomputed: the bones and the compensation must never disagree.</summary>
	Vector3 _riseApplied;

	/// <summary>Wall-seconds left in the current gesture phase (the hand's own arrival deadline),
	/// or −1 when no move is animating. Set each frame in <see cref="ApplyHandPose"/> and read by
	/// <see cref="RiseChaseK"/> so the pelvis rise and foot pins converge on the SAME deadline the
	/// hand does — see that helper for why a fixed rate was wrong.</summary>
	float _gestureRemain = -1f;

	/// <summary>How fast the rise (pelvis + foot pins) chases its target THIS frame.
	///
	/// <para><b>During a gesture it is a DEADLINE, not a rate</b> — the same one the hand reaches
	/// on. The hand darts to a piece in <see cref="Gambit.Chess.TerryPose.ReachTime"/> (0.12s) but
	/// the rise used to ease in on <c>RiseChaseRate</c> (~0.75s to settle): the body-lift the arm
	/// depends on lagged the reach, so on a far square the hand arrived SHORT and only extended as
	/// the rise caught up — the piece, already sliding, pulled away (owner report: "lerp on a
	/// lerp"). Converging the rise on the gesture's own phase deadline lands the lift WITH the
	/// hand, so the arm reaches as far as it honestly can immediately (the envelope ceiling is
	/// unchanged; this removes the lag, not the limit). The feet ride the same k so a fast pelvis
	/// can't tear a planted foot.</para>
	///
	/// <para>Off a gesture (relaxing back onto the chair) the old <c>RiseChaseRate</c> rate
	/// remains — a return has no deadline, exactly like the hand's own ease-out.</para></summary>
	float RiseChaseK() =>
		_gestureRemain >= 0f
			? Math.Clamp( Time.Delta / MathF.Max( _gestureRemain, Time.Delta ), 0f, 1f )
			: 1f - MathF.Exp( -SeatedHandSpikes.RiseChaseRate * Time.Delta );

	/// <summary>The spine-lean translation actually applied (world space, eased) — the arm
	/// chain rides pelvis + spine, so the hand compensation subtracts both.</summary>
	Vector3 _leanApplied;

	/// <summary>The torso pitch's eased shoulder-forward gain VECTOR (world). Its length
	/// through asin(len/torso) is the applied angle; the same vector is subtracted from the
	/// hand ask, so the rotation and the compensation cannot drift apart.</summary>
	Vector3 _pitchApplied;

	bool _riseFeetIk;   // foot_left/foot_right IK currently held
	bool _riseBraceIk;  // hand_left brace IK currently held
	float _measuredArm; // live |arm_upper_R→arm_lower_R| + |→hand_R|, 0 until first read
	float _measuredLeg; // live |leg_upper_R→leg_lower_R| + |→ankle_R|

	/// <summary>Fallbacks when the skeleton can't be read (M13's measured citizen).</summary>
	const float FallbackArm = 19.9f;
	const float FallbackLeg = 30f;

	/// <summary>Where the half-rise PLANTS the feet, relative to the pelvis (planner
	/// frame): a step's worth ahead, hip-width apart, on the floor. See PlanRise for why
	/// the animated ankles must not be used.</summary>
	const float FootPlantForward = 10f;
	const float FootPlantSpread = 7f;
	const float FootPlantZ = 1f;

	/// <summary>Grip/knee slack subtracted from the measured chains before planning, so the
	/// solver is never asked for a straight-locked limb.</summary>
	const float ReachMargin = 2f;
	const float LegMargin = 2f;

	/// <summary>
	/// Read the live skeleton, mirror everything into the planner's White frame, and plan
	/// this frame's reach. Null when any bone is unreadable — the caller then falls through
	/// to the seated M14 lean, so a missing bone degrades to the old world, never to a
	/// missing hand.
	/// </summary>
	Gambit.Chess.RisePlan? PlanRise( ChessStation station, ChessSeat seat, Vector3 grasp )
	{
		var r = _bodyRenderer;
		var ring = ChessRing.Instance;
		if ( r?.Model == null || ring == null ) return null;

		if ( ShoulderLocal( station ) is not { } shoulder ) return null;
		if ( BoneAnimLocal( station, "pelvis" ) is not { } pelvis ) return null;

		MeasureLimbs( r );

		// Feet may step toward the table but never INTO its base; the brace lands in the
		// player's left side margin on the tabletop. All derived from the ring's real
		// geometry rather than typed twice.
		var t = new Gambit.Chess.RiseTunables(
			Reach: _measuredArm - SeatedHandSpikes.ReachMargin,
			MaxLean: SeatedHandSpikes.MaxLean,
			RiseGrace: SeatedHandSpikes.RiseGrace,
			LegReach: SeatedHandSpikes.LegReachOverride > 0f
				? SeatedHandSpikes.LegReachOverride
				: _measuredLeg - LegMargin,
			MaxStep: SeatedHandSpikes.MaxStep,
			MaxRise: SeatedHandSpikes.MaxRise,
			RiseLift: SeatedHandSpikes.RiseLift,
			// The pitch budget in shoulder-forward units — COSMETIC-only by default: the
			// editor measured that override rotations do not carry child bones, so a
			// budget here is reach the plan promises and the skeleton can't deliver.
			PitchGain: SeatedHandSpikes.TorsoPitchMax > 0f
				? _measuredTorso * MathF.Sin( SeatedHandSpikes.TorsoPitchMax * ( MathF.PI / 180f ) )
				: 0f,
			// The hip cap is UNCONDITIONAL — a real body stops at the table; this is what
			// killed the plank, and it must not disappear with the pitch experiment.
			HipMaxX: -( ( ring.BoardSize + 3f ) * 0.5f * ring.TableScale - 2f ),
			FootMinX: -( ring.FootEdgeX + 1f ),
			BraceEngage: 6f,
			BraceMinX: -( ring.TableEdgeX - 6f ),
			BraceMaxX: 10f,
			BraceY: ( ring.BoardSize + 3f ) * 0.5f * ring.TableScale + 2.5f,
			BraceZ: ring.TableTopSurfaceZ );

		bool black = seat == ChessSeat.Black;
		var pelvisP = ToPlanner( pelvis, black );

		// CHOSEN plants, never the animated ankles. The sit pose tucks the feet ~25u
		// BEHIND the pelvis (measured live: ankle x −65 against pelvis −39), which spends
		// the whole ~30u leg budget before any rise — the doctor's first run showed the
		// rise collapsing to 11u because of exactly this. The feet are ours to place (the
		// foot IK pins them wherever we say, and EaseFootPin walks them there from the
		// tucked pose so nothing teleports): a person about to lean over a table steps
		// their feet under themselves first.
		var plantL = new Gambit.Chess.V3( pelvisP.X + FootPlantForward, +FootPlantSpread, FootPlantZ );
		var plantR = new Gambit.Chess.V3( pelvisP.X + FootPlantForward, -FootPlantSpread, FootPlantZ );

		var plan = Gambit.Chess.HalfRise.Plan(
			ToPlanner( grasp, black ), ToPlanner( shoulder, black ), pelvisP,
			plantL, plantR, t );

		// One-shot pipeline dump (gambit_terry_rise_dbg), first half: what the planner was
		// GIVEN and what it decided. The second half (applied values, bones) logs at the end
		// of ApplyHandPose once the overrides for this frame are down.
		if ( SeatedHandSpikes.RiseDebug )
		{
			Log.Info( "── rise debug: the planner ──" );
			Log.Info( $"   inputs (station-local, anim pose): grasp={Fmt( grasp )}  shoulder={Fmt( shoulder )}"
				+ $"  pelvis={Fmt( pelvis )}  plants (chosen, planner frame): "
				+ $"L=({plantL.X:0.0},{plantL.Y:0.0}) R=({plantR.X:0.0},{plantR.Y:0.0})" );
			Log.Info( $"   chains: arm measured {_measuredArm:0.0} → reach {t.Reach:0.0}"
				+ $"  leg measured {_measuredLeg:0.0} → budget {t.LegReach:0.0}"
				+ $"{( SeatedHandSpikes.LegReachOverride > 0f ? " (OVERRIDDEN)" : "" )}"
				+ $"  maxRise {t.MaxRise:0.0}  maxStep {t.MaxStep:0.0}  footMinX {t.FootMinX:0.0}" );
			Log.Info( $"   plan: lean {plan.Lean:0.0}  pitchGain {plan.PitchGain:0.0}  rise |Δ|={plan.PelvisDelta.Length:0.0}"
				+ $" Δ=({plan.PelvisDelta.X:0.0},{plan.PelvisDelta.Y:0.0},{plan.PelvisDelta.Z:0.0})"
				+ $"  hand=({plan.Hand.X:0.0},{plan.Hand.Y:0.0},{plan.Hand.Z:0.0})  residual {plan.Residual:0.0}"
				+ $"  stepped={plan.Stepped}  feet L=({plan.FootL.X:0.0},{plan.FootL.Y:0.0}) R=({plan.FootR.X:0.0},{plan.FootR.Y:0.0})" );
			Log.Info( "   → if |Δ| here is far below the harness's ~35 for a far square, the LIVE leg triangle is"
				+ " the collapse: check 'leg measured' and the pelvis/ankle Z inputs above. gambit_terry_leg <u> overrides it." );
		}

		return plan;
	}

	/// <summary>
	/// Turn the plan into the skeleton: ease the pelvis/lean toward it, override the bones,
	/// and pin the feet (and maybe the off hand) with pre-compensated IK targets. Runs every
	/// frame the hand is active, INCLUDING frames with no plan — that is how the rise eases
	/// back down instead of snapping when the reach ends.
	/// </summary>
	void ApplyRiseOverrides( SkinnedModelRenderer r, ChessStation station, ChessSeat seat )
	{
		Vector3 wantRise = Vector3.Zero, wantLean = Vector3.Zero, wantPitch = Vector3.Zero;
		if ( _risePlan is { } plan )
		{
			wantRise = station.WorldRotation * FromPlanner( plan.PelvisDelta, seat );
			wantLean = station.WorldRotation * ( FromPlanner( plan.LeanDir, seat ) * plan.Lean );
			wantPitch = station.WorldRotation * ( FromPlanner( plan.LeanDir, seat ) * plan.PitchGain );
		}

		// The same frame-rate-independent chase as the hand — and now the same DEADLINE while a
		// move animates (see RiseChaseK): the plan may step (a foot commits, the leg constraint
		// engages) and the bones must not, but they must also not lag the reach.
		float k = RiseChaseK();
		_riseApplied = Vector3.Lerp( _riseApplied, wantRise, k );
		_leanApplied = Vector3.Lerp( _leanApplied, wantLean, k );
		_pitchApplied = Vector3.Lerp( _pitchApplied, wantPitch, k );

		bool active = _riseApplied.Length > 0.05f || _leanApplied.Length > 0.05f
			|| _pitchApplied.Length > 0.05f;
		if ( !active )
		{
			_riseApplied = Vector3.Zero;
			_leanApplied = Vector3.Zero;
			_pitchApplied = Vector3.Zero;
			if ( _reachSpikeApplied ) { r.ClearPhysicsBones(); _reachSpikeApplied = false; }
			ReleaseRiseIk( r );
			return;
		}

		if ( r.Model is not { } model ) return;

		// Clear-then-set every frame — the engine's own physics-bone pattern, and the M14
		// lesson: always offset the ANIMATION pose (TryGetBoneTransformAnimation), never the
		// final one, or last frame's override compounds and the bone flies off to thousands
		// of units (it did: 712 → 1702 → 3e14).
		r.ClearPhysicsBones();
		_reachSpikeApplied = true;

		if ( model.Bones.GetBone( "pelvis" ) is { } pelvisBone
			&& r.TryGetBoneTransformAnimation( pelvisBone, out var pw ) )
		{
			pw.Position += _riseApplied;
			r.SetBoneTransform( pelvisBone, r.WorldTransform.ToLocal( pw ) );
		}

		// The spine lean rides ON TOP of the rise — its override is absolute, so it must
		// include the rise too, or it would pin the upper body back at the un-risen spot
		// and the "lean" would tear the torso from the hips.
		if ( ( _leanApplied.Length > 0.05f || _pitchApplied.Length > 0.05f )
			&& model.Bones.GetBone( SeatedHandSpikes.NaturalLeanBone ) is { } leanBone
			&& r.TryGetBoneTransformAnimation( leanBone, out var lw ) )
		{
			lw.Position += _riseApplied + _leanApplied;

			// ── Torso pitch: hinge the chest over the table edge ──
			// The planner budgeted PitchGain shoulder-forward units; the actual rotation
			// is asin(gain / torso) about the lateral axis (up × reach bearing), so the
			// top of the torso tips toward the piece. Whether the rotation carries the
			// arm subtree exactly is native territory — the servo trues the hand, and
			// the compensation below subtracts the same eased gain vector, so the two
			// halves cannot drift apart.
			if ( _pitchApplied.Length > 0.05f && _measuredTorso > 4f )
			{
				var dirP = _pitchApplied.Normal;
				float s = Math.Clamp( _pitchApplied.Length / _measuredTorso, 0f, 0.9f );
				float pitchDeg = MathF.Asin( s ) * ( 180f / MathF.PI );
				var axis = Vector3.Cross( Vector3.Up, dirP ).Normal;
				lw.Rotation = Rotation.FromAxis( axis, pitchDeg ) * lw.Rotation;
			}

			// ── Torso yaw: turn the shoulders toward the piece ──
			// Two-bone IK can never rotate the chest — there is no automation to hope
			// for; the turn has to be authored. Rotate the spine override about world-up
			// toward the reach bearing, capped and eased in with the rise. Whether the
			// rotation propagates to the arm subtree is an engine unknown — but the hand
			// SERVO trues the fingertip up either way, so the yaw only has to look right.
			float yawMax = SeatedHandSpikes.TorsoYawMax;
			if ( yawMax > 0f && _risePlan is { } yp && yp.LeanDir.Length > 0.1f )
			{
				var face = station.WorldRotation
					* ( seat == ChessSeat.White ? Vector3.Forward : Vector3.Backward );
				var dirW = station.WorldRotation * FromPlanner( yp.LeanDir, seat );
				float signed = MathF.Atan2( Vector3.Cross( face, dirW ).z, Vector3.Dot( face, dirW ) )
					* ( 180f / MathF.PI );
				float ramp = Math.Min( 1f, _riseApplied.Length / 8f );
				float yaw = Math.Clamp( signed, -yawMax, yawMax ) * ramp;
				lw.Rotation = Rotation.FromAxis( Vector3.Up, yaw ) * lw.Rotation;
			}

			r.SetBoneTransform( leanBone, r.WorldTransform.ToLocal( lw ) );
		}

		if ( _risePlan is { } p && _riseApplied.Length > 0.05f )
		{
			// The feet: pinned to the plan's plants through the engine's own foot IK,
			// pre-compensated by the rise alone (the legs ride the pelvis, not the
			// spine), and EASED from the tucked sit pose so engaging a pin is a step,
			// never a teleport.
			EaseFootPin( r, station, seat, "ankle_L", IkFootLeft, p.FootL, ref _footIkL );
			EaseFootPin( r, station, seat, "ankle_R", IkFootRight, p.FootR, ref _footIkR );
			_riseFeetIk = true;

			// The off hand braces on the table — planner already guaranteed the left arm
			// honestly reaches it, or gave none.
			if ( SeatedHandSpikes.BraceOn && p.Brace is { } b )
			{
				var world = station.WorldTransform.PointToWorld( FromPlanner( b, seat ) );
				var rot = station.WorldRotation
					* Rotation.From( 80f, seat == ChessSeat.White ? 0f : 180f, 0f );
				r.SetIk( IkLeft, new Transform( world - _riseApplied - _leanApplied - _pitchApplied, rot ) );
				_riseBraceIk = true;
			}
			else if ( _riseBraceIk )
			{
				r.ClearIk( IkLeft );
				_riseBraceIk = false;
			}
		}
		else
		{
			ReleaseRiseIk( r );
		}
	}

	/// <summary>Pin one foot: the animgraph solves against the UN-risen skeleton, so the
	/// target is the true plant minus the rise; after the pelvis override carries the leg,
	/// the foot lands exactly on the plant. Rotation is the foot's own animated one, so
	/// pinning never twists an ankle. The target EASES from the foot's actual (tucked)
	/// position to the plant at the rise's own chase rate — engaging a pin is a step, not
	/// a teleport.</summary>
	void EaseFootPin( SkinnedModelRenderer r, ChessStation station, ChessSeat seat,
		string ankleBone, string ik, Gambit.Chess.V3 plant, ref Vector3? eased )
	{
		var rot = Rotation.Identity;
		Vector3? animPos = null;
		if ( r.Model?.Bones.GetBone( ankleBone ) is { } bone
			&& r.TryGetBoneTransformAnimation( bone, out var tx ) )
		{
			rot = tx.Rotation;
			animPos = tx.Position;
		}

		var desired = station.WorldTransform.PointToWorld( FromPlanner( plant, seat ) );
		var cur = eased ?? animPos ?? desired;
		// Same k as the pelvis rise (RiseChaseK) — deadline while a move animates — so a foot pin
		// can't lag a fast-rising pelvis and lift off the plant.
		float k = RiseChaseK();
		cur = Vector3.Lerp( cur, desired, k );
		eased = cur;

		r.SetIk( ik, new Transform( cur - _riseApplied, rot ) );
	}

	Vector3? _footIkL;
	Vector3? _footIkR;

	/// <summary>The hand servo's state: the accumulated correction (horizontal, gated on a
	/// stable ask), the always-on vertical channel (the ask's Z is locked mid-gesture, so
	/// vertical error is warp by definition), and the previous frame's true ask (the
	/// horizontal channel's stability gate). See the servo block in ApplyHandPose.</summary>
	Vector3 _handServo;
	float _servoZ;
	Vector3? _servoTrueAsk;
	const float ServoRate = 5f;
	const float ServoClamp = 10f;

	/// <summary>The vertical channel's own, softer rate: it runs while the hand is in
	/// motion (the horizontal channel doesn't), where the bone read is one frame stale —
	/// integrating that at the full rate hunts visibly.</summary>
	const float ServoZRate = 3f;

	/// <summary>How the hand eases back to rest after a gesture (exponential rate). Fast:
	/// the lingering return read as loitering over the board.</summary>
	const float ReturnChaseRate = 9f;

	// Wrist clearance above the performed piece's bounds TOP while gesturing now lives on
	// SeatedHandSpikes.GraspClearance — a live TerryTuning slider, not a const here — so the
	// piece-relative grasp height can be dialled in the editor (and watched via
	// gambit_terry_scholars) without a recompile.

	void ReleaseRiseIk( SkinnedModelRenderer r )
	{
		_footIkL = null;
		_footIkR = null;
		if ( _riseFeetIk )
		{
			r.ClearIk( IkFootLeft );
			r.ClearIk( IkFootRight );
			_riseFeetIk = false;
		}
		if ( _riseBraceIk )
		{
			r.ClearIk( IkLeft );
			_riseBraceIk = false;
		}
	}

	/// <summary>Measure the working arm and leg off the LIVE skeleton once (they never change
	/// under a fixed avatar), with M13's measured numbers as the fallback — so a bone-name
	/// drift degrades the plan's inputs, never NaNs it. Logged once, because a silently wrong
	/// chain (a twist/helper bone resolving where the real one should) collapses the whole
	/// leg triangle and reads as "the terry won't rise" with nothing else visibly wrong.</summary>
	void MeasureLimbs( SkinnedModelRenderer r )
	{
		if ( _measuredArm > 0f ) return;
		float arm = ChainLength( r, new[] { "arm_upper_R" },
			new[] { "arm_lower_R1", "arm_lower_R" }, new[] { "hand_R1", "hand_R" } );
		float leg = ChainLength( r, new[] { "leg_upper_R" },
			new[] { "leg_lower_R1", "leg_lower_R" }, new[] { "ankle_R" } );
		float torso = 0f;
		if ( TryBoneWorld( r, new[] { "pelvis" }, out var pv )
			&& TryBoneWorld( r, new[] { "arm_upper_R" }, out var sh ) )
			torso = ( sh - pv ).Length;

		_measuredArm = arm > 4f ? arm : FallbackArm;
		_measuredLeg = leg > 4f ? leg : FallbackLeg;
		_measuredTorso = torso > 4f ? torso : FallbackTorso;
		Log.Info( $"[Gambit] terry limbs measured: arm {arm:0.0} (using {_measuredArm:0.0}), "
			+ $"leg {leg:0.0} (using {_measuredLeg:0.0}), torso {torso:0.0} (using {_measuredTorso:0.0}); "
			+ "arm should read ~19.9, torso ~20. gambit_terry_leg overrides the leg if it misresolved." );
	}

	float _measuredTorso;
	const float FallbackTorso = 20f;

	static string Fmt( Vector3 v ) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";

	/// <summary>Rise-debug: one bone's animation position vs its final one, station-local —
	/// the difference IS the override actually landing (or not).</summary>
	void DumpBone( SkinnedModelRenderer r, ChessStation station, string boneName )
	{
		string anim = "-", final_ = "-";
		if ( r.Model?.Bones.GetBone( boneName ) is { } bone )
		{
			if ( r.TryGetBoneTransformAnimation( bone, out var a ) )
				anim = Fmt( station.WorldTransform.PointToLocal( a.Position ) );
			if ( r.TryGetBoneTransform( boneName, out var f ) )
				final_ = Fmt( station.WorldTransform.PointToLocal( f.Position ) );
		}
		Log.Info( $"   bone {boneName,-12} anim {anim}  final {final_}" );
	}

	static float ChainLength( SkinnedModelRenderer r,
		string[] root, string[] mid, string[] end )
	{
		if ( !TryBoneWorld( r, root, out var a ) || !TryBoneWorld( r, mid, out var b )
			|| !TryBoneWorld( r, end, out var c ) )
			return 0f;
		return ( a - b ).Length + ( b - c ).Length;
	}

	static bool TryBoneWorld( SkinnedModelRenderer r, string[] names, out Vector3 pos )
	{
		foreach ( var n in names )
		{
			if ( r.TryGetBoneTransform( n, out var tx ) )
			{
				pos = tx.Position;
				return true;
			}
		}
		pos = default;
		return false;
	}

	/// <summary>A bone's ANIMATION (pre-override) position, station-local — the same
	/// feedback-proof read as <see cref="ShoulderLocal"/>, for the same reason: the rise
	/// moves these bones, and planning against the moved pose would oscillate.</summary>
	Vector3? BoneAnimLocal( ChessStation station, string boneName )
	{
		if ( _bodyRenderer?.Model?.Bones.GetBone( boneName ) is { } bone
			&& _bodyRenderer.TryGetBoneTransformAnimation( bone, out var tx ) )
			return station.WorldTransform.PointToLocal( tx.Position );
		return null;
	}

	/// <summary>Station frame → planner frame (rotate Black's world 180° about Z so the
	/// planner only ever reasons about White's seat; chirality survives, so Black's right
	/// hand stays its right hand).</summary>
	static Gambit.Chess.V3 ToPlanner( Vector3 v, bool black ) =>
		black ? new Gambit.Chess.V3( -v.x, -v.y, v.z ) : new Gambit.Chess.V3( v.x, v.y, v.z );

	/// <summary>Planner frame → station frame, the inverse of <see cref="ToPlanner"/>.</summary>
	static Vector3 FromPlanner( Gambit.Chess.V3 v, ChessSeat seat ) =>
		seat == ChessSeat.Black ? new Vector3( -v.X, -v.Y, v.Z ) : new Vector3( v.X, v.Y, v.Z );

	/// <summary>The animgraph's foot IK chains — citizen.vanmgrph declares exactly four
	/// game-facing IK targets (hand_left/right, foot_left/right); these are the other two.</summary>
	const string IkLeft = "hand_left";
	const string IkFootLeft = "foot_left";
	const string IkFootRight = "foot_right";

	/// <summary>Diagnostics for <c>gambit_terry_net</c> / <c>gambit_terry_doctor</c> — is
	/// there a renderer to pose, how much rise is actually applied on THIS machine, and
	/// what the planner last decided.</summary>
	public bool HasBody => _bodyRenderer != null;
	public float RiseAppliedDebug => _riseApplied.Length;
	public Gambit.Chess.RisePlan? RisePlanDebug => _risePlan;

	/// <summary>Where the working hand's wrist ACTUALLY is this frame (world, final pose —
	/// after IK, overrides, everything). Diagnostics only since the carry layer was cut
	/// (the wrist derives from the piece now, never the reverse).</summary>
	public Vector3? HandBoneWorld()
	{
		if ( _bodyRenderer != null && _bodyRenderer.TryGetBoneTransform( "hand_R", out var tx ) )
			return tx.Position;
		return null;
	}

	/// <summary>Where the hand was last told to go (station-local) — read by
	/// <c>gambit_terry</c>, which prints it beside the bone the IK actually achieved. The
	/// gap between those two IS the reach error, and it is the only way to tell "aiming at
	/// the wrong square" apart from "aiming right and not getting there" without guessing.
	/// That distinction already cost a round of tuning.</summary>
	public Vector3? LastHandTarget { get; private set; }

	/// <summary>Where the WRIST bone was actually aimed (station-local) — the grasp point
	/// pushed back by <see cref="ChessRing.HandGripOffset"/>. This, not
	/// <see cref="LastHandTarget"/>, is what to compare <c>hand_R</c> against to read pure
	/// reach shortfall, since the bone is meant to sit offset from the grasp point.</summary>
	public Vector3? LastHandIkTarget { get; private set; }

	/// <summary>
	/// Let the arm go — the whole arm, not just the IK.
	///
	/// <para><b>Releasing only the IK is not enough, and this is why a player who stood up
	/// walked around the lobby with an arm hanging out at nothing.</b> <c>holdtype</c> is
	/// what poses the arm AROUND an object, and once set to HoldItem <b>nothing in the
	/// engine ever sets it back</b>: MoveMode.OnUpdateAnimatorState writes sit, b_swim,
	/// b_climbing, b_grounded and duck — and no holdtype. So the arm keeps its
	/// carrying-something shape forever, IK or no IK. Both have to be released, and
	/// holdtype is the one with no owner but us.</para></summary>
	public void ClearHandPose()
	{
		if ( _bodyRenderer == null ) return;

		_bodyRenderer.ClearIk( IkRight );
		_bodyRenderer.Set( "holdtype", HoldTypeNone );
		_bodyRenderer.Set( "holdtype_pose_hand", 0f );
		_handLocal = null;
		LastHandTarget = null;
		LastHandIkTarget = null;

		// M14: drop any Approach B/C bone override too, so standing up (or toggling a spike off)
		// doesn't leave a leaned or scaled skeleton frozen in place.
		if ( _reachSpikeApplied ) { _bodyRenderer.ClearPhysicsBones(); _reachSpikeApplied = false; }

		// The half-rise: release the foot pins and the brace, and zero the eased state so
		// the next reach starts from the chair rather than from a stale mid-rise.
		ReleaseRiseIk( _bodyRenderer );
		_risePlan = null;
		_riseApplied = Vector3.Zero;
		_leanApplied = Vector3.Zero;
		_pitchApplied = Vector3.Zero;
		_handServo = Vector3.Zero;
		_servoZ = 0f;
		_servoTrueAsk = null;
	}

	static Vector3 SquareLocal( ChessRing ring, int square ) =>
		square < 0 ? Vector3.Zero : ring.SquareLocalPosition( square & 7, square >> 3 );

	/// <summary>The working arm's shoulder pivot (arm_upper_R), station-local — the centre the
	/// reach measures from. Reads the ANIMATION pose (post-sit, pre-override), NOT the final one:
	/// the natural lean moves the shoulder via a bone override, so reading the final (already-leaned)
	/// shoulder to compute this frame's lean would feed the lean back into itself and oscillate. The
	/// animation pose is the un-leaned shoulder, so the deficit — and the lean it drives — is stable.
	/// Null if the bone can't be resolved, in which case the reach clamp is skipped.</summary>
	Vector3? ShoulderLocal( ChessStation station )
	{
		if ( _bodyRenderer?.Model?.Bones.GetBone( "arm_upper_R" ) is { } bone
			&& _bodyRenderer.TryGetBoneTransformAnimation( bone, out var tx ) )
			return station.WorldTransform.PointToLocal( tx.Position );
		return null;
	}

	/// <summary>citizen.vanmgrph's holdtype enum (none, pistol, rifle, shotgun, holditem, …)
	/// — HoldItem is the one the finger-blend (holdtype_pose_hand) is wired into.
	/// Handedness: 2H, RH, LH — so Right is 1.</summary>
	const int HoldTypeNone = 0;
	const int HoldTypeItem = 4;
	const int HandednessRight = 1;

	/// <summary>The animgraph's IK CHAIN name (ik.hand_right.*) — not the bone name, which
	/// is hand_R. Two different vocabularies for one arm.</summary>
	const string IkRight = "hand_right";

	/// <summary>The hand's eased station-local position — see ApplyHandPose. Null until the
	/// first frame it is placed, so it starts where it belongs rather than flying in from
	/// the station's origin.</summary>
	Vector3? _handLocal;

	/// <summary>Undo <see cref="TrimSeatedAvatar"/>: put the head back and stop excluding
	/// our cosmetics. Idempotent, and safe to call when nothing was ever trimmed.</summary>
	void RestoreSeatedAvatar()
	{
		foreach ( var go in _viewerTagged )
			if ( go.IsValid() )
				go.Tags.Set( ViewerTag, false );
		_viewerTagged.Clear();

		if ( _headHidden )
		{
			_bodyRenderer?.SetBodyGroup( HeadGroup, BodyGroupOn );
			_headHidden = false;
		}
	}

	/// <summary>Switch the local player to the OTHER seat at the table they're engaged
	/// at, moving the avatar and blending the overhead camera to the new side. Used by
	/// the shareable-link colour chip so the board shows you where you'll play.
	///
	/// <para>Refused once a LOCAL game is live (leaving your side would abandon it) and
	/// no-op if the target seat is taken. A live LICHESS game is fine, and that is the
	/// point: for a random-colour link lichess assigns your side only when the game
	/// starts, and the physical seat is just which side the camera views from — the
	/// relayed game runs off your token, not the seat, so aligning them is cosmetic. (The
	/// paired lichess flow is protected anyway: its other seat is taken, so this no-ops.)
	/// Keeps <see cref="_seatReturnPos"/> so standing up still lands correctly.</para></summary>
	public void SwitchSeat( ChessSeat seat )
	{
		if ( IsProxy ) return;
		var station = ChessStation.Active;
		if ( station == null || ChessStation.ActiveSeat == seat ) return;
		if ( station.SeatTaken( seat ) ) return;

		if ( Gambit.Game.LocalGameController.For( station ) is { Playing: true } ) return;

		// The vertical axis to orbit around: the board centre. Both seats are symmetric
		// across it, and the two camera anchors are too, so a 180° turn about it carries a
		// seat to the opposite seat AND (below) the camera to the opposite anchor. Its
		// x/y is the board centre; z is irrelevant to a vertical-axis turn. Fall back to an
		// instant re-plant if the anchors aren't built yet.
		var wa = station.SeatAnchor( ChessSeat.White );
		var ba = station.SeatAnchor( ChessSeat.Black );

		station.SwitchActiveSeat( seat );
		if ( ChessStation.ActiveSeat != seat ) return; // the claim didn't take

		if ( wa == null || ba == null )
		{
			InstantReseat( station, seat );
			return;
		}

		// SCHWOOP: animate the AVATAR's whole transform 180° around the board centre. The
		// camera is a rigid CHILD of the avatar, so it's carried along for free and lands
		// exactly on the new anchor — no separate camera math to get out of sync (the bug
		// that made it jump-rotate-jump). And because the avatar is networked from us, other
		// players get to watch us physically swing around to the other side. We don't touch
		// _seatReturnPos — standing up must still land where we first stood.
		_switchAxis = ( wa.WorldPosition + ba.WorldPosition ) * 0.5f;
		_switchAvatarFrom = WorldPosition;
		_switchAvatarFromRot = WorldRotation;
		_switchTargetSeat = seat;
		_switchTime = 0;
		_switching = true;
	}

	// ── Seat-switch schwoop (SwitchSeat) ──
	bool _switching;
	TimeSince _switchTime;
	Vector3 _switchAvatarFrom;
	Rotation _switchAvatarFromRot;
	Vector3 _switchAxis; // board centre; only its x/y matter (a vertical-axis turn keeps z)
	ChessSeat _switchTargetSeat;
	const float SeatSwitchTime = 0.6f; // a touch slower than the engage swoop — it's a bigger sweep

	/// <summary>
	/// Put the avatar on a seat's chair, facing the board. The one place that answers
	/// "where does a seated body go", so the three callers that plant it —
	/// <see cref="BeginEngage"/>, <see cref="InstantReseat"/> and the seat switch's
	/// end-snap — cannot disagree.
	///
	/// <para><b>They used to, in a way that was invisible.</b> Each hand-rolled the same
	/// five lines, and the switch's end-snap wrote <c>seatPos.z = _switchAvatarFrom.z</c> —
	/// correct only by accident, and only for as long as the seated z happened to equal the
	/// standing z. The day SeatSitZ moves, that one pops and the other two don't.</para></summary>
	/// <summary>
	/// Take the seated body out of the physics world, and put it back on stand-up.
	///
	/// <para><b>Without this the table SHOVES you off your own chair, and the numbers say
	/// so.</b> The table's BoxCollider spans x ±31.5 and the citizen's BodyRadius is 16, so
	/// a body planted at |x| = 36 is 11 units deep inside it. Physics resolves that the only
	/// way it can: <c>gambit_terry</c> measured the seated plant at −40.73 and +41.59
	/// against the ±36 that was asked for, one of them lifted to z = 2.05 as well. A terry
	/// perched on the back lip of its chair, reaching across the table, because the table
	/// pushed it there.</para>
	///
	/// <para><b>The engine's own chair does exactly this</b>, which is the corroboration
	/// rather than a coincidence: <c>BaseChair.Sit</c> is <c>player.Body.Enabled = false;
	/// player.ColliderObject.Enabled = false;</c>, and <c>SitMoveMode.OnModeEnd</c> puts both
	/// back. A seated player has nothing to collide with and nowhere to fall.</para>
	///
	/// <para>Read through the CONTROLLER's own references rather than our cached
	/// <c>_rigidbody</c>: Body and ColliderObject are what the engine re-enables, and going
	/// through the same handles is what stops us and it disagreeing about which objects
	/// those are.</para>
	/// </summary>
	void SetSeatedPhysics( bool enabled )
	{
		if ( _controller == null ) return;

		if ( _controller.Body.IsValid() )
			_controller.Body.Enabled = enabled;

		if ( _controller.ColliderObject.IsValid() )
			_controller.ColliderObject.Enabled = enabled;
	}

	void PlantOnSeat( ChessStation station, ChessSeat seat )
	{
		// TerrySeated false is a full revert to the pre-M13 world, not just "don't draw
		// me": with no sit pose applied, the citizen is STANDING, and a standing citizen
		// belongs at the walk-up spot rather than planted on a chair it isn't sitting in.
		// A kill switch that only reverts half of a feature is one you can't trust in the
		// moment you need it.
		var seatPos = ChessRing.Instance is { TerrySeated: false }
			? station.SeatWorldPosition( seat )
			: station.SeatSitWorldPosition( seat );

		WorldPosition = seatPos;
		if ( SeatFacing( station, seat ) is { } face )
			WorldRotation = face;
	}

	/// <summary>The world rotation a body seated on <paramref name="seat"/> should face:
	/// yawed level at the board, so the pitch of the line from the chair up to the board
	/// can't tip the whole body forward. Null when the seat and board coincide (never in
	/// practice — the guard is for a degenerate build).
	///
	/// <para>Shared by <see cref="PlantOnSeat"/> (which yaws the parent GameObject) and
	/// <see cref="ApplySitPose"/> (which writes it straight onto the FROZEN body renderer,
	/// because that renderer does NOT inherit the parent yaw — see ApplySitPose for why).
	/// Deriving both from one place is what stops the two disagreeing.</para></summary>
	static Rotation? SeatFacing( ChessStation station, ChessSeat seat )
	{
		var seatPos = ChessRing.Instance is { TerrySeated: false }
			? station.SeatWorldPosition( seat )
			: station.SeatSitWorldPosition( seat );

		var boardFlat = station.WorldPosition;
		boardFlat.z = seatPos.z;
		var toBoard = boardFlat - seatPos;

		return toBoard.Length > 0.01f ? Rotation.LookAt( toBoard, Vector3.Up ) : null;
	}

	/// <summary>Instant seat change (fallback when anchors aren't built): teleport the
	/// avatar to the new seat and re-blend the camera, the pre-schwoop behaviour.</summary>
	void InstantReseat( ChessStation station, ChessSeat seat )
	{
		PlantOnSeat( station, seat );
		HideLocalAvatar();
		if ( _cameraObject != null )
		{
			_camFromPos = _cameraObject.WorldPosition;
			_camFromRot = _cameraObject.WorldRotation;
			_engageTime = 0;
		}
	}

	/// <summary>Turn the avatar's whole transform about the board's vertical axis, 180°
	/// clockwise, over SeatSwitchTime. The child camera rides rigidly, so it ends on the
	/// opposite anchor with no separate camera blend. Snaps to the exact seat pose and
	/// hands the camera back to the locked camera at the end.</summary>
	void UpdateSeatSwitch()
	{
		float t = Math.Clamp( (float)_switchTime / SeatSwitchTime, 0f, 1f );
		t = 1f - MathF.Pow( 1f - t, 3f ); // ease-out cubic, same feel as the engage blend

		// Negative yaw about world up = clockwise seen from above. Turning both the position
		// (orbit) and the rotation by the same yaw is a rigid turn about the axis.
		var spin = Rotation.FromAxis( Vector3.Up, -180f * t );
		WorldPosition = _switchAxis + spin * ( _switchAvatarFrom - _switchAxis );
		WorldRotation = spin * _switchAvatarFromRot;

		if ( _switchTime >= SeatSwitchTime )
		{
			_switching = false;
			// Snap the avatar to the exact new seat pose (symmetry lands it here already;
			// this removes any float drift), and mark the camera settled at the new anchor.
			//
			// M13: this hand-rolled the plant and took its z from _switchAvatarFrom — which
			// was correct ONLY by accident, because the seated z happened to be the standing
			// z. Through PlantOnSeat it is correct on purpose.
			var station = ChessStation.Active;
			if ( station != null )
				PlantOnSeat( station, _switchTargetSeat );
			// The camera rode to the new anchor; let UpdateLockedCamera hold it there.
			if ( _cameraObject != null )
			{
				_camFromPos = _cameraObject.WorldPosition;
				_camFromRot = _cameraObject.WorldRotation;
			}
			_engageTime = CamBlendTime;
		}
	}

	/// <summary>Stand up from whatever we're engaged at, blending the camera back to
	/// roaming. <paramref name="keepSeat"/> stands up from a chess seat WITHOUT releasing
	/// the networked occupancy — used to walk around mid-game without resigning, so the
	/// live game keeps running and we keep polling it (see ChessStation.LeaveCameraKeepSeat).
	/// The default releases the seat, which is every other case: an idle seat, a finished
	/// game, a wall board, or an explicit resign.</summary>
	public void Disengage( bool keepSeat = false )
	{
		_switching = false; // standing up cancels any in-flight seat-switch schwoop

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

		if ( keepSeat )
			ChessStation.Active?.LeaveCameraKeepSeat();  // roam, but the seat stays ours
		else
			ChessStation.Active?.Leave();
		SettingsStation.Active?.Leave();
		InfoStation.Active?.Leave();

		// Undo everything the seat did to our own body, in either direction: the wholesale
		// hide (TerrySeated false, or 2D play mode) and the M13 trim can BOTH have run this
		// session if the switch was flipped mid-game, so neither undo is conditional on it.
		ShowHiddenRenderers();
		RestoreSeatedAvatar();
		ClearSitPose();
		ClearHandPose();

		// Back into the physics world — after the un-plant above, so we land standing where
		// we stood rather than being resolved out of the table we were sitting at.
		SetSeatedPhysics( true );

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
	/// pre-aimed down at the board center by ChessRing, so no target math here.
	///
	/// <para>2D play mode (M16) picks the top-down (nadir) anchor instead of the orbit anchor;
	/// the existing lerp/slerp then eases between them for free, so switching mode while seated
	/// glides to the other view with no extra code. Read live off PlayMode, so the switch takes
	/// effect the next frame.</para></summary>
	void UpdateLockedCamera()
	{
		var station = ChessStation.Active;
		var seat = ChessStation.ActiveSeat;
		bool nadir = Gambit.Game.PlayerData.ClampPlayMode( Gambit.Game.PlayerData.Load()?.PlayMode ) == "2d";
		var anchor = nadir ? station?.TopAnchor( seat ) : station?.SeatAnchor( seat );
		if ( anchor == null || _cameraObject == null ) return;

		float t = Math.Clamp( _engageTime / CamBlendTime, 0f, 1f );
		t = 1f - MathF.Pow( 1f - t, 3f ); // ease-out cubic, same curve as the leave blend

		_cameraObject.WorldPosition = Vector3.Lerp( _camFromPos, anchor.WorldPosition, t );
		_cameraObject.WorldRotation = Rotation.Slerp( _camFromRot, anchor.WorldRotation, t );
	}
}
