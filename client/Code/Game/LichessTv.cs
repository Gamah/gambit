namespace Gambit.Game;

/// <summary>
/// The lichess TV channels Gambit offers on the spectator wall (M9).
///
/// <para>Deliberately Sandbox-free, like <see cref="LichessTable"/> and
/// <see cref="TimeControl"/> — plain C# with no engine types, so the standalone dotnet
/// harness on a host with no s&amp;box toolchain can prove it. This list decides what
/// the settings board offers, so it is worth being able to actually run.</para>
///
/// <para>This mirrors gamchess's <c>lichess.channels</c> allowlist. Both exist on
/// purpose: the server's is the gate (a client cannot be trusted to keep an arbitrary
/// string out of a lichess URL), this one is so the UI never offers a channel that
/// would be refused. If they ever disagree, <b>the server wins</b> and the player gets
/// an empty channel instead of a missing one — annoying, not broken.</para>
///
/// <para><b>All 16 of lichess's channels, variants included — and that needed correcting.</b>
/// M9 originally shipped the six standard speeds, on the reasoning that the vendored rules
/// are standard-only so a variant FEN "can't be drawn". That rule is real for <b>playing</b>
/// (<see cref="LichessTable"/>, where ChessGame parses the FEN and validates moves) and was
/// wrongly carried over to TV, which does no such thing: <c>SpectatorBoard3D</c> reads the
/// piece-placement field alone and walks its characters. So:</para>
/// <list type="bullet">
/// <item><b>Chess960</b> — placement is ordinary; the X-FEN castling field (<c>HDhd</c>) is
/// never read.</item>
/// <item><b>Crazyhouse</b> — the pockets ride on the end of the placement field
/// (<c>…/RNBQKBNR[Pp]</c>) and fall off the board walker's <c>file &lt; 8</c> guard, so the
/// 64 squares are correct. See <see cref="HidesState"/>.</item>
/// <item><b>Three-check</b> — the check counters ride at the end of the whole FEN.</item>
/// <item><b>King of the Hill, Antichess, Atomic, Horde, Racing Kings</b> — plain standard
/// placement; only the RULES differ, and the wall doesn't know the rules.</item>
/// </list>
/// <para>Verified against every variant's real starting FEN in the dotnet harness.</para>
/// </summary>
public static class LichessTv
{
	/// <summary>How a channel is grouped in the settings board. Display only.</summary>
	public enum Group
	{
		/// <summary>Standard chess at a given speed.</summary>
		Speed,
		/// <summary>A chess variant. The board draws the position; it does not know the rules.</summary>
		Variant,
		/// <summary>Bots and engines.</summary>
		Other,
	}

	/// <summary>One offered channel. <c>Key</c> is exactly how lichess spells it — the
	/// lcfirst of lila's <c>Tv.Channel</c>, and case-sensitive: <c>ultraBullet</c>, not
	/// <c>ultrabullet</c>. Read off <c>GET /api/tv/channels</c> on 2026-07-15.</summary>
	public readonly struct Info
	{
		public readonly string Key;
		public readonly string Label;
		public readonly Group Group;

		public Info( string key, string label, Group group )
		{
			Key = key;
			Label = label;
			Group = group;
		}
	}

	/// <summary>Every channel we offer, in display order. This IS the list — everything
	/// else here is derived from it, so adding a channel is a one-line change.</summary>
	public static readonly Info[] All =
	{
		new( "best", "Top Rated", Group.Speed ),
		new( "bullet", "Bullet", Group.Speed ),
		new( "blitz", "Blitz", Group.Speed ),
		new( "rapid", "Rapid", Group.Speed ),
		new( "classical", "Classical", Group.Speed ),
		new( "ultraBullet", "UltraBullet", Group.Speed ),

		new( "chess960", "Chess960", Group.Variant ),
		new( "crazyhouse", "Crazyhouse", Group.Variant ),
		new( "kingOfTheHill", "King of the Hill", Group.Variant ),
		new( "threeCheck", "Three-check", Group.Variant ),
		new( "antichess", "Antichess", Group.Variant ),
		new( "atomic", "Atomic", Group.Variant ),
		new( "horde", "Horde", Group.Variant ),
		new( "racingKings", "Racing Kings", Group.Variant ),

		new( "bot", "Bot", Group.Other ),
		new( "computer", "Computer", Group.Other ),
	};

	/// <summary>What a lobby suggests when nobody has chosen. Matches gamchess's
	/// <c>ChannelDefault</c>.</summary>
	public const string DefaultChannel = "blitz";

	/// <summary>Channel keys, in display order. Built once — a property that quietly
	/// minted a fresh array per call would read as free and not be.</summary>
	public static readonly string[] Channels = BuildKeys();

	static string[] BuildKeys()
	{
		var keys = new string[All.Length];
		for ( int i = 0; i < All.Length; i++ ) keys[i] = All[i].Key;
		return keys;
	}

	/// <summary>The channels in one group, in display order.</summary>
	public static Info[] InGroup( Group group )
	{
		int n = 0;
		foreach ( var c in All ) if ( c.Group == group ) n++;
		var outp = new Info[n];
		int i = 0;
		foreach ( var c in All ) if ( c.Group == group ) outp[i++] = c;
		return outp;
	}

	/// <summary>Human name for a channel key, or null if we don't offer it.</summary>
	public static string Label( string channel )
	{
		if ( channel == null ) return null;
		foreach ( var c in All )
			if ( c.Key == channel ) return c.Label;
		return null;
	}

	/// <summary>Is this a channel we offer? The UI's gate — NOT a security boundary.
	/// gamchess re-checks against its own allowlist, because that is the side where
	/// the key becomes a lichess URL.</summary>
	public static bool IsValid( string channel ) => Label( channel ) != null;

	/// <summary>Coerce anything into a channel we can actually show. A stored setting
	/// can outlive the list that produced it, and a <c>[Sync]</c>ed suggestion arrives
	/// from another machine — neither may put the wall on a dead channel.</summary>
	public static string Coerce( string channel ) => IsValid( channel ) ? channel : DefaultChannel;

	/// <summary>Does this channel have game state the wall CANNOT show?
	///
	/// <para>The board draws 64 squares and nothing else, so a variant whose state lives
	/// outside them is legible but incomplete — the position is right, the extra isn't
	/// there. Worth saying in the UI rather than letting someone conclude the board is
	/// broken:</para>
	/// <list type="bullet">
	/// <item><b>Crazyhouse</b> — captured pieces held in hand (the pockets).</item>
	/// <item><b>Three-check</b> — how many checks each side has delivered.</item>
	/// </list>
	/// <para>Every other channel shows everything it has.</para></summary>
	public static bool HidesState( string channel ) =>
		channel is "crazyhouse" or "threeCheck";

	/// <summary>What's missing on a <see cref="HidesState"/> channel, or null.</summary>
	public static string HiddenStateNote( string channel ) => channel switch
	{
		"crazyhouse" => "Pieces in hand aren't shown.",
		"threeCheck" => "Check counts aren't shown.",
		_ => null,
	};

	/// <summary>How long the wall holds on a finished game before moving on.
	///
	/// <para>lichess TV cuts to the next game the instant one ends, which is unreadable
	/// on a wall you're walking past — the result flashes and it's gone. We stop on it.
	/// Long enough to read one line, short enough not to feel like a hang.</para></summary>
	public const float FanfareSeconds = 3f;

	/// <summary>"White wins — out of time". Turns lichess's status vocabulary into a line
	/// a human reads, given the winner ("white"/"black", or null/empty for a DRAW).
	///
	/// <para>The mapping lives here, on the Sandbox-free side, so the harness can prove
	/// every status lichess documents produces a sentence — including the ones that are
	/// awkward, like a draw that arrives as <c>outoftime</c> (a flag with no mating
	/// material) or <c>timeout</c> (someone walked away).</para>
	///
	/// <para><paramref name="status"/> empty means we couldn't find out: the caller still
	/// wants to say the game ended, so this returns a bare "Game over".</para></summary>
	public static string ResultLine( string status, string winner )
	{
		bool draw = string.IsNullOrEmpty( winner );
		string who = winner == "white" ? "White" : winner == "black" ? "Black" : null;

		// The reason, in lichess's vocabulary. Anything unrecognised falls through to a
		// plain result line rather than printing a raw key at a player.
		string why = status switch
		{
			"mate" => "checkmate",
			"resign" => "resignation",
			"outoftime" => "out of time",
			"timeout" => "abandoned",
			"stalemate" => "stalemate",
			"draw" => "agreement",
			"cheat" => "cheat detected",
			"aborted" => "aborted",
			"noStart" => "never started",
			"variantEnd" => "variant end",
			"unknownFinish" => null,
			_ => null,
		};

		// Aborted and never-started have no winner and aren't really draws — saying
		// "Draw by aborted" would be nonsense.
		if ( status is "aborted" or "noStart" )
			return status == "aborted" ? "Game aborted" : "Game never started";

		if ( draw )
			return why == null ? "Draw" : $"Draw — {why}";

		if ( who == null )
			return "Game over";

		return why == null ? $"{who} wins" : $"{who} wins — {why}";
	}

	/// <summary>The next channel in the cycle, wrapping.</summary>
	public static string Next( string channel )
	{
		for ( int i = 0; i < All.Length; i++ )
		{
			if ( All[i].Key == channel )
				return All[( i + 1 ) % All.Length].Key;
		}
		return DefaultChannel;
	}
}
