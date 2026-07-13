using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Gambit.Api;
using Gambit.Audio;
using Sandbox;

namespace Gambit.Game;

public enum MpState { Idle, Connecting, Lobby, WaitingRoom, Playing, GameOver }

public sealed class MultiplayerController : Component
{
	public static MultiplayerController Instance { get; private set; }

	// ── Public state ──
	public MpState State { get; private set; } = MpState.Idle;
	public bool Is2P { get; private set; }

	// Lobby
	public string LobbyCode   { get; private set; }
	public bool   LobbyPublic { get; private set; }
	public int    LobbyCount  { get; private set; }
	public int    LobbyMax    { get; private set; }
	public List<LobbyPlayer> LobbyPlayers { get; private set; } = new();

	// Waiting room (post-match, pre-ready)
	public List<MpPlayerInfo> RoomPlayers { get; private set; } = new();
	public HashSet<string>    ReadyIds    { get; private set; } = new();
	public bool               SentReady   { get; private set; }
	/// <summary>Own public player_tag — server payloads identify players by tag,
	/// never GUID, so all self-identification compares against this.</summary>
	public string             MyTag       { get; private set; }

	// Room / game
	public int    MyColor  { get; private set; }
	public int[]  MyColors { get; private set; }
	public string RoomId   { get; private set; }

	public int[]             Grid      { get; private set; } = new int[GameBoard.CellCount];
	public List<MpSelectorInfo> Selectors { get; private set; } = new();
	public int               MoveCount { get; private set; }

	public int SelectorRow { get; private set; } = 4;
	public int SelectorCol { get; private set; } = 4;

	// Game over
	public string WinnerTag   { get; private set; }
	public int    WinnerColor { get; private set; }
	public long   DurationMs  { get; private set; }

	public string Error { get; private set; }

	// Animation (mirrors GameController pattern — cosmetic only, board is server-authoritative)
	public RotateAnimRequest PendingAnim  { get; private set; }
	public bool              Animating    { get; private set; }
	public float AnimProgress => Animating ? Math.Clamp( (float)_animStart / AnimDuration, 0f, 1f ) : 0f;

	public TimeSince GameElapsed { get; private set; }

	public event Action OnStateChanged;

	// ── Private ──
	WebSocket _socket;
	bool      _socketConnected;
	TimeSince _pingTimer;
	TimeSince _animStart;
	TimeSince _lastMoveSend;
	TimeSince _lastSelectorSend;
	int       _lastSentSelector = -1; // last encoded selector move sent (162–242)
	int[]     _pendingGrid;

	const float AnimDuration      = 0.14f;
	const float PingInterval      = 30f;
	const float MinMoveInterval   = 0.06f;
	const float SelectorThrottle  = 0.05f;

	static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

	protected override void OnAwake()  => Instance = this;
	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;
		CloseSocket();
	}

	// ── Connection ──

	public async Task Connect( bool is2p )
	{
		Is2P        = is2p;
		State       = MpState.Connecting;
		Error       = null;
		LobbyCode   = null;
		LobbyPublic = false;
		LobbyCount  = 0;
		LobbyMax    = is2p ? 2 : 4;
		LobbyPlayers = new List<LobbyPlayer>();
		RoomPlayers  = new List<MpPlayerInfo>();
		ReadyIds     = new HashSet<string>();
		SentReady    = false;
		OnStateChanged?.Invoke();

		var player = PlayerData.Load();
		if ( player?.Guid == null )
		{
			Error = "No player ID — complete setup first.";
			State = MpState.Idle;
			OnStateChanged?.Invoke();
			return;
		}

		// Tag backfill for identities saved before the player_tag migration
		if ( string.IsNullOrEmpty( player.PlayerTag ) )
		{
			try
			{
				var info = await ApiClient.GetPlayer( player.Guid );
				if ( !string.IsNullOrEmpty( info?.PlayerTag ) )
				{
					player.PlayerTag = info.PlayerTag;
					player.Save();
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"[Gambit/MP] player_tag fetch failed: {e.Message}" );
			}
		}
		MyTag = player.PlayerTag;

		CloseSocket();

		_socket = new WebSocket();
		_socket.OnMessageReceived += OnWsMessage;
		_socket.OnDisconnected    += ( _, _ ) => OnWsDone();

		var wsBase   = ApiClient.BaseUrl.Replace( "https://", "wss://" ).Replace( "http://", "ws://" );
		var endpoint = is2p ? "/ws/matchmaking2" : "/ws/matchmaking";

		// The GUID is the player's secret credential, so it must stay off the WS URL
		// (query strings leak into proxy/access logs, history and referrers — security
		// review C3). A WS upgrade can't carry a custom header, so we instead prove the
		// GUID once over HTTP (X-Player-ID) to mint a single-use, short-TTL ticket, then
		// connect with only ?ticket=. A failed mint fails the connect cleanly — we never
		// fall back to ?player_id=<guid>, which would defeat C3.
		WsTicketResponse ticket;
		try
		{
			ticket = await ApiClient.GetWsTicket();
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit/MP] ws ticket mint failed: {e.Message}" );
			Error = "Could not connect to server.";
			State = MpState.Idle;
			CloseSocket();
			OnStateChanged?.Invoke();
			return;
		}

		var url = $"{wsBase}{endpoint}?ticket={Uri.EscapeDataString( ticket.Ticket )}";

		try
		{
			await _socket.Connect( url );
			_socketConnected = true;
			_pingTimer = 0;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit/MP] connect failed: {e.Message}" );
			Error = "Could not connect to server.";
			State = MpState.Idle;
			OnStateChanged?.Invoke();
		}
	}

	public void Disconnect()
	{
		CloseSocket();
		State        = MpState.Idle;
		Animating    = false;
		PendingAnim  = null;
		_pendingGrid = null;
		LobbyCode    = null;
		RoomId       = null;
		Error        = null;
		OnStateChanged?.Invoke();
	}

	void CloseSocket()
	{
		if ( _socket != null )
		{
			_socket.Dispose();
			_socket = null;
		}
		_socketConnected = false;
	}

	// ── Send helpers ──

	void Send( string msg )
	{
		if ( _socketConnected && _socket != null )
			_ = _socket.Send( msg );
	}

	// Server requires the {public} payload: true lists the lobby in the public
	// match browser (GET /api/v1/lobbies/open), false = private code-only.
	public void CreateLobby( bool isPublic ) =>
		Send( $"{{\"type\":\"create_lobby\",\"payload\":{{\"public\":{( isPublic ? "true" : "false" )}}}}}" );

	public void JoinLobby( string code )
	{
		var trimmed = code.Trim().ToUpper();
		Send( $"{{\"type\":\"join_lobby\",\"payload\":{{\"code\":\"{trimmed}\"}}}}" );
	}

	public void SendReady()
	{
		if ( SentReady ) return;
		Send( "{\"type\":\"ready\",\"payload\":{}}" );
		SentReady = true;
		OnStateChanged?.Invoke();
	}

	public void RequestRotate( int dir )
	{
		if ( State != MpState.Playing ) return;
		if ( _lastMoveSend < MinMoveInterval ) return;
		// Don't block on the in-flight anim — snap it to done (applies any
		// pending synced grid) and rotate from the settled state
		if ( Animating ) FinishAnim();

		var preCells = new int[Grid.Length];
		for ( int i = 0; i < Grid.Length; i++ ) preCells[i] = Grid[i];
		var req = new RotateAnimRequest( SelectorRow, SelectorCol, dir, preCells );

		int encoded = dir * 81 + SelectorRow * 9 + SelectorCol;
		Send( $"{{\"type\":\"move\",\"payload\":{{\"move\":{encoded}}}}}" );
		_lastMoveSend = 0;
		// A rotation moves the server-side selector to its cell too.
		_lastSentSelector = 162 + SelectorRow * 9 + SelectorCol;

		PendingAnim = req;
		Animating   = true;
		_animStart  = 0;
		_pendingGrid = null;
		SoundPlayer.PlayWoosh();
	}

	// ── Message handling ──

	void OnWsMessage( string raw )
	{
		try
		{
			var env = JsonSerializer.Deserialize<WsEnvelope>( raw, JsonOpts );
			if ( env != null ) HandleMessage( env );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit/MP] parse error: {e.Message}" );
		}
	}

	void OnWsDone()
	{
		_socketConnected = false;
		if ( State != MpState.GameOver && State != MpState.Idle )
		{
			Error = "Disconnected from server.";
			State = MpState.Idle;
			OnStateChanged?.Invoke();
		}
	}

	void HandleMessage( WsEnvelope env )
	{
		var raw = env.Payload.GetRawText();
		switch ( env.Type )
		{
			case "lobby_prompt":
			{
				var p = JsonSerializer.Deserialize<LobbyPromptPayload>( raw, JsonOpts );
				LobbyMax     = p?.Max ?? (Is2P ? 2 : 4);
				LobbyCount   = 0;
				LobbyCode    = null;
				LobbyPlayers = new List<LobbyPlayer>();
				State        = MpState.Lobby;
				OnStateChanged?.Invoke();
				break;
			}
			case "lobby_created":
			{
				var p    = JsonSerializer.Deserialize<LobbyCreatedPayload>( raw, JsonOpts );
				LobbyCode    = p?.Code;
				LobbyPublic  = p?.Public ?? false;
				LobbyCount   = p?.Count ?? 1;
				LobbyMax     = p?.Max ?? LobbyMax;
				LobbyPlayers = p?.Players ?? new List<LobbyPlayer>();
				State        = MpState.Lobby;
				OnStateChanged?.Invoke();
				break;
			}
			case "lobby_update":
			{
				var p = JsonSerializer.Deserialize<LobbyUpdatePayload>( raw, JsonOpts );
				if ( p != null )
				{
					LobbyCode    = p.Code;
					LobbyCount   = p.Count;
					LobbyMax     = p.Max;
					LobbyPlayers = p.Players ?? new List<LobbyPlayer>();
				}
				OnStateChanged?.Invoke();
				break;
			}
			case "room_ready":
			{
				var p = JsonSerializer.Deserialize<RoomReadyPayload>( raw, JsonOpts );
				if ( p != null )
				{
					RoomId    = p.RoomId;
					MyColor   = p.Color;
					MyColors  = p.Colors != null ? p.Colors : new[] { p.Color };
					Grid      = p.FlatGrid();
					Selectors = new List<MpSelectorInfo>();
					MoveCount = 0;
					RoomPlayers = p.Players ?? new List<MpPlayerInfo>();
					ReadyIds    = new HashSet<string>();
					SentReady   = false;
				}
				State = MpState.WaitingRoom;
				OnStateChanged?.Invoke();
				break;
			}
			case "player_ready":
			{
				var p = JsonSerializer.Deserialize<PlayerReadyPayload>( raw, JsonOpts );
				if ( p?.PlayerTag != null )
					ReadyIds.Add( p.PlayerTag );
				OnStateChanged?.Invoke();
				break;
			}
			case "game_start":
			{
				State       = MpState.Playing;
				SelectorRow = 4;
				SelectorCol = 4;
				_lastSentSelector = -1;
				GameElapsed = 0;
				PlayerStats.Increment( PlayerStats.MpMatches ); // drives goldnova (≥1)
				OnStateChanged?.Invoke();
				break;
			}
			case "state_sync":
			{
				var p = JsonSerializer.Deserialize<StateSyncPayload>( raw, JsonOpts );
				if ( p != null )
				{
					var newGrid = p.FlatGrid();
					MoveCount = p.MoveCount;
					Selectors = p.Selectors ?? new List<MpSelectorInfo>();

					if ( Animating )
					{
						_pendingGrid = newGrid;
					}
					else if ( p.LastMove != null && !IsMyColor( p.LastMove.Color ) )
					{
						// Animate an opponent's rotation: keep the pre-move grid on
						// screen, slide the rotated 2×2 along its arc, then apply.
						var preCells = new int[Grid.Length];
						for ( int i = 0; i < Grid.Length; i++ ) preCells[i] = Grid[i];
						int dir = p.LastMove.Move / 81, pos = p.LastMove.Move % 81;
						PendingAnim  = new RotateAnimRequest( pos / 9, pos % 9, dir, preCells, Mine: false );
						Animating    = true;
						_animStart   = 0;
						_pendingGrid = newGrid;
						SoundPlayer.PlayWoosh();
					}
					else
					{
						Grid = newGrid;
					}
				}
				OnStateChanged?.Invoke();
				break;
			}
			case "game_over":
			{
				var p = JsonSerializer.Deserialize<MpGameOverPayload>( raw, JsonOpts );
				if ( p != null )
				{
					WinnerTag   = p.WinnerTag;
					WinnerColor = p.WinnerColor;
					DurationMs  = p.DurationMs;

					// "mp_wins" stat drives the globalelite achievement (≥1).
					if ( !string.IsNullOrEmpty( MyTag ) && p.WinnerTag == MyTag )
						PlayerStats.Increment( PlayerStats.MpWins );
				}
				Animating   = false;
				PendingAnim = null;
				State       = MpState.GameOver;
				OnStateChanged?.Invoke();
				break;
			}
			case "selector_sync":
			{
				var p = JsonSerializer.Deserialize<MpSelectorInfo>( raw, JsonOpts );
				if ( p != null )
				{
					var idx = Selectors.FindIndex( s => s.Color == p.Color );
					bool moved = idx < 0 || Selectors[idx].Row != p.Row || Selectors[idx].Col != p.Col;
					if ( idx >= 0 )
						Selectors[idx] = p;
					else
						Selectors.Add( p );
					if ( moved ) SoundPlayer.PlayTock();
					OnStateChanged?.Invoke();
				}
				break;
			}
			case "player_left":
				OnStateChanged?.Invoke();
				break;
			case "error":
			{
				if ( env.Payload.TryGetProperty( "error", out var errEl ) )
					Error = errEl.GetString() ?? "An error occurred.";
				else
					Error = "An error occurred.";
				// Stay in Lobby so user can retry (e.g. bad lobby code); escalate to Idle for anything else
				if ( State != MpState.Lobby )
					State = MpState.Idle;
				OnStateChanged?.Invoke();
				break;
			}
		}
	}

	// ── Per-frame update ──

	protected override void OnUpdate()
	{
		if ( _socketConnected && _pingTimer > PingInterval )
		{
			Send( "{\"type\":\"ping\",\"payload\":{}}" );
			_pingTimer = 0;
		}

		if ( State != MpState.Playing ) return;

		if ( GameController.InputActive )
			HandleInput();

		if ( Animating && _animStart > AnimDuration )
			FinishAnim();

		// Selector repositions go through the same encoded move stream as solo
		// (162 + row*9 + col) — sent only when the position actually changed.
		int selEncoded = 162 + SelectorRow * 9 + SelectorCol;
		if ( selEncoded != _lastSentSelector && _lastSelectorSend > SelectorThrottle )
		{
			Send( $"{{\"type\":\"move\",\"payload\":{{\"move\":{selEncoded}}}}}" );
			_lastSentSelector = selEncoded;
			_lastSelectorSend = 0;
		}
	}

	void FinishAnim()
	{
		PendingAnim = null;
		Animating   = false;
		if ( _pendingGrid != null )
		{
			// A rotation just shuffles cells around, so the number of solved (0) cells is
			// unchanged — comparing per-index would false-positive whenever a rotation moves
			// an already-black cell. Only resolution increases the zero count.
			int oldZeros = 0, newZeros = 0;
			for ( int i = 0; i < Grid.Length; i++ )
				if ( Grid[i] == 0 ) oldZeros++;
			for ( int i = 0; i < _pendingGrid.Length; i++ )
				if ( _pendingGrid[i] == 0 ) newZeros++;

			Grid         = _pendingGrid;
			_pendingGrid = null;
			if ( newZeros > oldZeros ) SoundPlayer.PlayPop();
		}
		OnStateChanged?.Invoke();
	}

	void HandleInput()
	{
		if ( Input.Pressed( "MoveUp" )    || GamepadBinds.Pressed( "MoveUp" ) )    MoveSel( -1,  0 );
		if ( Input.Pressed( "MoveDown" )  || GamepadBinds.Pressed( "MoveDown" ) )  MoveSel(  1,  0 );
		if ( Input.Pressed( "MoveLeft" )  || GamepadBinds.Pressed( "MoveLeft" ) )  MoveSel(  0, -1 );
		if ( Input.Pressed( "MoveRight" ) || GamepadBinds.Pressed( "MoveRight" ) ) MoveSel(  0,  1 );

		if ( Input.Pressed( "RotateCCW" ) || GamepadBinds.Pressed( "RotateCCW" ) ) RequestRotate( 1 );
		if ( Input.Pressed( "RotateCW"  ) || GamepadBinds.Pressed( "RotateCW" ) )  RequestRotate( 0 );
	}

	bool IsMyColor( int color )
	{
		if ( MyColors == null ) return color == MyColor;
		foreach ( var c in MyColors )
			if ( c == color ) return true;
		return false;
	}

	void MoveSel( int dr, int dc )
	{
		int nr = Math.Clamp( SelectorRow + dr, 0, GameBoard.Size - 2 );
		int nc = Math.Clamp( SelectorCol + dc, 0, GameBoard.Size - 2 );
		if ( nr == SelectorRow && nc == SelectorCol ) return;
		SelectorRow = nr;
		SelectorCol = nc;
		SoundPlayer.PlayTick();
	}

	public void ReturnToMenu()
	{
		Disconnect();
		GameEvents.FireReturnToMenu();
	}
}
