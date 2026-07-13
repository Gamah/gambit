using System;

namespace Rotaliate.Game;

/// <summary>
/// Pure game logic — no s&box dependencies. Matches the Go server's internal/game package exactly.
/// Grid is row-major: index = row * 10 + col. Colors: 0=solved, 1=Red, 2=Blue, 3=Green, 4=Yellow.
/// </summary>
public sealed class GameBoard
{
	public const int Size = 10;
	public const int CellCount = Size * Size;

	public int[] Cells { get; private set; }

	public GameBoard()
	{
		Cells = new int[CellCount];
	}

	public GameBoard( int[] cells )
	{
		if ( cells.Length != CellCount )
			throw new ArgumentException( $"Expected {CellCount} cells, got {cells.Length}" );
		Cells = new int[CellCount];
		for ( int i = 0; i < CellCount; i++ ) Cells[i] = cells[i];
	}

	/// <summary>Overwrite the whole grid in place (no resolution). Used by spectator
	/// views that are fed authoritative grids over the network and never resolve locally.</summary>
	public void SetCells( int[] cells )
	{
		if ( cells.Length != CellCount )
			throw new ArgumentException( $"Expected {CellCount} cells, got {cells.Length}" );
		for ( int i = 0; i < CellCount; i++ ) Cells[i] = cells[i];
	}

	public int Get( int row, int col ) => Cells[row * Size + col];
	public void Set( int row, int col, int value ) => Cells[row * Size + col] = value;

	/// <summary>
	/// Returns the four cell values for the 2×2 block whose top-left is (row, col).
	/// Order: TL, TR, BL, BR.
	/// </summary>
	public (int tl, int tr, int bl, int br) GetBlock( int row, int col ) =>
		(Get( row, col ), Get( row, col + 1 ), Get( row + 1, col ), Get( row + 1, col + 1 ));

	/// <summary>
	/// Applies a rotation in-place and then resolves all groups.
	/// dir=0 → CW, dir=1 → CCW. Returns cells that were resolved (for flash animation).
	/// </summary>
	public bool[] Rotate( int row, int col, int dir )
	{
		ApplyRotation( row, col, dir );
		return ResolveGroups();
	}

	/// <summary>
	/// Applies only the rotation, without resolution. Used during animation preview.
	/// </summary>
	public void ApplyRotation( int row, int col, int dir )
	{
		var (tl, tr, bl, br) = GetBlock( row, col );

		if ( dir == 0 ) // CW: newTL=BL, newTR=TL, newBR=TR, newBL=BR
		{
			Set( row, col,         bl );
			Set( row, col + 1,     tl );
			Set( row + 1, col + 1, tr );
			Set( row + 1, col,     br );
		}
		else // CCW: newTL=TR, newTR=BR, newBR=BL, newBL=TL
		{
			Set( row, col,         tr );
			Set( row, col + 1,     br );
			Set( row + 1, col + 1, bl );
			Set( row + 1, col,     tl );
		}
	}

	/// <summary>
	/// Repeatedly scans all 2×2 blocks; sets any solid same-color non-zero group to 0.
	/// Returns a bool[] marking cells that changed (for flash).
	/// </summary>
	public bool[] ResolveGroups()
	{
		var resolved = new bool[CellCount];
		bool found;

		do
		{
			found = false;

			for ( int r = 0; r < Size - 1; r++ )
			{
				for ( int c = 0; c < Size - 1; c++ )
				{
					var (tl, tr, bl, br) = GetBlock( r, c );

					if ( tl == 0 || tl != tr || tl != bl || tl != br )
						continue;

					Set( r, c,         0 );
					Set( r, c + 1,     0 );
					Set( r + 1, c,     0 );
					Set( r + 1, c + 1, 0 );

					resolved[r * Size + c]         = true;
					resolved[r * Size + c + 1]     = true;
					resolved[(r + 1) * Size + c]   = true;
					resolved[(r + 1) * Size + c + 1] = true;

					found = true;
				}
			}
		}
		while ( found );

		return resolved;
	}

	/// <summary>
	/// Returns true if all cells of the given color are solved (0).
	/// </summary>
	public bool IsColorCleared( int color )
	{
		foreach ( var c in Cells )
			if ( c == color )
				return false;
		return true;
	}

	/// <summary>
	/// Returns true if no active color has any cells remaining.
	/// </summary>
	public bool IsFullyCleared()
	{
		foreach ( var c in Cells )
			if ( c != 0 )
				return false;
		return true;
	}

	/// <summary>
	/// Returns true if the given top-left position would form a valid 2×2 selection (in-bounds).
	/// </summary>
	public static bool IsValidSelection( int row, int col ) =>
		row >= 0 && row < Size - 1 && col >= 0 && col < Size - 1;

	public int[] CloneCells()
	{
		var copy = new int[CellCount];
		for ( int i = 0; i < CellCount; i++ ) copy[i] = Cells[i];
		return copy;
	}
}
