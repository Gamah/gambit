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

	/// <summary>
	/// This player's hovered and selected board square, packed into one int and published
	/// by the OWNER. −1 = neither (the resting value, and what an unseated player carries).
	///
	/// <para><b>The only thing M13 puts on the wire, and the smallest thing that could
	/// be.</b> Everything else a seated terry does — the pose, the pickup animation, the
	/// chair's tuck, the tint — is derived locally from state that already replicates. Hover
	/// and selection are the exception because they have NO other evidence: nothing else in
	/// the game knows which square you are thinking about. A move, by contrast, is already
	/// relayed, so every client can drive the same pickup off LastMoveUci without being
	/// told.</para>
	///
	/// <para><b>Owner-synced, not host-authoritative</b>, unlike ChessStation's occupancy: a
	/// hover is the CLIENT's own truth and the host has no opinion on it.
	/// <see cref="GambitName"/> is the precedent — a bare [Sync] on a networked,
	/// player-owned component, published behind an explicit change gate.</para>
	///
	/// <para>Bandwidth is a handful of int writes per second per seated player: it changes
	/// only when the cursor crosses a square boundary, and NOTHING is sent while the cursor
	/// sits still or nobody is seated. [Sync] diffs anyway; the change gate in
	/// ChessBoardView is belt-and-braces and matches the house style.</para>
	///
	/// <para><b>It survives the lichess seam for free.</b> The payload is a raw square index
	/// — no game-source knowledge crosses the wire. An observer only needs to know which
	/// square to float a hand over, and the hovering player derives their own hover from
	/// their own source. Which is also why a third kind of game gets this by existing.</para></summary>
	[Sync] public int HandState { get; set; } = -1;

	/// <summary>Pack hover + selection into one int. 7 bits each — 0..64 after the +1 bias,
	/// so −1 (none) packs cleanly and the whole thing is one comparison to change-gate.</summary>
	public static int PackHand( int hover, int selected ) =>
		( hover + 1 ) | ( ( selected + 1 ) << 7 );

	/// <summary>Inverse of <see cref="PackHand"/>. −1 out for "none".</summary>
	public static void UnpackHand( int packed, out int hover, out int selected )
	{
		if ( packed < 0 )
		{
			hover = -1;
			selected = -1;
			return;
		}

		hover = ( packed & 0x7F ) - 1;
		selected = ( ( packed >> 7 ) & 0x7F ) - 1;
	}

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
			// Standing up mid-game resigns, so that path is two-stage (RequestLeave).
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
		var lichess = Gambit.Game.LichessGameController.For( station );

		// A lichess game is the one that counts: it is on the player's real record,
		// so standing up must resign it THERE. The local controller can't do that —
		// during a lichess game its ChessGame never advances, so it doesn't even
		// know a game is in progress.
		bool lichessForfeits = lichess is { Engaged: true, Playing: true }
			&& lichess.LocalSeat != null;

		// Standing up mid-game forfeits it — true for both the local two-seat game and
		// a live game arms the two-stage confirm.
		bool localForfeits = !lichessForfeits
			&& controller is { Playing: true }
			&& controller.LocalSeat != null
			&& ( controller.Game?.MoveCount ?? 0 ) > 0;
		bool forfeits = localForfeits || lichessForfeits;

		if ( forfeits && !LeaveArmed )
		{
			_leaveArm = 0f;
			return;
		}

		_leaveArm = 999f;

		// Walking away while still waiting on an opponent WITHDRAWS the request —
		// whether that's a lobby seek (its held connection IS the seek) or a direct
		// challenge (which gamchess must explicitly /cancel, or it stays acceptable
		// for hours). Without this we get dropped into a game nobody is sitting at.
		//
		// Below the arm gate, not above it: a press that only arms the confirm must
		// not silently bin the player's request while they read "Sure? This resigns"
		// and then decline. (Waiting and Playing are mutually exclusive — Adopt drops
		// both the moment a game goes live — so this never races a resign.)
		if ( lichess is { AwaitingOpponent: true } )
			lichess.CancelWaiting();

		// Standing up from a FINISHED lichess game: release the server-side play so its
		// pending slot and event stream don't linger to the 10-min sweep (which after a
		// link game can leave a stale gameStart the next link trips on). No-op otherwise.
		if ( lichess is { State: { finished: true } } )
			lichess.DismissFinished();

		if ( lichessForfeits )
			lichess.ResignLocal();
		else if ( localForfeits )
			controller.ResignLocal();

		// Safety: standing up always drops any lichess game state so the board doesn't
		// keep showing it. The other paths above already handle the withdrawing/resigning/
		// dismissing; this just guarantees the controller is cleared no matter which case
		// we came through (ResignLocal, notably, keeps polling to show the result — we
		// don't want that once we've left). The local table resets on its own as the seat
		// empties. Idempotent.
		lichess?.Clear();

		Disengage();
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
		if ( ChessRing.Instance is { TerrySeated: false } )
		{
			HideEveryRenderer();
			return;
		}

		TrimSeatedAvatar();
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
	public void ApplyHandPose( ChessStation station, ChessSeat seat, Gambit.Chess.HandPose pose )
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
			return;
		}

		// Where the hand would rest with nothing to do: elbows on the table, in the X
		// margin. CLAUDE.md calls both X margins "kept clear — they are the seat cameras'
		// sightlines", and this spends one of them ON PURPOSE: the hands ARE the sightline's
		// subject. The board frame's half-extent is 21.75 and the tabletop's is 30, so 26
		// lands squarely in the 8.25-wide margin — on the table, clear of the board.
		float side = seat == ChessSeat.White ? -1f : +1f;
		var idle = new Vector3( side * ring.HandIdleX, side * ring.HandIdleY, ring.HandIdleZ );

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

			var on = Vector3.Lerp( from, to, pose.Travel ) + Vector3.Up * ( pose.Height + ring.HandLift );

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
			if ( SeatedHandSpikes.UseSphereClamp )
			{
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
				target = idle; // out of band → rest, don't strain at a piece the arm can't make
			}
		}

		// ── Chase the target; never teleport to it ──
		//
		// What crosses the wire is a SQUARE, so the target jumps a whole square at a time as
		// the cursor crosses a boundary — and a hand that jumps reads as broken rather than
		// as thinking. Easing the POSITION is what makes a quantised signal look like a hand
		// vaguely following a mouse, which is the whole illusion. TerryPose's Weight already
		// eases the hand on and off the BOARD; this eases it ACROSS the board, which the
		// weight can't do because both ends are on it.
		//
		// Frame-rate independent: 1 − e^(−k·dt), not a raw lerp factor.
		if ( _handLocal is not { } current )
			_handLocal = target;                       // first frame: be there
		else
			_handLocal = Vector3.Lerp( current, target,
				1f - MathF.Exp( -ring.HandChaseRate * Time.Delta ) );

		// Fingers down over the board, reaching from this seat's side — so the hand faces
		// across the table the way the body does.
		var rot = station.WorldRotation
			* Rotation.From( ring.HandPitch, seat == ChessSeat.White ? 0f : 180f, 0f );

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

		// M14 Approach B/C: bend the skeleton BEFORE the IK solves, so the measurement reads
		// whether the two-bone IK re-derives against the changed shoulder. No-op unless a lever
		// is pulled; self-clears when they are all neutral.
		ApplyReachSpikes( r, station, seat );

		r.SetIk( IkRight, new Transform( world, rot ) );
	}

	/// <summary>Whether we currently hold a bone override (Approach B lean / C arm-scale), so the
	/// off-paths know to clear it exactly once rather than every frame.</summary>
	bool _reachSpikeApplied;

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
		bool lean = SeatedHandSpikes.LeanOn && SeatedHandSpikes.LeanForward != 0f;
		bool scale = SeatedHandSpikes.ArmScale != 1f;

		if ( !lean && !scale )
		{
			if ( _reachSpikeApplied ) { r.ClearPhysicsBones(); _reachSpikeApplied = false; }
			return;
		}

		if ( r.Model is not { } model ) return;

		r.ClearPhysicsBones();
		_reachSpikeApplied = true;

		if ( lean )
		{
			var bone = model.Bones.GetBone( SeatedHandSpikes.LeanBone );
			if ( bone is not null && r.TryGetBoneTransform( bone, out var world ) )
			{
				// "Forward" = toward the board = station +X for White (who sits at −X), −X for Black.
				float fwd = seat == ChessSeat.White ? +1f : -1f;
				var offset = station.WorldRotation * new Vector3( fwd * SeatedHandSpikes.LeanForward, 0f, 0f );
				world.Position += offset;
				r.SetBoneTransform( bone, r.WorldTransform.ToLocal( world ) );
			}
		}

		if ( scale )
		{
			// The IK chain is arm_upper_R → arm_lower_R1 → hand_R1; scale the two proximal segments.
			foreach ( var name in ArmScaleBones )
			{
				var b = model.Bones.GetBone( name );
				if ( b is null || !r.TryGetBoneTransform( b, out var w ) ) continue;
				w.Scale *= SeatedHandSpikes.ArmScale;
				r.SetBoneTransform( b, r.WorldTransform.ToLocal( w ) );
			}
		}
	}

	static readonly string[] ArmScaleBones = { "arm_upper_R", "arm_lower_R1" };

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
	}

	static Vector3 SquareLocal( ChessRing ring, int square ) =>
		square < 0 ? Vector3.Zero : ring.SquareLocalPosition( square & 7, square >> 3 );

	/// <summary>The working arm's shoulder pivot (arm_upper_R), station-local — the centre the
	/// reach clamp measures from. Read fresh so it follows the pose (a lean, a scoot) rather
	/// than a stale constant; null if the bone can't be resolved, in which case the clamp is
	/// simply skipped and the raw target stands.</summary>
	Vector3? ShoulderLocal( ChessStation station )
	{
		if ( _bodyRenderer != null && _bodyRenderer.TryGetBoneTransform( "arm_upper_R", out var tx ) )
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

		// Yaw at the board, level — a flat look, so the pitch of the line from the chair up
		// to the board can't tip the whole body forward.
		var boardFlat = station.WorldPosition;
		boardFlat.z = seatPos.z;
		var toBoard = boardFlat - seatPos;

		WorldPosition = seatPos;
		if ( toBoard.Length > 0.01f )
			WorldRotation = Rotation.LookAt( toBoard, Vector3.Up );
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

	public void Disengage()
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

		ChessStation.Active?.Leave();
		SettingsStation.Active?.Leave();
		InfoStation.Active?.Leave();

		// Undo everything the seat did to our own body, in either direction: the wholesale
		// hide (TerrySeated false) and the M13 trim can BOTH have run this session if the
		// switch was flipped mid-game, so neither undo is conditional on it.
		foreach ( var r in _hiddenRenderers )
		{
			if ( r.IsValid() )
				r.Enabled = true;
		}
		_hiddenRenderers.Clear();
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
