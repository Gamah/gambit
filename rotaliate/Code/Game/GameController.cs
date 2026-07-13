using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rotaliate.Api;
using Rotaliate.Audio;
using Sandbox;

namespace Rotaliate.Game;

public enum GameMode { Daily, Hourly, Freeplay }

public enum GameState { Idle, Loading, Playing, Complete }

public static class GameEvents
{
	public static event Action OnGameComplete;
	public static event Action OnReturnToMenu;
	internal static void FireComplete() => OnGameComplete?.Invoke();
	internal static void FireReturnToMenu() => OnReturnToMenu?.Invoke();
}

/// <summary>
/// Queued rotation request, stores pre-rotation colors for animation.
/// Mine is false only for opponent rotations animated from MP state_sync —
/// cabinet control animations key off it.
/// </summary>
public record RotateAnimRequest( int Row, int Col, int Dir, int[] PreCells, bool Mine = true )
{
	public RotateAnimRequest() : this( 0, 0, 0, new int[0] ) { }
}

public sealed class GameController : Component
{
	public static GameController Instance { get; private set; }

	/// <summary>
	/// Gates keyboard game input. False while the player is roaming the 3D lobby;
	/// set by ArcadeStation when the player locks into a screen.
	/// </summary>
	public static bool InputActive { get; set; }

	/// <summary>Every board event (rotation or selector reposition, encoded
	/// 0–242) as it happens, including replay playback — fired even when several
	/// land in one frame, so listeners (the ArcadeStation spectator relay) never
	/// miss one. Rotations fire at anim start, before the move applies.</summary>
	public static event Action<int> OnBoardEvent;

	public GameBoard Board { get; private set; }
	public GameMode Mode { get; private set; }
	public GameState State { get; private set; } = GameState.Idle;

	public int SelectorRow { get; private set; } = 4;
	public int SelectorCol { get; private set; } = 4;

	public bool[] FlashCells { get; private set; } = new bool[GameBoard.CellCount];
	[Hide] public RotateAnimRequest PendingAnim { get; private set; }
	public bool Animating { get; private set; }

	public TimeSince GameTime { get; private set; }
	public int MoveCount { get; private set; }
	public float CompletedTime { get; private set; }
	public int CompletedMoves { get; private set; }

	public string PuzzleId { get; private set; }
	public string PuzzleSeed { get; private set; }
	public string SessionId { get; private set; }


	// Serial move stream to the server session: each send awaits the previous one,
	// spaced >= MinSendInterval apart (server rate limit: 1/30ms sustained, burst 10).
	const float MinSendInterval = 0.06f;
	Task _sendChain;
	float _lastSendAt;
	bool _sessionLost;
	bool _timerStarted;

	const float AnimDuration = 0.14f;
	const float FlashDuration = 0.35f;
	TimeSince _animStart;
	TimeSince _flashStart;
	bool _flashActive;

	protected override void OnAwake()
	{
		Instance = this;
	}

	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;
	}

	public async Task StartGame( GameMode mode, string seed = null )
	{
		Mode = mode;
		State = GameState.Loading;
		MoveCount = 0;

		PuzzleResponse puzzle = mode switch
		{
			GameMode.Daily   => await ApiClient.GetDailyPuzzle(),
			GameMode.Hourly  => await ApiClient.GetHourlyPuzzle(),
			GameMode.Freeplay => await ApiClient.GetFreeplay( string.IsNullOrWhiteSpace( seed ) ? "0" : seed.Trim() ),
			_ => throw new ArgumentOutOfRangeException(),
		};

		Board = new GameBoard( puzzle.FlatGrid() );
		PuzzleId = puzzle.PuzzleId;
		PuzzleSeed = puzzle.Seed;
		SessionId = puzzle.SessionId;
		_sendChain = null;
		_sessionLost = false;
		_timerStarted = false;
		_serverSolveMs = null;
		_lastSendAt = float.MinValue;
		if ( string.IsNullOrEmpty( SessionId ) )
			Log.Warning( "[Rotaliate] No session_id from server — moves won't be recorded." );

		SelectorRow = 4;
		SelectorCol = 4;
		Array.Clear( FlashCells, 0, FlashCells.Length );
		PendingAnim = null;
		Animating = false;

		State = GameState.Playing;
		GameTime = 0;
		PlayerStats.Increment( PlayerStats.SpMatches ); // singleplayer game started
	}

	public void RequestRotate( int dir )
	{
		if ( State != GameState.Playing ) return;

		// Fast play never falls behind: snap the in-flight rotation to done and
		// start the new one immediately (same as the replay path)
		if ( Animating ) FinishAnim();
		if ( State != GameState.Playing ) return; // finishing it may have solved the board

		var req = new RotateAnimRequest( SelectorRow, SelectorCol, dir, Board.CloneCells() );
		BeginAnim( req );
		RecordMove( req.Dir * 81 + req.Row * 9 + req.Col );
	}

	void BeginAnim( RotateAnimRequest req )
	{
		PendingAnim = req;
		Animating = true;
		_animStart = 0;
		SoundPlayer.PlayWoosh();
		OnBoardEvent?.Invoke( req.Dir * 81 + req.Row * 9 + req.Col );
	}

	void FinishAnim()
	{
		if ( PendingAnim == null ) return;

		var req = PendingAnim;
		Board.ApplyRotation( req.Row, req.Col, req.Dir );
		var resolved = Board.ResolveGroups();

		bool anyResolved = false;
		int resolvedCells = 0;
		for ( int i = 0; i < resolved.Length; i++ )
		{
			FlashCells[i] = resolved[i];
			if ( resolved[i] ) { anyResolved = true; resolvedCells++; }
		}

		if ( anyResolved )
		{
			_flashActive = true;
			_flashStart = 0;
			SoundPlayer.PlayPop();

			// "matches" stat = 2×2 groups cleared (4 cells each); drives the showedup
			// achievement (matches ≥ 1).
			PlayerStats.Increment( PlayerStats.Matches, resolvedCells / 4 );
		}

		PendingAnim = null;
		Animating = false;

		if ( Board.IsFullyCleared() )
		{
			// Local fallback; the server's duration_ms from the solving move overwrites it.
			if ( _serverSolveMs.HasValue )
				CompletedTime = _serverSolveMs.Value / 1000f;
			else
				CompletedTime = GameTime;
			CompletedMoves = MoveCount;
			State = GameState.Complete;

			// "solves" = any SP board completed (drives extracredit ≥ 1). For daily/
			// hourly also bump the distinct-board stats, gated by the seed cache so the
			// same board doesn't recount (goingsteady / dedicated).
			PlayerStats.Increment( PlayerStats.Solves );
			if ( Mode == GameMode.Daily || Mode == GameMode.Hourly )
			{
				var key = $"{Mode}:{PuzzleSeed}";
				if ( PlayerData.Load()?.MarkBoardCounted( key ) == true )
					PlayerStats.Increment( Mode == GameMode.Daily ? PlayerStats.DailySolves : PlayerStats.HourlySolves );
			}

			GameEvents.FireComplete();
			return;
		}

	}

	protected override void OnUpdate()
	{
		if ( State != GameState.Playing ) return;

		if ( InputActive )
			HandleInput();

		if ( Animating && _animStart > AnimDuration )
			FinishAnim();

		if ( _flashActive && _flashStart > FlashDuration )
		{
			Array.Clear( FlashCells, 0, FlashCells.Length );
			_flashActive = false;
		}
	}

	void HandleInput()
	{
		if ( IsActionPressed( "MoveUp" ) )    MoveSel( -1, 0 );
		if ( IsActionPressed( "MoveDown" ) )  MoveSel(  1, 0 );
		if ( IsActionPressed( "MoveLeft" ) )  MoveSel( 0, -1 );
		if ( IsActionPressed( "MoveRight" ) ) MoveSel( 0,  1 );

		if ( IsActionPressed( "RotateCCW" ) ) RequestRotate( 1 );
		if ( IsActionPressed( "RotateCW" ) )  RequestRotate( 0 );
	}

	static bool IsActionPressed( string action )
	{
		// Controller: the button currently mapped to this action (remappable).
		if ( GamepadBinds.Pressed( action ) ) return true;
		// Keyboard: player override, else the action's default key.
		var bindings = PlayerData.Load()?.Bindings;
		if ( bindings != null && bindings.TryGetValue( action, out var key ) && !string.IsNullOrEmpty( key ) )
			return Input.Keyboard.Pressed( key );
		return Input.Pressed( action );
	}

	void MoveSel( int dr, int dc )
	{
		int nr = Math.Clamp( SelectorRow + dr, 0, GameBoard.Size - 2 );
		int nc = Math.Clamp( SelectorCol + dc, 0, GameBoard.Size - 2 );
		if ( nr == SelectorRow && nc == SelectorCol ) return;
		SelectorRow = nr;
		SelectorCol = nc;
		SoundPlayer.PlayTick();

		// Selector repositions are moves: counted, timed, and streamed like rotations.
		RecordMove( 162 + nr * 9 + nc );
		OnBoardEvent?.Invoke( 162 + nr * 9 + nc );
	}

	public float AnimProgress => Animating ? Math.Clamp( (float)_animStart / AnimDuration, 0f, 1f ) : 0f;

	/// <summary>Timer shown in the HUD: 0 until the first move of any kind.</summary>
	public float DisplayTime => State == GameState.Complete ? CompletedTime
		: _timerStarted ? (float)GameTime : 0f;

	long? _serverSolveMs;

	void RecordMove( int encoded )
	{
		if ( !_timerStarted )
		{
			_timerStarted = true;
			GameTime = 0;
		}

		MoveCount++;

		if ( string.IsNullOrEmpty( SessionId ) || _sessionLost ) return;
		_sendChain = SendMoveChained( _sendChain, SessionId, encoded );
	}

	async Task SendMoveChained( Task prev, string session, int encoded )
	{
		if ( prev != null )
		{
			try { await prev; } catch { }
		}

		if ( _sessionLost || session != SessionId ) return;

		var wait = MinSendInterval - (RealTime.Now - _lastSendAt);
		if ( wait > 0f ) await Task.DelayRealtimeSeconds( wait );
		_lastSendAt = RealTime.Now;

		try
		{
			var resp = await ApiClient.SendMove( session, encoded );
			if ( resp.Solved && session == SessionId )
			{
				_serverSolveMs = resp.DurationMs;
				CompletedTime = resp.DurationMs / 1000f;
			}
		}
		catch ( MoveRejectedException e )
		{
			_sessionLost = true;
			Log.Warning( $"[Rotaliate] Move rejected (HTTP {e.StatusCode}) — server session out of sync; this run won't be recorded." );
		}
		catch ( Exception e )
		{
			_sessionLost = true;
			Log.Warning( $"[Rotaliate] Move send failed ({e.Message}) — server session out of sync; this run won't be recorded." );
		}
	}

	public void ReturnToMenu()
	{
		State = GameState.Idle;
		Board = null;
		GameEvents.FireReturnToMenu();
	}
}
