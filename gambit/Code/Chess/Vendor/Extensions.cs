// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

namespace Chess;

internal static class Extensions
{
    /// <summary>
    /// See: https://stackoverflow.com/questions/49190830/is-it-possible-for-string-split-to-return-tuple
    /// Deconstruct into 2 vars
    /// </summary>
    internal static void Deconstruct<T>(this IList<T> list, out T first, out T second)
    {
        first = list.Count > 0 ? list[0] : default; // or throw
        second = list.Count > 1 ? list[1] : default; // or throw
    }

    /// <summary>
    /// GAMBIT VENDOR PATCH (s&box whitelist): Array.Clone() is blocked (SB1000)
    /// — manual 8x8 copy replaces the two board-clone call sites.
    /// </summary>
    internal static Piece?[,] CopyBoard(this Piece?[,] pieces)
    {
        var copy = new Piece?[pieces.GetLength(0), pieces.GetLength(1)];

        for (int i = 0; i < pieces.GetLength(0); i++)
        {
            for (int j = 0; j < pieces.GetLength(1); j++)
            {
                copy[i, j] = pieces[i, j];
            }
        }

        return copy;
    }

    internal static List<Piece> PiecesList(this Piece?[,] pieces)
    {
        var list = new List<Piece>();

        for (int i = 0; i < pieces.GetLength(0); i++)
        {
            for (int j = 0; j < pieces.GetLength(1); j++)
            {
                if (pieces[i, j] is not null)
                    list.Add(pieces[i, j]);
            }
        }

        return list;
    }
}