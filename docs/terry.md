# Terry

Seated bodies at the tables, the hands that play the moves, and the `gambit_terry_*` knobs.

## Switches and where values live

- **`ChessRing.TerrySeated`** is the kill switch: false is the world with no seated bodies, where
  the local avatar is simply not drawn while seated. It must stay a full revert. Under it,
  `SeatedHandSpikes.HandsOn` (bodies, no hands) and then `HalfRiseOn` (hands, no rise); the
  console chain is `gambit_terry_hands` → `gambit_terry_rise`.
- `SeatedTerry.ForceHidden` hides the bodies in PLAY MODE 2D (`ChessRing.ApplyPlayModeSetting`);
  the effective switch is `TerrySeated && !ForceHidden`.
- **Seat and chair knobs are code defaults on a runtime-built `ChessRing`**: edit and hotload.
- **Hand knobs live on `TerryTuning` in `lobby.scene`, and scene values rule there.** A new code
  default on a serialized slider does nothing. `TerryTuning` pushes into the
  `SeatedHandSpikes`/`TerryPose` statics on change, not every frame, so the console levers and
  the diagnostics that drive those statics mid-run are not fought.
- Inert `TerryTuning` sliders (nothing reads them for a real move): `HoverChaseRate`,
  `HandChaseRate`, `HandHoldSeconds`, `CarryHang`, `GrabRadius`, `LiftHeight`, `HoverHeight`,
  `GraspHeight`. Grasp height is piece-relative (`GraspClearance`), not board-relative.
- The bodies and hands are cosmetic; no player-facing copy describes them.

## The mechanism

A seated citizen's arm cannot reach the far ranks from the chair. When a square is past even the
leaned arm, the terry **half-rises**: the pelvis bone is overridden up and forward toward the
piece, bounded by the **legs** (feet planted, a small step allowed, never into the table's foot
plate) rather than by the chair. The feet stay planted via the animgraph's own
`foot_left`/`foot_right` IK (`citizen.vanmgrph` has four IK targets), and the off hand braces on
the tabletop via `hand_left` only when the left arm can actually reach it. The torso yaws toward
the piece (`TorsoYawMax`), authored on the spine override, because two-bone IK can never turn the
chest.

**How four IK chains coexist with bone overrides:** the animgraph solves IK *before* overrides
apply, so every IK target is aimed at **(true target − the override translation its chain will
ride)**: feet ride the pelvis, hands ride pelvis + spine. The override then carries the solved
limb onto the true target. Translation only, so the compensation is a vector subtraction.

The planner (`Code/Chess/HalfRise.cs`) is Sandbox-free:

- The rise is mostly **horizontal**; leg budget is the scarce resource.
- The foot step is exact (leg-triangle arithmetic), and the rise search is a descending scan,
  because the foot-plate clamp makes feasibility non-monotone in corners.
- **Never feed the planner the animated ankles**: the sit pose tucks the feet ~25u behind the
  pelvis, which spends the whole leg budget before any rise. The plants are chosen (pelvis + a
  step forward) and the foot pins ease there from the tucked pose.
- `SeatSitBack` (36) keeps the seated chest out of the tabletop; the rise, not a scoot, provides
  the reach.

## Engine facts, measured in the editor

1. **Bone-override translations carry the whole subtree, exactly.** **Rotations do not carry
   child bones.**
2. IK is solved before overrides apply, so pre-compensated targets work. A ~5u post-override
   native warp remains; the hand **servo** measures last frame's error and steers it out rather
   than modelling a stage no API reads. The torso yaw is safe for the same reason; a torso pitch
   is not covered by that argument (`TorsoPitchMax` defaults to 0).
3. The sit pose tucks the ankles behind the pelvis (above).
4. The spectator mirror carries the whole networked hand path, lichess games included.

## The gesture

- **Hands rest unless a move is confirmed** (the ply advanced). Nothing extra crosses the wire:
  a confirmed move is already relayed.
- **One clock.** The view's hold-then-slide is the only timeline, and the hand derives from the
  live performed-piece GameObject every frame (`ChessBoardView.PerformedPiece` →
  `ApplyHandPose`): deadline approach while the piece holds, hard-glued above it while it slides,
  ease home after it lands. The wrist is effectively a child of the piece.
- **Z is locked to the piece's own bounds top** (+ `GraspClearance` + `ChessRing.HandLift`). The
  reach sphere is sliced at the target's Z and every shortfall spent horizontally, so a short
  hand stops short, never floats above; the servo's vertical channel runs ungated during moves.
- **Budgeted deadline stages** (`TerryPose`): `ReachTime` 0.12 + `LiftTime` 0.18 + `TravelTime`
  0.35 + `DropTime` 0.2, scaled by `GestureSpeed`; each stage arrives or snaps, and a hand behind
  may rush up to `MaxRush`. The return is `FadeOutTime` (0.45) at `LobbyPlayer.ReturnChaseRate`.
- **A capture is a plain gesture** (`CaptureTime == MoveTime`): the hand follows only the taking
  piece, and the victim slides to its tray on its own the moment the move starts.
- **Reality always wins**: a new diff snaps stale board slides forward, a premove reply does not
  abandon the trigger gesture, and a same-frame collapse fires both hands
  (`ChessGame.UciFromEnd`).
- Finger poses drive `holdtype_pose_hand`; its open/closed polarity is read off the graph, not
  verified.

Tuning surface: the `TerryPose` stage constants and `GestureSpeed`; `ReturnChaseRate`,
`GraspClearance`; rest anchors `ChessRing.HandIdleX/Y/Z`; rise feel (`RiseChaseRate`, `MaxRise`,
`RiseGrace`); roll and wrist sliders.

## Where everything lives

| file | role |
|---|---|
| `Code/Chess/HalfRise.cs` | the planner (Sandbox-free) |
| `Code/Chess/TerryPose.cs` | gesture state machine and tempo constants |
| `Code/World/SeatedTerry.cs` | per-station driver, doctor/sweep/probe/net dumps |
| `Code/World/LobbyPlayer.cs` | runtime: `PlanRise`, `ApplyRiseOverrides`, servo, wrist rotation |
| `Code/World/ChessBoardView.cs` | hold-for-hand and the performed piece |
| `Code/Game/LichessGameController.cs` | spectator mirror |
| `Code/World/TerryTuning.cs` + `lobby.scene` | the inspector surface |
| `Code/World/SeatedHandSpikes.cs` | the statics and console levers |
| `scripts/halfrise_harness/` | the planner proof over all 64 squares from both seats (`dotnet run`, must print `ALL CHECKS PASSED`) |

## Console commands (`gambit_terry_*`)

Dev tools, not player-facing. Every value knob mutates a session-local static on
`SeatedHandSpikes` (the shipped values are `TerryTuning`'s); diagnostics dump or drive the local
seated hand. They carry no network authority: mutations re-apply on local and proxies, so at
worst a client reshapes seated arms on its own screen. Sit down first.

**Master switches:**

| Command | Effect |
|---|---|
| `gambit_terry_hands` | toggle the seated hands (`HandsOn`); off is bodies only |
| `gambit_terry_rise` | toggle the half-rise; off = seated lean only |
| `gambit_terry_natural` | toggle natural lean; off falls back to the isolated levers |
| `gambit_terry_servo` | toggle the closed-loop correction for the post-override warp |
| `gambit_terry_brace` | toggle the off-hand table brace |
| `gambit_terry_clamp` | out-of-reach mode: sphere clamp ⇄ idle band (compare only) |

**Rise / lean values:**

| Command | Value |
|---|---|
| `gambit_terry_maxrise <u>` | max pelvis rise |
| `gambit_terry_step <u>` | max foot step (0 = feet welded; the rise shrinks instead) |
| `gambit_terry_risechase <k>` | rise chase rate /s (lower = statelier) |
| `gambit_terry_grace <u>` | slack left to the slide before the body rises |
| `gambit_terry_lift <k>` | Z rise per unit forward (0 = horizontal glide) |
| `gambit_terry_maxlean <u>` | max natural-lean shoulder travel |
| `gambit_terry_natlbone <bone>` | natural-lean bone (`spine_2` default) |
| `gambit_terry_leg <u>` | override the pelvis→ankle leg budget (0 = live measurement) |
| `gambit_terry_margin <u>` | reach margin inside the measured arm |
| `gambit_terry_band <x>` | idle-band reach \|x\| ≥ (station-local) |
| `gambit_terry_pitch <deg>` | torso pitch cap (0 = uncapped glide) |
| `gambit_terry_yaw <deg>` | torso yaw cap toward the piece |

**Hand / wrist posture:**

| Command | Value |
|---|---|
| `gambit_terry_wristdrop <deg>` | grasp curl past the forearm bearing (capped at `ring.HandPitch`) |
| `gambit_terry_roll <deg>` | hand roll, which swings the elbow out of the torso |
| `gambit_terry_grasp <u>` | wrist clearance above the moved piece's top (negative sinks in) |

**Comparison levers** (alternative approaches, kept to re-measure):

| Command | Value |
|---|---|
| `gambit_terry_lean <u>` | manual torso lean on the lean bone each frame |
| `gambit_terry_leanbone <bone>` | that lean's bone (`spine_2` vs `arm_upper_R`) |
| `gambit_terry_armscale <k>` | best-effort arm bone scale (measures whether IK reads it) |
| `gambit_terry_sit <1-3>` | sit pose (1 = `sitting_01`, the default) |

**Diagnostics and drivers:**

| Command | What it does |
|---|---|
| `gambit_terry` | the seated-hand chain as this machine sees it |
| `gambit_terry_net` | the same from the network angle; a spectator needs `mirroring=True` |
| `gambit_terry_bones` | the citizen's real bone names, in station-local numbers |
| `gambit_terry_rise_dbg` | one-shot full-pipeline dump on the next far reach |
| `gambit_terry_probe` | sweep all 64 squares (forces hands on): which squares the arm lands |
| `gambit_terry_scholars` | play Scholar's Mate with the hand, to tune placement live |
| `gambit_terry_doctor` | automated far-rank reach trials → a verdict table; applies the winner |
| `gambit_terry_sweep` | every comparison lever → one verdict table |
| `gambit_terry_spikes` | live lever state and which lever to pull for which reading |
