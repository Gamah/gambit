# Seated hands can't reach the board — the reality

**Decision (M13):** the reaching-hand idea is **abandoned**, and the hand code is **cut** (see
"What M13 shipped, and what got cut" below). The seated bodies stay — sitting, gaze and blink
already work and are the bulk of "two people at a board." This file exists so a future milestone
does not re-attempt the impossible or re-derive the numbers. **The measurements are from the live
editor** (`gambit_terry` / `gambit_terry_probe`, White seat, station-local) — those tools are cut
too, but recoverable at tag **`terry-hands-final`**; **the engine facts are read from `sbox-public`
source** with file:line, not recalled.

---

## The one-paragraph reality

A seated Citizen's working arm is **~20 units** (two-bone IK, **no stretch anywhere in the
animgraph**, rooted at the pinned shoulder so it can't drag the body). The chessboard is **34
units deep**. The seated shoulder sits ~15u back from the near edge, so the hand reaches only to
about **x −13 — rank 2 — and no lever moves it past the middle of the board**. Scooting the seat
in is bounded by the table's foot plate (~1 more rank, then the knees collide). The only in-engine
"lean" a seated idle citizen has is a ~5° spine clamp worth **~2u**. Rescaling the board to fit
the arm needs it at **~¼ size** to reach your own half and **~⅒** to reach across — far below a
legible board — and it's a shared, centered board that can't be biased toward one seat anyway.
**Reaching for pieces across this board is geometrically impossible for a fixed-size citizen; the
only real fixes are a longer arm (model swap) or a much smaller/closer board, and neither is
worth it.**

---

## The measurements

| thing | value | note |
|---|---|---|
| Right arm (shoulder→wrist) | **19.9u** | `\|arm_upper_R→arm_lower_R\| + \|arm_lower_R→hand_R\|`, live skeleton — the authority |
| Shoulder x (arm_upper_R) | −44.6 @ `SeatSitBack`=36 · −31.8 @ 26 | shoulders sit BACK over the chair; ≈ `−(SeatSitBack + 6…9)`, pose-dependent |
| Shoulder z | 35.7 | only ~3.5 above the board surface |
| Pelvis / head / feet x | ≈ Back−3 / Back−4 / Back+8 | ruler |
| Board depth | 34u, near rank x−17.06 … far rank x+17.06, centered at x=0 | `ChessRing.CellCenter` |
| Board surface z / grasp target z | 32.25 / ≈43 | `ChessRing` |
| **Reach ceiling** | **x ≈ shoulder + 18 → −13.8 @ Back=26** | between rank 1 (−17) and rank 2 (−12) |

### The reach grid at `SeatSitBack`=26 (units short; `ok` = reachable)

```
       a      b      c      d      e      f      g      h
   8  +35.3  +33.3  +31.7  +30.5  +29.8  +29.6  +29.8  +30.5
   7  +31.1  +28.9  +27.1  +25.8  +25.0  +24.7  +25.0  +25.8
   6  +26.9  +24.5  +22.6  +21.2  +20.3  +20.0  +20.2  +21.1
   5  +22.9  +20.3  +18.2  +16.6  +15.5  +15.2  +15.5  +16.5
   4  +19.1  +16.3  +13.9  +12.0  +10.9  +10.4  +10.8  +11.9
   3  +15.6  +12.4  + 9.7  + 7.6  + 6.2  + 5.7  + 6.2  + 7.5
   2  +12.4  + 8.9  + 5.8  + 3.4  + 1.7  + 1.1  + 1.7  + 3.2
   1  + 9.7  + 5.8  + 2.3   ok     ok     ok     ok     ok
```

Only d1–h1 are honestly reachable. Half of White's OWN back rank isn't; the far rank is 30–35
short.

### Why none of the knobs save it

- **Scoot (`SeatSitBack`)** — bounded by the table foot plate. At Back=26 the feet sit at x −17.9,
  **2.9u** from the foot plate edge (−15); one more rank of scoot and the knees/feet hit the table
  base. Buys ~rank 2 and stops.
- **Reach clamp (`HandReach` sphere, built then rejected)** — makes far targets "reachable" by
  pulling them onto the reach sphere, but that **collapses all six far ranks onto the ~rank-2
  line**: the hand parks there for every far piece and never moves. This is the trap in the
  `gambit_terry_probe` grid reading all `ok` — the probe measures reach to the *clamped* target,
  trivially satisfied; the clamped target for a8 (real x+17) is x−14. It looks frozen because it is.
- **Height (`SitOffsetHeight`, hover/grasp/lift)** — vertical trims don't move the horizontal ceiling.
- **Arm length** — baked into the model; nothing lengthens it (see engine notes).

---

## Engine-source reality (`sbox-public`, file:line)

**Arm chain & no stretch.** The right-arm IK chain is `arm_upper_R → arm_lower_R1 → hand_R1`
(`citizen/prefabs/citizen_ikdata.vmdl_prefab:226-247`) — the clavicle is NOT in it, so the chain
**root is the shoulder**, pinned by the spine. The solver is `CSolveIKChainAnimNode`,
`IKSOLVER_TwoBone`, default settings (`citizen.vanmgrph:30345-30350`, +3 more). A full-graph
search for `stretch`/`soften`/`reachlength`/`chain_length` finds **nothing** on the active arm
path (the only `soften_*` fields are on the disabled legacy `IKChainOld`,
`citizen_ikdata.vmdl_prefab:16`). **So a target past ~20u → the two bones straighten and the hand
stops short; the solver cannot drag the shoulder/root.** `SetIk` itself is pure param-passing
(`SkinnedModelRenderer.Parameters.cs:163-171`).

**Bone names** (for future diagnostics): the eye bones are `eye_L`/`eye_R`, not `eyes`
(`citizen_weightlistlist.vmdl_prefab:56,60`); spine chain is `pelvis, spine_0..2, neck_0, head`.
That's why the ruler's `eyes` lookup missed — use `head` or `eye_R`.

**Lean is almost nothing for a seated idle.** There is no dedicated lean param. `aim_body` pitches
the spine toward the look target (`citizen.vanmgrph:88747`), fed while seated by
`BaseChair.UpdatePlayerAnimator` (`BaseChair.cs:243-246`) — but the **idle** upper-body aim node
clamps pitch to **5°** (`citizen.vanmgrph:2495,2524`), ≈ **~2u** of shoulder-forward travel. The
80–90° pitch nodes (`:2270,2326`) are **weapon-aim** states, not the unarmed seated idle. Head aim
allows 140° but the head carries no reach.

**Sit poses.** `sit` enum `not_sitting, sitting_01..03, sitting_ground_01..04`
(`citizen.vanmgrph:89246`). `ChairForward = sitting_02` (`BaseChair.cs:10-20`). Whether
`sitting_02` leans the torso forward — and how much reach that buys — is in the **binary
animation clip and is not readable on this host**. ⚠️ **Editor check worth doing** before any
future attempt: does `sit=2` visibly lean the shoulders over the table?

**Escape hatch (risky).** `SkinnedModelRenderer.SetBoneTransform` → `SceneModel.SetBoneOverride`
(`SkinnedModelRenderer.Bones.cs:172-178`) can override a spine bone each frame to *fake* a lean
(moving the whole upper-body subtree, IK root included). Caveats from source: it's a **full
world-transform override applied after the animator**, so it must be re-applied every `OnUpdate`,
and whether it **composes cleanly with the hand IK in the same frame is unverified here**. This is
the only path to a big lean, and it's a spike, not a knob.

**`BaseChair` offers no forward reach.** Its only seated spatial knob is `sit_offset_height`
(vertical ±12u, `BaseChair.cs:38,236`) — no `sit_offset_forward`, no lean, no reach. (Gambit
doesn't use `BaseChair`, but it confirms the engine has no primitive to borrow.)

**Max plausible reach, every lever combined:** arm ~20u + an aggressive (unverified, ugly) forced
lean ~10u ≈ **~30u from the shoulder**, landing the hand near **x −2…+3 — the middle of the
board**. **Ranks 5–8 can never be reached** without the shoulder leaving the chair.

---

## Rescaling the board/pieces to the arm

The opposite lever: shrink the board to the arm. The citizen is a **fixed** ~72u and shared with
the whole lobby, so only the table assembly can scale. Uniformly scale it by **k** vs today. The
catch: **the scoot limit shrinks with the board** (foot plate at −15k), so a smaller table lets
you sit only proportionally closer. With shoulder ≈ −(Back+7), reach ≈ 18u, min Back = 15k+8:

```
reach ceiling  x_ceiling = shoulder + 18 = 3 − 15k      (at max scoot)
White's ranks  x = (rank−3.5)·4.875·k  →  r1 −17.06k … r8 +17.06k, centre 0
reachable when  x_rank ≤ x_ceiling
```

| reach through… | needs board scale k ≤ | board vs today |
|---|---|---|
| your own near rank (r1) | any | — |
| r2 | ~1.0 | today (marginal) |
| r3 | 0.39 | 39% |
| **r4 (your own half)** | **0.24** | **24%** |
| the centre | 0.20 | 20% |
| the far half | <0.1 | <10% |
| opponent's back rank (r8) | 0.09 | 9% |

**Even reaching your OWN half needs the board at ~¼ size**; across needs ~⅒. At quarter-scale the
pieces are ~¼ tall and the citizens loom over a doll's board — not something that reads as chess.
(A big forced lean, ~30u reach, relaxes these by roughly a third — reaching own-half at ~⅓ scale
instead of ¼ — still unplayable, and the lean is unverified.)

**And it's a SHARED, centered board.** `CellCenter` puts x=(rank−3.5)·cell, symmetric about x=0;
both seats mirror across it (White −X, Black +X). You cannot slide it toward one player without
sliding it from the other — **no scale or offset makes the whole board reachable by both.**

**The trap in the naive shrink:** the reachable zone is a **fixed x-band (x ≤ ~−13)** owned by the
unscaled seated avatar, *independent of both `BoardSize` and `TableScale`*. Shrinking the board
around x=0 pulls the near rank **inward** (−17 → toward 0), i.e. **out of the reach band**, while
the far rank stays on the +x side and never enters it — so a board-only shrink makes reach
*worse*. To actually fit the board under the hand you must **also pull the seat in**
(`SeatSitBack`, and the camera-side `SeatOrbitRadius`/`SeatSpotX` that set where the body and its
sightline live), which is what cascades into the ring radius, both seat cameras, the walk-up spot
and the chair — the milestone-sized retune.

**Blast radius of a scale change (not one knob — milestone-sized):**
- `TableScale` (×1.5) multiplies the whole table stack, board, **pieces** (`PieceScale =
  TableScale·BoardSize/26`), **trays** (`TraySlotLocalPosition`), **clock** (`BuildStationClock`)
  and **plaque** — those follow it for free (all ride `× s`).
- `BoardSize` (26) drives `PieceScale`, the frame (`BoardSize+3`), `MarginInnerY`, tray length and
  `cell` — also mostly self-consistent.
- **Independent — would NOT follow and must be re-tuned or would break:** the **seat cameras**
  (`SeatOrbitRadius` 56 / `SeatPitch` 55 / `BuildSeatAnchor`) — a smaller board sits tiny in an
  unchanged frame; the **ring layout / room clamp** (`RingRadius`, `seatFootprint =
  SeatOrbitRadius·cos(SeatPitch)`); the **engaged-UI rect** (`UiFit`, `ScreenFractionRect`); the
  **seated body/plant** (`SeatSitBack`, the chair geometry); and the **spectator board**
  (`SpectatorBoard3D` has its OWN `CellSize`/`PieceScale`, unrelated). And the **citizen never
  scales** — which is the whole reason the reach problem exists.

**Verdict:** rescaling does not rescue reach at any legible size, and it's a milestone-sized
retune of framing + layout, not a one-knob change. It reinforces the decision.

---

## What M13 shipped, and what got cut

**Shipped:** the seated bodies — `TerrySeated` gate, sit pose (`sit=1`), the physics un-plant
(`SetSeatedPhysics`), the chair (`StationChair`), the seat-camera blend, and the head-trim
(`TrimSeatedAvatar`) that keeps the camera out of the skull. Gaze and blink are the stock Citizen
idle; nothing in Gambit drives them. This is "two people at a board," and it stands.

**Cut** (this commit — the whole reaching-hand path proven impossible above):

| file / symbol | was |
|---|---|
| `World/SeatedTerry.cs` (deleted) | the IBoardGame-seam watcher that drove the hands + the `gambit_terry_probe` sweep |
| `Code/Chess/TerryPose.cs` (deleted) | the pure, harness-proven hand state machine |
| `World/TerryCommands.cs` (deleted) | `gambit_terry` ruler + reach grid, `gambit_terry_probe` — the measurement tools that produced this doc |
| `LobbyPlayer.ApplyHandPose` / `ClearHandPose` / `ShoulderLocal` / `LastHandTarget` / `LastHandIkTarget` / `HandState` [Sync] / `PackHand` / `UnpackHand` | the IK targeting path + the hover/selection sync that fed it |
| `ChessBoardView.PublishHandState` / `SquareIndexOf` | published the local player's hover/selection for observers' hands |
| `ChessRing` `HandReach` / `HandLift` / `HandIdle*` / `HandGripOffset` / `HandPitch` / `HandDiscardReach` / `HandChaseRate` / `TrayHandLocalPosition` + the `SeatedTerry` wiring | the hand-targeting knobs |

**Recovery is one checkout.** All of it is preserved at the annotated tag **`terry-hands-final`**
(commit `ccde34f`) — the IK path, the state machine, and the measurement tools intact. M14 starts
by cherry-picking or reading from there, not by re-deriving. `git show terry-hands-final:client/Code/Chess/TerryPose.cs` et al.

---

## M14 plan: give the terries hands on the pieces (or decide not to)

The goal is a hand that touches the pieces, or an honest decision that the bodies are enough.
M13 proved the naive version is geometrically dead, so M14 is **three ranked approaches, each
gated on a spike done FIRST** — do not re-instate the full IK path and *then* discover it can't
work. Ordered cheapest-and-most-certain first.

**Approach A — near-half-only reach (cheap, feasibility already proven).**
- **Scope:** the hand reaches and touches pieces on **ranks 1–2 (your own side)**, which M13
  measured as genuinely reachable at `SeatSitBack=26` (see the reach grid above). Far moves keep
  `ChessBoardView`'s existing piece-slide, which has walked pieces since M11 — the hand simply
  doesn't animate a reach it can't make; it idles for unreachable squares.
- **Spike (a TASTE call, room-gated — this host cannot answer it):** does a hand that touches
  near pieces but sits idle for far ones read as *playing*, or as *broken/half-finished*? Restore
  `ApplyHandPose` from `terry-hands-final`, **clamp its domain to the reachable band** (drop the
  reach-clamp-onto-a-sphere hack — for out-of-band squares, don't move the hand at all), and look
  at it in the editor.
- **Gate:** if a partial reach reads worse than no reach, **stop here** and M14 ships nothing new —
  the bodies were already the deliverable. This is a real possible outcome, not a failure.
- **Cost if kept:** re-add the hover/selection sync (`HandState`/`PackHand`/`PublishHandState`,
  all recoverable from the tag) so an observer floats a hand over the square the mover is thinking
  about; the rest is the restored path with a tighter domain.

**Approach B — `SetBoneOverride` fake-lean (medium risk, reaches board CENTER, never the far ranks).**
- **The unknown that gates everything:** does `SkinnedModelRenderer.SetBoneTransform` →
  `SceneModel.SetBoneOverride` on a spine bone **compose with the hand IK in the same frame**?
  The override is applied *after* the animator; whether the two-bone IK then re-solves against the
  **leaned** shoulder or the animator's original one is **unverified** and decides the whole
  approach. (Source basis: `SkinnedModelRenderer.Bones.cs:172-178`, and the no-stretch chain facts
  in the engine-source section above.)
- **Spike:** override `spine_2` (or the pelvis subtree) forward ~10u each `OnUpdate`, then `SetIk`
  the hand at a center square, and measure `hand_R` (the old `gambit_terry` ruler technique) to see
  whether reach actually extended. Also eval: does the leaned torso clip the tabletop or look
  grotesque?
- **Best case:** honest reach to ~**x −2…+3**, i.e. ranks 1–4ish. **Ranks 5–8 remain
  unreachable** without the shoulder leaving the chair — B does not change that ceiling, it raises
  the floor.
- **Gate:** if the IK doesn't compose or the lean reads as broken, **fall back to A**.

**Approach C — longer-armed seated model (the only full-board fix, biggest cost).**
- **Research, before any art:** can a **seated** citizen get a modified arm **per-instance**
  without touching the shared roaming citizen (one model, whole lobby)? Candidates: a separate
  seated model, a per-instance **bone scale** on the seated renderer, or an additive arm-length.
  The open question is whether the two-bone IK **honors** a scaled bone as extra reach or
  re-normalizes it away (the animgraph has **no stretch** — a scaled bone may or may not read as a
  longer effective segment). Answer this in a spike before committing to a model pipeline.
- **Art:** CC0 only (repo rule). The Poly Haven set on file is *pieces*, not a citizen; a modified
  citizen arm is the realistic path, not a new body.
- **Gate:** high cost, only pursued if A and B are both rejected **and** physical full-board reach
  is judged worth it. Most likely C is documented-and-declined, like several PLAN.md rows.

**Cross-cutting, re-verify LIVE before any approach (house rule: derive, don't recall):**
- **The `sitting_02` lean question M13 never answered.** M13 used `sit=1` (`sitting_01`). Whether
  `sit=2` visibly leans the shoulders over the table — buying reach for free — is in the binary
  clip and needs an **editor look**. If it leans meaningfully, it changes the math for A and B.
- **The seat-framing cascade.** Moving the seat in, or shrinking the board, is **not one knob** —
  the blast-radius list above is the checklist: `SeatOrbitRadius`/`SeatPitch`/`BuildSeatAnchor`,
  `RingRadius`/`seatFootprint`, `UiFit`/`ScreenFractionRect`, the chair geometry, and the
  independent `SpectatorBoard3D`. Any approach that repositions the sitter pays this.
- **The info boards.** Per CLAUDE.md, if M14 changes what a player sees at the table, `CenterInfoPanel`
  and `InfoScreen`'s Welcome branch describe it and must not be left saying otherwise.

**If M14 does nothing:** that is a legitimate close. The bodies sit, look right, and the game plays;
"terries playing chess" is already true in every sense except a hand on the wood.
