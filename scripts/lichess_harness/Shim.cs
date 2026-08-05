// Stand-ins for the handful of engine and backend symbols the lichess client
// touches, so the REAL files compile here.
//
// # Why this exists
//
// HTTPFIX ported ~900 lines of Go into a codebase this host cannot compile. The
// repo's standing answer to that is a shim (CLAUDE.md: "A SHIM is a legitimate
// third option, and P99 is the worked example"), and its rule is the one that
// decides whether a shim is worth building: **worth doing when the engine surface
// is small, worth NOT doing when the shim would have to reimplement engine
// behaviour to be meaningful, because then it only tests the shim.**
//
// This is the good case. What the lichess client actually needs from s&box is
// four things — an HTTP call, a stream, a file, and a monotonic clock — and none
// of their BEHAVIOUR is under test here. What is under test is that the real
// files TYPE-CHECK: that every call matches the shipped signature, every
// namespace resolves, and nothing references a symbol that isn't there. That is
// exactly the class of error a first hotload would otherwise find one at a time.
//
// **So the signatures below are copied from the shipped engine, and are the
// load-bearing part of the file.** `Http.RequestAsync` /
// `Http.RequestStreamAsync` match `engine/Sandbox.Engine/Utility/Web/
// Http.Requests.cs` exactly, read 2026-08-05. If the engine's ever change, this
// stops compiling — which is the alarm working, not a bug.
//
// **What it does NOT prove**, and must not be claimed to: the stream lifecycle,
// cancellation, disposal under a real connection, hotload, thread affinity, or
// whether the s&box whitelist permits a given member at runtime. Those are
// editor work. The whitelist question was answered by READING
// `engine/Sandbox.Access/Rules/BaseAccess.cs` instead — see Pkce.cs.

using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sandbox;

/// <summary>Signatures copied from the shipped engine. Bodies throw: nothing here
/// is meant to run, and a shim that silently returned something plausible would
/// invite someone to write a "test" against it.</summary>
public static class Http
{
	public static Task<HttpResponseMessage> RequestAsync( string requestUri, string method = "GET",
		HttpContent content = null, Dictionary<string, string> headers = null,
		CancellationToken cancellationToken = default ) => throw new NotImplementedException();

	public static Task<System.IO.Stream> RequestStreamAsync( string requestUri, string method = "GET",
		HttpContent content = null, Dictionary<string, string> headers = null,
		CancellationToken cancellationToken = default ) => throw new NotImplementedException();
}

public static class Log
{
	public static void Info( object o ) { }
	public static void Warning( object o ) { }
	public static void Error( object o ) { }
}

/// <summary>`RealTime.Now` is the monotonic clock the etiquette governor is
/// pointed at in the game. The harness supplies its own, which is why
/// <c>LichessEtiquette.UseClock</c> takes a delegate rather than reading this
/// directly.</summary>
public static class RealTime
{
	public static float Now => 0f;
}

/// <summary>Counts DOWN: assign seconds, read the remainder. Positive means "not
/// yet". The real one is engine-driven; this one is frozen, which is fine because
/// nothing here tests timing.</summary>
public struct RealTimeUntil
{
	float _value;
	public static implicit operator RealTimeUntil( float f ) => new() { _value = f };
	public static implicit operator float( RealTimeUntil t ) => t._value;
}

/// <summary>Counts UP from when it was last assigned.</summary>
public struct RealTimeSince
{
	float _value;
	public static implicit operator RealTimeSince( float f ) => new() { _value = f };
	public static implicit operator float( RealTimeSince t ) => t._value;
}

public static class FileSystem
{
	public static BaseFileSystem Data { get; } = new();
}

public class BaseFileSystem
{
	public bool FileExists( string path ) => false;
	public string ReadAllText( string path ) => null;
	public void WriteAllText( string path, string text ) { }
	public void DeleteFile( string path ) { }
}
