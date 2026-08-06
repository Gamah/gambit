using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace Gambit.Api.Lichess;

/// <summary>
/// One long-lived lichess ndjson stream, read on a thread-pool thread and drained
/// on the game thread.
///
/// <para><b><see cref="Gambit.Game.LichessTvSource"/> is the model for the
/// lifecycle — and the ONE thing it does not teach is thread affinity.</b>
/// <c>Sandbox.WebSocket</c> hands you each message on the game thread; a raw
/// <c>Stream</c> read completes on a THREAD-POOL thread. So the read loop may
/// touch nothing but the queue below, and every <c>Scene</c> / <c>[Sync]</c> /
/// <c>GameObject</c> touch happens in <see cref="Drain"/> from <c>OnUpdate</c>.
/// TV gets this for free and therefore proves nothing about it.</para>
///
/// <para><b>Hotload is the matching hazard, and it fails silently.</b> An orphaned
/// read task leaves a LIVE HTTP CONNECTION to lichess — which on the Board API
/// means both "this player is still present" and "this token's one event-stream
/// slot is taken". Nothing errors; the next stream just never delivers. So every
/// loop carries a GENERATION, checked after every read and bumped on every start:
/// an orphan exits on its next line.</para>
///
/// <para><b>A stream failure must never degrade into a poll.</b> lichess answers a
/// poller with a literal "Please don't poll this endpoint, it is intended to be
/// streamed" 429. Reconnects are exponential (3s → 6s → 12s, capped), and a game
/// stream is never reopened once lichess has reported the game finished.</para>
/// </summary>
public sealed class LichessStream : IDisposable
{
	/// <summary>First reconnect delay. Doubles per consecutive failure.</summary>
	public const float BackoffStartSeconds = 3f;

	/// <summary>Ceiling on the backoff. A minute matches the post-429 rule, so a
	/// stream that keeps failing settles at the same cadence lichess asks for.</summary>
	public const float BackoffCapSeconds = 60f;

	readonly string _path;
	readonly string _method;
	readonly Func<string> _token;          // read late: the token can be forgotten mid-stream
	readonly Dictionary<string, string> _form;

	/// <summary>Lines the reader has produced and the game thread hasn't taken.
	///
	/// <para>A QUEUE, not a latest-wins slot, and that is the difference from TV.
	/// A TV snapshot is self-contained so the newest wins outright; a game stream
	/// interleaves shapes — a <c>gameFull</c> followed by <c>gameState</c>s — and
	/// dropping the middle of that is dropping the game. Every <c>gameState</c>
	/// does carry the whole move list, so a dropped line would not corrupt the
	/// board, but it could lose the only <c>gameFull</c> there will ever be.</para></summary>
	readonly Queue<string> _lines = new();

	readonly object _lock = new();

	CancellationTokenSource _cts;
	int _generation;
	bool _running;
	string _error;
	bool _ended;

	RealTimeUntil _retryAfter;
	float _backoff = BackoffStartSeconds;

	/// <summary>
	/// </summary>
	/// <param name="path">Path under lichess.org, already escaped.</param>
	/// <param name="token">Read LATE, on every (re)connect: a player who unlinks
	/// mid-game must not have a stale token replayed onto a reconnect. Returning
	/// null opens the stream ANONYMOUSLY, which is required for nothing here but
	/// costs nothing to allow.</param>
	public LichessStream( string path, Func<string> token, string method = "GET",
		Dictionary<string, string> form = null )
	{
		_path = path;
		_token = token;
		_method = method;
		_form = form;
	}

	/// <summary>Why the stream last stopped, or null. Read on the game thread.</summary>
	public string Error
	{
		get { lock ( _lock ) return _error; }
	}

	/// <summary>lichess closed the stream cleanly and we will not reopen it.
	///
	/// <para>A clean EOF on the Board API means the game is over — <b>or that this
	/// token opened a second event stream somewhere else</b>, which is why
	/// <see cref="LichessEventStream"/> is a process-wide singleton. There is no
	/// way to tell the two apart from here.</para></summary>
	public bool Ended
	{
		get { lock ( _lock ) return _ended; }
	}

	/// <summary>A read loop is live right now.</summary>
	public bool Running
	{
		get { lock ( _lock ) return _running; }
	}

	/// <summary>Call every frame from <c>OnUpdate</c>. Keeps a connection up and
	/// returns the lines that arrived since the last call, IN ORDER.
	///
	/// <para><b>This is the only method that may touch scene state</b>, because it
	/// is the only one that runs on the game thread.</para></summary>
	public List<string> Drain()
	{
		if ( !Ended && !Running && (float)_retryAfter <= 0f )
			Start();

		lock ( _lock )
		{
			if ( _lines.Count == 0 ) return null;
			var outp = new List<string>( _lines );
			_lines.Clear();
			return outp;
		}
	}

	/// <summary>Stop and never restart. The caller MUST reach this on every exit
	/// path — game over, stand-up, teardown, hotload.</summary>
	public void Dispose()
	{
		lock ( _lock ) _ended = true;
		Cancel();
	}

	/// <summary>Stop the current attempt without giving up on the stream.</summary>
	void Cancel()
	{
		// Cancel BEFORE disposing and null the reference BEFORE the task can
		// observe it — the same discipline as LichessTvSource.DisposeSocket's
		// "unhook BEFORE Dispose". Bumping the generation is what makes an orphan
		// that is already inside a read exit on its next line rather than keep
		// appending to a queue nobody drains.
		CancellationTokenSource cts;
		lock ( _lock )
		{
			_generation++;
			cts = _cts;
			_cts = null;
			_running = false;
		}
		if ( cts == null ) return;
		try { cts.Cancel(); } catch { }
		try { cts.Dispose(); } catch { }
	}

	void Start()
	{
		int gen;
		CancellationTokenSource cts = new();
		lock ( _lock )
		{
			if ( _running || _ended ) return;
			_running = true;
			_generation++;
			gen = _generation;
			_cts = cts;
		}
		_ = Read( gen, cts.Token );
	}

	async Task Read( int gen, CancellationToken ct )
	{
		var reader = new NdjsonReader();
		Stream stream = null;

		try
		{
			stream = await LichessClient.OpenStream( _path, _method, _token?.Invoke(), ct, _form );

			// The connect succeeded, so the next failure starts from the bottom of
			// the backoff again rather than inheriting an old streak's ceiling.
			_backoff = BackoffStartSeconds;

			var buf = new byte[8 << 10];
			while ( true )
			{
				int n = await stream.ReadAsync( buf, 0, buf.Length, ct );
				if ( n <= 0 ) break;   // clean EOF — lichess closed it

				// An orphan from before a hotload: stop appending and let the
				// finally block close the connection.
				lock ( _lock ) if ( gen != _generation ) return;

				foreach ( var line in reader.Push( buf, n ) )
					lock ( _lock ) _lines.Enqueue( line );

				if ( reader.Overran )
				{
					// We have lost sync with the framing; nothing after this is
					// trustworthy as a line boundary.
					lock ( _lock ) _error = "Lichess sent a line we couldn't frame.";
					return;
				}
			}

			// A CLEAN EOF is the end of this stream, not a failure to retry. On a
			// game stream it means the game is over; retrying would be polling an
			// endpoint lichess asks us not to poll.
			lock ( _lock ) if ( gen == _generation ) _ended = true;
		}
		catch ( OperationCanceledException )
		{
			// Ours. Nothing to report and nothing to retry.
		}
		catch ( LichessStreamException e )
		{
			lock ( _lock )
			{
				if ( gen != _generation ) return;
				_error = e.Message;
				// A dead token is not a transient failure. Retrying a revoked grant
				// forever would spend lichess's budget to be told the same thing.
				if ( e.Unauthorized ) _ended = true;
			}
		}
		catch ( Exception e )
		{
			lock ( _lock ) if ( gen == _generation ) _error = "Lichess stream ended: " + e.Message;
		}
		finally
		{
			// DISPOSE THE STREAM ON EVERY PATH. The returned stream owns the
			// response — "dispose it or the connection stays open" — and a leaked
			// one holds a presence signal and an event-stream slot at lichess with
			// nothing to show for it.
			try { stream?.Dispose(); } catch { }
			reader.Reset();

			bool schedule;
			lock ( _lock )
			{
				schedule = gen == _generation;
				if ( schedule ) _running = false;
			}
			if ( schedule )
			{
				_retryAfter = _backoff;
				_backoff = MathF.Min( _backoff * 2f, BackoffCapSeconds );
			}
		}
	}
}
