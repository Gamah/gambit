using System;

namespace Gambit.Api.Lichess;

/// <summary>
/// SHA-256, hand-rolled, for one caller: PKCE's S256 code challenge.
///
/// <para><b>Why not <c>System.Security.Cryptography.SHA256</c>?</b> Because we
/// cannot check. <c>System.Security.Cryptography</c> is assembly-whitelisted in
/// the engine's <c>AccessRules.Assemblies</c>, but assembly-level whitelisting
/// does not prove the member-level ACL allows a given static — and this host has
/// no s&amp;box toolchain to try it on. A blocked call would be an SB1000 at
/// hotload, on the one code path that gates the entire link flow.</para>
///
/// <para>Hand-rolling costs ~70 lines that are <b>Sandbox-free and therefore
/// provable right here</b> — <c>scripts/lichess_harness/</c> runs this against
/// RFC 6234's vectors and RFC 7636's worked PKCE example, which is strictly more
/// verification than "the engine probably allows it". It is the same trade the
/// vendored chess rules already make.</para>
///
/// <para><b>Not a general-purpose crypto primitive.</b> It hashes a short ASCII
/// verifier once per link. It is not constant-time, not streaming, and has no
/// business being reached for by anything else — if a second caller ever appears,
/// that is the moment to find out whether the engine's own SHA-256 is callable.
/// Note also what PKCE actually needs from it: pre-image resistance on a value
/// the client generated and lichess never sees. The spec is explicit that a
/// fully client-side app cannot hide its verifier from its own user anyway.</para>
///
/// <para>Straight from FIPS 180-4. No allocations beyond the message schedule and
/// the padded block buffer.</para>
/// </summary>
public static class Sha256
{
	// The first 32 bits of the fractional parts of the cube roots of the first 64
	// primes. FIPS 180-4 §4.2.2.
	static readonly uint[] K =
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

	/// <summary>The 32-byte digest of <paramref name="message"/>.</summary>
	public static byte[] HashData( ReadOnlySpan<byte> message )
	{
		// Initial hash: the first 32 bits of the fractional parts of the square
		// roots of the first 8 primes. FIPS 180-4 §5.3.3.
		uint h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a;
		uint h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;

		// Pad: 0x80, then zeros, then the length in BITS as a big-endian ulong,
		// to a multiple of 64 bytes.
		int total = message.Length + 1 + 8;
		int padded = ( total + 63 ) / 64 * 64;
		var buf = new byte[padded];
		message.CopyTo( buf );
		buf[message.Length] = 0x80;
		ulong bits = (ulong)message.Length * 8;
		for ( int i = 0; i < 8; i++ )
			buf[padded - 1 - i] = (byte)( bits >> ( 8 * i ) );

		var w = new uint[64];
		for ( int off = 0; off < padded; off += 64 )
		{
			for ( int i = 0; i < 16; i++ )
			{
				int j = off + i * 4;
				w[i] = (uint)( ( buf[j] << 24 ) | ( buf[j + 1] << 16 ) | ( buf[j + 2] << 8 ) | buf[j + 3] );
			}
			for ( int i = 16; i < 64; i++ )
			{
				uint s0 = Ror( w[i - 15], 7 ) ^ Ror( w[i - 15], 18 ) ^ ( w[i - 15] >> 3 );
				uint s1 = Ror( w[i - 2], 17 ) ^ Ror( w[i - 2], 19 ) ^ ( w[i - 2] >> 10 );
				w[i] = unchecked( w[i - 16] + s0 + w[i - 7] + s1 );
			}

			uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
			for ( int i = 0; i < 64; i++ )
			{
				uint s1 = Ror( e, 6 ) ^ Ror( e, 11 ) ^ Ror( e, 25 );
				uint ch = ( e & f ) ^ ( ~e & g );
				uint t1 = unchecked( h + s1 + ch + K[i] + w[i] );
				uint s0 = Ror( a, 2 ) ^ Ror( a, 13 ) ^ Ror( a, 22 );
				uint maj = ( a & b ) ^ ( a & c ) ^ ( b & c );
				uint t2 = unchecked( s0 + maj );

				h = g; g = f; f = e;
				e = unchecked( d + t1 );
				d = c; c = b; b = a;
				a = unchecked( t1 + t2 );
			}

			unchecked
			{
				h0 += a; h1 += b; h2 += c; h3 += d;
				h4 += e; h5 += f; h6 += g; h7 += h;
			}
		}

		var outp = new byte[32];
		Write( outp, 0, h0 ); Write( outp, 4, h1 ); Write( outp, 8, h2 ); Write( outp, 12, h3 );
		Write( outp, 16, h4 ); Write( outp, 20, h5 ); Write( outp, 24, h6 ); Write( outp, 28, h7 );
		return outp;
	}

	static uint Ror( uint x, int n ) => ( x >> n ) | ( x << ( 32 - n ) );

	static void Write( byte[] dst, int at, uint v )
	{
		dst[at] = (byte)( v >> 24 );
		dst[at + 1] = (byte)( v >> 16 );
		dst[at + 2] = (byte)( v >> 8 );
		dst[at + 3] = (byte)v;
	}
}
