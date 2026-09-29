# Matchmaking

Cross-lobby play: gamchess as a directory of open games, and as the authority for a game relayed
between two lobbies.

A player alone in their own s&box lobby cannot otherwise find an opponent: same-lobby play needs
one networking session. The entry point is the table setup panel (`UI/SetupPanel.razor`), beside
the bot and the lichess flows; the client coordinator is `Game/Matchmaking.cs`, static state so it
survives the scene teardown a `Networking.Connect` does. Routes are in `docs/api.md`.

## Two modes

| Mode | What happens | gamchess's role |
|---|---|---|
| `join` | the joiner `Networking.Connect`s into the opener's lobby and both play the ordinary two-seat game | directory only |
| `relay` | both stay in their own lobbies and moves go through gamchess (`RelayGameController`) | directory and live-game authority |

`relay` is the client default: it needs no shared lobby, and it is the only mode that works when
both sides are the same Steam account (two editors on one account cannot share a lobby). Joining
your own advert is allowed; that is self-play and the one-machine test path. The two sides are told
apart by `opener_color`, not by SteamID.

**A relay game requires gamchess**; every other game type degrades to "no matchmaking" when it is
down. Every relay path must fail closed to a legible message rather than a frozen board.

## Sides are random and assigned by the server

gamchess flips the coin (`crypto/rand`) when the second player joins and writes both seats; neither
client picks, so the opener cannot default to White. Player-facing copy says so: sides are random
and you learn your colour when the game starts.

- **`join`**: the opener's host force-seats both players on the assigned colours at the table it
  advertised from and readies both (`LobbyNetworkManager.HostSeatMatch`), overriding where the
  opener walked up. The joiner, possibly still connecting, arrives into the running game through
  the ordinary late-join resync.
- **`relay`**: each client engages `RelayGameController` on the table it is sitting at and switches
  to its assigned seat.

## The directory

Table `matchmaking` (`migrations/00004`, `00005`): opener, cosmetic `opener_name`, `lobby_id`,
`mode`, `time_control` (`secs+inc` or `-`), `status` (`open` → `matched` → `closed`), the assigned
seats, `opener_color`, and for `relay` the `game_id`.

- **One open row per opener**, enforced by a partial unique index; a second open replaces the first.
- **`lobby_id` is never listed.** It is the opener's host SteamID, a live connect target, and is
  handed out only in a successful join response.
- **Join is an atomic `open → matched` update**; the loser of a race gets 409.
- **Only the two participants may read a match**; anyone else gets 404, so ids are not probeable.
- **Presence is a heartbeat.** The opener's poll of its own row touches it; a sweep every 30s closes
  `open`/`matched` rows untouched for 90s. The heartbeat is the live signal and the TTL the backstop
  for a client that vanished.

## The relay game

Table `relay_games`. gamchess runs no chess engine: it trusts the mover, who ran the vendored rules,
exactly as the two-seat host trusts a `NetChessMove`, and carries the mover's `fen` as the checksum
both clients reconcile against. The mover also reports when its own rules ended the game (`over`,
`result`, `reason`).

**The clock is the one thing gamchess adjudicates.** A move applies under a row lock: it must be the
mover's turn (else 409), charges the mover's elapsed time, adds the increment and flips the turn.
**The flag is lazy**: no goroutine per game; any read or move that finds the ticking side at 0 ends
the game in the same transaction, which is enough because both players poll continuously.
`LiveClocks` decrements only the ticking side, so the server's clock never reads high.

Actions: `resign`, `abort`, `draw-offer`, `draw-accept`, `draw-decline`. There is no takeback.

Client side, `RelayGameController` is an `IBoardGame` (`docs/chess.md`): it plays a move locally and
POSTs it, polls every 1.2s and rebuilds from gamchess's move list, and runs the ticking side's clock
down locally between polls, snapping on each one, under the house rule that a live clock never
reads higher than the time left. Both players archive the finished game as for any other game.
