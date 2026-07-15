// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

namespace Chess;

internal static class SanBuilder
{
    public static (bool succeeded, ChessException? exception) TryParse(ChessBoard board, string san, out Move? move, bool resetSan = false)
    {
        move = null;

        // GAMBIT VENDOR PATCH (s&box whitelist): regex capture groups replaced
        // by Regexes.TryParseSanMove — same fields, same accept/reject set.
        if (san is null || !Regexes.TryParseSanMove(san, out var parts))
            return (false, new ChessArgumentException(board, "SAN move string should match pattern: " + Regexes.SanMovesPattern));

        var moveOut = new Move();
        var originalPos = new Position();
        var isCapture = false;

        if (parts.Castle is not null)
        {
            ParseCastling(board, parts.Castle, moveOut, ref originalPos);
        }
        else
        {
            if (parts.PieceChar != '\0')
                moveOut.Piece = new Piece(board.Turn, PieceType.FromChar(parts.PieceChar));
            if (parts.FromFile != '\0')
                originalPos.X = Position.FromFile(parts.FromFile);
            if (parts.FromRank != '\0')
                originalPos.Y = Position.FromRank(parts.FromRank);
            if (parts.Separator is 'x' or 'X')
                isCapture = true;
            moveOut.NewPosition = new Position(parts.TargetSquare);
            if (parts.Suffix is not null)
                moveOut.Parameter = IMoveParameter.FromString(parts.Suffix);
        }

        if (parts.CheckChar != '\0')
            ParseEndgameGroup(parts.CheckChar, moveOut);

        // If piece is not specified => Pawn
        moveOut.Piece ??= new Piece(board.Turn, PieceType.Pawn);

        if (isCapture && board[moveOut.NewPosition] is not null)
            moveOut.CapturedPiece = board[moveOut.NewPosition];

        moveOut.OriginalPosition = originalPos;

        var (succeeded, exception) = ParseOriginalPosition(board, san, moveOut);
        if (!succeeded)
            return (false, exception);

        if (resetSan)
        {
            TryParse(board, moveOut, out _);
        }

        move = moveOut;
        return (true, null);
    }

    private static (bool succeeded, ChessException? exception) ParseOriginalPosition(ChessBoard board, string san, Move move)
    {
        ChessException? GetException(int count, List<Move> moves)
        {
            return count switch
            {
                < 1 => new ChessSanNotFoundException(board, san),
                > 1 => new ChessSanTooAmbiguousException(board, san, moves.ToArray()),
                _ => null
            };
        }

        var ambiguousMoves = GetMovesOfPieceOnPosition(move.Piece, move.NewPosition, board).ToList();

        if (move.OriginalPosition.HasValueX)
            ambiguousMoves.RemoveAll(m => m.OriginalPosition.X != move.OriginalPosition.X);
        if (move.OriginalPosition.HasValueY)
            ambiguousMoves.RemoveAll(m => m.OriginalPosition.Y != move.OriginalPosition.Y);

        if (ambiguousMoves.Count != 1)
            return (false, GetException(ambiguousMoves.Count, ambiguousMoves));

        move.OriginalPosition = ambiguousMoves[0].OriginalPosition;

        // EnPassant
        if (ambiguousMoves[0].Parameter is MoveEnPassant enPassant)
        {
            move.Parameter = enPassant;
            move.CapturedPiece = ambiguousMoves[0].CapturedPiece;
        }

        return (true, null);
    }

    private static void ParseEndgameGroup(char checkChar, Move moveOut)
    {
        switch (checkChar)
        {
            case '+':
                moveOut.IsCheck = true;
                break;
            case '#':
                moveOut.IsCheck = true;
                moveOut.IsMate = true;
                break;
            case '$':
                moveOut.IsMate = true;
                break;
        }
    }

    private static void ParseCastling(ChessBoard board, string castle, Move move, ref Position originalPos)
    {
        move.Parameter = IMoveParameter.FromString(castle);
        if (board.Turn == PieceColor.White)
        {
            originalPos = new Position("e1");
            if (castle == "O-O")
                move.NewPosition = new Position("g1");
            else if (castle == "O-O-O")
                move.NewPosition = new Position("c1");
        }
        else if (board.Turn == PieceColor.Black)
        {
            originalPos = new Position("e8");
            if (castle == "O-O")
                move.NewPosition = new Position("g8");
            else if (castle == "O-O-O")
                move.NewPosition = new Position("c8");
        }
        move.Piece = board[originalPos] ?? new Piece(board.Turn, PieceType.King);
    }

    public static (bool succeeded, ChessException? exception) TryParse(ChessBoard board, Move move, out string? san)
    {
        san = null;

        if (move is not { HasValue: true })
            return (false, new ChessArgumentException(board, "Given move is null or doesn't have valid positions values"));

        // GAMBIT VENDOR PATCH (s&box whitelist): stackalloc span assembly
        // replaced by a StringBuilder — identical output.
        var builder = new StringBuilder(10);

        if (move.Parameter is MoveCastle)
        {
            builder.Append(move.Parameter.ShortStr);
        }
        else
        {
            if (move.Piece.Type != PieceType.Pawn)
            {
                builder.Append(char.ToUpper(move.Piece.Type.AsChar));

                // Only rooks, knights, bishops(second from promotion) and queens(second from promotion) can have ambiguous moves
                if (move.Piece.Type != PieceType.King)
                    builder.Append(HandleAmbiguousMovesNotation(move, board));
            }

            if (move.CapturedPiece is not null)
            {
                if (move.Piece.Type == PieceType.Pawn)
                    builder.Append(move.OriginalPosition.File());

                builder.Append('x');
            }

            // Destination position
            builder.Append(move.NewPosition.ToString());

            if (move.Parameter is MovePromotion)
                builder.Append(move.Parameter.ShortStr);
        }

        if (move.IsCheck && move.IsMate) builder.Append('#');
        else if (move.IsCheck) builder.Append('+');
        else if (move.IsMate) builder.Append('$');

        san = builder.ToString();
        move.San = san;

        return (true, null);
    }

    private static string HandleAmbiguousMovesNotation(Move move, ChessBoard board)
    {
        var ambiguousMoves = GetMovesOfPieceOnPosition(move.Piece, move.NewPosition, board).Where(m => m.OriginalPosition != move.OriginalPosition).ToList();

        if (!ambiguousMoves.Any())
            return "";

        char file = '\0', rank = '\0';

        if (ambiguousMoves.Any(m => m.OriginalPosition.Y == move.OriginalPosition.Y))
            file = move.OriginalPosition.File();

        if (ambiguousMoves.Any(m => m.OriginalPosition.X == move.OriginalPosition.X))
            rank = move.OriginalPosition.Rank();

        if (file == '\0' && rank == '\0')
            file = move.OriginalPosition.File();

        var notation = "";
        if (file != '\0') notation += file;
        if (rank != '\0') notation += rank;
        return notation;
    }

    private static IEnumerable<Move> GetMovesOfPieceOnPosition(Piece piece, Position newPosition, ChessBoard board)
    {
        for (short i = 0; i < 8; i++)
        {
            for (short j = 0; j < 8; j++)
            {
                if (board.pieces[i, j] is not null
                    && board.pieces[i, j].Color == piece.Color
                    && board.pieces[i, j].Type == piece.Type)
                {
                    // if original pos == new pos
                    if (newPosition.Y == i && newPosition.X == j) continue;

                    var move = new Move(new Position { Y = i, X = j }, newPosition) { Piece = piece };

                    if (ChessBoard.IsValidMove(move, board) && !ChessBoard.IsKingCheckedValidation(move, piece.Color, board))
                        yield return move;
                }
            }
        }
    }
}