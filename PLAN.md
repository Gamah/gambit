# PLAN.md

One flat table, ranked highest first. **A shipped row is deleted, not ticked**; when you close
one, put what a future session would get wrong without it into `CLAUDE.md` or `docs/`. Rows are
intentions, not specifications: a better idea beats the row, so change the row. The only hard
constraints are the canonical contracts named in `CLAUDE.md`.

| # | Row |
|---|---|
| 100 | **Lighting renders too dark on the current engine.** Terryball now looks right after three changes; which one carried the world is not isolated. (1) Its fullscreen pass (`BasePostProcess` blit at `Stage.AfterOpaque`) read the linear HDR `ColorBuffer` with `SrgbRead( true )` and wrote every pixel back, so each pixel came out about the old one ^2.2 (darks collapse far more than lights; lost light would scale both alike). `SrgbRead( false )`, as `tonemapping.shader` and `glass.shader` read it, fixed that. No shader here reads the frame, but a screenshot compared against an old one for that ^2.2 signature tells gamma from light. (2) `01cd5459` moved normal/tangent decoding from a compile-time combo to a runtime flag (`g_bUncompressedTangentFrame`, set natively) and says models and custom shaders must be force-recompiled; a procedural mesh with a zero tangent rendered solid black until given a real one. Force-recompile this project's models and shaders, and check every `new Vertex(...)` for a zero tangent. (3) `12b791d8` blends UI in sRGB space, so translucent dark backdrops read darker; terryball remaps black and near-black backdrop alphas to `1 − (1 − a)^(1/2.2)`. If none of that clears it, toggle the sun's `Shadows` and `r.shadows.contact.enabled 0` (shadow commits `8f20bf94`, `bf0b5627`/`1965d08d`, `0a22829c`, `798e68ec`); ambient (`SkyColor` + `AmbientLight`) is unchanged upstream (`sbox-public` `b5464937`, read 2026-09-29). Same row in rotaliate-client |
