namespace Gambit.Game;

/// <summary>
/// Which Gambit tables can mirror to lichess, and why (M8).
///
/// <para>Deliberately Sandbox-free, like <see cref="TimeControl"/> itself — plain
/// C# with no engine types, so the standalone dotnet harness on a host with no
/// s&amp;box toolchain can prove it. This rule decides what the UI offers, so it
/// is worth being able to actually run.</para>
///
/// <para>This mirrors gamchess's <c>lichess.BoardCompatible</c>. Both exist on
/// purpose: the server's is the gate (a client cannot be trusted to enforce it),
/// this one is so the UI never offers a button that would be refused. If they
/// ever disagree, the server wins and the player gets an error instead of a
/// missing button — annoying, not broken.</para>
/// </summary>
public static class LichessTable
{
	/// <summary>
	/// lichess's Board API refuses anything faster than blitz.
	///
	/// <para>lila gates every board challenge on
	/// <c>isBoardCompatible: speed >= Speed.Blitz</c>, and speed comes from
	/// scalachess's <c>Speed.byTime(limit + 40*increment)</c>, whose Blitz band
	/// starts at 180. So the floor is an estimated total of 180 seconds.</para>
	///
	/// <para><b>[SOURCE]</b> read from lila/scalachess master on 2026-07-15 — this
	/// is inferred from their source, not a documented contract, and can change
	/// without notice. Re-check before trusting it.</para>
	/// </summary>
	public const int BoardApiFloorSeconds = 180;

	/// <summary>scalachess's clock-to-speed estimate: the initial bank plus the
	/// increment over an assumed 40-move game.</summary>
	public static int EstimateTotalSeconds( TimeControl tc ) =>
		tc.InitialSeconds + 40 * tc.IncrementSeconds;

	/// <summary>
	/// Can a table at this control play its game on lichess?
	///
	/// <para><b>Bullet never can</b>, from any path — Gambit's Bullet 1+0 estimates
	/// at 60 and lands in lichess's Bullet band. That is lichess's rule, not ours.
	/// <b>Unlimited always can</b>: no clock means lichess calls it Correspondence,
	/// which is comfortably past the blitz floor.</para>
	/// </summary>
	public static bool CanMirror( TimeControl tc ) =>
		tc.IsUnlimited || EstimateTotalSeconds( tc ) >= BoardApiFloorSeconds;

	/// <summary>Why a table can't mirror, for the HUD. Null when it can.</summary>
	public static string WhyNot( TimeControl tc ) =>
		CanMirror( tc ) ? null
			: $"lichess won't play {tc.Name} — its Board API refuses anything faster than blitz.";

	/// <summary>
	/// A lobby seek's floor, which is STRICTER than a challenge's: rapid or slower.
	///
	/// <para>lila has two functions called <c>isBoardCompatible</c> with different
	/// thresholds — <c>Challenge.isBoardCompatible</c> is <c>speed >= Blitz</c>,
	/// while <c>lila.core.game.isBoardCompatible</c> (which gates the board seek
	/// form) is <c>Speed(clock) >= Rapid</c>. Same name, different files, different
	/// answers. Do not collapse them.</para>
	///
	/// <para><b>[SOURCE]</b> lila master, 2026-07-15. Re-check before trusting it.</para>
	/// </summary>
	public const int SeekFloorSeconds = 480;

	/// <summary>
	/// Can this control be played against a RANDOM lichess opponent?
	///
	/// <para>Stricter than <see cref="CanMirror"/>, and that gap is why a direct
	/// challenge is the primary flow: the default table is Blitz 3+0, which lichess
	/// will happily let you challenge someone with, but will not put in its lobby.
	/// Unlimited can't be seeked either — a real-time seek needs a clock.</para>
	/// </summary>
	public static bool CanSeek( TimeControl tc ) =>
		!tc.IsUnlimited && EstimateTotalSeconds( tc ) >= SeekFloorSeconds;

	/// <summary>The fastest control lichess's lobby will actually take, for copy that
	/// tells the player what to pick instead of only what's wrong.
	///
	/// <para>Computed, not written down: both the menu and lichess's floor have moved
	/// before, and a hardcoded "Rapid 10+0" would go quietly wrong the next time
	/// either does.</para>
	///
	/// <para>Takes the first entry <see cref="CanSeek"/> accepts, which is the fastest
	/// only because the menu is ordered fastest-first for the entries that HAVE a
	/// clock. Unlimited sits last with an initial of 0, so <c>All</c> is not
	/// numerically ascending and this would pick it if <c>CanSeek</c> didn't rule it
	/// out first. Reorder the menu and this needs re-reading.</para></summary>
	public static string FastestSeekableName()
	{
		foreach ( var tc in TimeControl.All )
			if ( CanSeek( tc ) ) return tc.Name;
		return null;
	}

	/// <summary>Why a control can't be seeked, for the HUD. Null when it can.
	///
	/// <para>Says what to do, not just what's refused. The rule is lichess's and the
	/// player has no way to know that, so an unexplained missing button reads as our
	/// bug — which is exactly how it read the first time.</para></summary>
	public static string WhySeekNot( TimeControl tc )
	{
		if ( CanSeek( tc ) ) return null;

		string pick = FastestSeekableName();
		string instead = pick == null ? "" : $" Pick {pick} to play a stranger.";

		// Bullet reaches lichess by NO route, so it must not be offered the
		// consolation the other two get. "You can still play the person opposite you"
		// is true for every control here except this one, and CanMirror is the only
		// thing that knows the difference.
		if ( !CanMirror( tc ) )
			return $"lichess won't take {tc.Name} by any route — it's faster than its Board API allows." + instead;

		if ( tc.IsUnlimited )
			return "lichess's lobby needs a clock, so an unlimited game can't be matched with a stranger — "
				+ "you can still play the person opposite you." + instead;

		return $"lichess only puts rapid and slower games in its lobby, so {tc.Name} can't be matched with a "
			+ "stranger — you can still play the person opposite you." + instead;
	}

	/// <summary>A seek's clock in MINUTES, which is lichess's unit for a seek
	/// (a challenge takes seconds). Getting this wrong asks for a ten-SECOND game
	/// while meaning ten minutes.</summary>
	public static float SeekTimeMinutes( TimeControl tc ) => tc.InitialSeconds / 60f;
}
