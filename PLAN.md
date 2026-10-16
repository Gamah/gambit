# PLAN.md

One flat table, ranked highest first. **A shipped row is deleted, not ticked**; when you close
one, put what a future session would get wrong without it into `CLAUDE.md` or `docs/`. Rows are
intentions, not specifications: a better idea beats the row, so change the row. The only hard
constraints are the canonical contracts named in `CLAUDE.md`.

| # | Row |
|---|---|
| 92 | **`LobbyPlayer.Update` and `SeatSettingsPlate.Update` throw every frame while seated**, with a render-tree NRE alongside, seen in the editor console. Get the stack first — the log beside `sbox-dev.exe` has it; nothing in the VR work touches either path, and `SkinnedModelRenderer.Set`/`SetLookDirection`/`SetBodyGroup` are all `SceneModel?.`-guarded, so a renderer disabled by the VR body hide is ruled out. Check master too: this may predate the branch. |
| 80 | **A VR UI that is not the desktop HUD on a quad.** The adopted screen layout is a 1920x1080 panel with a crosshair and keyboard hints, squared on the board and covering it; it is how a game is started only because nothing else is. Wall-board sized panels built the way `WallBoardGeometry` builds them (`docs/vr.md`), reachable by the beam, placed in the room rather than in the view. The hint copy still names keys ("Press E to sit") with no VR branch. |
| 74 | **Grab a piece with the grip and move it.** Grip is unused for exactly this (`VrInput`). It needs a pick that is a lift rather than a square-by-square select, a held-piece pose, and a drop that resolves to a square through the same `ChessGame` legality seam the click path uses — the move must not become a second rules path. |
| 60 | **Shadows flicker in VR only.** Never diagnosed. A shadow-casting sun moves every frame (`RoomLightOrbit`) plus four shadowed point lights, against a time-sliced shadow cache (`r.shadows.updates`, default 8) now serving three views. Ladder: `gambit_vr_companion` (drop the third view), `r.shadows.csm.enabled 0`, `r.shadows.updates 64`, `r.shadows.debug 1`. |
