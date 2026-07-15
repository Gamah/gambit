// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

namespace Chess;

internal static class PgnBuilder
{
    public static (bool succeeded, ChessException? exception) TryLoad(string pgn, out ChessBoard? board, AutoEndgameRules autoEndgameRules)
    {
        board = new ChessBoard()
        {
            AutoEndgameRules = autoEndgameRules
        };

        pgn = ExtractPgnHeaders(pgn, board);

        // Loading fen if exist
        if (board.headers.TryGetValue("FEN", out var fen))
        {
            var (succeeded, exception) = FenBoardBuilder.TryLoad(fen, out board.FenBuilder);

            if (!succeeded)
            {
                board = null;
                return (false, exception);
            }

            board.pieces = board.FenBuilder.Pieces;

            board.HandleKingChecked();
            board.HandleEndGame();

            if (board.IsEndGame)
            {
                return (true, null);
            }
        }

        pgn = Regexes.StripDelimited(pgn, '(', ')'); // Remove all alternative branches
        pgn = Regexes.StripDelimited(pgn, '{', '}'); // Remove all comments

        // Todo Save Alternative moves(branches) and comments for moves

        var sanMoves = Regexes.ExtractSanMoves(pgn);

        // Execute all found moves
        for (int i = 0; i < sanMoves.Count; i++)
        {
            var (succeeded, exception) = SanBuilder.TryParse(board, sanMoves[i], out var move, true);

            if (!succeeded)
            {
                board = null;
                return (false, exception);
            }

            // If san parsing succeeded => move is valid

            board.executedMoves.Add(move);
            board.DropPieceToNewPosition(move);
            board.moveIndex = board.executedMoves.Count - 1;
        }

        board.HandleKingChecked();
        board.HandleEndGame();

        // If not actual end game but game is in fact ended => someone resigned
        if (!board.IsEndGame)
        {
            if (pgn.Contains("1-0"))
                board.Resign(PieceColor.Black);

            else if (pgn.Contains("0-1"))
                board.Resign(PieceColor.White);

            else if (pgn.Contains("1/2-1/2"))
                board.Draw();
        }

        return (true, null);
    }

    private static string ExtractPgnHeaders(string pgn, ChessBoard board)
    {
        // GAMBIT VENDOR PATCH (s&box whitelist): one scanner pass extracts the
        // [Name "Value"] headers and returns the PGN with them removed.
        var headers = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
        var remainder = Regexes.ExtractHeaders(pgn, headers);

        foreach (var header in headers)
        {
            // [Black "Geras1mleo"] => Key = Black, Value = Geras1mleo
            board.headers.Add(header.Key, header.Value);
        }

        // San move can occur in header ex. in nickname of player => remove headers from string
        return remainder;
    }

    public static string BoardToPgn(ChessBoard board)
    {
        StringBuilder builder = new();

        foreach (var header in board.headers)
            builder.Append('[' + header.Key + @" """ + header.Value + '"' + ']' + '\n');

        if (board.headers.Count > 0)
            builder.Append('\n');

        // Needed for moves count logic
        board.moveIndex = -1;

        for (int i = 0, count = 0; i < board.executedMoves.Count; i++)
        {
            // Adding moves count when needed
            if (count != board.GetFullMovesCount())
            {
                count = board.GetFullMovesCount();

                // Add space before move count if not first move
                if (i != 0) builder.Append(' ');

                builder.Append(count + ".");
            }

            if (board.moveIndex == -1)
            {
                // From position?
                if (board.LoadedFromFen && board.FenBuilder.Turn == PieceColor.Black)
                    builder.Append("..");
            }

            builder.Append(' ' + board.executedMoves[i].San);

            // GAMBIT VENDOR PATCH (new behaviour, no upstream equivalent): emit this
            // move's brace comment, which is how Gambit writes {[%clk H:MM:SS]}. Move
            // numbers are appended before the SAN above, so a comment placed here lands
            // directly after the move it annotates, per PGN spec §8.2.5. No comment set
            // (the norm) appends nothing, leaving upstream's output untouched.
            var comment = board.executedMoves[i].Comment;
            if (!string.IsNullOrEmpty(comment))
                builder.Append(" {" + comment + "}");

            board.moveIndex++;
        }

        if (board.IsEndGame)
        {
            if (board.EndGame.WonSide == PieceColor.White)
                builder.Append(" 1-0");
            else if (board.EndGame.WonSide == PieceColor.Black)
                builder.Append(" 0-1");
            else
                builder.Append(" 1/2-1/2");
        }

        // Back to positions
        board.Last();

        return builder.ToString();
    }
}