// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

namespace Chess;

internal class FenBoardBuilder
{
    private readonly Piece?[,] pieces;

    /// <summary>
    /// "Begin Situation"
    /// </summary>
    internal Piece?[,] Pieces => (Piece?[,])pieces.Clone();

    internal PieceColor Turn { get; private set; }

    internal bool CastleWK { get; private set; }
    internal bool CastleWQ { get; private set; }
    internal bool CastleBK { get; private set; }
    internal bool CastleBQ { get; private set; }

    internal Position EnPassant { get; private set; }

    /// <summary>
    /// Count since the last pawn advance or piece capture
    /// </summary>
    internal int HalfMoves { get; private set; }

    /// <summary>
    /// Black moves Count
    /// </summary>
    internal int FullMoves { get; private set; }

    internal Piece[] WhiteCaptured { get; private set; }
    internal Piece[] BlackCaptured { get; private set; }

    private FenBoardBuilder(Piece?[,] pieces)
    {
        this.pieces = pieces;
    }

    private FenBoardBuilder()
    {
        pieces = new Piece[8, 8];
        EnPassant = new Position();
    }

    internal static (bool succeeded, ChessException? exception) TryLoad(string fen, out FenBoardBuilder? builder)
    {
        builder = null;

        // GAMBIT VENDOR PATCH (s&box whitelist): regex capture groups replaced
        // by Regexes.TryParseFen — same fields, same accept/reject set.
        if (fen is null || !Regexes.TryParseFen(fen, out var parts))
            return (false, new ChessArgumentException(null, "FEN board string should match pattern: " + Regexes.FenPattern));

        if (!Regexes.PlacementHasExactlyOneKing(parts.Placement, 'K') || !Regexes.PlacementHasExactlyOneKing(parts.Placement, 'k'))
            return (false, new ChessArgumentException(null, "Chess board should have exact 1 white king and exact 1 black king"));

        builder = new FenBoardBuilder();

        PlacePiecesOnBoard(builder, parts.Placement);

        builder.Turn = PieceColor.FromChar(parts.Turn);

        if (parts.Castling != "-")
        {
            if (parts.Castling.Contains('K'))
                builder.CastleWK = true;
            if (parts.Castling.Contains('Q'))
                builder.CastleWQ = true;
            if (parts.Castling.Contains('k'))
                builder.CastleBK = true;
            if (parts.Castling.Contains('q'))
                builder.CastleBQ = true;
        }

        if (parts.EnPassant != "-")
        {
            builder.EnPassant = new Position(parts.EnPassant);
        }

        (builder.HalfMoves, builder.FullMoves) = parts.Counters.Split(' ').Select(int.Parse).ToArray();

        AddCapturedPieces(builder);

        return (true, null);
    }

    private static void PlacePiecesOnBoard(FenBoardBuilder builder, string piecesSpan)
    {
        int x = 0, y = 7;
        foreach (var fenChar in piecesSpan)
        {
            if (fenChar == '/')
            {
                y--;
                x = 0;
            }
            else if (x < 8)
            {
                if (char.IsLetter(fenChar))
                {
                    builder.pieces[y, x] = new Piece(fenChar);
                    x++;
                }
                else if (char.IsDigit(fenChar))
                {
                    x += fenChar - '0';
                }
            }
        }
    }

    private static void AddCapturedPieces(FenBoardBuilder builder)
    {
        var whiteCaptured = new List<Piece>();
        var blackCaptured = new List<Piece>();
        var counts = (white: new Dictionary<PieceType, int>
                { { PieceType.Pawn, 0 }, { PieceType.Rook, 0 }, { PieceType.Bishop, 0 }, { PieceType.Knight, 0 }, { PieceType.Queen, 0 }, {PieceType.King , 0} },
            black: new Dictionary<PieceType, int>
                { { PieceType.Pawn, 0 }, { PieceType.Rook, 0 }, { PieceType.Bishop, 0 }, { PieceType.Knight, 0 }, { PieceType.Queen, 0 }, {PieceType.King , 0} });

        foreach (var piece in builder.pieces!.PiecesList())
        {
            if (piece.Color == PieceColor.White)
            {
                counts.white[piece.Type]++;
            }
            else
            {
                counts.black[piece.Type]++;
            }
        }

        AddPiecesToList(whiteCaptured, PieceColor.White, PieceType.Pawn, counts.white[PieceType.Pawn], 8);
        AddPiecesToList(whiteCaptured, PieceColor.White, PieceType.Rook, counts.white[PieceType.Rook], 2);
        AddPiecesToList(whiteCaptured, PieceColor.White, PieceType.Bishop, counts.white[PieceType.Bishop], 2);
        AddPiecesToList(whiteCaptured, PieceColor.White, PieceType.Knight, counts.white[PieceType.Knight], 2);
        AddPiecesToList(whiteCaptured, PieceColor.White, PieceType.Queen, counts.white[PieceType.Queen], 1);

        AddPiecesToList(blackCaptured, PieceColor.Black, PieceType.Pawn, counts.black[PieceType.Pawn], 8);
        AddPiecesToList(blackCaptured, PieceColor.Black, PieceType.Rook, counts.black[PieceType.Rook], 2);
        AddPiecesToList(blackCaptured, PieceColor.Black, PieceType.Bishop, counts.black[PieceType.Bishop], 2);
        AddPiecesToList(blackCaptured, PieceColor.Black, PieceType.Knight, counts.black[PieceType.Knight], 2);
        AddPiecesToList(blackCaptured, PieceColor.Black, PieceType.Queen, counts.black[PieceType.Queen], 1);

        builder.WhiteCaptured = whiteCaptured.ToArray();
        builder.BlackCaptured = blackCaptured.ToArray();
    }

    private static void AddPiecesToList(List<Piece> captured, PieceColor color, PieceType type, int actualCount, int targetCount)
    {
        for (int i = actualCount; i < targetCount; i++)
        {
            captured.Add(new Piece(color, type));
        }
    }

    internal static FenBoardBuilder Load(ChessBoard board)
    {
        return new FenBoardBuilder(board.pieces)
        {
            Turn = board.Turn,
            CastleWK = ChessBoard.HasRightToCastle(PieceColor.White, CastleType.King, board),
            CastleWQ = ChessBoard.HasRightToCastle(PieceColor.White, CastleType.Queen, board),
            CastleBK = ChessBoard.HasRightToCastle(PieceColor.Black, CastleType.King, board),
            CastleBQ = ChessBoard.HasRightToCastle(PieceColor.Black, CastleType.Queen, board),
            EnPassant = ChessBoard.LastMoveEnPassantPosition(board),
            HalfMoves = board.GetHalfMovesCount(),
            FullMoves = board.GetFullMovesCount()
        };
    }

    public override string ToString()
    {
        return string.Join(' ',
            GetPiecePlacement(),
            GetActiveColor(),
            GetCastlingAvailability(),
            GetEnPassantTargetSquare(),
            HalfMoves.ToString(),
            FullMoves.ToString());
    }

    private string GetPiecePlacement()
    {
        // GAMBIT VENDOR PATCH (s&box whitelist): stackalloc → StringBuilder.
        var builder = new StringBuilder(71); // Max length is 71

        for (int i = 7; i >= 0; i--)
        {
            int emptySquaresCount = 0;

            for (int j = 0; j < 8; j++)
            {
                if (pieces[i, j] is null)
                    emptySquaresCount++;
                else
                {
                    if (emptySquaresCount > 0)
                    {
                        builder.Append((char)('0' + emptySquaresCount));
                        emptySquaresCount = 0;
                    }

                    builder.Append(pieces[i, j]!.ToFenChar());
                }
            }

            if (emptySquaresCount > 0)
                builder.Append((char)('0' + emptySquaresCount));

            if (i > 0)
                builder.Append('/');
        }

        return builder.ToString();
    }

    private string GetActiveColor()
    {
        return Turn.AsChar.ToString();
    }

    private string GetCastlingAvailability()
    {
        // GAMBIT VENDOR PATCH (s&box whitelist): stackalloc → StringBuilder.
        var builder = new StringBuilder(4);

        if (CastleWK) builder.Append('K');
        if (CastleWQ) builder.Append('Q');
        if (CastleBK) builder.Append('k');
        if (CastleBQ) builder.Append('q');

        // Castling not available
        if (builder.Length == 0) builder.Append('-');

        return builder.ToString();
    }

    private string GetEnPassantTargetSquare()
    {
        if (EnPassant.HasValue)
            return EnPassant.ToString();

        return "-";
    }
}