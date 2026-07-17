using System.Text;

namespace Gambit.Chess;

/// <summary>
/// Turns a SAN move (<c>"Nf3"</c>, <c>"exd5+"</c>, <c>"O-O"</c>, <c>"e8=Q#"</c>) into a
/// phrase a speech synthesiser reads naturally (M12) — "knight f 3", "e takes d 5 check",
/// "castles kingside", "e 8 promotes to queen checkmate".
///
/// <para>Lives under <c>Code/Chess</c> (with <see cref="BoardDiff"/> and
/// <see cref="CapturedMaterial"/>) for one reason: it is pure string logic with no Sandbox
/// dependency, so it can be exercised in a plain dotnet harness on a host with no s&amp;box.
/// The reading of a promotion or a disambiguated capture is exactly the kind of thing that
/// looks obviously right and isn't.</para>
///
/// <para>Files are spelt as their letter and ranks as their digit, each its own token, so
/// the synthesiser says "f three" rather than trying to read "f3" as a word. Everything the
/// grammar can't place (a stray character) is dropped rather than spoken literally.</para>
/// </summary>
public static class MoveSpeech
{
	public static string Spoken( string san )
	{
		if ( string.IsNullOrWhiteSpace( san ) ) return "";
		san = san.Trim();

		// Castling first — the O-O / O-O-O forms don't fit the per-character mapping, and
		// they may carry a trailing check/mate marker. Accept zeros as well as letter O.
		var norm = san.Replace( '0', 'O' );
		if ( norm.StartsWith( "O-O-O" ) ) return "castles queenside" + Suffix( san );
		if ( norm.StartsWith( "O-O" ) ) return "castles kingside" + Suffix( san );

		var sb = new StringBuilder();
		foreach ( char c in san )
		{
			string tok = c switch
			{
				'N' => "knight",
				'B' => "bishop",   // uppercase B is a bishop; lowercase b is the b-file below
				'R' => "rook",
				'Q' => "queen",
				'K' => "king",
				'x' => "takes",
				'=' => "promotes to",
				'+' => "check",
				'#' => "checkmate",
				>= 'a' and <= 'h' => c.ToString(),
				>= '1' and <= '8' => c.ToString(),
				_ => null,          // '-', stray annotation glyphs, etc. — drop it
			};
			if ( tok == null ) continue;
			if ( sb.Length > 0 ) sb.Append( ' ' );
			sb.Append( tok );
		}
		return sb.ToString();
	}

	// The check / checkmate tail of a castling move, spoken.
	static string Suffix( string san )
	{
		if ( san.EndsWith( "#" ) ) return " checkmate";
		if ( san.EndsWith( "+" ) ) return " check";
		return "";
	}
}
