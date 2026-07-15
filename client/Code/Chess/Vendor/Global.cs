// *****************************************************
// *                                                   *
// * O Lord, Thank you for your goodness in our lives. *
// *     Please bless this code to our compilers.      *
// *                     Amen.                         *
// *                                                   *
// *****************************************************
//                                    Made by Geras1mleo

// GAMBIT VENDOR PATCH (s&box API whitelist, PLAN.md D2):
// - System.Text.RegularExpressions removed — Regexes.cs is now hand-written
//   parsers with the same semantics.
// - System.Collections.Concurrent removed — move generation is sequential
//   (ChessGenerations.cs).
// - System.Diagnostics.CodeAnalysis removed — [NotNullWhen] annotations
//   stripped (they were compile-time only).
// - Explicit System/Text usings added: the upstream csproj used ImplicitUsings,
//   which the s&box game project does not (System.Collections.Generic and
//   System.Linq come from the game's own Assembly.cs global usings).

global using System;
global using System.Text;
