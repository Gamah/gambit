using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Catches an exception thrown inside a per-frame step, names the step, and keeps the first full
/// stack for it.
///
/// <para><b>A throw in an <c>OnUpdate</c> costs the rest of that method.</b> The engine catches it
/// at the component boundary and keeps calling the component next frame, so a single bad deref in
/// an early line silently stops every line after it from ever running — the symptom is missing
/// behaviour, not a crash. Wrapping the steps individually means one broken step loses only
/// itself, and the log says which one.</para>
///
/// <para><b>The stack is kept once per step, not once per frame.</b> A fault in a 90Hz update
/// writes thousands of identical stacks and the console is then the least useful place to read
/// one. First occurrence logs in full; after that only the count moves, and
/// <c>gambit_trace</c> prints what accumulated.</para>
///
/// <para>Reachable from a headset through <see cref="Gambit.UI.VrStatusPanel"/>: a wrapped step
/// that is throwing is exactly the state a player cannot see and cannot type a command to
/// ask about.</para>
/// </summary>
public static class FrameTrace
{
	/// <summary>One wrapped step that has thrown at least once.</summary>
	public sealed class Fault
	{
		public string Site;
		/// <summary>The whole first exception, <c>ToString()</c>: type, message and stack.</summary>
		public string Stack;
		/// <summary>Latest message, which can differ from the first while the stack does not.</summary>
		public string Message;
		public int Count;
	}

	static readonly Dictionary<string, Fault> Faults = new();

	/// <summary>Where a first occurrence is written. Replaceable so this type can be exercised
	/// without an engine.</summary>
	public static Action<string> Sink { get; set; } = s => Log.Error( s );

	/// <summary>Run a per-frame step, and keep the stack rather than the frame if it throws.</summary>
	public static void Run( string site, Action step )
	{
		try
		{
			step();
		}
		catch ( Exception e )
		{
			Record( site, e.ToString(), e.Message );
		}
	}

	/// <summary>Record a fault against a step. Separate from <see cref="Run"/> so a caller that
	/// already has the exception — a <c>try</c> it needs for its own control flow — reports it the
	/// same way.</summary>
	public static void Record( string site, string stack, string message )
	{
		if ( !Faults.TryGetValue( site, out var fault ) )
		{
			Faults[site] = new Fault { Site = site, Stack = stack, Message = message, Count = 1 };
			Sink?.Invoke( $"[Gambit] trace: {site} threw — {stack}" );
			return;
		}

		fault.Count++;
		fault.Message = message;
	}

	/// <summary>How many wrapped steps are currently faulting, for a status line.</summary>
	public static int Count => Faults.Count;

	/// <summary>The faulting steps, worst-repeated first.</summary>
	public static IEnumerable<Fault> All => Faults.Values.OrderByDescending( f => f.Count );

	/// <summary>One line per faulting step, short enough for a panel read across a room.</summary>
	public static IEnumerable<string> Lines =>
		All.Select( f => $"{f.Site} ×{f.Count} · {f.Message}" );

	/// <summary>Print every step that has thrown, with the stack from its first throw.</summary>
	[ConCmd( "gambit_trace" )]
	public static void Dump()
	{
		if ( Faults.Count == 0 )
		{
			Log.Info( "[Gambit] trace: no wrapped step has thrown" );
			return;
		}

		foreach ( var fault in All )
			Log.Info( $"[Gambit] trace: {fault.Site} ×{fault.Count}\n{fault.Stack}" );
	}

	/// <summary>Forget everything recorded, so the next throw logs its stack again. What makes a
	/// fix verifiable in a running session: clear, play the case, dump.</summary>
	[ConCmd( "gambit_trace_clear" )]
	public static void Clear()
	{
		Faults.Clear();
		Log.Info( "[Gambit] trace: cleared" );
	}
}
