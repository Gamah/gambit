// The gamchess side of the seam, stubbed.
//
// GamchessApi and GamchessAuth are real files that reach further into the engine
// than this harness is worth shimming (Facepunch auth, Connection, the session
// dance). What the lichess client uses of them is small and stable, so it is
// stubbed here — and stubbed with the REAL signatures, so a change to either side
// of that seam stops this compiling.
//
// GamchessModels.cs, by contrast, is included for real: it is pure DTOs, and
// LichessLinkStatus is one of them.

using System.Net.Http;
using System.Threading.Tasks;

namespace Gambit.Api;

public static class GamchessApi
{
	public const string Base = "https://chess.gamah.net";
	public const string WsBase = "wss://chess.gamah.net";

	public struct Result
	{
		public bool Ok;
		public int Status;
		public string Body;
		public string Error;

		public readonly bool Unauthorized => Status == 401;
		public readonly bool NotFound => Status == 404;
	}

	public static Task<Result> SendAuthed( string path, string method, HttpContent content ) =>
		throw new NotImplementedException();

	public static HttpContent Json( object o ) => throw new NotImplementedException();

	public static T Deserialize<T>( string json ) where T : class => null;

	public static string NewClientGameId() => "00000000-0000-4000-8000-000000000000";

	public static string Redact( string token ) => "****";

	public static string Truncate( string s, int max ) => s;
}

public static class GamchessAuth
{
	public static bool Available => false;
}
