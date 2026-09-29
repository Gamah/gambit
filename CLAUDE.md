# CLAUDE.md — Terry's Gambit

**Terry's Gambit** (repo/ident `gambit`, org `gamah`, namespace `Gambit.*`): chess in a social
s&box lobby, backed by **gamchess**, our own Go/Postgres service in `server/`. Players walk a
shared room, sit at a ring of tables, and play each other, the built-in engine, someone in
another lobby, or a real lichess game.

General s&box engine lore (the API whitelist, panel layout, scene and networking rules) lives in
`~/.claude/sbox.md` and is not repeated here.

---

## The code is the truth; prose is a claim about it

Docs, comments and docstrings are read by the next session as fact. A false or stale sentence
costs more than a missing one, so:

- **When prose and code disagree, the code wins.** Fix or delete the prose in the same change
  that notices it. Never reason from prose you have not checked against the code it describes.
- **Canonical contracts outrank everything else**, this file included:
  - **The [lichess API spec](https://github.com/lichess-org/api)** for every lichess endpoint,
    scope and stream shape, and **lila's source** where the spec is silent (`docs/lichess.md`).
  - **The gamchess contract in `docs/api.md`**, which `server/` and `client/Code/Api/` both
    mirror by hand. A contract change is one commit across both halves.
  - **The vendored Gera rules** (`client/Code/Chess/Vendor/`) for move legality, behind
    `Code/Chess/ChessGame.cs`.
  - **skafinity's `Code/Engine/**`** for the music (the vendored `gamah.skafinity` library).
- **Third-party behaviour we observed rather than read in a published contract is `[SOURCE]`**:
  inferred from an implementation, a changelog, a forum answer or an experiment, and liable to
  change with any release. Re-derive it rather than trusting the sentence. A published contract
  (the s&box docs, sbox-public source) is cited by link or path instead.

## Writing docs, comments and docstrings

A sentence stays only if all three hold:

1. **It is true of the code as it stands**, and checkable against the code beside it.
2. **The code cannot say it**: the constraint a value or a shape is built around, a behaviour
   outside the repo (s&box, Steam, lichess, the API), or an interface other code depends on.
3. **It does not go stale on its own.**

That rules out:

- **History.** No "used to", "was N", earlier attempts, reversed decisions, dates, or branch
  names. `git log` holds it.
- **Symptoms and incidents.** State the constraint in the present tense, not the failure that
  taught it.
- **Measurements as story.** One number may stand where a constant would otherwise look
  arbitrary. No timestamps, run logs, hardware, build numbers used as evidence, or time estimates.
- **Pointers to things that get deleted or reused**: `PLAN.md` rows or ranks, issue status,
  sessions, branches.
- **Closed questions and negative results.** Code that does not exist needs no explanation. Keep
  one only if a reader would otherwise rebuild it, and then as one line.
- **Future-shaping** ("later", "until we ship", "COMING SOON"). State boundaries as present
  facts; intentions are `PLAN.md` rows.

Mechanism goes in a docstring beside the code. A file in `docs/` holds what spans files:
contracts, invariants, how the pieces fit, how to operate them. When cutting, delete rather than
reword; do not append what you just learned to the end of a doc, edit the section that owns the
fact.

---

## Where things are written down

| file | what is in it |
| --- | --- |
| `docs/api.md` | The gamchess contract, both halves: identity and auth, the game session, endpoints, the archive. |
| `docs/server.md` | Operating gamchess: config, make targets, hosts and ports, deploy rules. |
| `docs/lichess.md` | Real lichess games: token custody, scopes, the four flows, the traps, lichess TV, API etiquette. |
| `docs/matchmaking.md` | Cross-lobby matchmaking: the directory and the gamchess-relayed game. |
| `docs/chess.md` | The rules seam, the built-in engine, PGN clock annotations, the game controllers and the `IBoardGame` seam. |
| `docs/world.md` | The room, the table ring and tabletop layout, wall boards, occupancy, lobby networking. |
| `docs/seat.md` | Seated controls: world vs board settings, the tabletop plate, cursor vs LOOK aim. |
| `docs/terry.md` | Seated bodies and the hands that play the moves; the `gambit_terry_*` knobs. |
| `docs/audio.md` | Board sounds, spoken moves, music, chat, proximity voice. |
| `docs/engine.md` | s&box lore specific to Gambit, on top of `~/.claude/sbox.md`. |
| `docs/setup.md` | First-time project setup, repo layout, the vendored skafinity library, assets, dev console commands. |
| `PLAN.md` | The ranked table of work to start. Nothing else in the repo may cite it. |

**The two info boards are part of any change to how the world behaves**: `CenterInfoPanel.razor`
(east wall) and `InfoScreen.razor`'s Welcome branch describe the game to a player
(`docs/world.md`).

---

## Stack

| Layer | Choice |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels |
| Lobby networking | s&box multiplayer (`[Sync]`/`[Rpc]`) |
| Backend | gamchess: Go 1.22 + Postgres 16, `server/` |
| Identity | Steam: Facepunch auth token in-game, OpenID 2.0 on the web |
| Lichess | OAuth2 + PKCE exchanged client-side; the client speaks the Board API directly |
| Music | `gamah.skafinity`, vendored as source under `client/Libraries/` |

### Engine source and docs

`../sbox-docs/docs/` is the manual and `../sbox-public` the engine source: read them instead of
recalling an API.

## What runs on this host

No engine code compiles here. These do, and each is a gate worth using:

- **The Go server.** Go 1.22 from `~/.local/share/toolchains/`; `go build/vet/test ./... -race`
  in `server/`. The make targets run Go in Docker, which this host lacks.
- **`node scripts/chess_js_perft.mjs`**: the web viewer's chess rules, held to C# writer output.
- **Sandbox-free C#** via a scratch csproj, with `dotnet` 10.x from
  `~/.local/share/toolchains/dotnet10/`. Everything under `Code/Chess/`, plus
  `Code/Game/TimeControl.cs` and `Code/Game/LichessTable.cs`, has no engine dependency. It needs
  `<TargetFramework>net10.0` (only the 10.x runtime is installed) and `<ImplicitUsings>enable`
  (the vendored library leans on s&box's global usings).
- **Committed harnesses** (`dotnet run`): `scripts/halfrise_harness/`,
  `scripts/lichess_harness/`, and `scripts/seataim_harness/`, which compiles the real
  `Code/World/SeatAim.cs` against a small engine shim.

**It is worth moving code to make it testable.** Logic left as a private method on a `Component`
cannot run here; the same logic behind a plain signature under `Code/Chess/` can
(`CapturedMaterial` takes a `char[64]` for exactly this). A shim is worth it when the logic is a
state machine and not when it would have to reimplement engine behaviour to mean anything.

Client changes are otherwise review-only until opened in the editor.

## Scope

- Two-seat games at a table are host-folded over s&box networking; gamchess is never required for
  them, and nothing may block scene load, `OnStart` or a game ending on it.
- A cross-lobby relay game is the one game type that requires gamchess (`docs/matchmaking.md`).
- A lichess game runs between the client and lichess; lichess is its only authority.
- Chess rules are standard only; variants can be watched on lichess TV but not played.

## History is written to be public

The repo is public, so its whole history is. A secret committed and then deleted is still public.

- **Never commit a secret.** No `SESSION_SECRET`, Facepunch or gamchess tokens, lichess tokens or
  OAuth codes, or real SteamIDs in fixtures or logs.
- **Commit messages are for strangers.** Explain the change, not the session it came from.
- **Provenance stays clean.** Every asset is ours (CC0, recorded in
  `client/Assets/ATTRIBUTION.md`) or documented with its licence.
