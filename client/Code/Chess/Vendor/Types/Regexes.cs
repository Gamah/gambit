// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

// GAMBIT VENDOR PATCH (s&box API whitelist, CLAUDE.md D2):
// System.Text.RegularExpressions is not whitelisted in s&box game code, so the
// compiled Regex fields are replaced by hand-written parsers with the same
// accept/reject semantics and "capture groups" as the original patterns. The
// original pattern strings are kept as consts — they document the grammar and
// still appear in exception messages.

using System.Collections.Generic;

namespace Chess;

internal static class Regexes
{
    internal const string SanOneMovePattern = @"(^([PNBRQK])?([a-h])?([1-8])?(x|X|-)?([a-h][1-8])(=[NBRQ]| ?e\.p\.)?|^O-O(-O)?)(\+|\#|\$)?$";

    internal const string SanMovesPattern = @"(?:[PNBRQK]?[a-h]?[1-8]?[xX-]?[a-h][1-8](?:=[NBRQ]| ?e\.p\.)?|O-O(?:-O)?)[+#$]?";

    internal const string HeadersPattern = @"\[([^ ]+) ""([^""]*)""\]";

    internal const string FenPattern = @"^(((?:[rnbqkpRNBQKP1-8]+\/){7})[rnbqkpRNBQKP1-8]+) ([bw]) (-|[KQkq]{1,4}) (-|[a-h][36]) (\d+ \d+)$";

    internal const string PiecePattern = "^[wb][bknpqr]$";

    internal const string FenPiecePattern = "^[bknpqrBKNPQR]$";

    internal const string PositionPattern = "^[a-h][1-8]$";

    internal const string MovePattern = @"^{(([wb][bknpqr]) - )?([a-h][1-8]) - ([a-h][1-8])( - ([wb][bknpqr]))?( - (o-o|o-o-o|e\.p\.|=|=q|=r|=b|=n))?( - ([+#$]))?}$";

    // ── Character classes ──

    static bool IsFile(char c) => c >= 'a' && c <= 'h';
    static bool IsRank(char c) => c >= '1' && c <= '8';
    static bool IsSanPieceChar(char c) => c is 'P' or 'N' or 'B' or 'R' or 'Q' or 'K';
    static bool IsCheckChar(char c) => c is '+' or '#' or '$';

    internal static bool IsValidPosition(string s) =>
        s is { Length: 2 } && IsFile(s[0]) && IsRank(s[1]);

    internal static bool IsValidPieceString(string s) =>
        s is { Length: 2 } && (s[0] == 'w' || s[0] == 'b') && "bknpqr".IndexOf(s[1]) >= 0;

    internal static bool IsValidFenPieceChar(char c) => "bknpqrBKNPQR".IndexOf(c) >= 0;

    // ── SAN single move (SanOneMovePattern) ──

    /// <summary>Deconstructed SAN move — each field mirrors the same-numbered
    /// capture group of <see cref="SanOneMovePattern"/> ('\0'/null = absent).</summary>
    internal struct SanParts
    {
        public string Castle;       // group 1 when castling: "O-O" / "O-O-O"
        public char PieceChar;      // group 2: PNBRQK
        public char FromFile;       // group 3: a-h
        public char FromRank;       // group 4: 1-8
        public char Separator;      // group 5: x / X / -
        public string TargetSquare; // group 6: e.g. "e4"
        public string Suffix;       // group 7: "=Q".."=N" or "e.p." (pre-trimmed)
        public char CheckChar;      // group 9: + / # / $
    }

    /// <summary>Hand-rolled equivalent of SanOneMovePattern. Parses from the
    /// ends inward (check char, then suffix, then target square), which
    /// resolves the prefix ambiguity the regex handled by backtracking
    /// (in "e4" the 'e' is the target file, not a disambiguation file).</summary>
    internal static bool TryParseSanMove(string san, out SanParts parts)
    {
        parts = default;
        if (string.IsNullOrEmpty(san))
            return false;

        // Optional trailing check/mate char
        if (IsCheckChar(san[^1]))
        {
            parts.CheckChar = san[^1];
            san = san[..^1];
        }

        if (san is "O-O" or "O-O-O")
        {
            parts.Castle = san;
            return true;
        }

        // Optional promotion "=X" or en-passant "e.p."/" e.p." suffix
        if (san.Length >= 2 && san[^2] == '=' && "NBRQ".IndexOf(san[^1]) >= 0)
        {
            parts.Suffix = san[^2..];
            san = san[..^2];
        }
        else if (san.EndsWith("e.p."))
        {
            parts.Suffix = "e.p.";
            san = san[..^4].TrimEnd(' ');
        }

        // Mandatory target square
        if (san.Length < 2 || !IsFile(san[^2]) || !IsRank(san[^1]))
            return false;
        parts.TargetSquare = san[^2..];
        san = san[..^2];

        // Optional prefix, strictly in order: piece, from-file, from-rank, separator
        int i = 0;
        if (i < san.Length && IsSanPieceChar(san[i]))
            parts.PieceChar = san[i++];
        if (i < san.Length && IsFile(san[i]))
            parts.FromFile = san[i++];
        if (i < san.Length && IsRank(san[i]))
            parts.FromRank = san[i++];
        if (i < san.Length && (san[i] is 'x' or 'X' or '-'))
            parts.Separator = san[i++];

        return i == san.Length; // everything must be consumed
    }

    // ── FEN (FenPattern + one-king-per-side checks) ──

    /// <summary>Deconstructed FEN — each field mirrors the same-numbered capture
    /// group of <see cref="FenPattern"/> (group 2 was regex-internal).</summary>
    internal struct FenParts
    {
        public string Placement; // group 1: piece placement, 8 '/'-separated ranks
        public char Turn;        // group 3: w / b
        public string Castling;  // group 4: "-" or 1-4 of KQkq
        public string EnPassant; // group 5: "-" or [a-h][36]
        public string Counters;  // group 6: "halfmoves fullmoves"
    }

    internal static bool TryParseFen(string fen, out FenParts parts)
    {
        parts = default;
        if (string.IsNullOrEmpty(fen))
            return false;

        var fields = fen.Split(' ');
        if (fields.Length != 6)
            return false;

        // Placement: exactly 8 ranks of [rnbqkpRNBQKP1-8]+ (the regex validated
        // the charset and slash count, not per-rank square sums — same here)
        var ranks = fields[0].Split('/');
        if (ranks.Length != 8)
            return false;
        foreach (var rank in ranks)
        {
            if (rank.Length == 0)
                return false;
            foreach (var c in rank)
                if (!IsValidFenPieceChar(c) && c is not (>= '1' and <= '8'))
                    return false;
        }

        if (fields[1].Length != 1 || (fields[1][0] != 'w' && fields[1][0] != 'b'))
            return false;

        if (fields[2] != "-")
        {
            if (fields[2].Length is < 1 or > 4)
                return false;
            foreach (var c in fields[2])
                if ("KQkq".IndexOf(c) < 0)
                    return false;
        }

        if (fields[3] != "-"
            && !(fields[3].Length == 2 && IsFile(fields[3][0]) && fields[3][1] is '3' or '6'))
            return false;

        foreach (var field in new[] { fields[4], fields[5] })
        {
            if (field.Length == 0)
                return false;
            foreach (var c in field)
                if (!char.IsDigit(c))
                    return false;
        }

        parts.Placement = fields[0];
        parts.Turn = fields[1][0];
        parts.Castling = fields[2];
        parts.EnPassant = fields[3];
        parts.Counters = fields[4] + " " + fields[5];
        return true;
    }

    /// <summary>Replaces the FenContainsOne{White,Black}King regexes: exactly
    /// one king of the given FEN char in the placement field.</summary>
    internal static bool PlacementHasExactlyOneKing(string placement, char kingChar)
    {
        int count = 0;
        foreach (var c in placement)
            if (c == kingChar)
                count++;
        return count == 1;
    }

    // ── Long move notation (MovePattern) — see Move(string) ──

    /// <summary>Splits "{wp - e4 - e5 - bp - e.p. - +}" into its " - "-separated
    /// tokens (2 to 6 of them), or null if the braces/shape are wrong. Token
    /// validation stays in Move(string), mirroring the original group switch.</summary>
    internal static string[] SplitLongMove(string move)
    {
        if (move.Length < 2 || move[0] != '{' || move[^1] != '}')
            return null;

        var tokens = move[1..^1].Split(" - ");
        return tokens.Length is >= 2 and <= 6 ? tokens : null;
    }

    internal static bool IsValidMoveParameterString(string s) =>
        s is "o-o" or "o-o-o" or "e.p." or "=" or "=q" or "=r" or "=b" or "=n";

    // ── PGN helpers (HeadersPattern / CommentsPattern / AlternativesPattern /
    //    SanMovesPattern as used by PgnBuilder) ──

    /// <summary>Extracts all [Name "Value"] headers and returns the PGN with
    /// them removed — one pass replacing both RegexHeaders.Matches and
    /// RegexHeaders.Replace in PgnBuilder.</summary>
    internal static string ExtractHeaders(string pgn, List<KeyValuePair<string, string>> headers)
    {
        var remainder = new StringBuilder(pgn.Length);

        int i = 0;
        while (i < pgn.Length)
        {
            if (pgn[i] == '[' && TryMatchHeaderAt(pgn, i, out var name, out var value, out int end))
            {
                headers.Add(new KeyValuePair<string, string>(name, value));
                i = end;
            }
            else
            {
                remainder.Append(pgn[i]);
                i++;
            }
        }

        return remainder.ToString();
    }

    /// <summary>[Name "Value"] at <paramref name="start"/>: name is 1+ chars
    /// without space/quote/bracket, one space, quoted value, ']'.</summary>
    static bool TryMatchHeaderAt(string pgn, int start, out string name, out string value, out int end)
    {
        name = value = null;
        end = start;

        int i = start + 1;
        int nameStart = i;
        while (i < pgn.Length && pgn[i] != ' ' && pgn[i] != '"' && pgn[i] != ']' && pgn[i] != '\n')
            i++;
        if (i >= pgn.Length || pgn[i] != ' ' || i == nameStart)
            return false;
        name = pgn[nameStart..i];
        i++; // the space

        if (i >= pgn.Length || pgn[i] != '"')
            return false;
        i++;
        int valueStart = i;
        while (i < pgn.Length && pgn[i] != '"')
            i++;
        if (i >= pgn.Length)
            return false;
        value = pgn[valueStart..i];
        i++; // closing quote

        if (i >= pgn.Length || pgn[i] != ']')
            return false;
        end = i + 1;
        return true;
    }

    /// <summary>Removes {...} comments (CommentsPattern) or (...) alternative
    /// branches (AlternativesPattern). Runs innermost-out so nested variations
    /// are fully removed.</summary>
    internal static string StripDelimited(string pgn, char open, char close)
    {
        var builder = new StringBuilder(pgn);
        bool removed = true;

        while (removed)
        {
            removed = false;
            int openIndex = -1;
            for (int i = 0; i < builder.Length; i++)
            {
                if (builder[i] == open)
                    openIndex = i;
                else if (builder[i] == close && openIndex >= 0)
                {
                    builder.Remove(openIndex, i - openIndex + 1);
                    removed = true;
                    break;
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>Replaces RegexSanMoves.Matches over cleaned movetext: splits on
    /// whitespace, strips move numbers ("1." / "23..." — also fused, "1.e4")
    /// and trailing !? annotations, and keeps tokens that parse as SAN.</summary>
    internal static List<string> ExtractSanMoves(string movetext)
    {
        var moves = new List<string>();

        foreach (var raw in movetext.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw;

            int i = 0;
            while (i < token.Length && char.IsDigit(token[i]))
                i++;
            if (i > 0 && i < token.Length && token[i] == '.')
            {
                while (i < token.Length && token[i] == '.')
                    i++;
                token = token[i..];
            }
            else if (i == token.Length)
            {
                continue; // bare move number
            }

            token = token.TrimEnd('!', '?');

            if (token.Length == 0 || token is "1-0" or "0-1" or "1/2-1/2" or "*")
                continue;

            if (TryParseSanMove(token, out _))
                moves.Add(token);
        }

        return moves;
    }
}
