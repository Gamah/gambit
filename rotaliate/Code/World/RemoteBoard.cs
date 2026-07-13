using System;
using System.Collections.Generic;
using Rotaliate.Api;
using Rotaliate.Audio;
using Rotaliate.Game;
using Sandbox;

namespace Rotaliate.World;

/// <summary>
/// Local view of another player's board (solo or multiplayer), fed authoritative
/// grids relayed through ArcadeStation. Mode-agnostic: it never resolves groups
/// itself — the occupant always relays the resolved grid it sees, so the spectator
/// can't diverge (critical for MP, whose board is server-resolved). Rotations are
/// animated from the relayed move purely for looks, then the grid snaps to the
/// authoritative state. Drives a CubeBoardView in remote mode; sounds play
/// positionally at the station (suppressed via Silent for the big wall board).
/// </summary>
public sealed class RemoteBoard
{
	public GameBoard Board { get; }
	public RotateAnimRequest PendingAnim { get; private set; }
	public bool Animating { get; private set; }

	/// <summary>All selectors to draw. Color 0 = a plain white "mine" ring (solo);
	/// nonzero colors are MP players' rings, drawn in their palette color. The first
	/// entry is the occupant's own selector (drives the cabinet joystick).</summary>
	public List<MpSelectorInfo> Selectors { get; private set; } = new();

	/// <summary>The occupant's own selector top-left, for the cabinet joystick anim.</summary>
	public int SelectorRow { get; private set; }
	public int SelectorCol { get; private set; }

	/// <summary>Board fully cleared — the view plays the completion outro.</summary>
	public bool Finished { get; private set; }

	/// <summary>Force the completion outro without a full-board clear. MP games end on a
	/// win with the losers' colors still on the board, so the replay driver flags this
	/// once the recorded moves run out — the view then explodes whatever cubes remain,
	/// matching a live match.</summary>
	public void MarkFinished() => Finished = true;
	/// <summary>Occupant backed out / left — the view retracts quietly.</summary>
	public bool Cleared { get; set; }

	/// <summary>Suppress the positional SFX. The giant spectator board mirrors a
	/// cabinet that already plays its own move sounds, so the duplicate wall copy
	/// stays silent.</summary>
	public bool Silent { get; set; }

	/// <summary>No "own" selector — every entry in <see cref="Selectors"/> is a peer
	/// drawn in its own color, keyed by color so it stays stable across updates. Set
	/// for replays, which have no local occupant: otherwise the renderer treats entry 0
	/// as the white "mine" ring and it jumps between players as the mover changes.</summary>
	public bool NoOwnSelector { get; set; }

	public float AnimProgress => Animating ? Math.Clamp( (float)_animStart / AnimDuration, 0f, 1f ) : 0f;

	const float AnimDuration = 0.14f; // match GameController / MultiplayerController

	/// <summary>Resolves a 2×2 selector top-left (row,col) to its world position. Set by
	/// the rendering CubeBoardView so move SFX emanate from the acting selector rather than
	/// a fixed board point — a little spatial nuance. Null (no view / geometry not ready
	/// yet) falls back to <see cref="_soundPos"/>.</summary>
	public Func<int, int, Vector3> SelectorWorldPos { get; set; }

	readonly Vector3 _soundPos;
	int _lastMoveRow = 4, _lastMoveCol = 4; // block of the most recent rotation, for the pop locus
	int[] _pendingGrid; // authoritative grid to snap to once the current rotation anim finishes
	TimeSince _animStart;

	// Sound origin for a selector at (row,col): the acting selector if the view wired a
	// resolver, else the fixed board point.
	Vector3 SoundAt( int row, int col ) => SelectorWorldPos?.Invoke( row, col ) ?? _soundPos;

	public RemoteBoard( int[] cells, Vector3 soundPos, List<MpSelectorInfo> selectors = null )
	{
		Board = new GameBoard( cells );
		_soundPos = soundPos;
		SetSelectors( selectors, silent: true );
	}

	/// <summary>Apply one relayed update. <paramref name="grid"/> is the authoritative
	/// resolved grid (null = no grid in this message). <paramref name="move"/> is an
	/// encoded rotation (0–161) to animate, or -1 for none. Selectors always refreshed.</summary>
	public void ApplySync( int[] grid, int move, List<MpSelectorInfo> selectors )
	{
		SetSelectors( selectors, silent: false );

		if ( move >= 0 && move < 162 )
		{
			// Rotation announced: animate it from the currently shown grid, then snap
			// to the authoritative grid (now if present, else held until it arrives).
			if ( Animating ) FinishAnim();
			int dir = move / 81, pos = move % 81;
			_lastMoveRow = pos / 9; _lastMoveCol = pos % 9;
			PendingAnim = new RotateAnimRequest( _lastMoveRow, _lastMoveCol, dir, Board.CloneCells() );
			Animating   = true;
			_animStart  = 0;
			_pendingGrid = grid;
			if ( !Silent ) SoundPlayer.PlayWooshAt( SoundAt( _lastMoveRow, _lastMoveCol ) );
			return;
		}

		// Grid-only correction / selector update.
		if ( grid == null ) return;
		if ( Animating )
			_pendingGrid = grid;
		else
			SetGrid( grid );
	}

	public void Update()
	{
		if ( Animating && _animStart > AnimDuration )
			FinishAnim();
	}

	void SetSelectors( List<MpSelectorInfo> selectors, bool silent )
	{
		if ( selectors == null ) return;
		int oldRow = SelectorRow, oldCol = SelectorCol;
		Selectors = selectors;
		if ( selectors.Count > 0 )
		{
			SelectorRow = selectors[0].Row;
			SelectorCol = selectors[0].Col;
		}
		if ( !silent && !Silent && (SelectorRow != oldRow || SelectorCol != oldCol) && !Animating )
			SoundPlayer.PlayTickAt( SoundAt( SelectorRow, SelectorCol ) );
	}

	void SetGrid( int[] grid )
	{
		// A resolution only ever increases the solved (0) count; a bare rotation keeps
		// it the same. So more zeros than before = a group resolved → pop.
		int oldZeros = 0, newZeros = 0;
		for ( int i = 0; i < Board.Cells.Length; i++ ) if ( Board.Cells[i] == 0 ) oldZeros++;
		for ( int i = 0; i < grid.Length; i++ ) if ( grid[i] == 0 ) newZeros++;

		Board.SetCells( grid );
		// Resolution follows the rotation that caused it — play the pop from that block.
		if ( newZeros > oldZeros && !Silent ) SoundPlayer.PlayPopAt( SoundAt( _lastMoveRow, _lastMoveCol ) );
		if ( Board.IsFullyCleared() ) Finished = true;
	}

	void FinishAnim()
	{
		if ( PendingAnim == null ) { Animating = false; return; }

		var req = PendingAnim;
		PendingAnim = null;
		Animating   = false;

		if ( _pendingGrid != null )
		{
			var g = _pendingGrid;
			_pendingGrid = null;
			SetGrid( g );
		}
		else
		{
			// Authoritative grid hasn't arrived yet — show the rotated (unresolved)
			// state so cubes don't snap back; the next grid sync corrects it.
			Board.ApplyRotation( req.Row, req.Col, req.Dir );
		}
	}
}
