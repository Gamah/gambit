using System;
using System.Text;
using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api;

/// <summary>
/// PLAN.md D4 "spike 1" — the project's biggest risk: is s&amp;box's <c>Http</c>
/// able to read an ndjson response <b>incrementally</b>, as the Board API play
/// loop needs (held-open event/game streams)? There is no polling fallback if
/// not.
///
/// <para><b>Isolated on purpose.</b> The stream-reading APIs used here
/// (<c>HttpContent.ReadAsStreamAsync</c>, <c>Stream.ReadAsync(byte[],…)</c>) have
/// no precedent in this repo or the parent, so they might trip the s&amp;box
/// whitelist (SB1000) — and one SB1000 fails the <i>whole</i> assembly. Keeping
/// this in its own file means: if the compiler rejects a symbol here, delete just
/// this file and everything else in M3 (the import fix, sign-in, other console
/// commands) still builds. Report the rejected symbol to Facepunch — a blocked
/// streaming API is itself the D4 answer and drives the mitigation (whitelist
/// request).</para>
/// </summary>
public static class LichessTvSpike
{
	/// <summary>Stream lichess TV (no auth) and log each ndjson line with the gap
	/// since the previous one. Lines arriving spread out over seconds — not all at
	/// once at the very end — prove incremental streaming works and the Board API
	/// is viable. If "response headers received" never logs, <c>RequestAsync</c> is
	/// buffering the whole (infinite) stream: streaming is unavailable via this path.</summary>
	[ConCmd( "gambit_lichess_tv" )]
	public static void Run() => _ = RunAsync();

	static async Task RunAsync()
	{
		Log.Info( "[Gambit] D4 streaming spike → GET https://lichess.org/api/tv/feed (no auth)" );
		Log.Info( "[Gambit]   watch the per-line gaps: trickling arrivals = incremental streaming works." );
		try
		{
			var resp = await Http.RequestAsync( "https://lichess.org/api/tv/feed", "GET", null, null );
			Log.Info( $"[Gambit]   response headers received: HTTP {(int)resp.StatusCode} — reading body stream…" );
			if ( !resp.IsSuccessStatusCode )
			{
				Log.Error( $"[Gambit]   TV feed returned {(int)resp.StatusCode}; spike inconclusive." );
				return;
			}

			using var stream = await resp.Content.ReadAsStreamAsync();
			var buffer = new byte[4096];
			string pending = "";
			int lines = 0;
			RealTimeSince started = 0;
			RealTimeSince sinceLast = 0;

			// Bounded so the command self-terminates and closes the connection.
			while ( lines < 12 && started < 25f )
			{
				int n = await stream.ReadAsync( buffer, 0, buffer.Length );
				if ( n <= 0 ) { Log.Info( "[Gambit]   server closed the stream" ); break; }

				pending += Encoding.UTF8.GetString( buffer, 0, n );

				int nl;
				while ( ( nl = pending.IndexOf( '\n' ) ) >= 0 )
				{
					var line = pending[..nl].Trim();
					pending = pending[(nl + 1)..];

					if ( line.Length == 0 )
					{
						Log.Info( $"[Gambit]   ·keep-alive· (+{sinceLast.Relative:0.0}s)" );
						sinceLast = 0;
						continue;
					}
					lines++;
					Log.Info( $"[Gambit]   line {lines} (+{sinceLast.Relative:0.0}s, {line.Length} chars): {LichessApi.Truncate( line, 100 )}" );
					sinceLast = 0;
				}
			}

			Log.Info( $"[Gambit] D4 spike done: {lines} line(s) over {started.Relative:0.0}s. Spread-out arrivals ⇒ incremental streaming ⇒ Board API is viable." );
		}
		catch ( Exception e )
		{
			Log.Error( $"[Gambit] D4 streaming spike threw: {e.Message}" );
		}
	}
}
