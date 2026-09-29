# Chess

The rules, the built-in engine, the PGN clock annotations, and the seam every kind of game
renders through.

## Rules

- Gera Chess Library (MIT, `d4f3f69`) is vendored and patched in `Code/Chess/Vendor/`:
  regex, `Task`, `Span` and reflection are stripped for the whitelist, and every change is
  marked `GAMBIT VENDOR PATCH`. It is proven in a dotnet harness mirroring s&box compile
  settings: perft depths 1–4 on six reference positions, upstream's xunit tests, and the
  wrapper tests.
- Most patches only remove off-whitelist constructs. **Two add behaviour**: `Move.Comment` and
  the `PgnBuilder.BoardToPgn` line that emits it, which is how `{[%clk]}` reaches the PGN. Both
  no-op when no comment is set, so an un-annotated game serialises byte-for-byte as upstream.
- **`Code/Chess/ChessGame.cs` is the only seam callers may touch.** It caches
  `Fen`/`LastMoveUci`/`MoveCount` between moves so per-frame polling is free.
  `TryFromPgnAtPly(pgn, ply)` / `TryFromPgn(pgn)` rebuild a position from movetext;
  `SetMoveComment(ply, text)` / `ClkField(seconds)` write the clock annotations.
- Re-prove the rules before trusting a gate, via the dotnet harness or
  `node scripts/chess_js_perft.mjs` (the web viewer's rules). `ChessGame.Perft` is there for it.

## The built-in engine

`Code/Chess/ChessEngine.cs` is a `partial ChessGame`: negamax + alpha-beta over the same
vendored move generation, and so **the one place allowed to touch `_board` and `UciOf`**. It is
Sandbox-free, so a full game of it runs in a scratch csproj in seconds; keep it that way.

- **The vendored board refuses to move in a drawn position, and a draw rule can fire while legal
  moves remain.** `ChessBoard.Move` throws `ChessGameEndedException` whenever `IsEndGame`, and
  `EndGameProvider.ResolveDrawRules` sets that for repetition, fifty-move and insufficient
  material, none of which empty the move list. So `moves.Length == 0` does not mean "nothing is
  playable": any loop that plays moves on `_board` checks `IsEndGame` too. Both recursion points
  score such a node as 0, which is what a draw is worth. Quiescence reaches it fastest.
- **When touching the search, play full games in the harness, not puzzles.** Only a game long
  enough to repeat reaches the draw rules.
- The bot is a host-driven virtual seat (SteamId 0, difficulty `[Sync]`ed on `ChessStation`),
  which is why seat/ready/abandon logic counts a bot as filled and ready, and why a bot clears
  when the human stands (a bot never sits alone).
- The search runs off the main thread (`GameTask.RunInThreadAsync`) over a throwaway position
  rebuilt from the FEN, never the live `Game`, so it cannot race the main thread's reads. It is
  bounded by a fixed depth per level and a hard node cap (`ChessEngine.Config`).
- Think time is charged to the bot's own clock, so a fast game may flag the bot. By design.
- A bot game is never a lichess game and runs at any speed, Bullet included. The think pose is
  `LocalGameController.BotPoseSeconds`.

## PGN clock annotations (`%clk`)

`{[%clk H:MM:SS[.ff]]}` per move, plus a `[TimeControl "180+2"]` header (seconds+increment;
`-` when untimed). **The one format Gambit shares with the outside world**, so it follows
lichess's, as written by lichess-org's dartchess and python-chess:

- Hours unpadded, minutes and seconds zero-padded to two, fraction optional, trailing zeros
  stripped: a whole second is `0:03:00`, and `.70` is written `.7`.
- Both readers cap the fraction at three decimals. We emit at most two (centiseconds): the clock
  is decremented by a frame delta, and lichess keeps clocks in centiseconds.
- `ChessGame.ClkField` **rounds**; `TimeControl.Format` (every live clock) **truncates**. A live
  clock must never read higher than the time actually left; the archive matches the reference
  writers.

Clocks are stamped by the **host** (`NetClockStamp`), never read from a client's own synced
copy, which lags the increment. `chess_js_perft.mjs` holds the JS parser to real C# writer
output, including a sub-second bullet fixture; the fixtures come from the dotnet harness, so
regenerate them there rather than hand-editing. Where the live clock is drawn is in
`docs/world.md`.

## Game controllers

`Game/IBoardGame.cs` is the render/drive abstraction. `ChessBoardView` renders the active source
through one shared resolver, `BoardGame.Source( local, lichess, relay )`: lichess when engaged or
mirroring, else relay when engaged, else local.

**`relay` is an optional argument.** A two-argument call compiles and silently answers
`LocalGameController` during a relay game, a shell, exactly as during a lichess game. **Pass all
three.** Call sites that pass two, each a known cosmetic gap: `SeatedTerry.Source` (the hands
don't animate in a relay game), `LobbyPlayer.PremoveAt` (the roaming premove reminder misses a
relay game), and `GamchessCommands` (dev console).

Anything reading the position goes through it. `ChessBoardView`, `GameHud`, `TableClock`,
`MoveHistoryPanel` and `Audio/TableSounds` resolve `Source` with the identical expression on
purpose: what you see, what the HUD says and what you hear must be the same game.

**The seam only protects what is on it.** `GameOver`, `LocalSeatClock` and `PremoveDropped` are
on it because a reactive feature would otherwise read them off `LocalGameController`, where
during a lichess or relay game they are wrong by construction. A new reactive feature that has
you typing `LocalGameController` is that mistake.

| Controller | Networked? | What it does |
|---|---|---|
| `LocalGameController` | host-folded `[Sync] BoardFen`/`Phase`/`ClientGameId` | the two-seat game at a table (a bot fills one seat), and the archive upload |
| `LichessGameController` | participants stream lichess directly; spectators mirror | a real lichess game on this table. Each participant holds its own `/api/board/game/stream/{id}` and `[Rpc.Host]`-reports its observed move list into `[Sync] MirrorMoves/MirrorLive`, from which every non-engaged client rebuilds a display game through the same seam. Adjudicates nothing: lichess is the only authority and the position is rebuilt from its UCI list. It runs the ticking seat's clock down locally between moves, because lichess only sends a clock on a move; a fixed `ClockLeadSeconds` undershoot keeps it reading low, and a local clock at 0 clamps and waits for lichess to call the flag |
| `RelayGameController` | polls gamchess's relay | a game against someone in another lobby, paired through gamchess's directory (`docs/matchmaking.md`). gamchess is the authority. Colour is assigned at random by the server |
| `SpectatorController` | reads the host-folded FEN; holds the TV socket | north wall: cycles live tables, then lichess TV |

**While a lichess game runs, the local controller is a shell** holding the seats and the
`ClientGameId`. Its `ChessGame` never advances (moves go to lichess, not `NetChessMove`), so its
clocks and result are stale by construction; `HostTickClocks` early-returns on `LichessGame` so
it cannot flag a player who is fine on lichess's clock. **Anything reading a clock, a turn or a
result during a lichess game reads the lichess source, not `ctrl`.**
