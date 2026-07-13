using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rotaliate.Api;
using Rotaliate.Game;
using Sandbox;

namespace Rotaliate.World;

/// <summary>
/// Client setting "spectator board": a giant copy of the cabinet cube board
/// (CubeBoardView) floating above the leaderboard wall, scaled up uniformly
/// (SizeScale, 12×) so the whole lobby can watch one active game from across the
/// room. Lives as a component on the Room GO (lobby.scene), but only renders cubes
/// while this client has it enabled (PlayerData.SpectatorBoard, default on).
///
/// It always mirrors cabinet 0 (ArcadeStation0) — the fixed "featured" cabinet,
/// the one ArcadeRing aims at the player spawn. It reuses the board relay that
/// already drives each cabinet's own spectator view: ArcadeStation re-broadcasts
/// every relayed grid update through the static RelayStarted/Synced/Ended events
/// (solo and multiplayer alike, and on every client including cabinet 0's own
/// occupant), so this board mirrors cabinet 0's game with its own (silent)
/// grid-driven RemoteBoard sim, rendered through a SuppressCabinet CubeBoardView.
/// It tears down when that game ends. A BoardState poll covers the case where the
/// setting is switched on (or a player late-joins) mid-game, after the start event
/// has already fired.
///
/// Placement is room-relative: centered on the wall's width, floating a little
/// above the wall top, facing back into the room. Several knobs are exposed
/// because none of this could be verified in an editor on this machine — flip
/// Yaw if the board faces away, and the rotation sign lives in CubeBoardView.
/// </summary>
public sealed class SpectatorBoard : Component
{
	/// <summary>Uniform size multiplier vs. a cabinet board. 12×.</summary>
	[Property, Range( 8f, 28f )] public float SizeScale { get; set; } = 18f;

	/// <summary>Gap between the wall top and the bottom of the floating board. The
	/// board's floor clearance tracks the size automatically — its bottom always
	/// sits this far above the wall top regardless of SizeScale (the vertical center
	/// is wall top + this + halfSpan, and halfSpan scales with SizeScale).</summary>
	[Property, Range( 10f, 30f )] public float ClearAboveWall { get; set; } = 20f;

	/// <summary>How far the board sits in front of the wall plane, toward the room.
	/// 0 = coplanar with the wall (inline), positive pulls it toward the room.</summary>
	[Property, Range( -10f, 10f )] public float WallInset { get; set; } = 0f;

	/// <summary>Yaw (room-relative) that turns the board's back into the wall and its
	/// face toward the room. Flip by 180 if it renders facing away.</summary>
	[Property] public float Yaw { get; set; } = 90f;

	/// <summary>The floating board sits high above the wall, so the orbiting sun barely
	/// catches its room-facing cube faces (and nothing lights it at night). It gets its
	/// own dedicated SPOT light, placed this far in front of the board toward the room
	/// and aimed back at the board — a cone, so it lights the board without flooding the
	/// room the way a point light did.</summary>
	[Property, Range( 200f, 800f )] public float BoardLightDistance { get; set; } = 400f;

	/// <summary>Brightness of the spectator board's dedicated spot light.</summary>
	[Property, Range( 0f, 30f )] public float BoardLightBrightness { get; set; } = 10f;

	/// <summary>Spot cone half-angles (degrees). Outer must cover the board's half-span
	/// at BoardLightDistance; default ~46° suits the 18× board from 400 units.</summary>
	[Property, Range( 5f, 80f )] public float BoardLightConeOuter { get; set; } = 46f;
	[Property, Range( 0f, 80f )] public float BoardLightConeInner { get; set; } = 30f;

	/// <summary>The Room GO's LobbyRoom, used for wall dimensions. Self-resolved from
	/// this GameObject (the component lives on the Room GO).</summary>
	public LobbyRoom Room { get; set; }

	/// <summary>Per-client setting (PlayerData.SpectatorBoard, default on): whether this
	/// player sees the giant board. Replaces the old host-authoritative toggle.</summary>
	static bool SpectatorEnabled => PlayerData.Load()?.SpectatorBoard ?? true;

	/// <summary>The one giant board in this client's scene. Set on enable; used by the
	/// profile UI to start a replay and by LobbyPlayer to stop one.</summary>
	public static SpectatorBoard Instance { get; private set; }

	/// <summary>A recorded-game replay is currently playing on the giant board (or its
	/// data is still loading). The board ignores live cabinet mirroring while true.</summary>
	public static bool Replaying => Instance?._replayActive ?? false;

	/// <summary>Fetch a session replay and play it on the giant board, replacing any
	/// live mirror. Triggered by the profile "watch" affordance.</summary>
	public static void StartReplay( string sessionId ) => Instance?.BeginReplay( sessionId );

	/// <summary>Stop and tear down any playing/loading replay.</summary>
	public static void StopReplay() => Instance?.EndReplay();

	bool _started;

	ArcadeStation _featured;
	RemoteBoard _sim;
	CubeBoardView _view;
	GameObject _viewGo;
	GameObject _boardLightGo;
	TimeSince _featuredAge;

	// ── Replay playback ──
	const float ReplayFallbackDelay = 0.5f; // per-move delay when played_at wasn't recorded
	const float ReplayLinger = 3f;          // hold the final (uncleared) board before teardown
	bool _replayActive;
	int _replayGen;                         // bumped to cancel an in-flight fetch / playback
	GameBoard _replayBoard;                 // local sim resolving the recorded moves
	List<ReplayMove> _moves;
	int _moveIdx;
	bool _hasTimings;
	long _t0;
	float _replayStart;
	bool _replayDone;
	TimeSince _replayDoneSince;
	// Multiplayer: each player's ring color + last selector position, so the replay
	// shows every player's selector (in their color) rather than one jumping ring.
	Dictionary<int, int> _playerColor;            // player_number → ring color (0 = solo white)
	Dictionary<int, (int row, int col)> _playerSel; // player_number → selector top-left
	bool _mpReplay;                               // ≥2-player roster: ends on a win, not a full clear

	protected override void OnStart()
	{
		_started = true;
		Room ??= Components.Get<LobbyRoom>();
	}

	protected override void OnEnabled()
	{
		Instance = this;
		Room ??= Components.Get<LobbyRoom>();
		ArcadeStation.RelayStarted += OnRelayStarted;
		ArcadeStation.RelaySynced += OnRelaySynced;
		ArcadeStation.RelayEnded += OnRelayEnded;
	}

	protected override void OnDisabled()
	{
		if ( Instance == this ) Instance = null;
		ArcadeStation.RelayStarted -= OnRelayStarted;
		ArcadeStation.RelaySynced -= OnRelaySynced;
		ArcadeStation.RelayEnded -= OnRelayEnded;
		EndReplay(); // resets replay state + tears down the view
	}

	protected override void OnUpdate()
	{
		if ( !_started ) return; // never builds geometry in the editor preview

		// A replay takes the board over completely; live cabinet mirroring is paused.
		if ( _replayActive )
		{
			DriveReplay();
			return;
		}

		if ( !SpectatorEnabled )
		{
			if ( _featured != null ) Teardown();
			return;
		}

		// Adopt a game already in progress (setting toggled on / late join mid-game),
		// after RelayStarted has come and gone. Seeds from the host snapshot; the live
		// grid updates then resume through RelaySynced.
		if ( _featured == null )
			PollForLiveBoard();

		// Cabinet 0's game ended and the outro/debris finished — tear down; it rebuilds
		// when cabinet 0 starts a new game.
		if ( _featured != null && _view.IsValid() && _sim != null &&
			(_sim.Cleared || _sim.Finished) && _view.Idle )
			Teardown();
	}

	/// <summary>The giant board always mirrors cabinet 0 (ArcadeStation0) — a fixed
	/// "featured" cabinet, not whichever started first. ArcadeRing aims station 0 at
	/// the player spawn, so it's the natural one to feature.</summary>
	static bool IsCabinetZero( ArcadeStation station ) =>
		station.IsValid() && station.GameObject.Name == "ArcadeStation0";

	void OnRelayStarted( ArcadeStation station, string grid, string selectors )
	{
		if ( !_started || _replayActive || _featured != null || !SpectatorEnabled )
			return;
		if ( !IsCabinetZero( station ) ) return;
		var cells = ParseGrid( grid );
		if ( cells == null || cells.All( c => c == 0 ) ) return;
		Build( station, cells, selectors );
	}

	void OnRelaySynced( ArcadeStation station, string grid, int move, string selectors )
	{
		if ( station == _featured )
			_sim?.ApplySync( ParseGrid( grid ), move, ArcadeStation.ParseSelectors( selectors ) );
	}

	void OnRelayEnded( ArcadeStation station )
	{
		// Let the outro play; OnUpdate tears down once the view goes idle.
		if ( station == _featured && _sim != null )
			_sim.Cleared = true;
	}

	void PollForLiveBoard()
	{
		foreach ( var station in Scene.GetAllComponents<ArcadeStation>() )
		{
			if ( !IsCabinetZero( station ) ) continue;
			var grid = station.BoardState;
			if ( station.OccupantSteamId == 0 || string.IsNullOrEmpty( grid ) ) continue;
			var cells = ParseGrid( grid );
			if ( cells == null || cells.All( c => c == 0 ) ) continue;
			Build( station, cells, station.BoardSelectors );
			return;
		}
	}

	void Build( ArcadeStation station, int[] cells, string selectors )
	{
		Teardown();
		_featured = station;
		_featuredAge = 0;

		_viewGo = new GameObject( true, "SpectatorCubeBoard" );
		_viewGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		PlaceView();

		// Silent: the cabinet this mirrors already plays the move SFX for everyone.
		_sim = new RemoteBoard( cells, Vector3.Zero, ArcadeStation.ParseSelectors( selectors ) ) { Silent = true };
		_view = _viewGo.AddComponent<CubeBoardView>();
		_view.SizeScale = SizeScale;
		_view.SuppressCabinet = true; // no cabinet furniture; never grab the player's controls
		_view.Remote = _sim;          // Station deliberately left null
	}

	/// <summary>Position the floating board: centered on the wall width, its bottom
	/// edge ClearAboveWall above the wall top, facing into the room. The cube grid's
	/// internal vertical center sits at child-local z = ScreenHeight, so the child GO
	/// origin is offset down by that much from the world center.</summary>
	void PlaceView()
	{
		if ( !_viewGo.IsValid() ) return;

		var ring = ArcadeRing.Instance;
		float cabinetScale = ring?.CabinetScale ?? 1.5f;
		float boardSize = ring?.BoardSize ?? 28f;
		float screenHeight = ring?.ScreenHeight ?? 70f;
		float halfSpan = boardSize * cabinetScale * SizeScale * 0.5f;

		float roomSize = Room?.RoomSize ?? 800f;
		float wallHeight = Room?.WallHeight ?? 150f;

		// Room-local board center: middle of the wall width, floating just above the
		// wall top, set a little in front of the wall plane.
		var localCenter = new Vector3(
			0f,
			roomSize * 0.5f - WallInset,
			wallHeight + ClearAboveWall + halfSpan );

		var roomPos = Room?.WorldPosition ?? WorldPosition;
		var roomRot = Room?.WorldRotation ?? Rotation.Identity;

		var center = roomPos + roomRot * localCenter;
		var facing = roomRot * Rotation.FromYaw( Yaw );

		_viewGo.WorldRotation = facing;
		_viewGo.WorldPosition = center - facing * new Vector3( 0f, 0f, screenHeight );

		// Dedicated spot light in front of the board (toward the room), aimed back at the
		// board so the cone lights only the board, not the room. Reads day and night
		// regardless of the sun. Parented to the view so it tears down with it.
		var lightLocal = localCenter + new Vector3( 0f, -BoardLightDistance, 0f );
		_boardLightGo = new GameObject( true, "SpectatorBoardLight" );
		_boardLightGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_boardLightGo.Parent = _viewGo;
		_boardLightGo.WorldPosition = roomPos + roomRot * lightLocal;
		_boardLightGo.WorldRotation = Rotation.LookAt( (center - _boardLightGo.WorldPosition).Normal );
		var light = _boardLightGo.AddComponent<SpotLight>();
		float b = BoardLightBrightness;
		light.LightColor = new Color( b, b, b * 0.97f );
		light.ConeInner = BoardLightConeInner;
		light.ConeOuter = BoardLightConeOuter;
		light.Radius = BoardLightDistance + halfSpan * 2f + 100f;
		light.Shadows = false; // pure fill — no self-shadowing across the cubes
	}

	// ── Replay ──

	void BeginReplay( string sessionId )
	{
		if ( !_started || string.IsNullOrEmpty( sessionId ) ) return;
		ArcadeStation.KillDemo( Scene ); // a replay claims the board — end the attract demo for good
		EndReplay();           // clear any prior replay / live mirror
		_replayActive = true;  // claims the board immediately; data loads async
		_ = LoadReplay( sessionId, ++_replayGen );
	}

	async Task LoadReplay( string sessionId, int gen )
	{
		ReplayResponse replay;
		PuzzleResponse puzzle;
		try
		{
			replay = await ApiClient.GetReplay( sessionId );
			if ( replay?.Moves == null || replay.Moves.Count == 0 )
			{
				Log.Warning( "[Rotaliate] No move data recorded for this session." );
				if ( gen == _replayGen ) EndReplay();
				return;
			}
			puzzle = await ApiClient.RegenerateGrid( replay.Seed );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Rotaliate] Replay load failed: {e.Message}" );
			if ( gen == _replayGen ) EndReplay();
			return;
		}

		if ( gen != _replayGen ) return; // cancelled (E pressed / new replay) while loading
		BuildReplay( puzzle.FlatGrid(), replay );
	}

	void BuildReplay( int[] cells, ReplayResponse replay )
	{
		Teardown();
		var moves = replay.Moves;
		_replayBoard = new GameBoard( cells );
		_moves = moves;
		_moveIdx = 0;
		_hasTimings = moves[0].PlayedAt > 0;
		_t0 = _hasTimings ? moves[0].PlayedAt : 0;
		_replayStart = RealTime.Now;
		_replayDone = false;

		// Multiplayer roster (≥2 players): map each player_number to a ring color from
		// the colors it owns, and seed every selector at board center. Solo replays
		// (no roster) keep one plain white "mine" ring (color 0).
		_playerColor = new();
		_playerSel = new();
		if ( replay.Players is { Count: > 1 } )
		{
			foreach ( var p in replay.Players )
			{
				_playerColor[p.PlayerNumber] = p.Colors is { Length: > 0 } ? p.Colors[0] : 0;
				_playerSel[p.PlayerNumber] = (4, 4);
			}
		}

		bool mp = _playerColor.Count > 0;
		_mpReplay = mp;

		_viewGo = new GameObject( true, "SpectatorReplayBoard" );
		_viewGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		PlaceView();

		// Unlike the live mirror, a replay has no cabinet playing its move SFX, so it
		// plays its own — positionally, at the giant board. MP replays have no local
		// occupant, so every selector is a color-keyed peer ring (NoOwnSelector).
		_sim = new RemoteBoard( _replayBoard.CloneCells(), _viewGo.WorldPosition, CurrentSelectors( 4, 4 ) )
		{
			NoOwnSelector = mp,
		};
		_view = _viewGo.AddComponent<CubeBoardView>();
		_view.SizeScale = SizeScale;
		_view.SuppressCabinet = true; // no cabinet furniture; never grab the player's controls
		_view.Remote = _sim;
	}

	/// <summary>Move <paramref name="player"/>'s selector to (row,col) and return the full
	/// selector set. Solo (no roster) → one white ring at the move; MP → every player's
	/// current selector in its own color (stable order, color-keyed by the renderer).</summary>
	List<MpSelectorInfo> BuildSelectors( int player, int row, int col )
	{
		if ( _playerColor == null || _playerColor.Count == 0 )
			return new() { new( 0, row, col ) };

		// Only roster players have a selector; ignore a move by an unknown player_number.
		if ( _playerSel.ContainsKey( player ) )
			_playerSel[player] = (row, col);
		return CurrentSelectors( row, col );
	}

	/// <summary>The current selector set. Solo fallback uses (row,col) for its single ring.</summary>
	List<MpSelectorInfo> CurrentSelectors( int row, int col )
	{
		if ( _playerColor == null || _playerColor.Count == 0 )
			return new() { new( 0, row, col ) };

		var list = new List<MpSelectorInfo>();
		foreach ( var (pn, pos) in _playerSel )
			list.Add( new( _playerColor.GetValueOrDefault( pn ), pos.row, pos.col ) );
		return list;
	}

	void DriveReplay()
	{
		if ( _sim == null || !_view.IsValid() ) return; // data still loading

		_sim.Update();

		// Apply every move whose scheduled time has arrived.
		while ( _moveIdx < _moves.Count )
		{
			var m = _moves[_moveIdx];
			float at = _hasTimings ? (m.PlayedAt - _t0) / 1000f : (_moveIdx + 1) * ReplayFallbackDelay;
			if ( RealTime.Now - _replayStart < at ) break;
			// Pace rotations so each flip actually animates: don't start the next
			// rotation until the previous one's animation settles. In fast (esp. MP)
			// replays multiple moves fall due in one frame, which otherwise coalesces
			// into a single visible flip — blocks teleport without rotating. Selector
			// repositions stay free-flowing.
			if ( !m.Selector && _sim.Animating ) break;
			ApplyReplayMove( m );
			_moveIdx++;
		}

		if ( _moveIdx < _moves.Count || _sim.Animating ) return;

		// MP games end on a win, not a full-board clear, so RemoteBoard never flags
		// Finished on its own — explicitly finish it once the recorded moves run out so
		// the board plays the same completion explosion as a live match (flinging the
		// colors still on the board). Solo abandoned runs (no roster) keep lingering.
		if ( _mpReplay && !_sim.Finished ) _sim.MarkFinished();

		// All moves played and the last rotation settled — destroy the board. A cleared
		// game plays the cube outro first (wait for Idle); an abandoned run just lingers.
		if ( !_replayDone ) { _replayDone = true; _replayDoneSince = 0; }
		if ( _sim.Finished ? _view.Idle : _replayDoneSince >= ReplayLinger )
			EndReplay();
	}

	void ApplyReplayMove( ReplayMove m )
	{
		var sel = BuildSelectors( m.PlayerNumber, m.Row, m.Col );
		if ( m.Selector )
		{
			_sim.ApplySync( null, -1, sel );
		}
		else
		{
			_replayBoard.Rotate( m.Row, m.Col, m.Direction ); // rotate + resolve locally
			int encoded = m.Direction * 81 + m.Row * 9 + m.Col;
			_sim.ApplySync( _replayBoard.CloneCells(), encoded, sel );
		}
	}

	void EndReplay()
	{
		_replayGen++;          // cancel any in-flight fetch
		_replayActive = false;
		_replayDone = false;
		_moves = null;
		_moveIdx = 0;
		_replayBoard = null;
		Teardown();
	}

	void Teardown()
	{
		if ( _viewGo.IsValid() ) _viewGo.Destroy();
		_viewGo = null;
		_boardLightGo = null; // child of _viewGo — destroyed with it
		_view = null;
		_sim = null;
		_featured = null;
	}

	static int[] ParseGrid( string grid )
	{
		if ( grid == null || grid.Length != GameBoard.CellCount ) return null;
		var cells = new int[grid.Length];
		for ( int i = 0; i < grid.Length; i++ )
			cells[i] = grid[i] - '0';
		return cells;
	}

	protected override void OnDestroy() => Teardown();
}
