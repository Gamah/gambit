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
/// <para><b>Standard speeds only, and that is a rendering constraint rather than a
/// preference.</b> The vendored chess rules are standard-only, so a Crazyhouse FEN
/// (pockets: <c>…/RNBQKBNR[] w …</c>) or a Chess960 X-FEN castling field (<c>HAha</c>)
/// arrives as something the board cannot parse — and a channel that draws an empty
/// board is worse than a channel that isn't there. lichess also publishes
/// <c>bot</c> and <c>computer</c>, which would parse fine but are noise on a wall in a
/// chess bar. Adding any of them means checking the board can draw it first.</para>
/// </summary>
public static class LichessTv
{
	/// <summary>A channel key, exactly as lichess spells it (the lcfirst of lila's
	/// <c>Tv.Channel</c>). Case-sensitive: <c>ultraBullet</c>, not <c>ultrabullet</c>.</summary>
	public static readonly string[] Channels =
	{
		"best", "bullet", "blitz", "rapid", "classical", "ultraBullet",
	};

	/// <summary>What a lobby suggests when nobody has chosen. Matches gamchess's
	/// <c>ChannelDefault</c>.</summary>
	public const string DefaultChannel = "blitz";

	/// <summary>Human name for a channel key, or null if we don't offer it.</summary>
	public static string Label( string channel ) => channel switch
	{
		"best" => "Top Rated",
		"bullet" => "Bullet",
		"blitz" => "Blitz",
		"rapid" => "Rapid",
		"classical" => "Classical",
		"ultraBullet" => "UltraBullet",
		_ => null,
	};

	/// <summary>Is this a channel we offer? The UI's gate — NOT a security boundary.
	/// gamchess re-checks against its own allowlist, because that is the side where
	/// the key becomes a lichess URL.</summary>
	public static bool IsValid( string channel ) => Label( channel ) != null;

	/// <summary>Coerce anything into a channel we can actually show. A stored setting
	/// can outlive the list that produced it — a player who picked a channel we later
	/// dropped must get the default, not a wall that never loads.</summary>
	public static string Coerce( string channel ) => IsValid( channel ) ? channel : DefaultChannel;

	/// <summary>The next channel in the cycle, wrapping. Used by the settings board's
	/// picker.</summary>
	public static string Next( string channel )
	{
		for ( int i = 0; i < Channels.Length; i++ )
		{
			if ( Channels[i] == channel )
				return Channels[( i + 1 ) % Channels.Length];
		}
		return DefaultChannel;
	}
}
