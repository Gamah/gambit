using System.Text;
using Gambit.Api.Lichess;

// The Sandbox-free half of the client's lichess code, executed for real.
//
// `dotnet run` from this directory (dotnet 10.x lives at
// ~/.local/share/toolchains/dotnet10/ and is NOT on the default PATH).
//
// What this replaces: the Go tests HTTPFIX deleted. oauth_test.go's RFC 7636
// vector and etiquette_test.go's governor cases moved here with the code they
// covered, rather than being lost with it.

int failures = 0;

void Check( string what, bool ok, string detail = null )
{
	Console.WriteLine( ( ok ? "  ok   " : "  FAIL " ) + what + ( detail is null ? "" : "  — " + detail ) );
	if ( !ok ) failures++;
}

static string Hex( byte[] b ) => Convert.ToHexString( b ).ToLowerInvariant();

// ── SHA-256 ─────────────────────────────────────────────────────────────────
// FIPS 180-4 / RFC 6234 vectors. The empty and one-block cases catch padding
// mistakes; the 1,000,000-'a' case is the only one that exercises the multi-block
// loop and the 64-bit length field, which is where a hand-rolled hash goes wrong.
Console.WriteLine( "SHA-256 (RFC 6234 vectors)" );
Check( "empty string",
	Hex( Sha256.HashData( Array.Empty<byte>() ) ) ==
	"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" );
Check( "\"abc\"",
	Hex( Sha256.HashData( Encoding.ASCII.GetBytes( "abc" ) ) ) ==
	"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" );
Check( "56-byte two-block message",
	Hex( Sha256.HashData( Encoding.ASCII.GetBytes(
		"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq" ) ) ) ==
	"248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1" );
Check( "exactly 64 bytes (the padding-block edge)",
	Hex( Sha256.HashData( Encoding.ASCII.GetBytes( new string( 'a', 64 ) ) ) ) ==
	"ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb" );
Check( "exactly 55 bytes (last size that fits one block)",
	Hex( Sha256.HashData( Encoding.ASCII.GetBytes( new string( 'a', 55 ) ) ) ) ==
	"9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318" );
Check( "one million 'a' (multi-block + 64-bit length)",
	Hex( Sha256.HashData( Encoding.ASCII.GetBytes( new string( 'a', 1_000_000 ) ) ) ) ==
	"cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0" );

// ── PKCE ────────────────────────────────────────────────────────────────────
// RFC 7636 Appendix B's worked example, which is the SAME vector the deleted
// Go oauth_test.go used. It proves the whole chain: ASCII verifier → SHA-256 →
// base64url-without-padding.
Console.WriteLine();
Console.WriteLine( "PKCE (RFC 7636)" );
{
	const string rfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
	const string rfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

	string got = Pkce.Base64Url( Sha256.HashData( Encoding.ASCII.GetBytes( rfcVerifier ) ) );
	Check( "Appendix B challenge", got == rfcChallenge, got );

	var p = Pkce.New();
	Check( "verifier length is inside RFC 7636's [43,128]",
		p.Verifier.Length is >= 43 and <= 128, $"{p.Verifier.Length}" );
	Check( "verifier is 86 chars (64 bytes — margin over lichess's short-verifier floor)",
		p.Verifier.Length == 86, $"{p.Verifier.Length}" );
	Check( "challenge is 43 chars", p.Challenge.Length == 43, $"{p.Challenge.Length}" );

	// base64url, unpadded. Standard base64 here is a failed exchange, not a
	// cosmetic difference — and gamchess's validCodeChallenge rejects it too.
	bool urlSafe = true;
	foreach ( char c in p.Verifier + p.Challenge )
		if ( !( char.IsAsciiLetterOrDigit( c ) || c == '-' || c == '_' ) ) urlSafe = false;
	Check( "both are base64url with no padding", urlSafe );

	Check( "the challenge really is the verifier's hash",
		p.Challenge == Pkce.Base64Url( Sha256.HashData( Encoding.ASCII.GetBytes( p.Verifier ) ) ) );

	var q = Pkce.New();
	Check( "two mints differ", p.Verifier != q.Verifier );
}

// ── The etiquette governor ──────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine( "Etiquette" );
{
	double now = 1000;
	LichessEtiquette.UseClock( () => now );
	LichessEtiquette.Reset();

	Check( "the User-Agent identifies us",
		LichessEtiquette.UserAgent.Contains( "TerrysGambit" ) &&
		LichessEtiquette.UserAgent.Contains( "chess.gamah.net" ) &&
		LichessEtiquette.UserAgent.Contains( "contact:" ) );

	Check( "ready when nothing has happened", !LichessEtiquette.BackingOff );

	LichessEtiquette.Note429();
	Check( "a 429 stops everything", LichessEtiquette.BackingOff );
	Check( "for the full minute lichess asks for",
		Math.Abs( LichessEtiquette.BackoffRemaining - 60 ) < 0.001,
		$"{LichessEtiquette.BackoffRemaining}" );

	now += 59;
	Check( "still backing off at 59s", LichessEtiquette.BackingOff );
	now += 2;
	Check( "clear after the minute", !LichessEtiquette.BackingOff );

	// The seek window.
	LichessEtiquette.Reset();
	for ( int i = 0; i < LichessEtiquette.SeeksPerMinute; i++ )
		Check( $"seek {i + 1} of {LichessEtiquette.SeeksPerMinute} allowed",
			LichessEtiquette.TakeSeekSlot( out _ ) );

	bool refused = !LichessEtiquette.TakeSeekSlot( out string why );
	Check( "the next one is refused locally", refused );
	// A refusal a player can't read is a refusal they'll retry into a 429.
	Check( "and the refusal says how long to wait",
		why is not null && why.Contains( "wait" ), why );

	now += 61;
	Check( "the window slides open again", LichessEtiquette.TakeSeekSlot( out _ ) );

	Check( "the self-limit matches lila's setupPost (5/min)",
		LichessEtiquette.SeeksPerMinute == 5 );
}

// ── ndjson framing ──────────────────────────────────────────────────────────
// The half of stream reading that IS testable without an engine. Chunk sizes a
// live test would never reliably produce are the whole point.
Console.WriteLine();
Console.WriteLine( "ndjson framing" );
{
	// Real shapes: a gameFull, a keepalive, a gameState, a non-ASCII name.
	const string feed =
		"{\"type\":\"gameFull\",\"id\":\"abcd1234\",\"state\":{\"moves\":\"\"}}\n" +
		"\n" +
		"{\"type\":\"gameState\",\"moves\":\"e2e4 e7e5\",\"wtime\":180000}\n" +
		"\n" +
		"{\"type\":\"gameState\",\"moves\":\"e2e4 e7e5 g1f3\",\"white\":\"Ekström\"}\n";

	var all = Encoding.UTF8.GetBytes( feed );

	List<string> ReadIn( int chunk )
	{
		var r = new NdjsonReader();
		var lines = new List<string>();
		for ( int i = 0; i < all.Length; i += chunk )
		{
			int n = Math.Min( chunk, all.Length - i );
			var buf = new byte[n];
			Array.Copy( all, i, buf, 0, n );
			lines.AddRange( r.Push( buf, n ) );
		}
		return lines;
	}

	var whole = ReadIn( all.Length );
	Check( "keepalives skipped, 3 lines from one read", whole.Count == 3, $"{whole.Count}" );
	Check( "the last line survives a multi-byte name intact",
		whole[2].Contains( "Ekström" ), whole.Count > 2 ? whole[2] : "(missing)" );

	// Every chunk size from 1 byte up must produce the identical result. A
	// 1-byte chunk splits every UTF-8 sequence there is.
	bool identical = true;
	for ( int chunk = 1; chunk <= all.Length; chunk++ )
	{
		var got = ReadIn( chunk );
		if ( got.Count != whole.Count ) { identical = false; break; }
		for ( int i = 0; i < got.Count; i++ )
			if ( got[i] != whole[i] ) { identical = false; break; }
		if ( !identical ) break;
	}
	Check( "every chunk size from 1 byte up frames identically", identical );

	// A partial line is held, not emitted.
	{
		var r = new NdjsonReader();
		var half = Encoding.UTF8.GetBytes( "{\"type\":\"gameSt" );
		var lines = new List<string>( r.Push( half, half.Length ) );
		Check( "a partial line is held back", lines.Count == 0, $"{lines.Count}" );

		var rest = Encoding.UTF8.GetBytes( "ate\",\"moves\":\"e2e4\"}\n" );
		lines = new List<string>( r.Push( rest, rest.Length ) );
		Check( "and completed by the next read",
			lines.Count == 1 && lines[0].EndsWith( "\"e2e4\"}" ), lines.Count == 1 ? lines[0] : "(none)" );
	}

	// CRLF: lichess sends bare LF, but an intermediary that rewrites line
	// endings must not leave a stray \r inside the JSON we parse.
	{
		var r = new NdjsonReader();
		var crlf = Encoding.UTF8.GetBytes( "{\"type\":\"gameState\"}\r\n" );
		var lines = new List<string>( r.Push( crlf, crlf.Length ) );
		Check( "CRLF is trimmed", lines.Count == 1 && !lines[0].Contains( '\r' ) );
	}

	// An unbounded line must be refused rather than grown into.
	{
		var r = new NdjsonReader();
		var flood = new byte[NdjsonReader.MaxLineBytes + 16];
		Array.Fill( flood, (byte)'x' );
		var lines = new List<string>( r.Push( flood, flood.Length ) );
		Check( "a line past the cap yields nothing and flags an overrun",
			lines.Count == 0 && r.Overran );
	}

	// Reset must not leak a truncated line into the next connection.
	{
		var r = new NdjsonReader();
		var half = Encoding.UTF8.GetBytes( "{\"partial\":" );
		foreach ( var _ in r.Push( half, half.Length ) ) { }
		r.Reset();
		var fresh = Encoding.UTF8.GetBytes( "{\"type\":\"gameState\"}\n" );
		var lines = new List<string>( r.Push( fresh, fresh.Length ) );
		Check( "Reset drops the partial line",
			lines.Count == 1 && lines[0] == "{\"type\":\"gameState\"}",
			lines.Count == 1 ? lines[0] : "(none)" );
	}
}

Console.WriteLine();
Console.WriteLine( failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)" );
return failures == 0 ? 0 : 1;
