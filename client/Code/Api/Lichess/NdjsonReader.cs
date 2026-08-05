using System;
using System.Collections.Generic;
using System.Text;

namespace Gambit.Api.Lichess;

/// <summary>
/// Turns a byte stream into ndjson lines.
///
/// <para>Split out and Sandbox-free for one reason: <b>framing is the half of
/// stream reading that can be tested on a dev host with no engine</b>. The
/// harness feeds it a real captured lichess stream in adversarial chunk sizes —
/// a line split across three reads, a keepalive alone in a chunk, a UTF-8
/// sequence straddling a boundary — none of which a live editor test would ever
/// reliably produce. What is left in <see cref="LichessStream"/> is the socket
/// lifecycle, which genuinely cannot be proven here.</para>
///
/// <para><b>A blank line is a keepalive, not an error.</b> lichess sends one
/// roughly every 7 seconds on every ndjson stream to keep intermediaries from
/// closing it. Skipping them is not tolerance for junk; it is the protocol.</para>
/// </summary>
public sealed class NdjsonReader
{
	/// <summary>Bound on one line. A <c>gameFull</c> with a long move list is a few
	/// KB; a megabyte is slack, not a budget. Past it we give up on the line rather
	/// than grow without limit — a stream that sends a megabyte without a newline is
	/// not sending us ndjson.</summary>
	public const int MaxLineBytes = 1 << 20;

	readonly List<byte> _buf = new();

	/// <summary>True once a line overran <see cref="MaxLineBytes"/>. The caller
	/// should drop the connection: we have lost sync with the framing and the
	/// bytes after it cannot be trusted to be a line boundary.</summary>
	public bool Overran { get; private set; }

	/// <summary>Feed the bytes just read; yields each COMPLETE line, keepalives
	/// already skipped. A partial line is held for the next call.</summary>
	public IEnumerable<string> Push( byte[] chunk, int count )
	{
		for ( int i = 0; i < count; i++ )
		{
			byte b = chunk[i];
			if ( b != (byte)'\n' )
			{
				if ( _buf.Count >= MaxLineBytes )
				{
					Overran = true;
					_buf.Clear();
					yield break;
				}
				_buf.Add( b );
				continue;
			}

			// Decode from BYTES, never from a per-chunk string: a multi-byte UTF-8
			// sequence can straddle a read boundary, and decoding each chunk
			// separately would mangle any non-ASCII username lichess sends.
			string line = Encoding.UTF8.GetString( _buf.ToArray() ).Trim( '\r', ' ', '\t' );
			_buf.Clear();
			if ( line.Length > 0 ) yield return line;
		}
	}

	/// <summary>Drop any partial line. Call when a connection ends: a truncated
	/// line must never be handed on as if it were complete, and must not survive
	/// into the next connection's framing.</summary>
	public void Reset()
	{
		_buf.Clear();
		Overran = false;
	}
}
