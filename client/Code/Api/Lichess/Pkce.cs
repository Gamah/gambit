using System;
using System.Security.Cryptography;
using System.Text;

namespace Gambit.Api.Lichess;

/// <summary>
/// PKCE (RFC 7636), client side.
///
/// <para><b>This is the whole reason gamchess can no longer become a custodian by
/// accident.</b> The verifier is minted here and never leaves this machine; only
/// its SHA-256 challenge is registered with gamchess, and only the code comes
/// back. A code without its verifier cannot be exchanged, so the parking slot on
/// the server holds nothing that could become a token — not by a bug, not by a
/// database dump, not by a log line.</para>
///
/// <para>Sandbox-free on purpose: <c>scripts/lichess_harness/</c> runs it against
/// RFC 7636's own worked example, which is the same vector the deleted Go
/// <c>oauth_test.go</c> used. That continuity matters — the test moved with the
/// code rather than being lost with it.</para>
///
/// <para><b>The engine's own SHA-256 is used, and that was CHECKED rather than
/// assumed.</b> HTTPFIX.md flagged "is <c>SHA256.HashData</c> callable?" as a
/// day-one unknown and suggested hand-rolling if not — one was written and then
/// deleted, because <c>sbox-public</c>'s
/// <c>engine/Sandbox.Access/Rules/BaseAccess.cs:362</c> whitelists
/// <c>System.Security.Cryptography.SHA256*</c> outright (read 2026-08-05). A
/// whole hash implementation to review is a real cost, and "the assembly is
/// whitelisted but the member ACL might not be" was answerable by reading one
/// file.</para>
///
/// <para><b>It is fine that the player can read their own verifier.</b> The spec
/// says so outright: <i>"it is fine if the user themselves can extract
/// code_verifier, which will always be possible for fully client-side apps."</i>
/// PKCE binds the exchange to whoever started the flow; it is not a secret kept
/// from the user.</para>
/// </summary>
public readonly struct Pkce
{
	/// <summary>The secret. Kept on this machine, sent only to lichess's token
	/// endpoint, never to gamchess.</summary>
	public string Verifier { get; }

	/// <summary>The S256 challenge. Public by construction — it is what goes in a
	/// URL bar.</summary>
	public string Challenge { get; }

	Pkce( string verifier, string challenge )
	{
		Verifier = verifier;
		Challenge = challenge;
	}

	/// <summary>
	/// Mint a fresh pair.
	///
	/// <para>64 random bytes → 86 base64url chars, comfortably inside RFC 7636's
	/// [43, 128] range. The Go version was 32 bytes (43 chars) — exactly the RFC
	/// floor — which sat right on lichess's undocumented CodeVerifierTooShort
	/// threshold; 64 buys margin at zero cost and removes the "if linking fails at
	/// the exchange, suspect this first" footgun. Keep it at 64.</para>
	///
	/// <para><b>Randomness source.</b> <c>System.Random.Shared</c>, and unlike the
	/// hash above this one really is forced: the whitelist takes
	/// <c>SHA256*</c>/<c>SHA1*</c>/<c>MD5*</c>/<c>HashAlgorithm*</c> and <b>nothing
	/// else</b> from <c>System.Security.Cryptography</c> — no
	/// <c>RandomNumberGenerator</c> (checked in <c>BaseAccess.cs:359-363</c>,
	/// 2026-08-05). <c>Random.Shared</c> is what <c>GamchessApi.NewClientGameId</c>
	/// already uses. That
	/// is a real weakening and worth stating: a predictable verifier would let
	/// someone who could also steal the authorization code complete the exchange. But
	/// the code never leaves lichess → our HTTPS callback → this client's own
	/// authenticated collect, and the verifier is a per-link value that lives for
	/// under ten minutes. If a whitelisted CSPRNG ever appears, switch to it — this
	/// is the one line in the flow that would benefit.</para>
	/// </summary>
	public static Pkce New()
	{
		var raw = new byte[64];
		System.Random.Shared.NextBytes( raw );

		string verifier = Base64Url( raw );
		string challenge = Base64Url( SHA256.HashData( Encoding.ASCII.GetBytes( verifier ) ) );
		return new Pkce( verifier, challenge );
	}

	/// <summary>Base64url without padding — RFC 7636's encoding, and the one
	/// lichess compares byte-for-byte. Standard base64 with <c>+</c>, <c>/</c> or
	/// <c>=</c> in it is a failed exchange, not a cosmetic difference.</summary>
	public static string Base64Url( ReadOnlySpan<byte> bytes ) =>
		Convert.ToBase64String( bytes ).TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );
}
