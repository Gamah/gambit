using System;
using Sandbox;

namespace Gambit.Game;

/// <summary>Thin wrapper over <see cref="Sandbox.Services.Stats"/>. The s&box platform
/// accumulates these per-player across sessions/devices and batches the network sends
/// (the docs: "call these apis as often as you like. We batch the stats"), so even the
/// per-group <see cref="Matches"/> increment is cheap.
///
/// Stat-based achievements are configured in the s&box dashboard against these idents
/// (Aggregation: Sum, Max = threshold) and auto-unlock — no <c>Unlock</c> call needed:
///   matches      ≥1 → showedup     |  solves        ≥1 → extracredit
///   daily_solves ≥5 → goingsteady  |  hourly_solves ≥3 → dedicated
///   deaths       ≥1 → adventurer   |  mp_matches    ≥1 → goldnova
///   mp_wins      ≥1 → globalelite
/// sp_matches is a general-purpose counter (profile / future achievements).</summary>
public static class PlayerStats
{
	public const string SpMatches   = "sp_matches";   // singleplayer games started
	public const string MpMatches   = "mp_matches";   // 2p/4p games begun
	public const string MpWins      = "mp_wins";      // 2p/4p games won
	public const string Matches     = "matches";      // 2×2 groups cleared (SP)
	public const string Solves      = "solves";       // SP boards completed (any mode)
	public const string DailySolves = "daily_solves"; // distinct daily boards finished
	public const string HourlySolves = "hourly_solves"; // distinct hourly boards finished
	public const string Deaths      = "deaths";       // falls off the map

	public static void Increment( string name, int amount = 1 )
	{
		if ( amount <= 0 ) return;
		try
		{
			Sandbox.Services.Stats.Increment( name, amount );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit] stat '{name}' increment failed: {e.Message}" );
		}
	}
}
