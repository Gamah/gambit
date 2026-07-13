using System;
using System.Text;
using System.Threading.Tasks;
using Sandbox;
using Gambit.Game;

namespace Gambit.Api;

/// <summary>
/// lichess OAuth2 Authorization-Code + PKCE sign-in (PLAN.md D3), as a manual
/// "code paste" flow — the sandbox can't open a browser or bind a loopback
/// listener, so instead of auto-catching the redirect we let the user copy it.
///
/// Steps: <see cref="Start"/> builds the authorize URL (with an S256 challenge)
/// and copies it; the user opens it, authorizes, and their browser lands on the
/// <see cref="RedirectUri"/> (a localhost URL that won't load — that's fine, the
/// <c>?code=…</c> is right there in the address bar). They paste that URL (or just
/// the code) back, and <see cref="Complete"/> verifies <c>state</c>, exchanges the
/// code + verifier at <c>POST /api/token</c>, then hands the resulting bearer
/// token to <see cref="LichessAuth.SignInWithToken"/> to validate and store — so
/// an OAuth login and a pasted personal token converge on the same storage.
///
/// <para><b>Whitelist-safe by construction.</b> PKCE needs SHA-256 + base64url;
/// rather than risk <c>System.Security.Cryptography</c> / <c>Convert.ToBase64String</c>
/// against the s&amp;box whitelist, both are hand-rolled below in pure integer
/// arithmetic (verified on the dev host against System crypto, the framework
/// base64 encoder, and the RFC 7636 vector). Everything else here is already
/// proven in the repo: <c>System.Random.Shared</c> (FloorCheckerboard),
/// <c>Encoding.UTF8</c>, <c>Uri.Escape/UnescapeDataString</c>, <c>StringBuilder</c>.</para>
/// </summary>
public static class LichessOAuth
{
	// Arbitrary public-client id (no registration/secret for PKCE — D3).
	public const string ClientId = "gambit.gamah";

	// A localhost redirect the browser will try (and fail) to load; the code is in
	// the address bar regardless. localhost is a valid redirect for public clients.
	public const string RedirectUri = "http://localhost/gambit-oauth";

	const string Scopes = "board:play challenge:read challenge:write";

	// In-flight PKCE state for the one login attempt (single session, single flow).
	static string _verifier;
	static string _state;

	/// <summary>True once <see cref="Start"/> has produced a URL and we're waiting
	/// for the user to paste the redirect back.</summary>
	public static bool Pending => _verifier != null;

	/// <summary>Begin a login: returns the authorize URL to open in a browser
	/// (also worth copying to the clipboard for the user).</summary>
	public static string Start()
	{
		_verifier = RandomUrlToken( 64 );
		_state = RandomUrlToken( 16 );
		var challenge = Base64Url( Sha256( Encoding.UTF8.GetBytes( _verifier ) ) );

		return "https://lichess.org/oauth"
			+ "?response_type=code"
			+ "&client_id=" + Uri.EscapeDataString( ClientId )
			+ "&redirect_uri=" + Uri.EscapeDataString( RedirectUri )
			+ "&scope=" + Uri.EscapeDataString( Scopes )
			+ "&code_challenge_method=S256"
			+ "&code_challenge=" + challenge
			+ "&state=" + _state;
	}

	/// <summary>Abandon a pending login (user closed the modal, etc.).</summary>
	public static void Cancel()
	{
		_verifier = null;
		_state = null;
	}

	/// <summary>Finish a login from the pasted redirect URL (or bare code).
	/// Exchanges the code and stores the token. Returns (ok, error-for-the-user).</summary>
	public static async Task<(bool ok, string error)> Complete( string pastedRedirect )
	{
		if ( _verifier == null )
			return (false, "Start the sign-in first.");

		var (code, state, err) = ParseRedirect( pastedRedirect );
		if ( err != null ) return (false, err);
		if ( state != null && state != _state )
			return (false, "That code is from a different sign-in attempt — start again.");

		// Exchange the authorization code for an access token.
		var body =
			"grant_type=authorization_code"
			+ "&code=" + Uri.EscapeDataString( code )
			+ "&code_verifier=" + Uri.EscapeDataString( _verifier )
			+ "&redirect_uri=" + Uri.EscapeDataString( RedirectUri )
			+ "&client_id=" + Uri.EscapeDataString( ClientId );

		var res = await LichessApi.PostForm( "/api/token", body );
		if ( !res.Ok )
		{
			// lichess returns 400 with {"error":...} on a bad/expired/mismatched code.
			Log.Warning( $"[Gambit] OAuth token exchange failed ({res.Status}): {LichessApi.Truncate( res.Body, 200 )}" );
			return (false, res.Status == 400
				? "lichess rejected the code (expired or mismatched) — sign in again."
				: res.Error ?? "Token exchange failed.");
		}

		var token = LichessApi.Deserialize<LichessTokenResponse>( res.Body )?.access_token;
		if ( string.IsNullOrEmpty( token ) )
			return (false, "lichess didn't return a token — try again.");

		// Converge with the paste flow: validate via /api/account + persist.
		Cancel();
		return await LichessAuth.SignInWithToken( token );
	}

	// ── Redirect parsing ──

	/// <summary>Pull <c>code</c> (and <c>state</c>) from a pasted redirect URL, or
	/// accept a bare code. lichess appends <c>?error=access_denied</c> if the user
	/// declines.</summary>
	static (string code, string state, string error) ParseRedirect( string pasted )
	{
		pasted = pasted?.Trim();
		if ( string.IsNullOrEmpty( pasted ) )
			return (null, null, "Paste the address-bar URL (or the code) here.");

		// No query at all → treat the whole paste as a bare code.
		int q = pasted.IndexOf( '?' );
		if ( q < 0 && !pasted.Contains( '=' ) )
			return (pasted, null, null);

		string query = q >= 0 ? pasted[(q + 1)..] : pasted;
		string code = null, state = null, error = null;
		foreach ( var pair in query.Split( '&' ) )
		{
			int eq = pair.IndexOf( '=' );
			if ( eq < 0 ) continue;
			var key = pair[..eq];
			var val = Uri.UnescapeDataString( pair[(eq + 1)..] );
			if ( key == "code" ) code = val;
			else if ( key == "state" ) state = val;
			else if ( key == "error" ) error = val;
		}

		if ( error != null )
			return (null, null, error == "access_denied"
				? "You declined the authorization on lichess."
				: $"lichess returned an error: {error}");
		if ( string.IsNullOrEmpty( code ) )
			return (null, null, "Couldn't find a code in that — copy the whole address-bar URL.");
		return (code, state, null);
	}

	// ── PKCE primitives ──

	// Unreserved chars for a PKCE verifier / state (RFC 7636 / 3986).
	const string TokenChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

	static string RandomUrlToken( int length )
	{
		var sb = new StringBuilder( length );
		for ( int i = 0; i < length; i++ )
			sb.Append( TokenChars[System.Random.Shared.Next( TokenChars.Length )] );
		return sb.ToString();
	}

	// base64url alphabet (RFC 4648 §5): '+'→'-', '/'→'_'.
	const string B64Url = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

	/// <summary>base64url without padding (RFC 4648 §5) — PKCE challenge encoding.
	/// Hand-rolled (not <c>Convert.ToBase64String</c>) so the whole OAuth module
	/// leans only on primitives already proven in the repo; verified on the dev
	/// host against the framework encoder.</summary>
	static string Base64Url( byte[] d )
	{
		var sb = new StringBuilder( (d.Length + 2) / 3 * 4 );
		int i = 0;
		for ( ; i + 3 <= d.Length; i += 3 )
		{
			int n = (d[i] << 16) | (d[i + 1] << 8) | d[i + 2];
			sb.Append( B64Url[(n >> 18) & 63] ).Append( B64Url[(n >> 12) & 63] )
			  .Append( B64Url[(n >> 6) & 63] ).Append( B64Url[n & 63] );
		}
		int rem = d.Length - i;
		if ( rem == 1 )
		{
			int n = d[i] << 16;
			sb.Append( B64Url[(n >> 18) & 63] ).Append( B64Url[(n >> 12) & 63] );
		}
		else if ( rem == 2 )
		{
			int n = (d[i] << 16) | (d[i + 1] << 8);
			sb.Append( B64Url[(n >> 18) & 63] ).Append( B64Url[(n >> 12) & 63] ).Append( B64Url[(n >> 6) & 63] );
		}
		return sb.ToString();
	}

	/// <summary>SHA-256 (FIPS 180-4), hand-rolled to avoid the crypto whitelist.
	/// Verified on the dev host against System.Security.Cryptography and the RFC
	/// 7636 PKCE vector.</summary>
	public static byte[] Sha256( byte[] message )
	{
		// Round constants (first 32 bits of the fractional parts of the cube roots
		// of the first 64 primes).
		uint[] k =
		{
			0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
			0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
			0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
			0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
			0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
			0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
			0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
			0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
		};

		// Initial hash values: first 32 bits of the fractional parts of the square
		// roots of the first 8 primes (2,3,5,7,11,13,17,19).
		uint h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a;
		uint h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;

		// Pre-processing: append 0x80, pad with zeros, then the 64-bit bit length.
		long bitLen = (long)message.Length * 8;
		int padded = message.Length + 1;
		while ( padded % 64 != 56 ) padded++;
		var data = new byte[padded + 8];
		for ( int i = 0; i < message.Length; i++ ) data[i] = message[i];
		data[message.Length] = 0x80;
		for ( int i = 0; i < 8; i++ )
			data[data.Length - 1 - i] = (byte)(bitLen >> (8 * i));

		var w = new uint[64];
		for ( int chunk = 0; chunk < data.Length; chunk += 64 )
		{
			for ( int i = 0; i < 16; i++ )
				w[i] = (uint)(data[chunk + i * 4] << 24 | data[chunk + i * 4 + 1] << 16
					| data[chunk + i * 4 + 2] << 8 | data[chunk + i * 4 + 3]);
			for ( int i = 16; i < 64; i++ )
			{
				uint s0 = Ror( w[i - 15], 7 ) ^ Ror( w[i - 15], 18 ) ^ (w[i - 15] >> 3);
				uint s1 = Ror( w[i - 2], 17 ) ^ Ror( w[i - 2], 19 ) ^ (w[i - 2] >> 10);
				w[i] = w[i - 16] + s0 + w[i - 7] + s1;
			}

			uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, hh = h7;
			for ( int i = 0; i < 64; i++ )
			{
				uint S1 = Ror( e, 6 ) ^ Ror( e, 11 ) ^ Ror( e, 25 );
				uint ch = (e & f) ^ (~e & g);
				uint t1 = hh + S1 + ch + k[i] + w[i];
				uint S0 = Ror( a, 2 ) ^ Ror( a, 13 ) ^ Ror( a, 22 );
				uint maj = (a & b) ^ (a & c) ^ (b & c);
				uint t2 = S0 + maj;
				hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
			}

			h0 += a; h1 += b; h2 += c; h3 += d; h4 += e; h5 += f; h6 += g; h7 += hh;
		}

		var hash = new byte[32];
		WriteBe( hash, 0, h0 ); WriteBe( hash, 4, h1 ); WriteBe( hash, 8, h2 ); WriteBe( hash, 12, h3 );
		WriteBe( hash, 16, h4 ); WriteBe( hash, 20, h5 ); WriteBe( hash, 24, h6 ); WriteBe( hash, 28, h7 );
		return hash;
	}

	static uint Ror( uint x, int n ) => (x >> n) | (x << (32 - n));

	static void WriteBe( byte[] dst, int at, uint v )
	{
		dst[at] = (byte)(v >> 24);
		dst[at + 1] = (byte)(v >> 16);
		dst[at + 2] = (byte)(v >> 8);
		dst[at + 3] = (byte)v;
	}
}
