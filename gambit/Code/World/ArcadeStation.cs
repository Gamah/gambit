using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Game;
using Gambit.UI;
using Gambit.UI.Screens;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// A wall screen the player can play Gambit on. One per screen instance —
/// there are four in the lobby, one per wall, each independently usable.
/// Occupancy is networked: the GameObject is network-spawned by the host
/// (see LobbyNetworkManager) and the occupant fields are host-authoritative
/// [Sync] properties driven by Rpc.Host requests, so everyone in the lobby
/// sees who is on which screen and can't double-occupy one.
/// Gameplay still runs against the Go backend, but solo boards are mirrored to
/// spectators: the occupant relays encoded moves ([Rpc.Broadcast]) and every
/// other client simulates + renders the board locally — see "Board streaming"
/// below.
/// </summary>
public sealed class ArcadeStation : Component
{
	/// <summary>The station the local player is currently locked into, if any.</summary>
	public static new ArcadeStation Active { get; private set; }

	// ── Relay taps (spectator board) ──
	// The board relay (NetBoard* RPCs) drives each cabinet's own spectator
	// CubeBoardView. These static events re-broadcast the same stream so a single
	// extra consumer — the giant SpectatorBoard over the leaderboard wall — can
	// mirror whichever cabinet it features without duplicating the relay plumbing.
	// They fire on every client (including the occupant, who has no per-cabinet
	// spectator view of their own game) so the wall board can show any live game.

	/// <summary>A station announced a fresh board: grid (100 digit chars) + encoded
	/// selectors (see SelectorsToString) — works for solo and multiplayer alike.</summary>
	public static event Action<ArcadeStation, string, string> RelayStarted;
	/// <summary>A featured station pushed a state update: authoritative grid (null if
	/// unchanged this message), an encoded rotation to animate (0–161, or -1), and
	/// the current selectors.</summary>
	public static event Action<ArcadeStation, string, int, string> RelaySynced;
	/// <summary>A station's relayed board ended (occupant left / backed out).</summary>
	public static event Action<ArcadeStation> RelayEnded;

	/// <summary>Camera lock target while a player is using this screen.</summary>
	[Property] public GameObject CameraAnchor { get; set; }

	/// <summary>SteamId of the connection using this screen, 0 if free. Host-authoritative.</summary>
	[Sync( SyncFlags.FromHost )] public ulong OccupantSteamId { get; set; }

	// NOTE: the occupant is identified by SteamId + display name only. The player's
	// Gambit GUID is a secret backend credential and is NEVER replicated to lobby
	// peers — doing so would let anyone in a public lobby impersonate the occupant.
	[Sync( SyncFlags.FromHost )] public string OccupantName { get; set; }

	public bool Occupied => OccupantSteamId != 0 || Active == this;

	/// <summary>Late-join snapshot of the occupant's board: 100 digit chars,
	/// host-maintained from the relayed grid stream; null/empty = no board.</summary>
	[Sync( SyncFlags.FromHost )] public string BoardState { get; set; }
	/// <summary>Occupant's selectors (encoded, see SelectorsToString), host-maintained
	/// for late joiners. Default = one white selector centered at (4,4).</summary>
	[Sync( SyncFlags.FromHost )] public string BoardSelectors { get; set; } = "044";

	// Local-only physical cube board shown while playing (never networked)
	GameObject _cubeBoard;

	// ── Demo mode (attract replay, locked to the OG cabinet) ──
	public bool   DemoActive   { get; private set; }
	public string DemoUsername { get; private set; }
	public string DemoSeed     { get; private set; }
	public string DemoMode     { get; private set; }
	int  _demoGen;
	bool _demoTaskRunning;
	GameObject _demoTextGo; // caption floating in front of the cabinet while DemoActive
	static bool _sessionDemoPlayed; // any station Enter() kills demo for the whole session

	/// <summary>Demo only ever runs on the OG cabinet — ArcadeRing aims station 0
	/// at the player spawn, so it's the one players walk up to first.</summary>
	bool IsDemoStation => GameObject.Name == "ArcadeStation0";

	// Spectator copy of the occupant's board (all other clients)
	GameObject _remoteGo;
	CubeBoardView _remoteView;
	RemoteBoard _remoteSim;
	TimeSince _remoteAge;

	// Occupant-side relay state — last values pushed, to send only on change
	bool   _relayActive;
	string _relayGrid;
	string _relaySels;
	RotateAnimRequest _relayAnim;

	public void Enter()
	{
		if ( Active != null || Occupied ) return;
		// This GO is about to be destroyed mid-slide — don't lock onto it
		if ( ArcadeRing.Instance?.Rebuilding ?? false ) return;

		_demoGen++;
		_sessionDemoPlayed      = true;
		DemoActive       = false;
		_demoTaskRunning = false;
		DestroyRemoteView();
		// Entering bumps _demoGen, so the demo task's DemoRelease won't fire RelayEnded —
		// drop any giant board still mirroring this cabinet's demo so the live game's
		// relay (or SpectatorBoard.PollForLiveBoard) takes over cleanly.
		RelayEnded?.Invoke( this );

		Active = this;
		RefreshOccupant();
		ValidateIdentity();
		// First-run onboarding creates the GUID after we enter — pick it up when it lands
		SplashScreen.OnPlayerReady += RefreshOccupant;
		GameController.InputActive = true;

		_cubeBoard = new GameObject( true, "CubeBoard" );
		_cubeBoard.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_cubeBoard.WorldPosition = GameObject.WorldPosition;
		_cubeBoard.WorldRotation = GameObject.WorldRotation;
		_cubeBoard.AddComponent<CubeBoardView>().Station = this;
	}

	public void Leave()
	{
		if ( Active != this ) return;

		SplashScreen.OnPlayerReady -= RefreshOccupant;
		GameController.InputActive = false;

		// Clear any session state this screen owns
		var game = GameController.Instance;
		if ( game != null && game.State != GameState.Idle )
			game.ReturnToMenu();

		// Always disconnect — a failed lobby can land in Idle with the socket still open
		MultiplayerController.Instance?.Disconnect();

		// Reset the screen UI to the main menu even when no session was active,
		// so re-entering doesn't restore whatever sub-view was left open
		GameEvents.FireReturnToMenu();

		if ( _cubeBoard.IsValid() )
			_cubeBoard.Destroy();
		_cubeBoard = null;

		if ( _relayActive )
		{
			ResetRelayState();
			NetBoardClear();
		}

		Active = null;
		RequestLeave();

		// Refresh the wall leaderboards now that a run may have just finished.
		WallLeaderboardPanel.RefreshAll();
	}

	protected override void OnUpdate()
	{
		if ( !_sessionDemoPlayed && IsDemoStation && !DemoActive && !_demoTaskRunning &&
			!Occupied && Active == null && !( PlayerData.Load()?.DemoSkip ?? false ) )
		{
			_ = TryStartDemo();
		}

		UpdateDemoText();

		if ( Active == this )
			UpdateRelay();
		else
			UpdateRemoteView();
	}

	// ── Board streaming (spectator views) ──
	// The occupant relays the authoritative grid it sees (+ all selectors) for
	// whichever game it's playing — solo or multiplayer, treated identically. Every
	// other client renders it with its own CubeBoardView fed by a grid-driven
	// RemoteBoard, so effects follow each viewer's local explodiness/gravity. The
	// occupant never resolves on the spectators' behalf; it always pushes the grid
	// it already has (solo: post-resolution from GameController; MP: server-synced),
	// so spectators can't diverge. Rotations carry the encoded move purely so the
	// spectator can play the 90ms arc before snapping to the grid. The host folds the
	// stream into BoardState/BoardSelectors for late joiners.

	void UpdateRelay()
	{
		DestroyRemoteView(); // never spectate the station we're playing on

		// MP takes priority over solo (a station only ever runs one at a time, but a
		// stale GameController can linger Idle behind a live MP session).
		var mp   = MultiplayerController.Instance;
		var game = GameController.Instance;
		bool mpLive   = mp != null && (mp.State == MpState.Playing || mp.State == MpState.GameOver);
		bool soloLive = !mpLive && game?.Board != null &&
			(game.State == GameState.Playing || game.State == GameState.Complete);

		if ( !mpLive && !soloLive )
		{
			if ( _relayActive )
			{
				ResetRelayState();
				NetBoardClear();
			}
			return;
		}

		int[] cells; bool animating; RotateAnimRequest anim; string sels;
		if ( mpLive )
		{
			cells = mp.Grid; animating = mp.Animating; anim = mp.PendingAnim;
			sels = MpSelectorsToString( mp );
		}
		else
		{
			cells = game.Board.Cells; animating = game.Animating; anim = game.PendingAnim;
			sels = SoloSelectorToString( game );
		}
		string grid = GridToString( cells );

		if ( !_relayActive )
		{
			_relayActive = true;
			_relayGrid = grid; _relaySels = sels; _relayAnim = anim;
			NetBoardStart( grid, sels );
			return;
		}

		// A new rotation began — relay it so spectators can animate the arc.
		if ( animating && anim != null && !ReferenceEquals( anim, _relayAnim ) )
		{
			_relayAnim = anim;
			_relaySels = sels;
			NetBoardSync( null, anim.Dir * 81 + anim.Row * 9 + anim.Col, sels );
		}

		// The authoritative grid settled (post-resolution) — snap spectators to it.
		if ( grid != _relayGrid )
		{
			_relayGrid = grid;
			_relaySels = sels;
			NetBoardSync( grid, -1, sels );
		}
		else if ( sels != _relaySels )
		{
			_relaySels = sels;
			NetBoardSync( null, -1, sels );
		}
	}

	void ResetRelayState()
	{
		_relayActive = false;
		_relayGrid = null;
		_relaySels = null;
		_relayAnim = null;
	}

	void UpdateRemoteView()
	{
		// Late join (or missed start RPC): board in progress, no view yet.
		// All-zeros means a finished board whose outro already played — skip.
		if ( _remoteGo == null && OccupantSteamId != 0 &&
			!string.IsNullOrEmpty( BoardState ) && !AllZeros( BoardState ) )
			CreateRemoteView( BoardState, BoardSelectors );

		if ( _remoteGo == null || _remoteSim == null ) return;

		// Occupant vanished or the host cleared the snapshot — retract.
		// (_remoteAge guard: BoardState sync can lag the start RPC by a beat.)
		// DemoActive guard: demo view has no occupant by design — don't retract it early.
		if ( !_remoteSim.Cleared && _remoteAge > 2f && !DemoActive &&
			(OccupantSteamId == 0 || string.IsNullOrEmpty( BoardState )) )
			_remoteSim.Cleared = true;

		// Tear down once the sim ended and the outro/debris finished
		if ( (_remoteSim.Cleared || _remoteSim.Finished) && _remoteView.Idle )
			DestroyRemoteView();
	}

	void CreateRemoteView( string grid, string selectors )
	{
		DestroyRemoteView();
		var cells = ParseGrid( grid );
		if ( cells == null ) return;

		_remoteGo = new GameObject( true, "CubeBoard (remote)" );
		_remoteGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_remoteGo.WorldPosition = GameObject.WorldPosition;
		_remoteGo.WorldRotation = GameObject.WorldRotation;
		_remoteSim = new RemoteBoard( cells, GameObject.WorldPosition + Vector3.Up * 46f, ParseSelectors( selectors ) );
		_remoteView = _remoteGo.AddComponent<CubeBoardView>();
		_remoteView.Station = this;
		_remoteView.Remote = _remoteSim;
		_remoteAge = 0;
	}

	void DestroyRemoteView()
	{
		if ( _remoteGo.IsValid() )
			_remoteGo.Destroy();
		_remoteGo = null;
		_remoteView = null;
		_remoteSim = null;
	}

	/// <summary>Host calls this per-cabinet as it starts a slide leg (issue #54) so
	/// every client hears the positional slide SFX, not just the host.</summary>
	[Rpc.Broadcast]
	public void NetSlideSfx( string variant, bool ascend )
	{
		Audio.SoundPlayer.PlaySlide( GameObject, variant, ascend );
	}

	[Rpc.Broadcast]
	void NetBoardStart( string grid, string selectors )
	{
		if ( Networking.IsHost )
		{
			BoardState = grid;
			BoardSelectors = selectors;
		}
		RelayStarted?.Invoke( this, grid, selectors );
		if ( Active == this ) return;
		CreateRemoteView( grid, selectors );
	}

	[Rpc.Broadcast]
	void NetBoardSync( string grid, int move, string selectors )
	{
		if ( Networking.IsHost )
		{
			if ( !string.IsNullOrEmpty( grid ) ) BoardState = grid;
			if ( !string.IsNullOrEmpty( selectors ) ) BoardSelectors = selectors;
		}
		RelaySynced?.Invoke( this, grid, move, selectors );
		if ( Active == this ) return;
		_remoteSim?.ApplySync( ParseGrid( grid ), move, ParseSelectors( selectors ) );
	}

	[Rpc.Broadcast]
	void NetBoardClear()
	{
		if ( Networking.IsHost ) BoardState = null;
		RelayEnded?.Invoke( this );
		if ( _remoteSim != null ) _remoteSim.Cleared = true;
	}

	// ── Selector relay encoding ──
	// Each selector is three digit chars: color (0–4, 0 = a plain white solo ring),
	// row (0–8), col (0–8). Concatenated; the first selector is the occupant's own.

	static string SoloSelectorToString( GameController game ) =>
		$"0{game.SelectorRow}{game.SelectorCol}";

	static string MpSelectorsToString( MultiplayerController mp )
	{
		// Own selector first (colored), then every other player's, de-duped by color.
		var sb = new System.Text.StringBuilder();
		sb.Append( (char)('0' + Math.Clamp( mp.MyColor, 0, 4 )) );
		sb.Append( (char)('0' + Math.Clamp( mp.SelectorRow, 0, 8 )) );
		sb.Append( (char)('0' + Math.Clamp( mp.SelectorCol, 0, 8 )) );
		if ( mp.Selectors != null )
		{
			foreach ( var s in mp.Selectors )
			{
				if ( s.Color == mp.MyColor ) continue;
				sb.Append( (char)('0' + Math.Clamp( s.Color, 0, 4 )) );
				sb.Append( (char)('0' + Math.Clamp( s.Row, 0, 8 )) );
				sb.Append( (char)('0' + Math.Clamp( s.Col, 0, 8 )) );
			}
		}
		return sb.ToString();
	}

	internal static List<MpSelectorInfo> ParseSelectors( string s )
	{
		if ( string.IsNullOrEmpty( s ) || s.Length % 3 != 0 ) return null;
		var list = new List<MpSelectorInfo>( s.Length / 3 );
		for ( int i = 0; i + 2 < s.Length; i += 3 )
			list.Add( new MpSelectorInfo( s[i] - '0', s[i + 1] - '0', s[i + 2] - '0' ) );
		return list;
	}

	static string GridToString( int[] cells )
	{
		var chars = new char[cells.Length];
		for ( int i = 0; i < cells.Length; i++ )
			chars[i] = (char)('0' + cells[i]);
		return new string( chars );
	}

	static int[] ParseGrid( string grid )
	{
		if ( grid == null || grid.Length != GameBoard.CellCount ) return null;
		var cells = new int[grid.Length];
		for ( int i = 0; i < grid.Length; i++ )
			cells[i] = grid[i] - '0';
		return cells;
	}

	static bool AllZeros( string grid )
	{
		foreach ( var c in grid )
			if ( c != '0' ) return false;
		return true;
	}

	/// <summary>Re-announce the local identity to the host — also called after a
	/// Profile GUID import so the synced occupant fields don't go stale.</summary>
	public void RefreshOccupant()
	{
		var data = PlayerData.Load();
		RequestEnter( data?.Username );
	}

	/// <summary>Check the saved GUID against the server on cabinet entry; a definite
	/// 404 (server lost/never had the player) clears the local identity so the splash
	/// re-enrolls. Network errors leave the identity alone.</summary>
	async void ValidateIdentity()
	{
		var guid = PlayerData.Load()?.Guid;
		if ( string.IsNullOrEmpty( guid ) ) return;
		try
		{
			if ( !await ApiClient.PlayerExists( guid ) && Active == this )
			{
				// Never log the full GUID (it's a credential); the public tag is safe.
				Log.Warning( $"[Gambit] Player {Redact( guid )} unknown to server — re-enrolling" );
				PlayerData.ClearIdentity();
				return;
			}

			// Existing account: (re)link the local SteamID64 once (issue #68). No-op
			// on non-Steam builds; best-effort, so it never blocks the tag backfill.
			await ApiClient.LinkSteamId( guid );

			if ( string.IsNullOrEmpty( PlayerData.Load()?.PlayerTag ) )
			{
				// Tag backfill for identities saved before the player_tag migration
				var player = await ApiClient.GetPlayer( guid );
				if ( !string.IsNullOrEmpty( player?.PlayerTag ) )
				{
					var data = PlayerData.Load();
					data.PlayerTag = player.PlayerTag;
					data.Save();
				}
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit] Identity check failed: {e.Message}" );
		}
	}

	/// <summary>Truncate a GUID for logging so the full credential never lands in
	/// s&box logs/console (which get shared in bug reports/screenshots).</summary>
	static string Redact( string guid ) =>
		string.IsNullOrEmpty( guid ) ? "(none)" : guid.Substring( 0, Math.Min( 8, guid.Length ) ) + "…";

	[Rpc.Host]
	void RequestEnter( string name )
	{
		// First request wins; lets the current occupant refresh their own info
		if ( OccupantSteamId != 0 && OccupantSteamId != Rpc.Caller.SteamId ) return;

		OccupantSteamId = Rpc.Caller.SteamId;
		OccupantName = string.IsNullOrEmpty( name ) ? Rpc.Caller.DisplayName : name;
	}

	[Rpc.Host]
	void RequestLeave()
	{
		if ( OccupantSteamId != Rpc.Caller.SteamId ) return;
		ClearOccupant();
	}

	/// <summary>Host-side: free this station if the disconnecting player occupied it.</summary>
	internal void HostHandleDisconnect( ulong steamId )
	{
		if ( OccupantSteamId != 0 && OccupantSteamId == steamId )
			ClearOccupant();
	}

	void ClearOccupant()
	{
		OccupantSteamId = 0;
		OccupantName = null;
		BoardState = null;
		BoardSelectors = "044";
	}

	// ── Demo replay ──

	/// <summary>Caption WorldPanel floating in front of the cabinet — the screen
	/// panel sits behind the slid-out cube board, so text drawn there is hidden.
	/// Local-only, created/destroyed to track DemoActive.</summary>
	void UpdateDemoText()
	{
		if ( DemoActive && !_demoTextGo.IsValid() )
		{
			// Centered on the slid-out cube board: same y/z as the Screen child
			// (y=0, z=ScreenHeight) and outset only a small bit toward the player so it
			// reads as sitting just in front of the cubes rather than floating far out
			// (a large -x parallaxes up/left from the angled roaming camera).
			float s = ArcadeRing.Instance?.CabinetScale ?? 1.5f;
			float screenHeight = ArcadeRing.Instance?.ScreenHeight ?? (46f * s);
			_demoTextGo = new GameObject( true, "DemoInfo" );
			_demoTextGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			_demoTextGo.Parent = GameObject;
			_demoTextGo.LocalPosition = new Vector3( -2f * s, 0f, screenHeight );
			// Same facing as the Screen child: yaw 180 points the panel front at the player
			_demoTextGo.LocalRotation = Rotation.FromYaw( 180f );
			_demoTextGo.LocalScale = 3f * s;
			_demoTextGo.AddComponent<WorldPanel>();
			_demoTextGo.AddComponent<Gambit.UI.DemoInfoPanel>().Station = this;
		}
		else if ( !DemoActive && _demoTextGo.IsValid() )
		{
			_demoTextGo.Destroy();
			_demoTextGo = null;
		}
	}

	/// <summary>Permanently end the attract demo for this session and tear down any
	/// instance currently playing it. Called when the player kicks off a replay (which
	/// claims the giant board) so the demo can't keep mirroring cabinet 0 behind it.</summary>
	public static void KillDemo( Scene scene )
	{
		_sessionDemoPlayed = true;
		if ( scene == null ) return;
		foreach ( var station in scene.GetAllComponents<ArcadeStation>() )
		{
			if ( !station.DemoActive && !station._demoTaskRunning ) continue;
			station._demoGen++;
			station.DemoActive       = false;
			station._demoTaskRunning = false;
			station.DestroyRemoteView();
			RelayEnded?.Invoke( station );
		}
	}

	// Release demo flags only if this task is still the current one.
	// If a newer task (higher gen) has taken over, leave its flags alone.
	void DemoRelease( int gen )
	{
		if ( gen != _demoGen ) return;
		_demoTaskRunning = false;
		DemoActive       = false;
		DestroyRemoteView();
		RelayEnded?.Invoke( this ); // tear down the giant board mirroring this demo
	}

	bool DemoShouldRun( int gen ) =>
		gen == _demoGen && !_sessionDemoPlayed && !Occupied && Active == null;

	async Task TryStartDemo()
	{
		_demoTaskRunning = true;
		int gen = ++_demoGen;
		DemoActive   = false;
		DemoUsername = null;
		DemoSeed     = null;
		DemoMode     = null;

		List<LeaderboardEntry> lb = null;
		string mode = null;
		try
		{
			lb = await ApiClient.GetHourlyLeaderboard( "time" );
			mode = "hourly";
			if ( lb == null || lb.Count == 0 )
			{
				lb = await ApiClient.GetDailyLeaderboard( "time" );
				mode = "daily";
			}
		}
		catch { }

		if ( !DemoShouldRun( gen ) || lb == null || lb.Count == 0 )
		{
			DemoRelease( gen );
			return;
		}

		var entry = lb[0];

		ReplayResponse replay;
		PuzzleResponse puzzle;
		try
		{
			replay = await ApiClient.GetReplay( entry.SessionId );
			if ( replay?.Moves == null || replay.Moves.Count == 0 ) { DemoRelease( gen ); return; }
			puzzle = await ApiClient.RegenerateGrid( replay.Seed );
		}
		catch { DemoRelease( gen ); return; }

		if ( !DemoShouldRun( gen ) ) { DemoRelease( gen ); return; }

		DemoActive   = true;
		DemoUsername = !string.IsNullOrEmpty( entry.Username ) ? entry.Username : ( entry.PlayerTag ?? "Anonymous" );
		DemoSeed     = replay.Seed;
		DemoMode     = mode;
		// The demo plays back locally (no RPC relay): it resolves the recorded moves on
		// a private board and feeds the resulting grids straight into the RemoteBoard,
		// the same grid-driven path the network relay uses. It also fires the static
		// relay events directly (local only, no broadcast) so this client's giant
		// SpectatorBoard — which features cabinet 0 — mirrors the attract demo too.
		var demoBoard = new GameBoard( puzzle.FlatGrid() );
		string demoGrid = GridToString( demoBoard.Cells );
		CreateRemoteView( demoGrid, "044" );
		RelayStarted?.Invoke( this, demoGrid, "044" );

		static List<MpSelectorInfo> SoloSel( int row, int col ) =>
			new() { new MpSelectorInfo( 0, row, col ) };

		bool hasTimings = replay.Moves[0].PlayedAt > 0;
		long t0    = hasTimings ? replay.Moves[0].PlayedAt : 0;
		float start = RealTime.Now;

		for ( int i = 0; i < replay.Moves.Count; i++ )
		{
			var m = replay.Moves[i];
			float at   = hasTimings ? (m.PlayedAt - t0) / 1000f : (i + 1) * 0.5f;
			float wait = start + at - RealTime.Now;
			if ( wait > 0f ) await Task.DelayRealtimeSeconds( wait );

			if ( !DemoShouldRun( gen ) ) { DemoRelease( gen ); return; }

			var sel = SoloSel( m.Row, m.Col );
			string selStr = $"0{m.Row}{m.Col}";
			if ( m.Selector )
			{
				_remoteSim?.ApplySync( null, -1, sel );
				RelaySynced?.Invoke( this, null, -1, selStr );
			}
			else
			{
				// Animate the rotation, then snap to the resolved grid.
				int encoded = m.Direction * 81 + m.Row * 9 + m.Col;
				_remoteSim?.ApplySync( null, encoded, sel );
				RelaySynced?.Invoke( this, null, encoded, selStr );
				demoBoard.Rotate( m.Row, m.Col, m.Direction );
				var snap = demoBoard.CloneCells();
				_remoteSim?.ApplySync( snap, -1, sel );
				RelaySynced?.Invoke( this, GridToString( snap ), -1, selStr );
			}
		}

		// Board solved — pause on final state, then let OnUpdate restart via polling.
		await Task.DelayRealtimeSeconds( 4f );
		DemoRelease( gen );
	}

	protected override void OnDestroy()
	{
		if ( Active == this )
			Leave();
		DestroyRemoteView();
	}
}
