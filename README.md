# rotaliate-client

s&box reimplementation of [Rotaliate](https://github.com/Gamah/rotaliate) — a competitive puzzle game built in C# on the Source 2 engine.

## What is Rotaliate?

Players rotate 2×2 blocks on a 10×10 grid to form same-color groups. Matched groups go black (solved). Race the clock, minimize moves, or clear your color before opponents do.

- 10×10 grid, 4 active colors (24 cells each): Red, Blue, Green, Yellow
- A 2×2 block of identical active color → all four cells become solved (black)
- CW or CCW rotation of any 2×2 selection

## Game Modes

| Mode | Description |
|---|---|
| Daily Puzzle | Shared daily board; compete for time or moves |
| Hourly Puzzle | Shared hourly board; compete for time or moves |
| Free Play | Seeded solo session |
| 2-Player | WebSocket — each player owns 2 colors; first to clear both wins |
| 4-Player | WebSocket — each player owns 1 color; first to clear theirs wins |

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels + HudPainter |
| Networking | `Http` (REST) + `WebSocket` (s&box built-ins) |
| Backend | [rotaliate](https://github.com/Gamah/rotaliate) (Go) |

## 3D Lobby

The game boots into a third-person lobby room (`Assets/scenes/lobby.scene`). Walk up to the
wall screen and press **E** to play Rotaliate on it; **Escape** or the Leave button returns
you to the room. Movement/camera use the built-in s&box `PlayerController`.

## Project Setup

s&box's package manager tracks local projects in its own registry — cloning and opening the `.sbproj` directly will fail.

1. Open the s&box editor → **New Project** → Game (Empty), pointed at this repo folder
2. The editor registers the project and writes its own `.sbproj`; use that file going forward
3. C# changes hotload in milliseconds — check the error list for compile errors

## Scripts

**`scripts/gen_sounds.py`** — regenerates the synthesized WAV source files for all sound effects. Run from the repo root; requires `numpy`.

```
python scripts/gen_sounds.py
```

After running, open the s&box asset browser so it recompiles the WAVs to `.vsnd`. The `.sound` event files in `rotaliate/Assets/sounds/` already reference the correct `.vsnd` paths.

## License

GamahCode License v1.2 — see [LICENSE](LICENSE).
