using System;
using System.Collections.Generic;
using Sandbox;

namespace Gambit.Api.Lichess;

/// <summary>
/// <c>/api/stream/event</c>, as a PROCESS-WIDE SINGLETON with a reference count.
///
/// <para><b>ONE ACTIVE EVENT STREAM PER TOKEN. This is not a guideline — opening a
/// second closes the first, server-side, and lichess says nothing about it.</b>
/// The dropped stream reports a clean EOF, which is indistinguishable from "the
/// game ended". So a second stream does not error, it makes the FIRST flow hang
/// forever with no message.</para>
///
/// <para>With client custody that hazard is newly live: a second table, a second
/// s&amp;box instance, a hotload orphan. One owner with refcounting is what makes
/// it structurally impossible within a process — and lichess's own rule then
/// enforces a partial version for free ACROSS processes, which is the backstop
/// that makes the one-lichess-game-at-a-time gate tolerable as advisory.</para>
///
/// <para><b>Only the flows that need it should take a reference.</b> A seek needs
/// it (a real-time seek's response carries no game id — it is a stream of empty
/// lines whose only job is to stay open). An open/shareable-link game needs it
/// (we watch for the browser opponent joining). The PAIRED flow deliberately does
/// NOT: White's challenge response carries the id, both seats are in the same
/// s&amp;box lobby, and the station already <c>[Sync]</c>s state — so the id is
/// <c>[Sync]</c>ed and Black accepts by id. That preserves exactly the property
/// the server's relay documented: the paired flow never watches the event stream
/// and so is not bound by this rule at all.</para>
/// </summary>
public static class LichessEventStream
{
	static LichessStream _stream;
	static int _refs;

	/// <summary>Subscribers, in the order they registered. Each gets every event —
	/// there is no routing here, because a flow cannot know which gameStart is
	/// "its" one until it looks.</summary>
	static readonly List<Action<LichessEvent>> _listeners = new();

	/// <summary>Why the stream last stopped, or null.</summary>
	public static string Error => _stream?.Error;

	/// <summary>Somebody is holding it open.</summary>
	public static bool Open => _refs > 0;

	/// <summary>Take a reference and start listening.
	///
	/// <para>The returned handle MUST be disposed — a leaked reference holds a live
	/// HTTP connection to lichess and keeps this token's one slot occupied, which
	/// fails silently. Every caller uses <c>using</c> or disposes on a definite
	/// path (game over, stand-up, teardown).</para></summary>
	public static IDisposable Listen( Action<LichessEvent> onEvent )
	{
		if ( onEvent != null ) _listeners.Add( onEvent );
		_refs++;

		if ( _stream == null )
		{
			// The token is read LATE, per connect, so unlinking mid-flow does not
			// replay a dead token onto a reconnect.
			_stream = new LichessStream( "/api/stream/event", () => LichessTokenStore.Token );
		}
		return new Handle( onEvent );
	}

	sealed class Handle : IDisposable
	{
		Action<LichessEvent> _fn;

		public Handle( Action<LichessEvent> fn ) => _fn = fn;

		public void Dispose()
		{
			if ( _fn == null ) return;   // idempotent: double-dispose must not double-decrement
			_listeners.Remove( _fn );
			_fn = null;
			Release();
		}
	}

	static void Release()
	{
		_refs--;
		if ( _refs > 0 ) return;

		// Nobody is listening. DROP THE CONNECTION rather than leave it idling:
		// on the Board API an open stream is a presence signal, and holding one
		// for a player who is doing nothing is both a lie and a wasted slot.
		_refs = 0;
		_stream?.Dispose();
		_stream = null;
	}

	/// <summary>Pump the stream. Call every frame from exactly one place while
	/// anyone holds a reference — <c>LichessGameController.OnUpdate</c> does, and
	/// calling it from a second component would be harmless but pointless.
	///
	/// <para>Runs on the game thread, which is what makes it safe for a listener to
	/// touch scene state.</para></summary>
	public static void Pump()
	{
		if ( _stream == null ) return;

		var lines = _stream.Drain();
		if ( lines == null ) return;

		foreach ( var line in lines )
		{
			var ev = LichessClient.Parse<LichessEvent>( line );
			if ( ev?.type == null ) continue;   // a malformed line is skipped, never fatal

			// Snapshot: a listener may dispose itself on the event that resolves it,
			// and mutating the list mid-iteration would drop the next listener.
			var snapshot = _listeners.ToArray();
			foreach ( var fn in snapshot )
			{
				try { fn( ev ); }
				catch ( Exception e )
				{
					// One flow's bug must not stop the others hearing their events.
					Log.Warning( $"[Gambit] a lichess event listener threw: {e.Message}" );
				}
			}
		}
	}

	/// <summary>Drop everything: sign-out, unlink, teardown. Leaves no listeners
	/// and no connection.</summary>
	public static void Reset()
	{
		_listeners.Clear();
		_refs = 0;
		_stream?.Dispose();
		_stream = null;
	}
}
