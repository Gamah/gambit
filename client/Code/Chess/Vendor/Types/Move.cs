// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

namespace Chess;

/// <summary>
/// Move on chess board
/// </summary>
public class Move
{
    /// <summary>
    /// Whether Positions are initialized
    /// </summary>
    public bool HasValue => OriginalPosition.HasValue && NewPosition.HasValue;

    /// <summary>
    /// Moved Piece
    /// </summary>
    public Piece Piece { get; internal set; }

    /// <summary>
    /// Original position of moved piece
    /// </summary>
    public Position OriginalPosition { get; internal set; }

    /// <summary>
    /// New Position of moved piece
    /// </summary>
    public Position NewPosition { get; internal set; }

    /// <summary>
    /// Captured piece (if exist) or null
    /// </summary>
    public Piece? CapturedPiece { get; internal set; }

    /// <summary>
    /// Move additional parameter
    /// This property gives move information about a move
    /// When one of the following moves occurs, we need info about:
    /// 1. Promotion: To which figure (Queen or another)?
    /// 2. Castle: King/Queen side castle?
    /// 3. En Passant: What is the position of the pawn that has been captured?
    /// </summary>
    public IMoveParameter? Parameter { get; internal set; }

    /// <summary>
    /// Move places opponent's king in check? => true
    /// </summary>
    public bool IsCheck { get; internal set; }

    /// <summary>
    /// Move places opponent's king in checkmate => true
    /// </summary>
    public bool IsMate { get; internal set; }

    /// <summary>
    /// Move in SAN Notation<br/>
    /// -> Use board.MoveToSan() to get san string for this move according to your board positions
    /// </summary>
    public string? San { get; internal set; }

    /// <summary>
    /// GAMBIT VENDOR PATCH (new field, no upstream equivalent): PGN brace-comment text
    /// for this move, without the braces — e.g. "[%clk 0:02:58]". Emitted by
    /// PgnBuilder.BoardToPgn after the SAN; null/empty writes nothing at all, so an
    /// un-annotated game is byte-for-byte what upstream produced.
    /// <br/>
    /// Set it through Gambit.Chess.ChessGame.SetMoveComment — the vendored types are
    /// not a public seam. Purely decorative: nothing in the rules or the parser reads it.
    /// </summary>
    public string? Comment { get; internal set; }

    /// <summary>
    /// Move is En Passant
    /// </summary>
    public bool IsEnPassant => this.Parameter is MoveEnPassant;

    /// <summary>
    /// Move is castling
    /// </summary>
    public bool IsCastling => this.Parameter is MoveCastle;

    /// <summary>
    /// Move is promotion
    /// </summary>
    public bool IsPromotion => this.Parameter is MovePromotion;

    /// <summary>
    /// Promoted piece or null when there is no promotion.
    /// </summary>
    public Piece? Promotion => this.Parameter is MovePromotion promotion ? new Piece(Piece.Color, promotion.PromotionResult) : null;

    /// <summary>
    /// Initializes new Move object by given positions
    /// </summary>
    public Move(Position originalPosition, Position newPosition)
    {
        OriginalPosition = originalPosition;
        NewPosition = newPosition;
    }

    /// <summary>
    /// Initializes new Move from long move notation
    /// </summary>
    /// <param name="move">
    /// Move as long string<br/>
    /// ex.:{wr - a1 - h8 - bq - e.p. - +}<br/>
    /// Or: {a1 - h8}<br/>
    /// See: move.ToString()
    /// </param>
    /// <exception cref="ChessArgumentException">Move didn't match regex pattern</exception>
    public Move(string move)
    {
        move = move.ToLower();

        // GAMBIT VENDOR PATCH (s&box whitelist): regex groups replaced by a
        // token walk over the " - "-separated fields; token shapes are disjoint
        // (piece "wp" vs position "e4" vs parameter vs check char), so the walk
        // is deterministic like the original pattern.
        var tokens = Regexes.SplitLongMove(move);
        bool valid = tokens is not null;

        if (valid)
        {
            int idx = 0;

            if (Regexes.IsValidPieceString(tokens[idx]))
                Piece = new(tokens[idx++]);

            if (idx + 1 < tokens.Length
                && Regexes.IsValidPosition(tokens[idx]) && Regexes.IsValidPosition(tokens[idx + 1]))
            {
                OriginalPosition = new(tokens[idx++]);
                NewPosition = new(tokens[idx++]);
            }
            else
            {
                valid = false;
            }

            if (valid && idx < tokens.Length && Regexes.IsValidPieceString(tokens[idx]))
                CapturedPiece = new(tokens[idx++]);

            if (valid && idx < tokens.Length && Regexes.IsValidMoveParameterString(tokens[idx]))
                Parameter = IMoveParameter.FromString(tokens[idx++]);

            if (valid && idx < tokens.Length && tokens[idx] is "+" or "#" or "$")
            {
                switch (tokens[idx++])
                {
                    case "+":
                        IsCheck = true;
                        break;
                    case "#":
                        IsCheck = true;
                        IsMate = true;
                        break;
                    case "$":
                        IsMate = true;
                        break;
                }
            }

            valid &= idx == tokens.Length;
        }

        if (!valid)
            throw new ChessArgumentException(null, "Move should match pattern: " + Regexes.MovePattern);
    }

    /// <summary>
    /// Initializes new Move object by given positions
    /// </summary>
    public Move(string originalPos, string newPos)
    {
        OriginalPosition = new(originalPos);
        NewPosition = new(newPos);
    }

    internal Move(Move source, PromotionType promotion)
    {
        Piece = source.Piece;
        OriginalPosition = source.OriginalPosition;
        NewPosition = source.NewPosition;
        CapturedPiece = source.CapturedPiece;
        Parameter = new MovePromotion(promotion);
        IsCheck = source.IsCheck;
        IsMate = source.IsMate;
        San = source.San;
    }

    internal Move(Move source)
    {
        if (source.Piece is not null)
            Piece = new Piece(source.Piece);

        OriginalPosition = new Position(source.OriginalPosition.X, source.OriginalPosition.Y);
        NewPosition = new Position(source.NewPosition.X, source.NewPosition.Y);

        if (source.CapturedPiece is not null)
            CapturedPiece = new Piece(source.CapturedPiece);

        if (source.Parameter is not null)
            Parameter = IMoveParameter.FromString(source.Parameter.ShortStr);

        IsCheck = source.IsCheck;
        IsMate = source.IsMate;
        San = source.San;
    }

    /// <summary>
    /// Needed to Generate move from SAN in ChessConversions
    /// </summary>
    internal Move()
    {
        OriginalPosition = new();
        NewPosition = new();
    }

    /// <summary>
    /// Long move notation as: <br/>
    /// {wr - a1 - h8 - bq - e.p. - +}<br/>
    /// Or: {a1 - h8}
    /// </summary>
    public override string ToString()
    {
        StringBuilder builder = new();

        builder.Append('{');

        if (Piece is not null)
            builder.Append(Piece + " - ");

        builder.Append(OriginalPosition + " - " + NewPosition);

        if (CapturedPiece is not null)
            builder.Append(" - " + CapturedPiece);

        if (Parameter is not null)
            builder.Append(" - " + Parameter.ShortStr);

        if (IsCheck)
        {
            if (IsMate)
                builder.Append(" - #");
            else
                builder.Append(" - +");
        }
        else if (IsMate)
            builder.Append(" - $");

        builder.Append('}');

        return builder.ToString();
    }
}