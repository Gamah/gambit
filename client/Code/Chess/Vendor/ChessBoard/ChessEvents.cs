// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

namespace Chess;

public partial class ChessBoard
{
    /// <summary>
    /// Raises when trying to make or validate move but after the move would have been made, king would have been checked
    /// </summary>
    public event ChessCheckedChangedEventHandler OnInvalidMoveKingChecked = delegate { };
    /// <summary>
    /// Raises when white king is (un)checked
    /// </summary>
    public event ChessCheckedChangedEventHandler OnWhiteKingCheckedChanged = delegate { };
    /// <summary>
    /// Raises when black king is (un)checked
    /// </summary>
    public event ChessCheckedChangedEventHandler OnBlackKingCheckedChanged = delegate { };
    /// <summary>
    /// Raises when user has to choose promotion action
    /// </summary>
    public event ChessPromotionResultEventHandler OnPromotePawn = delegate { };
    /// <summary>
    /// Raises when it's end of game
    /// </summary>
    public event ChessEndGameEventHandler OnEndGame = delegate { };
    /// <summary>
    /// Raises when any piece has been captured
    /// </summary>
    public event ChessCaptureEventHandler OnCaptured = delegate { };
    // GAMBIT VENDOR PATCH (s&box whitelist): upstream marshalled event raises
    // through SynchronizationContext.Current (System.Threading, not
    // whitelisted). Game code is single-threaded — direct invocation.

    private void OnWhiteKingCheckedChangedEvent(CheckEventArgs e)
    {
        OnWhiteKingCheckedChanged(this, e);
    }

    private void OnBlackKingCheckedChangedEvent(CheckEventArgs e)
    {
        OnBlackKingCheckedChanged(this, e);
    }

    private void OnInvalidMoveKingCheckedEvent(CheckEventArgs e)
    {
        OnInvalidMoveKingChecked(this, e);
    }

    private void OnPromotePawnEvent(PromotionEventArgs e)
    {
        OnPromotePawn(this, e);
    }

    private void OnEndGameEvent()
    {
        OnEndGame(this, new EndgameEventArgs(this, EndGame));
    }

    private void OnCapturedEvent(Piece piece)
    {
        OnCaptured(this, new CaptureEventArgs(this, piece, CapturedWhite, CapturedBlack));
    }
}
