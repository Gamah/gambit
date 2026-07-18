# The half-rise: terries that pick up the pieces (M14, second attempt)

> **MILESTONE PAUSE (2026-07-18) — MVP works, looks janky. Read this first.**
>
> The mechanics are DONE and verified across two clients: the terry leans/rises to a
> square, the wrist follows the arm, the piece waits on its square until the hand grabs
> it, rides the hand through the move, and spectators see all of it — including real
> lichess games, via the spectator mirror. Every knob is an inspector slider
> (**TerryTuning** in lobby.scene, saved values = shipping truth). No hanging objectives.
>
> **The next session's job is purely the LOOK.** Start by diffing this branch against
> master; the goal is "good enough", and the current state is extremely janky. Known
> jank, in the order it was observed:
> - the motion quality: everything is exponential-chase easing — no anticipation, no
>   settle, reads robotic; the rise/lean amounts are taste-tunable but the MOTION is not
> - finger polarity (holdtype_pose_hand open/closed ends) still unverified from M13
> - a capture's victim slides to the tray on its own, not synced to the hand's
>   clear-the-victim gesture (v1 scope cut, documented below)
> - the brace hand and foot steps engage/release visibly (eased, but not choreographed)
> - mirrored lichess boards show no clocks; one unexplained one-off NRE
>   (OnParametersSetAsync, no stack) on a joiner
>
> **"Better methods for testing and tuning" is the request.** Candidates for the next
> session's first hour: a `gambit_terry_replay` that loops a scripted move sequence
> (near/far/capture) at a table so sliders can be tuned against a repeating motion
> instead of playing games; a fixed spectator camera bookmark; and the doctor pattern
> extended to score motion (jerk/settle time), not just reach.
>
> If procedural posing can't reach "good enough", the researched fallback is authored
> clips via `DirectPlayback.Play(name, target, heading, interpTime)` — the engine
> supports bespoke reach animations with root-motion retargeting; that is a design fork
> to decide deliberately, not drift into.

**Branch `m14-terry-halfrise-ik`.** The reaching-hand idea was nuked (5fd4157) because a
SEATED citizen can't reach: ~20u arm, far corner ~35u away, and every seated lever combined
lands mid-board (the old SEATED-HANDS-REACH.md, recoverable at `origin/m14-terry-hands-spikes`,
has the proof). This attempt keeps everything M14 *proved* and removes the one assumption that
killed it: **that the pelvis stays on the chair.**

## The mechanism, in one paragraph

When a square is past even the leaned arm, the terry **half-rises**: the pelvis bone is
overridden up-and-forward toward the piece — the same `SetBoneTransform` subtree-carry M14
proved on `spine_2`, one bone higher — bounded by the **legs** (feet planted, allowed a small
step, never into the table's foot plate) instead of by the chair. The feet stay planted via
the engine's own `foot_left`/`foot_right` animgraph IK (game-facing; confirmed in
`citizen.vanmgrph` — four IK targets exist, not two), the off hand braces on the tabletop via
`hand_left`, and the moved piece **rides the rendered hand bone** through
Lifting/Carrying/Dropping, so the room sees a terry pick a piece up and put it down.

**The one trick that makes four IK chains coexist with bone overrides:** the animgraph solves
IK *before* overrides apply, so every IK target is aimed at **(true target − the override
translation its chain will ride)** — feet ride the pelvis; hands ride pelvis + spine. The
override then carries the solved limb exactly onto the true target. Translation-only, no
rotations anywhere, so the compensation is a vector subtraction and the mechanism is the one
already proven in-editor.

## The proof (dotnet harness, this host)

`Code/Chess/HalfRise.cs` is the whole geometry, Sandbox-free, driven over all 64 squares from
both seats with the measured M13 skeleton — at **`SeatSitBack = 36`**, the pre-M13-scoot seat
(see below):

```
                 a ... h
   1-8                       all ok  (64/64, residual 0.0)
```

**64/64 squares honestly reachable, zero residual** — with the LIVE-measured skeleton from
the doctor dump (pelvis z 16.6, not the guessed 23 — the lower pelvis buys the leg triangle
far more headroom), the CHOSEN foot plants, `MaxLean 12`, `RiseLift 0.3`. Two earlier grids
(51/64 then 54/64) predate the live measurements; the doctor's inputs dump is the authority
for skeleton numbers now. Three insights the earlier cuts got wrong, all caught by
harness-vs-live disagreement:

- The rise must be mostly **horizontal** — leg budget is the scarce resource.
- The foot step must be **exact** (leg-triangle arithmetic), and the rise search a
  descending scan (the foot-plate clamp makes feasibility non-monotone in corners).
- **Never feed the planner the animated ankles**: the sit pose tucks the feet ~25u behind
  the pelvis, which spends the whole leg budget before any rise. The plants are chosen
  (pelvis + a step forward), and the foot pins ease there from the tucked pose.

**`SeatSitBack` is back to 36 (M13 scooted it to 26 for seated reach).** The scoot buried
the seated chest a third of the way into the tabletop — invisible from this host and from
the seated player's own first-person view; the first joined client to look reported it. The
half-rise makes the scoot pointless, and the harness reads BETTER at 36 (54 vs 51): the
longer horizontal run lines the rise up with the far squares.

## What the editor sessions answered (engine facts, keep)

1. **Bone-override TRANSLATIONS carry the whole subtree, exactly** — pelvis and spine
   overrides landed to the decimal every run. **ROTATIONS do not carry child bones**
   (pitch budgeted 15.8u of shoulder travel; ~3 materialised). Both measured, not read.
2. The animgraph solves IK before overrides apply — pre-compensated targets work; a
   ~5u post-override native warp remains (procedural bones by elimination) and the hand
   SERVO steers it out rather than modelling it.
3. The sit pose tucks the ankles ~25u behind the pelvis — never feed animated ankles to
   a reach planner; choose the plants.
4. The spectator mirror works (mirroring=True on a joiner during the host's browser-link
   lichess game), and so does the whole networked hand path.

## Decisions already made (don't relitigate)

- **Real pick-up**: the piece rides the rendered `hand_R` bone (release = short settle slide,
  so the abandon rule degrades to a glide, never a teleport). The spectator wall keeps the
  plain slide — it has no terries.
- **v1 carries the attacker only**; a capture's victim keeps its existing tray slide. The
  two-trip choreography (hand clears the victim first) is modelled in TerryPose already;
  synchronising the victim's slide to the hand is a later polish, not a blocker.
- **The brace is honest or absent**: offered only when the left arm can actually reach it.
- **One rotation ships: the torso YAW** (`gambit_terry_yaw`, capped 30°, eased in with the
  rise) — two-bone IK can never turn the chest, so the turn is authored on the spine
  override. Whether override rotation propagates to the arm subtree is STILL unproven — the
  yaw is safe anyway because the **hand servo** trues the fingertip against the true ask
  regardless. The same argument does NOT extend to a torso pitch: pitch moves the head/
  camera-relevant mass, so spike it behind a lever first if wanted.
- **The servo is the answer to "the model is wrong somewhere"**: every stage we control was
  proven exact (overrides land to the decimal, the animation-domain IK nails its compensated
  target) and the final hand still warped ~5u — some post-override native stage no API
  reads. Don't model the unmodellable; measure last frame's error and steer it out.
