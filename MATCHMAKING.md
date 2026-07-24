# MATCHMAKING.md — cross-session matchmaking (M19, folded in)

**Status: BUILDING in M19.** This is the durable design reference — the "why" that outlives
the code. Player-facing copy stays in the info boards; the gamchess wire contract stays in
`README.md`; this file is the architecture and the traps.

## Built so far / what remains

- ✅ **gamchess backend — DONE, compiles + vets + tests green** on this host. Directory
  (`internal/api/matchmaking.go`, `store/matchmaking.go`, migration `00004`) and the relay
  live-game (`internal/api/relaygame.go`, relay store, `relay_games` table). Pure logic
  (clock ticking/flag, time-control parse, colour, coin fairness) is unit-tested; the
  DB-integration paths (open/join CAS, expiry) run via `make test` with Postgres.
- ✅ **Client API layer — DONE (review-only).** `Api/MatchmakingApi.cs`: the five directory
  calls + the three relay-game calls + response DTOs.
- ⬜ **Client controllers + UI — REMAINING (review-only, the big untestable part):**
  - `GamchessRelayController : IBoardGame` (Mode B) — a sibling of `LichessGameController`
    (~1200 lines): POST local moves, poll opponent moves, run the ticking clock down locally
    (never HIGH), render through the `Source =>` seam. The largest piece; mirror the lichess one.
  - A lobby-level matchmaking coordinator: open/list/poll/cancel, and on a Mode-A join
    `Networking.Connect(lobby_id)`; on the opener side, the **host reserved-seat handshake**
    that seats both players on gamchess's assigned colour when they arrive (the untestable crux).
  - `SetupPanel` UI: "OR FIND AN OPPONENT ONLINE" — mode toggle, list yourself, browse+join,
    random-sides copy.
  - Info-board copy (`InfoScreen` Welcome) — a new way to play.

## The goal, in the user's words

> "Add matchmaking to gamchess so people who are in **solo sessions** can see a game to
> join. By default it should be **random sides** when a match is made. Verbiage should be
> explicit about that so that the 'opener' can't just always play White. If it grows we can
> let people pick sides." … "make this all m19, we going big."

A "solo session" is a player alone in their own s&box lobby — sitting at a table with no
opponent, or playing the computer (M19). Two such players **cannot find each other** today:
same-lobby play needs them in one s&box networking session, and the only cross-player path is
lichess. Matchmaking is the missing directory.

## Two join modes (both built)

- **Mode A — "Join up to play":** the joiner enters the opener's s&box lobby
  (`Networking.Connect(openerSteamId)`); both end up in ONE world and play the **existing
  two-seat game**. gamchess stays a thin directory. Reuses everything.
- **Mode B — "Play in current sessions":** both players stay in their own lobbies; moves
  **relay through gamchess** — a gamchess-authoritative live game, like the lichess relay but
  gamchess adjudicates the clock and is the message bus. A new `IBoardGame` controller on the
  client, and a live-game engine on the server.

## Settled decisions

- **gamchess is the directory** (both modes) and additionally the **live-game host** (Mode B
  only). For Mode A it never runs the game.
- **Random sides, gamchess-assigned.** The opener does NOT default to White. gamchess flips
  the coin when the second player joins and writes `white_steam_id`/`black_steam_id`; neither
  client picks. Copy everywhere: **"Sides are random — you'll find out your colour when the
  game starts."** A colour *preference* is a deliberately-deferred "if it grows" feature.
- **Entry point: the table setup panel** (`SetupPanel.razor`), beside "Play the computer" and
  the lichess flows.
- **"gamchess is never required" holds for Mode A, NOT Mode B.** A relayed game cannot run
  without gamchess — the one game type in Gambit that depends on it. Mode A degrades to "you
  can't matchmake right now"; local/bot/lichess play is unaffected either way.

## Verified facts (2026-07-24 — re-check before trusting)

- **`Networking.Connect(ulong steamId)`** joins a lobby by the **host's SteamId**; a Gambit
  lobby IS its host's SteamId. Gambit lobbies are `LobbyPrivacy.Public, Hidden:false`
  (`LobbyNetworkManager`), so Mode A's join is one call and needs no privacy change.
- **gamchess session auth already names a verified SteamID** (`session.go`, the `gcs_` bearer /
  FP path) — a match POST is attributable and unspoofable. Reuse `requireSteam` as `games.go` does.
- The **lichess relay** (`internal/api/relay.go` + `lichess.go` play endpoints, and client
  `LichessGameController`) is the structural template for Mode B: POST a move, poll state since
  a cursor, server-authoritative clock. Mode B is "that, but gamchess is the authority instead
  of lichess" — and simpler, because there is no upstream token/stream to hold.

## Server: the directory (shared by both modes)

`internal/api/matchmaking.go` + `internal/store/matchmaking.go` + `migrations/00004_matchmaking.sql`,
registered in `router.go`, session-gated, JSON — mirroring `games.go`.

Table `matchmaking`: `id` (uuid), `opener_steam_id`, `opener_name`, `lobby_id` (opener's
SteamId string; Mode A), `mode` (`join`|`relay`), `time_control`, `status`
(`open`→`matched`→`live`/`closed`), `white_steam_id`/`black_steam_id`/`joiner_steam_id` (null
till matched), `game_id` (uuid of the relay game, Mode B), `created_at`/`updated_at`.

Endpoints (all `requireSteam`):
- `POST /api/v1/matchmaking` `{mode, lobby_id, time_control}` → `{id}`. One open row per opener.
- `GET /api/v1/matchmaking` → open rows, not your own, newest-first, capped. **No `lobby_id`
  in the list** — handed out only on a successful join, so an idle browser can't harvest ids.
- `POST /api/v1/matchmaking/{id}/join` → atomic `open→matched` CAS, coin-flip the colour here,
  set joiner. For Mode B, create the relay game and store `game_id`. Returns `{mode, lobby_id
  (A), game_id (B), your_color}`. Lost race → 409.
- `DELETE /api/v1/matchmaking/{id}` → opener cancels.
- `GET /api/v1/matchmaking/{id}` → poll status (the opener learns someone joined + colour +,
  Mode A, to expect an inbound connection; Mode B, the `game_id` to start relaying).

**Staleness:** `open` rows are presence; presence lies when a client vanishes. A sweeper closes
`open`/`matched` rows past a short TTL unless re-touched; the client heartbeats its open row.
Model on the TV ref-count/linger discipline (CLAUDE.md): the guaranteed path is a client that
stops heartbeating, the TTL is the backstop. This is our own service — no lichess IP-share
concern — but don't busy-poll.

## Server: the relay live-game (Mode B)

`internal/api/relaygame.go` (or fold into matchmaking) + store. A game record: `id`,
`white`/`black` SteamIDs, `time_control`, `moves` (UCI list), per-ply clock stamps, `status`,
`winner`, `last_move_at`. Endpoints, all `requireSteam` and **membership-checked** (only the two
players):
- `POST /api/v1/relaygame/{id}/move` `{uci, fen}` — append if it's the caller's turn; stamp the
  mover's clock (server clock is authority); apply increment; flip turn. **Adjudication choice:**
  gamchess does NOT run a Go chess engine — it trusts the mover's client (which ran the vendored
  rules) exactly as the local two-seat game trusts the mover's `NetChessMove`, and carries the
  `fen` as the checksum both clients reconcile against. Same trust model as same-lobby play.
- `GET /api/v1/relaygame/{id}?since=N` — moves since ply N + both clocks (ticking side computed
  lazily from `last_move_at`) + status. **Flag lazily:** when a GET (or a move) finds the ticking
  side past 0, end the game against them. No goroutine per game — laziness is enough because a
  live game is being polled by both players continuously.
- `POST /api/v1/relaygame/{id}/{action}` — resign / draw-offer / draw-accept / abort.

Both players **archive** the finished relay game the same way local/lichess games do (each POSTs
the PGN; idempotent on the game id). It IS a real game between two real accounts.

## Client

- **`GamchessApi`**: the five matchmaking calls + the relay-game calls (mirror the lichess
  play methods).
- **`MatchmakingController`** (or fold into the lobby): open / list / poll; on join, either
  `Networking.Connect` (A) or engage a relay controller (B).
- **Mode A seating:** the opener's host, on its poll returning `matched`, **reserves a table**
  for the two SteamIDs and seats each on the gamchess-assigned colour when present — overriding
  walk-up colour (the whole point). Reserved-seat handshake keyed on the match is the
  untestable crux; guard against a third player grabbing the table in the gap.
- **Mode B: `GamchessRelayController : IBoardGame`** — a sibling of `LichessGameController`.
  POSTs the local player's moves, polls the opponent's, runs the ticking clock down locally
  between polls (the M12/M18 house rule — never read HIGH), renders through the same `Source =>`
  seam (which already absorbed lichess with no renderer change). Clocks are gamchess's.
- **UI:** `SetupPanel` gets an "OR FIND AN OPPONENT ONLINE" block: a mode toggle (Join up /
  Play here), a "list yourself" button, and the open-games list to join. Random-sides copy on it.

## Traps (each already cost someone, or clearly will)

- **Mode A joiner leaves their own world** — a bot game (M19) ends; confirm before `Connect`.
  When the relayed... no: when the joined game ends they're a guest in the opener's lobby (fine).
- **Reserved-seat race** (A): a third person in the opener's lobby must not grab the reserved
  seat between match-fill and the joiner arriving — host-side reservation, not "first free table".
- **Auto-seat overrides walk-up colour** (A): that is the feature — the opener can't self-assign
  White by sitting first; the host seats by gamchess's assignment regardless of where they walked.
- **Mode B clock never reads HIGH** — inherit the LichessTvSource / LichessGameController
  countdown discipline verbatim (CLAUDE.md's TV clock section). gamchess is the sole authority;
  local drift can't outlive one move.
- **Mode B is gamchess-required** — every path must fail closed to a legible "matchmaking's
  down" rather than a frozen board, exactly as `GamchessApi`'s timeout/breaker already does.
- **`lobby_id` is withheld from the list** — it becomes a live connect target, so only a
  successful join receives it. (Not a hard secret — it's a public lobby — but don't hand out a
  connect list to idle browsers.)
