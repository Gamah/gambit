# The half-rise: terries that play the moves (M14 — SHIPPED as the mechanism doc)

> **MVP PASSED (owner judgment, 2026-07-19). M14 stays as a feature; what remains is
> TUNING — timing and positions — tracked as a PLAN.md row.** Five in-editor rounds of
> owner reports drove the final shape; the amendment stack below records each decision and
> its why, and the ARCHITECTURE IS SETTLED — tune knobs, don't re-litigate the shape:
>
> - **Hands rest unless a move is confirmed** (thinking hand cut; nothing on the wire).
> - **One clock; the wrist is a CHILD of the piece** (carry/grab glue deleted, not tuned).
> - **Z locked to the piece's own bounds top** (+ GraspClearance); the reach sphere is
>   always sliced at the target's Z — a short hand stops SHORT, never floats ABOVE.
> - **Budgeted deadline stages** — Reach 0.12 + Lift 0.18 + Carry 0.35 + Drop 0.2 ≈ 0.85s,
>   arrive per stage or snap; return fast (FadeOutTime 0.45, ReturnChaseRate).
> - **A capture is a plain gesture**; the victim tray-slides in parallel on its own.
> - **Reality always wins**: a new diff snaps stale board slides forward; a premove reply
>   does not abandon the trigger gesture; a same-frame collapse fires BOTH hands
>   (`ChessGame.UciFromEnd`).
>
> **Tuning surface** (the PLAN row's detail): `TerryPose` stage consts + `GestureSpeed`;
> `ReturnChaseRate` / `GraspClearance` (LobbyPlayer consts); rest anchors `HandIdleX/Y/Z`;
> rise feel (`RiseChaseRate`, `MaxRise`, `RiseGrace`); roll/wrist sliders. **Scene rule:**
> TerryTuning sliders serialize in `lobby.scene` and scene values RULE — a new code
> default on a serialized slider does nothing. Inert sliders (prunable): `HoverChaseRate`,
> `HandChaseRate`, `HandHoldSeconds`, `CarryHang`, `GrabRadius`, `LiftHeight`,
> `HoverHeight`.

> **AMENDMENT (2026-07-19, owner decision): the thinking hand is CUT.** Hands rest on the
> table unless a move has been CONFIRMED (the ply advanced) — no hover tracking, no
> selected-piece parking, no lazy drift. Removed wholesale, not gated: `TerryPose.Advance`
> no longer produces Hover/Selected (the enum values stay, probe-only), and the wire state
> that carried it (`LobbyPlayer.HandState` + Pack/Unpack + `ChessBoardView.PublishHandState`)
> is deleted — a confirmed move is already relayed, so nothing extra crosses the wire now.
> Every gesture starts from rest; the driver's fast gesture chase (HandChaseRate) covers the
> approach and the piece's slide-wait covers the timing. This moots the banner's "lazy
> hover tempo" half of item 4 below — only the committed-move budget half still applies.
>
> **Further owner decisions, same date:**
> - **The hand is LOCKED in Z for the whole gesture** (float in to piece height, then flat;
>   pick-up/put-down height variance is an explicit later). Three mechanisms enforce it: the
>   timeline runs entirely at GraspHeight (LiftHeight/HoverHeight sliders inert), the
>   planner and the final solver-domain clamp slice the reach sphere at the target's Z and
>   spend every shortfall horizontally (so neither our clamp nor the ENGINE's own two-bone
>   clamp can lift the wrist), and the servo's vertical channel now runs UN-gated during
>   moves (a locked-Z ask makes vertical error warp by definition).
> - **The capture choreography below is SUPERSEDED**: no Clearing/Discarding prologue, no
>   DropAndSwap exchange. The hand follows only the TAKING piece (a capture is the same
>   gesture as a move, CaptureTime == MoveTime); the victim lerps to its tray on its own,
>   simultaneously, the moment the move starts (the view already ran the victim's tray
>   slide with no hand-hold — only the hand's shuttle was cut).
> - **THE WRIST IS A CHILD OF THE PIECE — the carry layer is deleted, not tuned.** The
>   owner named the fundamental disconnect: piece slides and hand timeline were two
>   independent authorities glued by grab-on-contact / ReportHandCarry (piece riding the
>   hand bone) / piece-led placement / the release settle, and every look-pass timing bug
>   was that glue tearing. Now there is ONE clock — the view's hold-then-slide (the hold
>   derived from the hand's approach deadlines) — and the hand derives from the live
>   performed-piece GameObject every frame (`ChessBoardView.PerformedPiece` →
>   `ApplyHandPose`): deadline-approach while the piece holds, HARD-glued above it while
>   it slides, ease home after it lands. Reach limits still clamp downstream (a hand that
>   can't have a far piece shadows it from as close as the arm gets, at piece height).
>   CarryHang/GrabRadius/HandHoldSeconds sliders are inert. Gesture stages are budgeted
>   deadlines (Reach 0.25 + Lift 0.18 + Carry 0.35 + Drop 0.2 ≈ 1s): stages when there is
>   time, a snap when there isn't, reality always wins.

> **SUPERSEDED — historical (2026-07-18 milestone-pause snapshot). Do NOT action this.**
> Kept only as the "why" behind the shipped shape; the amendment stack at the top of this
> file is the truth and OVERRULES everything below (the thinking hand was cut, the capture
> became a plain gesture with no DropAndSwap, v1 ships no finger pose, the two-tempo model
> was mooted). Every "next session's job" / "OPEN" / "specified by the owner" line here was
> either resolved in that stack or dropped. Read it for the jank causes it diagnosed, never
> as a task list.
>
> ---
>
> The mechanics are DONE and verified across two clients: the terry leans/rises to a
> square, the wrist follows the arm, the piece waits on its square until the hand grabs
> it, rides the hand through the move, and spectators see all of it — including real
> lichess games, via the spectator mirror. Every knob is an inspector slider
> (**TerryTuning** in lobby.scene, saved values = shipping truth). No hanging objectives.
>
> **The next session's job is purely the LOOK.** Start by diffing this branch against
> master. The bar, in the owner's words: clipping is fine, weird timing and small jank are
> fine — right now it looks like a COMPLETELY BUSTED GAME, and "good enough" means it
> stops looking broken. The jank, RANKED by the owner, with causes where known:
>
> 1. **Bent elbow.** Cause known: the doctor's margin verdict (8) pulls every ask deep
>    inside the reach sphere, so the arm never extends. With the servo truing the hand,
>    margin should drop back to ~2-3 — retest that FIRST, it may be one slider.
> 2. **Super kinked wrist.** The hand rotation is a fixed HandPitch (60°) + bearing yaw;
>    it needs to derive from the forearm's actual direction (or soften pitch with reach).
> 3. **Tempo** — partially fixed at the pause: the gesture clock was retimed from the M13
>    "slow on purpose" doctrine (proven backwards) to 0.73s moves / 1.21s captures, Rush
>    still compresses under fast play, and GestureSpeed is a TerryTuning slider. STILL
>    OPEN inside this item: **the piece moves after the hand starts to reset** — the
>    release-settle only starts once carry reports stop (after Dropping ends) and covers
>    whatever distance the clamped hand left; candidate fix is piece-led placement (release
>    the piece onto its square as Dropping STARTS, quick settle, hand follows it down).
> 4. **The tempo is TWO tempos, not one** (owner-specified, replaces the single
>    HandChaseRate story):
>    - **Hover/selection = lazy.** While a player is deciding — including having a piece
>      SELECTED — the hand may drift; a-file to h-file taking ~3s is fine. Selecting a
>      piece is NOT a move and stays on this tempo.
>    - **A committed move = a hard wall-time budget.** From the ply landing to the piece
>      set down — INCLUDING catching up from wherever the lazy hover left the hand —
>      the whole gesture completes inside one tunable budget, default ~1s. The catch-up
>      is part of the budget, so a lagging hand moves FAST to its pickup, not at the
>      hover drift rate. Implementation sketch: split HoverChaseRate (slow) from
>      GestureChaseRate (fast), and scale the phase durations down when the approach
>      distance eats into the budget. Captures get **up to 1.5× the base move budget**,
>      total, on the same catch-up rule.
>
> **The capture choreography, owner-specified — and it INVERTS the current timeline.**
> TerryPose today plays victim-first (Clearing → Discarding → then the attacker's move),
> on M13's "you cannot put a piece on an occupied square" doctrine. Overruled, explicitly:
>
> 1. Pick up the TAKING piece first — plain thumb+index "okay" pinch.
> 2. Carry it to the taken piece's square and set it down. **Both pieces simply share the
>    square for now** — how that looks is a later tune, not a blocker.
> 3. Then carry the TAKEN piece to the capture tray with the modified grip (index
>    extended, thumb+MIDDLE holding it).
>
> **The EXCHANGE between 2 and 3 is a single specific animation beat, not two gestures —
> and NOT a one-frame swap.** The distinction: the swap has real DURATION (the fingers
> visibly morph from pinch to middle-grip over the beat), but zero DWELL — the hand never
> stops, never rests, never returns to an idle pose between placing one piece and leaving
> with the other. The taking piece lands and the hand departs with the taken piece in the
> same continuous motion: the pinch is releasing the attacker WHILE the middle finger is
> closing on the victim, the grip morph overlapping the hand's own arrival-and-departure
> arc rather than inserted between them as a pause.
>
> So the timeline reorders to Lift → Carry → **DropAndSwap (one beat)** → Discard, the
> victim's tray slide must wait for the swap (today it fires the instant the FEN lands),
> and the exchange beat is the centre of the whole capture gesture.
>
> **The finger choreography, specified by the owner** (replaces the single
> `holdtype_pose_hand` blend, whose open/closed polarity was never even verified):
>
> - Every MOVE is a **thumb+index pinch** — the "okay" hand — from pickup to drop.
> - A CAPTURE starts with the same "okay" hand travelling to the TAKEN piece, then swaps
>   to the **modified "okay": index extended, thumb+MIDDLE finger holding the victim**,
>   held that way until the victim is dropped in the capture tray (then presumably back
>   to the plain pinch for the attacker's own pickup).
>
> Mechanism to investigate, in order: (a) the `FingerAdjustment_{L|R}{1-5}_{Bend|Curl|
> Roll|Spread}` procedural params (floats −60..60) — documented on the first-person arms
> graph; UNVERIFIED whether the third-person citizen graph has them; (b) per-bone finger
> overrides à la the engine's own VrHand (`SetBoneOverride` per phalanx) — remember the
> measured fact: override ROTATIONS don't carry children, so each phalanx bone needs its
> own override, which is exactly what VrHand does anyway. TerryPose's `FingerClose` float
> stays the timeline's signal; the driver maps it to whichever mechanism wins.
>
> **The authored-clips fork is OPEN** ("worth investigating"): `DirectPlayback.Play(name,
> target, heading, interpTime)` with the agreed concessions — map the board coarsely and
> always grab at a uniform height (~1.5 squares); fingers may clip/collide. That trades
> per-square precision for real motion quality, and the servo/carry machinery survives it.
>
> **Deferred by choice**: the `gambit_terry_replay` tuning loop — not until the above stop
> being broken ("probably worth just nuking until the above are in a better state").

**Where everything lives** (the 30-second map for a fresh session):
`Code/Chess/HalfRise.cs` planner (Sandbox-free) · `Code/Chess/TerryPose.cs` gesture state
machine + tempo constants · `Code/World/SeatedTerry.cs` per-station driver + doctor/sweep/
probe/net dump · `Code/World/LobbyPlayer.cs` the runtime (PlanRise / ApplyRiseOverrides /
servo / wrist rot; search "half-rise") · `Code/World/ChessBoardView.cs` carry + hold-for-hand
· `Code/Game/LichessGameController.cs` spectator mirror · `Code/World/TerryTuning.cs` +
lobby.scene the inspector surface · `Code/World/SeatedHandSpikes.cs` the statics + console
levers · **`scripts/halfrise_harness/`** the planner proof (`dotnet run`, must stay green).

**Shipped on `m14-terry-halfrise-ik`, merged to master.** The reaching-hand idea was nuked (5fd4157) because a
SEATED citizen can't reach: ~20u arm, far corner ~35u away, and every seated lever combined
lands mid-board (the old SEATED-HANDS-REACH.md, recoverable at `origin/m14-terry-hands-spikes`,
has the proof). This attempt keeps everything M14 *proved* and removes the one assumption that
killed it: **that the pelvis stays on the chair.**

## Console commands (`gambit_terry_*`)

The 35 `gambit_terry_*` console commands are the M14 hand-tuning harness — the
edit-and-hotload lever set that drove the five in-editor rounds. They are **dev tools, not
player-facing**: every value knob mutates a session-local static on `SeatedHandSpikes` (the
shipped values live on `TerryTuning` in `lobby.scene`, which RULES — see the scene rule up
top), and every diagnostic just dumps or drives the local seated hand. They persist for the
session only and carry no network authority (mutations re-apply on local + proxies, so at
worst a client reshapes how seated arms look *on its own screen*). Kept in-tree because
"remaining work is TUNING"; gate or drop them (`Application.IsEditor`, the repo's runtime
idiom — there is no `#if DEBUG` precedent) before a public ship. Sit down first — they drive
YOUR seated hand.

**Master switches** (the kill chain is `ChessRing.TerrySeated` → `_hands` → `_rise`):

| Command | Effect |
|---|---|
| `gambit_terry_hands` | toggle the seated hands on/off (`HandsOn`) — off is the shipped bodies-only world |
| `gambit_terry_rise` | toggle the half-rise (default ON); off = seated lean only, reach ceiling ~rank 2 |
| `gambit_terry_natural` | toggle natural lean (default ON); off falls back to the isolated A/sphere levers |
| `gambit_terry_servo` | toggle the closed-loop correction for the ~5u post-override solver warp |
| `gambit_terry_brace` | toggle the off-hand table brace |
| `gambit_terry_clamp` | out-of-reach mode: OLD M13 sphere clamp ⇄ Approach-A idle band (compare only) |

**Rise / lean value knobs:**

| Command | Value |
|---|---|
| `gambit_terry_maxrise <u>` | max pelvis rise (harness plans ~24 for the far rank) |
| `gambit_terry_step <u>` | max foot step (0 = feet welded, the rise shrinks instead) |
| `gambit_terry_risechase <k>` | rise chase rate /s (lower = statelier) |
| `gambit_terry_grace <u>` | slack left to the slide before the body rises (bigger = lazier) |
| `gambit_terry_lift <k>` | Z rise per unit forward (0 = horizontal glide) |
| `gambit_terry_maxlean <u>` | max natural-lean shoulder travel (~15 = a real lean; higher looks like a dive) |
| `gambit_terry_natlbone <bone>` | natural-lean bone (`spine_2` default; lower spine hinges from the waist) |
| `gambit_terry_leg <u>` | override the pelvis→ankle leg budget (0 = live chain measurement) |
| `gambit_terry_margin <u>` | reach margin inside the measured arm (bigger = bent-elbow asks the solver lands) |
| `gambit_terry_band <x>` | Approach-A reach band \|x\| ≥ (station-local) |
| `gambit_terry_pitch <deg>` | torso pitch cap over the table (0 = uncapped glide) |
| `gambit_terry_yaw <deg>` | torso yaw cap toward the piece |

**Hand / wrist posture:**

| Command | Value |
|---|---|
| `gambit_terry_wristdrop <deg>` | grasp curl past the forearm bearing (capped at `ring.HandPitch`) |
| `gambit_terry_roll <deg>` | hand roll — swings the elbow out of the torso (the t-rex fix); 0 = old vertical-plane arm |
| `gambit_terry_grasp <u>` | wrist clearance above the moved piece's own top (negative sinks into it) |

**Comparison spikes** (the declined/alternative approaches, kept to re-measure):

| Command | Value |
|---|---|
| `gambit_terry_lean <u>` | Approach B: manual torso lean on `LeanBone` each frame |
| `gambit_terry_leanbone <bone>` | Approach B lean bone (`spine_2` torso vs `arm_upper_R` IK-root test) |
| `gambit_terry_armscale <k>` | Approach C: best-effort bone-scale on the arm (no runtime scale API — measures if IK reads it) |
| `gambit_terry_sit <1-3>` | sit pose (1 = sitting_01 shipped; 2 = does it lean the shoulders forward?) |

**Diagnostics & harness drivers** (dump or auto-drive; no persistent knob):

| Command | What it prints / does |
|---|---|
| `gambit_terry` | the seated-hand chain as this machine sees it: kill switch, camera, plant, chair, hands, spike state |
| `gambit_terry_net` | the same chain from the NETWORK angle — refs/game/avatar/body per `SeatedTerry`; a spectator needs `mirroring=True` |
| `gambit_terry_bones` | list the citizen's real bone names (seated → station-local numbers) |
| `gambit_terry_rise_dbg` | arm a one-shot full-pipeline dump (inputs → plan → applied → bones) on the next far reach |
| `gambit_terry_probe` | sweep all 64 squares (~40s, forces hands ON) — which squares the arm actually lands |
| `gambit_terry_scholars` | play the Scholar's Mate with Terry's hand (e2e4 · f1c4 · d1h5 · h5xf7) to tune placement live |
| `gambit_terry_doctor` | ~6s of automated reach trials at the far rank → one verdict table, applies the winner |
| `gambit_terry_sweep` | ~7s running every quantitative spike (baseline/sit2/lean/armscale) → one verdict table |
| `gambit_terry_spikes` | the playbook: live lever state + which lever to pull for which reading |

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
