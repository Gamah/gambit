# NEXT — the VR spike

Handoff for `spike/vr` only. The durable facts live in `docs/vr.md`; this file is what a fresh
session cannot read off the code: what has been tried, what is unexplained, and what to do next.
It is not `PLAN.md` (that is the shipping table, and this branch is not expected to ship), and it
is deleted if the spike lands.

## Where it is

A headset player can walk the lobby, sit at a table and point at things. Working and confirmed in
the headset: stereo rendering, head tracking, the playspace anchor, smooth turn, head-relative
walking, both controllers drawn as primitives with a pointing beam, trigger/A/B mapped onto the
game's own actions, and the board pick coming off the controller instead of a screen pixel.

## Do next

**Play a full game against the built-in engine, in the headset, without taking it off.** That is
the first end-to-end test of the whole chain, and every part of it is unproven:

- Sitting down, then starting an engine game from the HUD with the beam. The HUD is the desktop
  screen UI on a quad — janky, deliberately kept for now because every control for starting a
  game lives on it.
- Picking squares and moving pieces with the controller ray through a whole game.
- The promotion picker, the clock, draw/resign offers, standing up.

Each of those either works or names the next piece of work. Nothing should be redesigned before
the game has been played once.

## Open, unexplained

- **`LobbyPlayer.Update` and `SeatSettingsPlate.Update` throw every frame while seated**, with a
  render-tree NRE alongside. Seen in the editor console; no stack has been read yet, and nothing in
  the VR changes obviously touches those paths. **Get the stack first** — the log beside
  `sbox-dev.exe` has it. This is the highest-value unknown on the branch.
- **Shadows flicker in VR only.** Never diagnosed. The room has a shadow-casting sun that moves
  every frame (`RoomLightOrbit`) plus four shadowed point lights, against a time-sliced shadow
  cache (`r.shadows.updates`, default 8) now serving three views. Console ladder:
  `gambit_vr_companion` (drop the third view), `r.shadows.csm.enabled 0`, `r.shadows.updates 64`,
  `r.shadows.debug 1`.
- **The Index B button may never bind.** `game/core/cfg/fpxr/valve_knuckles.json` in the engine
  gives `button_b` a `paths` key holding a single string, where every other single binding in both
  shipped profiles uses `path`. Escape answers to A as well, so nothing depends on it. Worth a
  one-character PR upstream if confirmed.
- **A `LineRenderer` with its component enabled and two valid points drew nothing.** Replaced with
  a stretched box rather than chased. If one is wanted later, it needs an answer first.

## Traps that cost sessions here

- **Launch with `scripts/vr.bat`.** VR needs `-vr` on `sbox-dev.exe` itself, and the switch does
  not survive every route into the editor.
- **Restart Play; do not hotload.** A `GameObjectSystem` added mid-session is never instantiated
  (`docs/engine.md`), and `LobbyPlayer.OnStart` runs once per spawn.
- **A headset cannot read a console.** `gambit_vr_debug` puts the rig's state on a board in the
  room; `gambit_vr_status` prints it; `gambit_vr_log <seconds>` leaves a trail to read afterwards.
- **Verify VR facts against `../sbox-public`, never from memory.** Three separate failures on this
  branch were engine behaviour that nothing documents: `VRController.GetModel` returns a dev cube
  for every device, `AnalogMove` is consumed in the fixed update while `AnalogLook` is consumed in
  the update, and a stereo camera never renders to the swap chain.

## Knobs

`gambit_vr_hud` · `gambit_vr_hud_distance` · `gambit_vr_hud_height` · `gambit_vr_hud_scale` ·
`gambit_vr_debug` · `gambit_vr_status` · `gambit_vr_log` · `gambit_vr_recentre` ·
`gambit_vr_companion`
