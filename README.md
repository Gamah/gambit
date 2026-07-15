# Terry's Gambit

Chess in a social s&box lobby, backed by [lichess](https://lichess.org).
Published as **Terry's Gambit** (s&box package `gamah.gambit`).

Walk around a shared room with up to 8 players. Sit down at one of the chess
boards arranged in a ring and:

- **Play real lichess games** — sign in with your lichess account and seek/challenge
  anyone on lichess (Board API), with clocks, chat, and spectators at your board.
- **Play anonymously** — two players share a board in the lobby, no account needed;
  when the game ends you get a shareable lichess link (PGN import).
- **Watch** — other boards in the room, or Lichess TV on the big wall board.
- **Solve puzzles** — lichess puzzles at any board (local solving; doesn't affect
  your lichess puzzle rating).

Forked from rotaliate-client; the lobby/station scaffolding is inherited, the game
and backend are being replaced. See **[PLAN.md](PLAN.md)** for the full design,
lichess API notes, milestones, and current status.

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels |
| Chess backend | lichess API (OAuth2 PKCE, Board API, NDJSON streams) |
| Lobby networking | s&box multiplayer (`[Sync]`/`[Rpc]`) |

## Assets

All art is CC0 — procedural geometry from engine primitives, with the
[Poly Haven chess set](https://polyhaven.com/a/chess_set) (CC0) as a planned model
upgrade and the portablejim CC0 2D piece set for floor glyphs. Provenance is
recorded in `Assets/ATTRIBUTION.md` as assets land.

## Development

Open `client/gambit.sbproj` in the s&box editor (first time on a new machine: see
"Project Setup" in `CLAUDE.md`). Startup scene is `scenes/lobby.scene`.
