# MATCHMAKING.md — plan for cross-session matchmaking (M20)

**Status: DESIGN / plan-and-park.** Nothing here is built yet. This is the spec a build
session implements; the decisions below are settled, the open questions are marked. The
gamchess API contract is written concretely so the backend slice can be built and tested
first (Go is testable on the dev host; the s&box client is review-only — see CLAUDE.md).

## The goal, in the user's words

> "Add matchmaking to gamchess so people who are in **solo sessions** can see a game to
> join. By default it should be **random sides** when a match is made. Verbiage should be
> explicit about that so that the 'opener' can't just always play White. If it grows we can
> let people pick sides."

A "solo session" is a player alone in their own s&box lobby — sitting at a table with no
opponent, or playing the computer (M19). Today two such players **cannot find each other**:
same-lobby play needs them in one s&box networking session, and the only cross-player path
is lichess. Matchmaking is the missing directory: solo players advertise "I want a game",
browse who else is open, and get paired.

## Settled decisions

- **gamchess is the directory.** It holds the list of open games and who is looking. It does
  NOT (for Mode A) run the game. This is what the user asked for and it keeps the directory a
  small, testable Go feature.
- **Two join modes, offered as a choice** (the user's "join up to play" vs "play in current
  session(s)"):
  - **Mode A — "Join up to play":** the joiner enters the opener's s&box lobby
    (`Networking.Connect(openerSteamId)`); both end up in ONE world and play the **existing
    two-seat game** (they can also see/voice each other). gamchess stays a thin directory.
  - **Mode B — "Play in current sessions":** both players stay in their own lobbies; moves
    **relay through gamchess** (POST move / poll opponent's moves), like the lichess relay but
    gamchess is the authority. No world-hopping.
- **Random sides by default, and the copy says so.** The opener does NOT get White by
  default. Sides are assigned at match time (see "Random sides" below). Picking a side is a
  deliberately-deferred "if it grows" feature — build the random path first, leave a clean
  seam for a colour preference later.
- **Entry point: the table setup panel** (`SetupPanel.razor`), alongside "Play the computer"
  and the lichess flows — it is where a player goes to start a game. (A dedicated wall board
  was considered and declined; revisit if the panel gets crowded.)

## Facts already verified (2026-07-24, don't re-derive blindly but do re-check before trusting)

- **`Networking.Connect(ulong steamId)` / `Connect(string)`** joins a lobby by the **host's
  SteamId** — an s&box lobby IS its host's SteamId. (`engine/.../Networking.cs`.) So Mode A's
  "join" is one call, given the opener's SteamId, which gamchess already knows (it authed them).
- **Gambit lobbies are `LobbyPrivacy.Public`, `Hidden: false`** (`LobbyNetworkManager.OnHostInitialize`),
  so they are joinable/queryable. Mode A needs no privacy change. `Networking.QueryLobbies(gameIdent)`
  also exists — but we use gamchess, not that, because gamchess carries the *intent* ("seeking a
  chess match, random sides") that a raw lobby list can't.
- **gamchess session auth already names a verified SteamID** (`internal/api/session.go`: the
  `gcs_` bearer / FP path). A match POST is therefore attributable and unspoofable — the opener
  in the directory is really that SteamID. Reuse `requireSteam`/session exactly as `games.go` does.
- **"gamchess is never required" holds for Mode A** (it's a directory; if it's down you just
  can't matchmake, local/bot/lichess play is unaffected). **It does NOT hold for Mode B** — a
  relayed game cannot run without gamchess. That asymmetry is the single biggest reason to ship
  Mode A first and treat Mode B as a separate, larger milestone.

## Random sides

The rule: **neither player may count on a colour.** Options, cheapest first:

1. **Host-side RNG at seat time (Mode A):** when the joiner arrives, the host seats the two
   players White/Black by a coin flip and starts. Simple, reuses the seating code. Trust model
   is the same as the rest of the local game (the host is already authoritative over
   everything), so a modified opener-host *could* rig its own flip — acceptable for a casual
   feature, and the **copy must not imply it's enforced against a cheating host**.
2. **gamchess assigns the colour (both modes), authoritative:** the match record carries
   `white_steam_id`/`black_steam_id`, decided by gamchess when the second player joins. Neither
   client picks. This is the honest version of "the opener can't always play White" and is
   **required for Mode B anyway** (gamchess runs that game), so Mode B gets it for free; wiring
   it into Mode A's seating is a small extra step and is the recommended target even for A.

→ **Build target:** gamchess assigns the colour (option 2) so the rule is real, not just UI
copy. Copy everywhere: **"Sides are random — you'll find out which colour you are when the
game starts."**

## gamchess directory — the shared backend (build + test this FIRST)

A new `internal/api/matchmaking.go` + `internal/store/matchmaking.go` + migration
`00004_matchmaking.sql` + `matchmaking_test.go`, registered in `router.go`. All session-gated
(`requireSteam`), all JSON, mirroring `games.go`'s conventions.

**Table `matchmaking`:**

| column | notes |
|---|---|
| `id` | uuid, PK — the match id |
| `opener_steam_id` | bigint, the verified opener |
| `opener_name` | text, display name for the list |
| `lobby_id` | text — the opener's s&box lobby (their SteamId as string). **Mode A only**; the joiner connects here. |
| `mode` | text: `join` or `relay` |
| `time_control` | text, PGN spec ("180+2", "-") — shown in the list; the game is played at it |
| `status` | text: `open` → `matched` → (`live`/`closed`) |
| `white_steam_id` / `black_steam_id` | bigint, null until matched — gamchess's authoritative side assignment |
| `joiner_steam_id` | bigint, null until matched |
| `created_at` / `updated_at` | timestamptz; `open` rows expire (see below) |

**Endpoints (all `requireSteam`):**

- `POST /api/v1/matchmaking` — open a game. Body: `{mode, lobby_id, time_control}`. Opener
  from the session. One open row per opener (upsert / refuse a second). Returns `{id}`.
- `GET /api/v1/matchmaking` — list `open` rows (not your own), newest first, capped. Returns
  `[{id, opener_name, mode, time_control, created_at}]`. **No `lobby_id` in the list** — it's
  handed out only on a successful join, so an idle browser can't harvest lobby ids.
- `POST /api/v1/matchmaking/{id}/join` — claim an open match. Joiner from the session. Atomic
  compare-and-set `open→matched` (row lock / `UPDATE ... WHERE status='open'`), assign
  `white/black` by a coin flip **here**, set `joiner_steam_id`. Returns
  `{mode, lobby_id (Mode A only), your_color, white_name, black_name}`. Losing the race → 409.
- `DELETE /api/v1/matchmaking/{id}` — opener cancels their open/matched row.
- `GET /api/v1/matchmaking/{id}` — poll a match's status (the opener waits on this to learn
  someone joined, their colour, and — Mode A — that they should expect an inbound connection).

**Staleness:** `open` rows are presence, and presence lies when a client vanishes (crash,
quit). A sweeper closes `open`/`matched` rows older than a short TTL (~60–120s) unless the
client re-touches them; the client heartbeats its own open row (a cheap `POST .../{id}` touch,
or re-uses the poll). Model this on the **TV ref-count / linger** discipline in CLAUDE.md: the
guaranteed-decrement path is a client that stops heartbeating; the TTL is the backstop.

**Testable here:** open→list→join (colour assignment, the CAS race, 409 on double-join),
cancel, expiry, "can't see your own row", "lobby_id withheld from list". Write these like
`games_test.go` / `tv_test.go`.

## Mode A client flow ("Join up to play") — build SECOND (review-only on this host)

1. **Open:** in `SetupPanel`, a seated solo player picks "Find an opponent online" → mode
   "Join up" → `POST /matchmaking {mode:"join", lobby_id: myHostSteamId, time_control}`. Panel
   shows "Waiting — sides are random." Client polls `GET /matchmaking/{id}`.
   - Only the **host** of a lobby can be an opener whose `lobby_id` is joinable. A player who
     JOINED someone else's lobby can't open a "join up" match on it (they're not the host). Gate
     the "Join up" option on `Networking.IsHost`; a non-host can still open a **relay** match (Mode B).
2. **Browse + join:** the panel also lists `GET /matchmaking` (open games). Tapping one →
   `POST /matchmaking/{id}/join` → returns `{lobby_id, your_color}` → the client
   **`Networking.Connect(lobby_id)`** (leaving its own lobby — warn if mid-bot-game). On arrival
   in the opener's lobby, the two are auto-seated by colour and the game starts.
3. **Auto-seat by assigned colour:** the opener's host, on learning the match filled (its poll
   returns `matched` with `white/black_steam_id`), reserves the two seats at a free table for
   those two SteamIDs and seats each on their assigned side when they're present. This is the
   **trickiest untestable bit** — it needs a host-side "expected players / reserved table"
   handshake keyed on the match. Sketch: the opener holds the match id + assignment; when a
   connection with `joiner_steam_id` appears, seat both. Reuse `ChessStation` seating; the
   colour is gamchess's, not walk-up-derived.
4. **Copy:** "Sides are random — you'll find out your colour when the game starts." Never
   "you'll play White."

**Mode A traps:**
- The joiner **leaves their own world** — if they were playing the bot (M19), that game ends;
  confirm before connecting. When the match game ends they're a guest in the opener's lobby
  (fine — they can play on there, or disconnect back to their own).
- **Opener leaves / lobby dies** → the joiner's connection drops (standard s&box). The match
  row should close.
- **Reserved-seat race:** a third person in the opener's lobby must not grab the reserved
  table/seat between match-fill and the joiner arriving. Needs a host-side reservation, not just
  "first free table when they show up."
- Auto-seating **overrides walk-up colour**: the whole point is the opener can't self-assign
  White by sitting first, so seating is gamchess's assignment, applied regardless of where each
  player walked up.

## Mode B client flow ("Play in current sessions" / relay) — build THIRD, its own milestone

This is the large one. Both players stay in their own lobbies; the game exists only on
gamchess. It re-implements, over HTTP polling, what the two-seat game does over s&box
networking — and it is structurally the lichess relay with **gamchess** as the authority
instead of lichess.

- **gamchess grows a live-game engine:** a game record with the move list + clocks + result,
  `POST .../{id}/move`, `GET .../{id}?since=N` (poll opponent moves), draw/resign/flag. The
  authority Gambit has never had to be before — the lichess relay *delegates* adjudication to
  lichess; here gamchess must adjudicate (or trust the vendored rules echoed by both clients and
  reconcile). **Decide: does gamchess validate moves (needs a Go chess engine) or trust+reconcile
  two clients running the vendored rules?** Open question, and it's the crux of Mode B's size.
- **Client:** a new `IBoardGame` controller (like `LichessGameController`) that POSTs the local
  player's moves and polls the opponent's, driving the same board/HUD/sounds seam. Clocks are
  gamchess-authoritative (like lichess's are lichess's). This slots into the existing
  `Source =>` seam with no renderer change (that seam already absorbed lichess).
- **Why it's separate:** it breaks "gamchess is never required" for this game type, needs a
  polling transport with the latency/breaker care the lichess relay already documents (PLAN.md
  #4), and needs the adjudication decision above. Ship A first; B is a milestone of its own.

## Build order (each a mergeable slice)

1. **gamchess directory** — table, endpoints, sweeper, tests. Fully testable here. Freezes the
   API both modes share. *(Recommended immediate next step.)*
2. **Mode A client** — SetupPanel entry, open/list/join, `Networking.Connect`, host-side
   reserved-seat + colour assignment. Review-only; editor-verified.
3. **Mode B relay** — gamchess live-game engine + a relay `IBoardGame` controller. Own
   milestone; settle the adjudication question first.

## Open questions to resolve before/while building

- **Mode B adjudication:** gamchess validates (Go engine) vs trust-two-clients-and-reconcile.
- **Colour authority in Mode A:** gamchess-assigned (recommended, honest) — confirm the
  host-side seating can honour an externally-assigned colour cleanly.
- **Heartbeat cadence / TTL** for open rows (tie to the etiquette budget; this is our own
  service, not lichess, so no IP-share concern — but don't build a busy-poll either).
- **Matchmaking pool scope:** global list for now. Filtering by time control / a "quick match"
  auto-pair queue are "if it grows" like colour choice.
