# Terry's Gambit

Chess in a social s&box lobby, backed by **gamchess** — our own Go/Postgres service
at [chess.gamah.net](https://chess.gamah.net). Published as **Terry's Gambit** (s&box
package `gamah.gambit`).

Walk around a shared room with up to 8 players. Sit down at one of the chess boards
arranged in a ring and:

- **Play** — two players share a board. You're already signed in: s&box is Steam-gated,
  so your name and identity come with you.
- **Keep your games** — every finished game is archived to gamchess and replayable at
  [chess.gamah.net](https://chess.gamah.net), signed in with Steam. Your archive is
  private: you only ever see games you sat in.
- **Play for real on lichess** — link your lichess account and a game at a Gambit table can
  be a real lichess game, in your real lichess history: either against the person sitting
  opposite you, or against a random opponent from lichess's lobby. Rated if you want.
- **Watch** — a live game from the tables mirrors onto the big wall board.

See **[CLAUDE.md](CLAUDE.md)** for how it's built and where each subject is documented under
[`docs/`](docs/); the gamchess API contract is [`docs/api.md`](docs/api.md).

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels |
| Backend | gamchess — Go 1.22 + Postgres 16, `server/` |
| Identity | Steam: Facepunch auth token in-game, OpenID 2.0 on the web |
| Lichess | OAuth2 Authorization Code + PKCE, exchanged **client-side**; the client holds its own token and speaks the Board API directly |
| Lobby networking | s&box multiplayer (`[Sync]`/`[Rpc]`) |

## Assets

Pieces are runtime meshes, floor glyphs are our own DejaVu raster, sounds are synthesized
(`scripts/gen_sounds.py`), and the web viewer uses Unicode glyphs. The one licensed-in asset is
the lichess logo on the web button that links to lichess. Provenance is recorded in
`client/Assets/ATTRIBUTION.md`.

## Development

Open `client/gambit.sbproj` in the s&box editor (first time on a new machine: see
[`docs/setup.md`](docs/setup.md)). Startup scene is `scenes/lobby.scene`.
