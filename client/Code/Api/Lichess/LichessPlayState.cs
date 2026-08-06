namespace Gambit.Api;

/// <summary>
/// One snapshot of the lichess game at a table.
///
/// <para><b>It used to be a wire DTO and is now a client-side MODEL</b>, and that
/// is the whole HTTPFIX change in one class. gamchess used to run the game and
/// publish this over a long poll; the client now holds the lichess stream itself
/// and BUILDS this from <c>gameFull</c>/<c>gameState</c> lines. Nothing
/// deserializes it any more.</para>
///
/// <para>It kept its name and its shape deliberately: <c>GameHud</c>,
/// <c>SetupPanel</c>, <c>ChessBoardView</c> and the sounds all read
/// <c>State.something</c>, and none of them care where the something came from.
/// Changing the transport should not have been a change to every consumer.</para>
///
/// <para><b>What is GONE, and must not come back:</b> <c>version</c> (a long-poll
/// cursor), <c>clock_age_ms</c> and <c>hold_ms</c> (staleness reconciliation for a
/// held poll). A stream has no hold to measure and no cursor to reconcile — M18
/// deleted the same machinery when TV moved off its own long poll, for the same
/// reason. Keeping it against a stream would reintroduce the M11 sawtooth, where
/// the clock ticked down and then jumped back UP: the one direction the house rule
/// forbids.</para>
///
/// <para><c>moves</c> is lichess's own full UCI list from the start position —
/// never a delta — which is why a dropped or duplicated line costs nothing and
/// there is no reconciliation to get wrong. Replay it into a ChessGame.</para>
/// </summary>
public sealed class LichessPlayState
{
	/// <summary>"waiting" (nothing has happened yet) · "challenging" (an invitation
	/// is out) · "live" · "over" · "failed".</summary>
	public string status { get; set; }
	public string error { get; set; }

	public string game_id { get; set; }
	public string url { get; set; }

	/// <summary>The link the seated player hands to their browser opponent — the
	/// OPPOSITE colour's url of an open-challenge game. Empty for every other flow.</summary>
	public string share_url { get; set; }

	public string white_name { get; set; }
	public string black_name { get; set; }

	public string moves { get; set; }

	/// <summary>Milliseconds, straight from lichess (the TV feed sends the same
	/// idea in seconds; two endpoints, two units).
	///
	/// <para>lichess only SENDS a clock when a move happens, so the controller runs
	/// the side-to-move's value down locally between moves and snaps back to these
	/// on the next state.</para></summary>
	public long white_time_ms { get; set; }
	public long black_time_ms { get; set; }
	public long white_inc_ms { get; set; }
	public long black_inc_ms { get; set; }

	/// <summary>A game against someone who is not sitting opposite — a lobby seek,
	/// a challenge to a named stranger, or a shareable link. The other seat at the
	/// table is empty, so <see cref="your_color"/> is the only way to know which
	/// side we have.</summary>
	public bool seek { get; set; }

	/// <summary>"white" | "black" | null — which side WE play.
	///
	/// <para>Read from lichess's own <c>gameFull</c> (which player id is ours),
	/// never from the station's seats: in a seek there is no opponent SteamID to
	/// match against, and if the two ever disagreed the board must follow
	/// lichess.</para></summary>
	public string your_color { get; set; }

	/// <summary>lichess's own status: created/started/mate/resign/outoftime/…</summary>
	public string lichess_status { get; set; }
	public string winner { get; set; }        // "white" | "black" | null
	public bool finished { get; set; }
	public bool white_draw { get; set; }      // that side is offering a draw
	public bool black_draw { get; set; }
	public bool white_takeback { get; set; }  // that side is proposing a takeback
	public bool black_takeback { get; set; }
}
