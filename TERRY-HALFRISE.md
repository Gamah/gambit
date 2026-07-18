# The half-rise: terries that pick up the pieces (M14, second attempt)

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
       a      b      c      d      e      f      g      h
   8    6.8    6.5    4.7    3.1    3.0    3.0    3.0    4.7
   7    3.0    1.4   ok     ok     ok     ok     ok     ok
  1-6                       all ok
```

**54/64 squares honestly reachable** (M13 seated: 5/64, far rank 30–35 short); worst corner
6.8u, inside the piece-slide fallback that already ships. Legs never over-extend, feet never
enter the table base, mirror round-trips exact, no NaNs, no cliffs. Two insights the first
cuts got wrong, both caught by the harness:

- The rise must be **horizontal** — the legs are the scarce resource, and altitude bought
  from them is altitude the arm's own sphere covers for free. Worth 20 squares.
- The foot step must be **exact** (leg-triangle arithmetic), not a fudge-factor overshoot —
  the heuristic made feasibility non-monotone at the a-file corner and the rise search
  landed on the wrong branch (a 13u pelvis pop). The search is now a descending scan, which
  doesn't care about monotonicity.

**`SeatSitBack` is back to 36 (M13 scooted it to 26 for seated reach).** The scoot buried
the seated chest a third of the way into the tabletop — invisible from this host and from
the seated player's own first-person view; the first joined client to look reported it. The
half-rise makes the scoot pointless, and the harness reads BETTER at 36 (54 vs 51): the
longer horizontal run lines the rise up with the far squares.

## What only the editor can answer (the next session's checklist)

1. **Does the pelvis override carry the LEG chains' solve** the way `spine_2` carried the
   arm's? `gambit_terry_sweep` is the verdict table: half-rise gain should be large with miss
   ≤ ~4 at e8. A gain of ~0 means the rise never moved the skeleton.
2. **Do the pre-compensated foot pins keep the feet still** through a rise? Look at the feet.
3. **Does it read as a person** leaning over a table, and does the piece-in-hand carry sell
   it? Taste calls: `gambit_terry_probe` (all-64), a real game, a second client watching.
4. Finger polarity (`holdtype_pose_hand` open/closed ends) — still unverified from M13.

Levers: `gambit_terry_rise / _maxrise / _step / _risechase / _brace` plus all of M14's.
Kill chain, three deep: `ChessRing.TerrySeated` → `gambit_terry_hands` → `gambit_terry_rise`.

**SOLVED (retest): the joiner saw no animation because a lichess game was INVISIBLE to every
non-participant by construction.** Nothing about a lichess game was networked — each
participant polls gamchess privately, a solo flow (seek / challenge / shareable link) starts
no local game at all, and `Engaged` only goes true on the client that asked. The joiner's
`gambit_terry_net` dump said it plainly: `game=NULL, playing=False`. Not an animation bug —
spectators couldn't see the BOARD either. Fixed with the **spectator mirror** in
`LichessGameController`: the participant `[Rpc.Host]`-reports the observed move list, the
host folds it into `[Sync] MirrorMoves/MirrorLive`, every non-engaged client rebuilds a
display game and exposes it through the same `IBoardGame` seam (`Mirroring`) — so the view,
sounds, hands and carry all light up for spectators and late joiners at once. Mirrored
boards show no clocks (v1) and never accept input. NOTE: the probe/sweep are LOCAL-only
diagnostics — a joiner is not supposed to see them; test mirroring with a real game.
`gambit_terry_net` remains the diagnostic if the retest still fails.

## Decisions already made (don't relitigate)

- **Real pick-up**: the piece rides the rendered `hand_R` bone (release = short settle slide,
  so the abandon rule degrades to a glide, never a teleport). The spectator wall keeps the
  plain slide — it has no terries.
- **v1 carries the attacker only**; a capture's victim keeps its existing tray slide. The
  two-trip choreography (hand clears the victim first) is modelled in TerryPose already;
  synchronising the victim's slide to the hand is a later polish, not a blocker.
- **The brace is honest or absent**: offered only when the left arm can actually reach it.
- **No rotations in the overrides.** A torso *pitch* would read better than a shear-lean, but
  whether rotation propagates through a bone override subtree is unproven; translations are
  proven. If the editor session wants pitch, spike it behind a lever first.
