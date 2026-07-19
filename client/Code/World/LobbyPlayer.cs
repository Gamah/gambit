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
			ClearHandPose();
			return;
		}

		r.Set( "sit", SitPoseChair );

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

		// Re-assert the board-facing every frame so gambit_terry_face turns terry LIVE (and so a
		// proxy uses THIS machine's FaceYaw while you tune). Skipped mid-schwoop by ordering, not
		// a flag: the local seat-switch's UpdateSeatSwitch runs after this and wins until it lands.
		if ( SeatedAt is { } s )
			ApplySeatedFacing( s.Station, s.Seat );

		// M14: the working hand. Called from here because ApplySitPose is the one per-frame
		// pose write that runs for BOTH the local player and every seated proxy — so every
		// terry animates its own move the same way, "everyone sees the hand including the
		// mover" (design point 9) for free. It self-gates on TerryHands.HandsOn.
		ApplyHandPose();
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

	// ─────────────────────────────── M14: the working hand ───────────────────────────────
	//
	// Drives the seated terry's RIGHT hand (chess is one-handed) via the four game-facing IK
	// chains. Phase 1 is the whole feature in miniature: the hand rests at a fixed anchor, and
	// on a committed move by this seat it travels to the piece, tracks it across the board, and
	// returns -- all from the ONE clock ChessBoardView owns (ActiveHandMove). Phase 2 adds the
	// move-only half-rise so far pieces come inside the arm; a piece past even the risen arm has
	// the hand trail and the piece finish its own slide (design point 6).
	//
	// This is called from ApplySitPose, so it runs identically for the local player and every
	// seated proxy -- everyone animates their own move (design point 9). It reads the global
	// TerryHands statics, so one tuning slider moves every terry at once.

	const string IkRight = "hand_right";      // the animgraph IK CHAIN name -- NOT the bone (hand_R)
	const string IkFootLeft = "foot_left";
	const string IkFootRight = "foot_right";

	// citizen.vanmgrph enums: holdtype (none, …, holditem) is what the finger-blend
	// (holdtype_pose_hand) is wired into; handedness 2H/RH/LH, so Right = 1.
	const int HoldTypeNone = 0;
	const int HoldTypeItem = 4;
	const int HandednessRight = 1;

	bool _handIkActive;              // IK + holdtype currently written -- must be cleared on stand-up
	bool _riseActive;               // pelvis/spine overrides + foot pins currently applied
	float _measuredArm;             // live |arm_upper_R → hand_R1|, 0 until first read
	Vector3 _riseApplied;           // world pelvis-override translation this frame; the foot pins
	                                //   pre-compensate by this (the arm pre-comps by the full ride)
	Vector3 _handServo;             // closed-loop correction for the ~5u native warp (fact #4)
	Vector3? _servoTrueAsk;         // last frame's true world ask, to detect a new move

	/// <summary>Is there a citizen renderer to pose? (gambit_terry)</summary>
	public bool HasBody => _bodyRenderer != null;
	/// <summary>Live-measured arm length (gambit_terry).</summary>
	public float MeasuredArmDebug => _measuredArm;
	/// <summary>How much half-rise is actually applied on THIS machine right now (gambit_terry).</summary>
	public float RiseAppliedDebug => _riseApplied.Length;

	/// <summary>
	/// Place the working hand for this frame. Self-gates on the kill switches and the seat; when
	/// off or not-seated it releases any prior IK so a stood-up player never walks away with an
	/// arm frozen mid-reach (the attempt-2 "arm hanging out at nothing" bug -- holdtype has no
	/// owner but us, so it must be cleared explicitly).
	/// </summary>
	void ApplyHandPose()
	{
		var r = _bodyRenderer;
		if ( r == null ) return;

		if ( !TerryHands.HandsOn || ChessRing.Instance is not { TerrySeated: true }
			|| SeatedAt is not { } seated || seated.Station == null )
		{
			if ( _handIkActive ) ClearHandPose();
			return;
		}
		var station = seated.Station;
		var seat = seated.Seat;

		// The station's board view owns the move clock. Is a move being hand-animated on OUR seat?
		var view = station.GameObject.Components.Get<ChessBoardView>();
		var move = view?.ActiveHandMove;
		bool animating = move is { } m && m.MoverSeat == seat;

		if ( !animating && !TerryHands.RestAnchorOn )
		{
			// Nothing to do and no anchored rest wanted: let the arm hang in the raw sit pose.
			if ( _handIkActive ) ClearHandPose();
			return;
		}

		UpdateMeasuredArm( r, station );

		// The grasp target the wrist aims at, station-local. Rest anchor (mirrored for Black),
		// blended toward the piece's grasp point by the gesture's HandWeight during a move. The
		// blend is what makes the hand TRAVEL to the piece over Approach rather than snap to it.
		Vector3 restLocal = SeatMirror( seat, TerryHands.RestAnchorLocal );
		Vector3 graspLocal = restLocal;
		float gripClose = 0f;
		if ( animating )
		{
			var mv = move.Value;
			var pieceLocal = station.WorldTransform.PointToLocal( mv.PieceWorld )
				+ new Vector3( 0f, 0f, TerryHands.GraspHeight );
			graspLocal = Vector3.Lerp( restLocal, pieceLocal, mv.HandWeight );
			gripClose = mv.GripClose;
		}

		// Phase 2: the move-only half-rise. Returns the world override translation the ARM will
		// ride (pelvis + spine), which the IK target is pre-compensated by -- the animgraph solves
		// IK BEFORE the override applies, so aim at (true - ride) and the override carries it home.
		Vector3 ride = Vector3.Zero;
		if ( animating && TerryHands.HalfRiseOn )
			ride = ApplyHalfRise( r, station, seat, graspLocal );
		else if ( _riseActive )
			ReleaseHalfRise( r );

		// Hand rotation (facts #2): yaw along the shoulder→target bearing (a fixed yaw claws the
		// wrist on a diagonal reach); pitch from the forearm's own declination + a curl (a fixed
		// nose-down pitch hyper-flexes a flat far reach); roll swings the elbow OUT of the torso
		// (0 traps the arm in a vertical plane -- the t-rex arm). Roll is mirrored for Black.
		float handYaw = seat == ChessSeat.White ? 0f : 180f;
		float handPitch = TerryHands.HandPitchCap;
		if ( BoneLocalAnim( r, station, "arm_upper_R" ) is { } shoulder )
		{
			var bearing = graspLocal - shoulder;
			float horiz = bearing.WithZ( 0f ).Length;
			if ( horiz > 1f )
				handYaw = MathF.Atan2( bearing.y, bearing.x ) * ( 180f / MathF.PI );
			float decl = MathF.Atan2( -bearing.z, MathF.Max( horiz, 0.001f ) ) * ( 180f / MathF.PI );
			handPitch = MathF.Min( TerryHands.HandPitchCap, MathF.Max( decl, 0f ) + TerryHands.WristDrop );
		}
		float roll = seat == ChessSeat.White ? TerryHands.HandRoll : -TerryHands.HandRoll;
		var rot = station.WorldRotation * Rotation.From( handPitch, handYaw, roll );

		// The IK aims the WRIST; pull it back along the hand axes so the fingers -- not the palm
		// -- land on the piece (tune against gambit_terry's hand_R readout).
		var world = station.WorldTransform.PointToWorld( graspLocal ) + rot * TerryHands.GripOffset;

		// The servo (fact #4): only meaningful while the half-rise bends the skeleton. Steer the
		// residual native warp out by measuring last frame's hand_R vs the true ask.
		Vector3 servo = ( TerryHands.ServoOn && animating && TerryHands.HalfRiseOn )
			? UpdateServo( r, world ) : Vector3.Zero;

		r.SetIk( IkRight, new Transform( world - ride + servo, rot ) );
		// holdtype ONLY while a move is being animated. At rest the hand is just anchored off
		// the board (RestAnchorLocal) -- forcing holditem there makes every seated terry grip an
		// invisible object every frame, which reads as "the sitting pose is broken." Rest =
		// relaxed open hand (holdtype none); the grip closes over the move via gripClose.
		r.Set( "holdtype", animating ? HoldTypeItem : HoldTypeNone );
		r.Set( "holdtype_handedness", HandednessRight );
		r.Set( "holdtype_pose_hand", gripClose );
		_handIkActive = true;
	}

	/// <summary>Let the whole arm go -- both the IK and holdtype (holdtype has no owner but us,
	/// so releasing only the IK leaves the arm posed around an invisible item forever), plus any
	/// half-rise overrides. Idempotent.</summary>
	void ClearHandPose()
	{
		var r = _bodyRenderer;
		if ( r == null ) { _handIkActive = false; return; }

		r.ClearIk( IkRight );
		r.Set( "holdtype", HoldTypeNone );
		r.Set( "holdtype_pose_hand", 0f );
		if ( _riseActive ) ReleaseHalfRise( r );
		_handIkActive = false;
	}

	/// <summary>The move-only half-rise: carry the pelvis (and the shoulder on it) toward the
	/// grasp target until the arm honestly reaches, bounded by the legs. Returns the world
	/// translation the ARM rides (pelvis + spine) so the caller can pre-compensate the IK. Any
	/// missing bone read degrades gracefully to Phase 1 (no rise, the hand trails). Everything
	/// is planned in <see cref="Gambit.Chess.HandReach"/>'s White frame and mirrored back.</summary>
	Vector3 ApplyHalfRise( SkinnedModelRenderer r, ChessStation station, ChessSeat seat, Vector3 graspLocal )
	{
		if ( BoneLocalAnim( r, station, "arm_upper_R" ) is not { } shoulderL
			|| BoneLocalAnim( r, station, "pelvis" ) is not { } pelvisL )
		{
			if ( _riseActive ) ReleaseHalfRise( r );
			return Vector3.Zero;
		}

		// CHOOSE the foot plants (fact: never feed the planner the animated ankles -- the sit
		// pose tucks them ~25u behind the pelvis, spending the whole leg budget before any rise).
		// Plants are the pelvis projected to the floor, hip-width apart, a touch forward; the
		// foot pins below ease the real ankles onto them.
		var pelvisWhite = SeatMirror( seat, pelvisL );
		const float footFwd = 6f, hipHalf = 6f;
		var footLW = new Gambit.Chess.R3( pelvisWhite.x + footFwd, pelvisWhite.y + hipHalf, 0f );
		var footRW = new Gambit.Chess.R3( pelvisWhite.x + footFwd, pelvisWhite.y - hipHalf, 0f );

		var t = TerryHands.Reach( _measuredArm > 0f ? _measuredArm : 18f );
		var plan = Gambit.Chess.HandReach.Plan(
			ToR3( SeatMirror( seat, graspLocal ) ),
			ToR3( SeatMirror( seat, shoulderL ) ),
			ToR3( pelvisWhite ), footLW, footRW, t );

		if ( plan.PelvisDelta.Length < 0.01f && plan.Lean < 0.01f )
		{
			if ( _riseActive ) ReleaseHalfRise( r );
			return Vector3.Zero;
		}

		// Mirror the plan back to the seat frame, then to world directions.
		Vector3 pelvisDeltaWorld = station.WorldRotation * SeatMirror( seat, ToV3( plan.PelvisDelta ) );
		Vector3 leanWorld = station.WorldRotation * SeatMirror( seat, ToV3( plan.LeanDir * plan.Lean ) );
		_riseApplied = pelvisDeltaWorld;   // set BEFORE the foot pins, which pre-compensate by it
		_riseActive = true;

		var model = r.Model;
		if ( model?.Bones.GetBone( "pelvis" ) is { } pelvisBone && r.TryGetBoneTransformAnimation( pelvisBone, out var pw ) )
		{
			pw.Position += pelvisDeltaWorld;
			r.SetBoneTransform( pelvisBone, r.WorldTransform.ToLocal( pw ) );
		}
		if ( model?.Bones.GetBone( "spine_2" ) is { } spineBone && r.TryGetBoneTransformAnimation( spineBone, out var sw ) )
		{
			sw.Position += leanWorld;
			r.SetBoneTransform( spineBone, r.WorldTransform.ToLocal( sw ) );
		}

		PinFoot( r, station, seat, "ankle_L", IkFootLeft, plan.FootL );
		PinFoot( r, station, seat, "ankle_R", IkFootRight, plan.FootR );

		return pelvisDeltaWorld + leanWorld;
	}

	/// <summary>Pin one foot to the planner's plant. The foot rides ONLY the pelvis override, so
	/// its IK target is pre-compensated by the pelvis delta alone (not the spine lean).</summary>
	void PinFoot( SkinnedModelRenderer r, ChessStation station, ChessSeat seat, string ankleBone,
		string ikName, Gambit.Chess.R3 footWhite )
	{
		var footWorld = station.WorldTransform.PointToWorld( SeatMirror( seat, ToV3( footWhite ) ) ) - _riseApplied;
		var rot = Rotation.Identity;
		if ( r.TryGetBoneTransform( ankleBone, out var tx ) )
			rot = tx.Rotation;
		r.SetIk( ikName, new Transform( footWorld, rot ) );
	}

	/// <summary>Drop the half-rise: clear the bone overrides and foot pins, zero the eased state
	/// so the next reach starts from the chair rather than a stale mid-rise.</summary>
	void ReleaseHalfRise( SkinnedModelRenderer r )
	{
		if ( !_riseActive ) return;
		r.ClearPhysicsBones();
		r.ClearIk( IkFootLeft );
		r.ClearIk( IkFootRight );
		_riseApplied = Vector3.Zero;
		_handServo = Vector3.Zero;
		_servoTrueAsk = null;
		_riseActive = false;
	}

	/// <summary>Closed-loop hand servo: measure last frame's rendered hand_R against the true
	/// world ask, integrate the error, clamp and decay. Steers out the ~5u post-override native
	/// warp no API can read (fact #4).</summary>
	Vector3 UpdateServo( SkinnedModelRenderer r, Vector3 worldAsk )
	{
		float k = 1f - MathF.Exp( -TerryHands.ServoRate * Time.Delta );
		if ( _servoTrueAsk is { } prev && ( prev - worldAsk ).Length < 20f
			&& r.TryGetBoneTransform( "hand_R", out var handNow ) )
		{
			var err = worldAsk - handNow.Position;
			_handServo += err * k;
			if ( _handServo.Length > TerryHands.ServoClamp )
				_handServo = _handServo.Normal * TerryHands.ServoClamp;
		}
		else
		{
			// New move (big jump) or no bone: decay toward zero rather than snap.
			_handServo = Vector3.Lerp( _handServo, Vector3.Zero, k );
		}
		_servoTrueAsk = worldAsk;
		return _handServo;
	}

	/// <summary>Measure the working arm's length off the bones (fact #9: the reach numbers must
	/// come from the live skeleton, not a guess). Sum of the two proximal segments.</summary>
	void UpdateMeasuredArm( SkinnedModelRenderer r, ChessStation station )
	{
		if ( BoneWorld( r, "arm_upper_R" ) is { } a && BoneWorld( r, "arm_lower_R1" ) is { } b
			&& BoneWorld( r, "hand_R1" ) is { } c )
			_measuredArm = ( b - a ).Length + ( c - b ).Length;
	}

	/// <summary>A bone's ANIMATION-pose position (pre-override), station-local. Reading the
	/// animation pose -- not the final, already-leaned one -- keeps the reach deficit from
	/// feeding back into the lean that produced it and oscillating.</summary>
	Vector3? BoneLocalAnim( SkinnedModelRenderer r, ChessStation station, string bone )
	{
		if ( r.Model?.Bones.GetBone( bone ) is { } b && r.TryGetBoneTransformAnimation( b, out var tx ) )
			return station.WorldTransform.PointToLocal( tx.Position );
		return null;
	}

	/// <summary>A bone's final rendered world position (for measuring the arm). Uses the
	/// by-name overload of TryGetBoneTransform (the one attempt 2 verified).</summary>
	Vector3? BoneWorld( SkinnedModelRenderer r, string bone )
	{
		if ( r.TryGetBoneTransform( bone, out var tx ) )
			return tx.Position;
		return null;
	}

	/// <summary>Mirror a station-local vector through the station centre for the Black seat (a
	/// 180° Z rotation -- negate X and Y, keep Z). White is the planner's native frame, so this
	/// is identity for White and the whole Black-seat story for Black. Works for both points
	/// (station-local) and direction vectors, since it is linear.</summary>
	static Vector3 SeatMirror( ChessSeat seat, Vector3 v ) =>
		seat == ChessSeat.White ? v : new Vector3( -v.x, -v.y, v.z );

	static Gambit.Chess.R3 ToR3( Vector3 v ) => new( v.x, v.y, v.z );
	static Vector3 ToV3( Gambit.Chess.R3 v ) => new( v.X, v.Y, v.Z );

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
		ApplySeatedFacing( station, seat );
	}

	/// <summary>Point the seated body at the board. Split out of <see cref="PlantOnSeat"/> and
	/// re-run EVERY FRAME from <see cref="ApplySitPose"/> (local and proxy) so
	/// <c>gambit_terry_face</c> turns terry LIVE — no re-sit. During a seat-switch schwoop this
	/// is harmless: UpdateSeatSwitch runs after ApplySitPose and overrides the rotation until it
	/// lands. LookAt aims the rig at the board; TerryHands.FaceYaw is the model-forward offset
	/// (0 = raw LookAt, matches the shipped M13 baseline), which can only be settled in-engine.</summary>
	void ApplySeatedFacing( ChessStation station, ChessSeat seat )
	{
		if ( station == null ) return;

		// Yaw at the board, level — a flat look, so the pitch of the line from the chair up
		// to the board can't tip the whole body forward.
		var seatPos = WorldPosition;
		var boardFlat = station.WorldPosition;
		boardFlat.z = seatPos.z;
		var toBoard = boardFlat - seatPos;
		if ( toBoard.Length < 0.01f ) return;

		WorldRotation = Rotation.LookAt( toBoard, Vector3.Up )
			* Rotation.FromAxis( Vector3.Up, TerryHands.FaceYaw );
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
