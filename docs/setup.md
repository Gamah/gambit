# Setup

First-time project setup, repo layout, the vendored skafinity library, assets, dev console.

## Opening the project

s&box's package manager tracks local projects in its own registry, by path, so opening the
committed `.sbproj` fails with `Unable to find package 'local.gambit#local'`.

1. Editor → **New Project** → Game (Empty), pointed at **`client/`** (the s&box project root, not
   the repo root).
2. The editor writes and registers its own `.sbproj`; use that one. The committed
   `client/gambit.sbproj` is a reference template.
3. The editor hotloads C#; check the error list.

A machine that once registered the project at a different path (a `gambit/` folder at the repo
root) needs both of these, or the editor may open a source-less husk that builds the world and
renders nothing: delete the orphan folder once `git ls-files gambit/` and
`git status --porcelain gambit/` are both empty, and unregister the old entry before adding
`client/`.

Paths in `gambit.slnx` and the csproj files assume Steam sits beside the checkout's parent
(`../../../Steam/…`); the editor regenerates them.

The runtime-loaded `chess_glyphs.png` ships to joiners only if listed in the `.sbproj`
`Resources` field, and the editor generates the real `.sbproj`, so set it in Project Settings on
each dev machine (`docs/engine.md`).

## Layout

```
scripts/               dev utilities, not s&box assets (gen_sounds.py needs numpy)
  *_harness/           dotnet harnesses for Sandbox-free code (`dotnet run`)
client/                the s&box project root
  gambit.sbproj        reference template; the editor generates the real one
  Code/                all game C# and Razor (capital C)
  Editor/              editor assembly (HotloadRebuild.cs)
  Assets/scenes/       lobby.scene is the only production scene
  Assets/sounds/       .sound events referencing compiled .vsnd in sfx/
  ProjectSettings/     Input.config, Collision.config, Platform.config
  Libraries/gamah.skafinity/   procedural music library, source-committed
server/                gamchess, the Go/Postgres backend (docs/server.md)
docs/                  one file per area (see CLAUDE.md)
```

Each half ignores its own build output (`client/.gitignore`, `server/.gitignore`); the root
`.gitignore` holds only repo-wide junk. Unanchored `bin/`/`obj/`/`*_c` entries never go in the
root file: they match at any depth and would swallow `server/bin/`.

## The vendored skafinity library

`client/Libraries/gamah.skafinity/` is source-committed and updated by **installing the published
package in the editor**, not by copying from `../skafinity`, which may be ahead of or diverged from
what is published. Commit the result verbatim, `.version` included, with one exception:
`Code/Skafinity.csproj` is not committed (the solution generator rewrites it per machine, and
`client/.gitignore` excludes `Code/*.csproj`).

An install can land **append-only**: the new version's files arrive beside the old version's stale
ones, and the compiler reports a wall of "does not contain a definition for". The recovery needs no
diagnosis: **delete the whole folder, commit the deletion, then install.** With nothing on disk
the manifest lands whole. Do not add a second `.version`.

`SkafinityMusicPanel.razor.scss` stays inside the library. A copy under `client/Code/UI/` would be
a second file at one resolved path with nothing keeping the two equal, and every library update
would leave the panel with no layout. The cost, and why it is accepted, is in `docs/engine.md`.

## Assets

Provenance for every asset goes in `client/Assets/ATTRIBUTION.md`, CC0 included. Everything is
ours: pieces are runtime meshes from `ChessSetBuilder`, floor glyphs are our own DejaVu Sans
raster (`scripts/gen_glyph_atlas.py`), sounds are synthesized (`scripts/gen_sounds.py`), and the
web viewer uses Unicode glyphs with zero image assets.

The one exception is the lichess logo on the web button that leaves for lichess; its terms and
rules are in `ATTRIBUTION.md` and `docs/lichess.md`.

CC0 sources on file for replacing the procedural pieces with models (`ChessSetBuilder.BuildPiece`
tries `models/chess/{type}.vmdl` first): Poly Haven "Chess Set" by Riley Queen
(https://polyhaven.com/a/chess_set, glTF/FBX); portablejim 2D chess set on FreeSVG
(https://freesvg.org/portablejim-2d-chess-set-pieces); OpenGameArt /content/chess-pieces-0,
/content/3d-chess-pieces, /content/chess-set-1, /content/chess. Kenney has no chess pack.

## Dev console commands

In `client/Code/Api/GamchessCommands.cs`:

| Command | What it answers |
|---|---|
| `gambit_gamchess_ping` | Is gamchess up? A TLS error means the vhost has no cert; any HTTP status means gamchess answered. |
| `gambit_gamchess_signin` | Mints a Facepunch token and proves the auth round-trip. |
| `gambit_gamchess_games` | Lists your archived games. |
| `gambit_lichess` | Am I linked? Prints this PC's token state first (no network), then gamchess's view, and names the case where they disagree: a local token gamchess doesn't know breaks only the two-seat directory lookup. |
| `gambit_lichess_unlink` | Revokes at lichess with the token, deletes it from this PC, then tells gamchess to forget the row, in that order: the revoke must be signed by the token. |
| `gambit_tv` | The whole TV chain: the local setting, the channel, what the wall thinks it shows, and gamchess's raw state. |
| `gambit_clock` | For the table you are seated at: which controller owns the board, what it thinks the game is doing, and what the seam answers for each seat's clock. |

The 34 `gambit_terry_*` commands are the seated-hands tuning harness, dev tools rather than
player-facing; see `docs/terry.md`.
