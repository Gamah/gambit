# VR

A headset on the existing desktop rig, with the controllers bridged onto the controls the lobby
already has. Mouse and keyboard keep working throughout. Two things cannot stay as they are: the
screen UI, which moves onto a quad in front of the eye, and the board's pick ray, which comes off
the pointing controller.

**VR starts only with `-vr` on the command line**, and `-novr` overrides it; the check runs once
at startup, so a headset plugged in later does nothing
(`engine/Sandbox.Engine/Platform/VR/VRSystem.cs`, `Init`). The manual's "plug a headset in and
you'll launch in VR" is older than that code. `Game.IsRunningInVR` is the result, so there is no
setting of ours and no second scene: `LobbyPlayer.OnStart` adds `World/VrRig.cs` to the local
player and nothing else in the client knows about VR.

A standalone export is the exception: `StandaloneAppSystem` appends `-vr` by itself when the
package's control modes are VR-only (`ControlModeSettings.IsVROnly` — VR on, keyboard and gamepad
off). Gambit's `.sbproj` declares no control modes at all, so nothing is implied either way.

## Running it

- **`-vr` goes on s&box itself**, not on the project: `VRSystem.Init` reads the process command
  line once at bootstrap. `-novr` beats `-vr` when both are present, so the switch has to be
  replaced rather than added to.
- **VR only submits frames from a game scene**: `CameraComponent.UpdateSceneCameraStereo` sets
  `WantsStereoSubmit = !Scene.IsEditor`, so the headset stays dark while editing and lights up on
  Play.
- **The editor's VR toggle (titlebar, and Editor → Settings → VR) is not a way to turn VR on.**
  The titlebar button only exists when `VRSystem.HasHeadset`, which is set inside the `Init` that
  `-vr` gates; and `VRSystem.Enable()` only moves the state to Active, without the instance
  `Init` would have created. Launched with `-vr`, it is a pause button; launched without, it is
  absent or a lie.
- `vr_info` in the console prints the refresh rate, IPD and tracked devices, or
  "VR not initialized."
- **`scripts/vr.bat` is the launcher**: it finds `sbox-dev.exe` (Steam's registry key, then the
  usual library folders; `SBOX_DEV` overrides) and starts the editor on this project with `-vr`.
  Make a desktop shortcut to it rather than a copy, since it resolves the project relative to
  itself.

## The monitor sees nothing unless something else draws it

A camera whose `TargetEye` is not `None` goes down `SceneCamera.RenderStereo`, which renders both
eyes for the compositor and never touches the swap chain. So the camera driving the headset cannot
also feed the screen, and `VrRig` adds a second flat one on the head
(`gambit_vr_companion` toggles it; it renders the scene twice, so turn it off when measuring
frame rate).

It must never be `Scene.Camera` — the cursor ray, `ScreenPixelToRay` and board picking all read
that. `Scene.UpdateMainCamera` takes the main camera first and the **lowest** `Priority` after, so
the companion is not main and sits at 16 against the authored camera's 1.

## The playspace anchor, not the camera

`Input.VR.Head` is the headset pose inside the playspace, which `Input.VR.Anchor` places in the
world. So the thing to move is the anchor, and it belongs on the **floor**: an anchor at the eye
point stacks the player's real height on top of it, and an anchor recomputed from the eye every
frame pins the eye there and throws positional tracking away.

| where the player is | anchor |
| --- | --- |
| roaming | their feet, yawed by `PlayerController.EyeAngles` — so WASD still goes where the mouse points, and head yaw rides on top |
| at a chess seat | the seat's **eye** anchor's position and yaw, offset by the head pose captured at sit-down, yawed by the right stick since that capture |
| at a wall board | left where it was |

The seated capture is what makes the board land in front of a player whose chair is anywhere in
the room and at any height: position so their seated head is on the seat's eye point, yaw so
facing forward in that chair faces the board. It happens once per seat, so leaning in is real
movement afterwards. `gambit_vr_recentre` captures again.

**The seated eye is its own anchor, not the camera's.** `ChessStation.WhiteAnchor`/`BlackAnchor`
sit at the desktop seat camera's orbit point — `ChessRing.SeatOrbitRadius` 56 at `SeatPitch` 55°,
which is 80 above the floor and 32 back from the board centre — and a unit is an inch, so a head
placed there is 31 inches above where a seated player's eyes are. `ChessRing.SeatEyeBlend` is the
knob that walks a seat camera from that orbit point down to the eye, but it moves the **desktop**
camera, where the eye end is unplayable: 13.6° to the far rank, a king hiding eight squares behind
it. So the eye end is built as a second anchor per seat instead —
`ChessStation.WhiteEyeAnchor`/`BlackEyeAnchor`, `ChessStation.EyeAnchor(seat)`, one blend formula
in `ChessRing.BuildSeatAnchor` — read by `VrRig` and nothing else, exactly as `BuildTopAnchor`
gives 2D mode its own. The eye anchor is aimed at the board centre from the eye, so it is pitched
about 30° down rather than the orbit anchor's 63°, which is what everything hung along it inherits.

`SeatEyeBack` and `SeatEyeHeight` are both `[GUESS]` — "seated eye ≈ 31 above the pad", from human
proportion scaled to a 72-unit citizen — so the anchor is right in shape and unverified in size.
`gambit_terry` is what prints them beside the citizen's own eye bone (`docs/terry.md`).

**The right stick turns the chair, because nothing else can turn it.** `AnalogLook` reaches the
view through `PlayerController.UpdateEyeAngles`, and the controller is disabled while seated, so
left as it comes the right stick is dead at a table. `VrInput.TurnYaw` accumulates what the stick
has asked for — once a frame, in the system's own update, because `VrRig.Drive` runs in both
`OnUpdate` and `OnPreRender` and would integrate the same frame twice — and the seated anchor yaws
by the difference since it captured the seat. Measured from the capture, so a stick pushed while
roaming does not arrive as a crooked chair, and `gambit_vr_recentre` squares it up by capturing
again.

The yaw goes on the **anchor, about the seat's eye point**, so turning moves the view and nothing
else: the board, the HUD quad and the state board all hang off the seat anchor rather than off the
playspace, so turning away and back puts the board exactly where it was. Remote players see the
seated citizen where it has always been — the turn is local to the headset.

A wall board is the one engaged state that does **not** move the playspace. The desktop camera
flies to the board; a VR player walked up to it and is standing there, and taking the room out
from under them to show a screen panel they can already read is worse than leaving it.

**2D play mode is a desktop view.** The nadir anchor looks straight down, where a yaw
decomposition is the Euler singularity and there is no playspace orientation to recover; the
seated anchor is always the 3D one.

## Why this beats the camera code without touching it

A camera moved after the view composes "stomps the composition and renders from its raw values"
(`CameraComponent.UpdateSceneCameraTransform`), and composition runs after Update and before
PreRender. `VrRig` writes the camera in `OnPreRender`, so `PlayerController.ModifyCamera` and
`LobbyPlayer.UpdateLockedCamera` can keep running untouched.

The engage and leave blends are therefore not seen in VR: the anchor is derived from the seat,
not from the camera, so sitting down is a cut. That is the right way round — a 0.35s swoop
towards a table is exactly the camera motion that makes people sick in a headset.

The engine's `VRTrackedObject` would do the same write, but in a component order this does not
control; doing it in `VrRig` keeps the anchor update and the pose that reads it in one place.

A camera whose `TargetEye` is left at `StereoTargetEye.None` renders only to the companion
window and the headset shows nothing, so `VrRig` sets `Both`.

## The UI is on a quad, because a screen panel cannot be in a stereo view

Two facts in the engine's `ScreenPanel.cs`, neither reachable from CSS:

- `GameRootPanel.IsWorldPanel` is false. A screen panel composites flat into each eye at identical
  screen coordinates, so it carries no disparity: it reads as infinitely far away while covering
  things an arm's length off.
- `GameRootPanel.UpdateScale` forces `Scale = 2.33` in high-quality VR. A row anchored to a screen
  edge at that scale sits outside the lens-visible part of the eye render, which is a smaller
  inset than the render target.

So `World/VrHud.cs` swaps the **root**, not the panels: the scene's UI object keeps every panel,
stylesheet and `Ensure*` attach point, and its `ScreenPanel` is destroyed in favour of a
`WorldPanel` hung in the world. Panel pixels stay panel pixels (the quad's layout space is
1920x1080, what the desktop's `ConsistentHeight` strategy gives them), so edge-anchored rows land on
the quad's edges. `gambit_vr_hud_distance`, `gambit_vr_hud_height`, `gambit_vr_hud_scale` and
`gambit_vr_hud_pitch` tune it from inside the headset.

**`WorldPanel.RenderScale` is not a size.** It multiplies the quad's transform and divides the
layout rect by the same number, so the world size cancels and only the layout space moves: at 0.76
a 1920-wide panel lays out in 2526 px and every row in it is drawn smaller than its CSS asks for
(`engine/Sandbox.Engine/Scene/Components/UI/WorldPanel.cs`, `OnPreRender` — `[SOURCE]`, read off the
implementation). It is 1, so `PanelSize` is the layout space the stylesheets were written against,
and the quad's world size is the host object's **scale** (`VrHud.Size`), which is what
`gambit_vr_hud_scale` sets.

**Nothing about the quad follows head rotation.** `VrRig.ViewAnchor` is the one place that decides
where "in front of the player" is — the eye anchor while seated, the controller's eye angles while
roaming, never the headset's — and both the quad and the state board hang off it, so they cannot
disagree. The quad hangs along that seated direction, which is aimed at the board centre, and that
is the only direction with the board **behind** the quad rather than below it. The state board takes
the yaw alone, because it hangs at a fixed height in the room.

**How much of that pitch the quad keeps is `VrHud.PitchBlend`** (`gambit_vr_hud_pitch`): 1 squares
the quad onto the board, 0 stands it upright. Fully squared is a panel lying 30° back towards the
ceiling, which reads as a panel on the floor; flat loses the aim at the thing being looked at.
Position and rotation both take the blended direction, so the quad is always square to the view and
`gambit_vr_hud_height` alone decides how far above the board it hangs.

**The adopted desktop UI is parked, because the seated controls are at the table now** (below the
room, never disabled: disabling the world panel deletes the root its panels are children of while
they go on building trees against it). It is still the only home of the pre-game flows — the time
control, Ready, the four lichess start flows — and of the promotion picker, so two things bring it
back: the GAME MENU plate while a table is idle, and a pending promotion, which shows it whatever
the toggle says. A parked picker is a game that cannot continue; `VrHud.PromotionWaiting` is that
override, and `gambit_vr_hud` is the console's way in.

**A VR panel is wall-board sized and built the way the wall boards are** (`WallBoardGeometry`:
default `PanelSize`, content `height:auto`, a `*BoardScale` from that file as the GO scale); the
state board and the seated board below are both built that way.

`LobbyPlayer.HudHost()` is the one seam that knows about this: the self-attaching screens hang on
the UI **GameObject**, whichever root panel it carries. `EnsureVr` runs before them so the world
panel is in place when they are built, and it calls `VrHud.Adopt` directly rather than leaving it to
a component's own `OnStart` for the same reason.

## The seated controls are at the table

`World/VrTableUi.cs`, built by `VrRig` the same lazy way the hands are, and client-local and
parented to nothing for the reason the settings plate records: a `ChessStation` is NetworkSpawned,
so a child of one rides the host's snapshot.

**Reading and doing want opposite things, so they are different objects.** What a player reads is a
wall-board-shaped panel off to the seat's right, a glance away and never in front of the board
(`UI/VrSeatBoardPanel.razor`: the seats, whose move it is, the result, the move list). What a
player presses is a flat plate on the tabletop, where their hands already are and big enough to hit
with a beam — the one world control this room already proves (`SeatSettingsPlate`, issue #28).

`WallBoardGeometry.TableMaster` is the only thing a table board changes: same `Stretch`, so the px
values in its stylesheet mean what they mean on every wall board and glyphs are distorted by the
same amount. A wall sign is read across the room, this one at arm's length.

**The plates keep their slots whether or not they are showing.** Stand up is slot 0 and always
there; slot 1 is NEW GAME, the draw offer, or GAME MENU; slot 2 is resign, in its own two-stage
press. A plate that slid along the strip as the game changed state would move the resign plate
under the hand reaching for "stand up", so an inapplicable verb leaves a bare patch of table rather
than closing the gap. Three is what fits: `ChessRing.VerbPlateCenterY` runs them in from the
tabletop's right-hand edge, against the settings plate's own 13.6 on the left.

`ChessRing` owns all of that geometry — `SeatVerbPlateLocal`, `VrSeatBoardLocal`, and the one
`BuildTextPlate` both plate kinds are built by — because it is the file that knows where the
table's margins are. `gambit_vr_board`, `gambit_vr_board_pos` and `gambit_vr_board_scale` move the
board from inside the headset. **Its numbers are world units, like `SeatEyeBack`**, not base units
× `TableScale`: it is placed against the seat's eye anchor rather than against the tabletop.

## The controllers, bridged onto the controls that already exist

VR input is a parallel API: nothing in the engine's VR code writes the action system, so
`Input.Pressed("Select")` never hears a trigger. But `Input.SetAction`, `Input.EscapePressed`,
`Input.AnalogMove` and `Input.AnalogLook` are all public and settable, so `World/VrInput.cs` — a
`GameObjectSystem` at `Stage.StartUpdate`, after the frame's real keyboard and mouse state is
computed and before any component runs — writes them, and **nothing downstream knows VR exists**.

| control | does |
| --- | --- |
| either trigger | `Select` (pick a square), `use` (sit down, engage a board) while roaming, and a panel click through `WorldInput`'s own VR branch — which watches **one named hand**, `WorldInput.VRHandSource`, defaulting to Left: `VrHands` writes it from `VrInput.Pointing` every frame, or the beam hovers a button that no trigger can press. Over a button it is a click and nothing else: `VrHands.PointerHovering` suppresses the `Select` pulse, or the pull that clicked a panel also picked the square behind it |
| B, and A | `Input.EscapePressed` — the cursor toggle while seated, standing up otherwise. **Both, because B may never arrive on an Index**: `game/core/cfg/fpxr/valve_knuckles.json` gives `button_b` a `paths` key holding a bare string, where every other `paths` in both shipped profiles is the per-hand object map and every single binding uses `path`. `[SOURCE]` — the fpxr loader is native, so the shape is the evidence. |
| left stick | `Input.AnalogMove`, in the direction the player is LOOKING |
| right stick | yaw, smooth, `VrInput.TurnRate` degrees per second: `Input.AnalogLook` while roaming, the playspace anchor while seated |
| grip | pick a piece up, carry it, drop it on a square (`ChessBoardView.UpdateCarry`) |

**Escape answers to A as well as B**, which is not symmetry for its own sake: of the two
controller profiles the engine ships (`game/core/cfg/fpxr/`), the Index one gives `button_b` a
`paths` key holding a single string, where every other single binding in both files uses `path` and
every `paths` holds a per-hand object. Whether the native loader forgives that is not visible from
the managed source, so the mapping does not rest on it.

**The stick walks where the head faces, not where the body does.** A headset never writes
`EyeAngles` — head rotation moves the playspace anchor and nothing else — while
`PlayerController.InputMove` builds its wish direction out of `EyeAngles`. Left as it comes, "stick
forward" means "forward along the body", which after any head turn is not where the player is
facing. `VrInput.Walk` reads the stick in the head's frame and hands it over in the body's
(`VrRig.BodyYaw`). The stick's own axes are not ambiguous: up is forward.

**Movement is written in the FIXED stage, look in the update stage**, because that is where each is
read: `UpdateEyeAngles` takes `Input.AnalogLook` from `OnUpdate`, but `InputMove` takes
`Input.AnalogMove` from `OnFixedUpdate` — and `Scene.SharedTick` runs the whole fixed update
*before* it signals `StartUpdate`. With `Input.Process` recomputing both analogs from real keyboard
state once per frame ahead of the tick, a move written at `StartUpdate` is consumed as zero and
overwritten before anything reads it. Turning works and walking does nothing, with both written
side by side in the same function; `Stage.StartFixedUpdate` is where the move write survives.

Two rules hold this together:

- **Actions are pulsed true on the press and never written false.** `Input.Pressed` compares
  against a previous-frame bitfield captured before this write (`Input.Context.Flip`), so an action
  held true every frame would fire `Pressed` every frame — a held trigger picking a square per
  frame. Writing false is equally wrong, because the same bit carries the keyboard's state. One
  true on the rising edge, and the engine's own flip clears it.
- **The sticks write only off centre**, which is what leaves mouse look and WASD working exactly as
  they do on the desktop.
- **`use` is pulsed only while roaming.** It is also what closes an engaged info board, so a
  trigger pull that clicked a button on that board would shut it on the same press. Engaged, the
  trigger is the pointer and nothing else, and B is the way out.

### The hands and the beam

Both controllers are drawn with the model the runtime reports for the device
(`VRController.GetModel`), so no art is ours to keep in step with whatever headset is plugged in.
The model sits at the **grip** pose and the pointer at the **aim** pose — different poses on the
same device, and no runtime promises they agree.

The beam, and the panel pointer, belong to whichever hand last pulled its trigger
(`VrInput.Pointing`): two live pointers need a priority rule for one hover state, and "the one you
just used" is the answer. Panels are pointed at through the engine's `WorldInput`, which casts its
object's forward and reads the trigger as a click in VR — so the HUD quad and the tabletop plate
needed nothing new. It has to be the **only** one in the scene, because the ray it feeds the UI is a
global; `LobbyPlayer` leaves the camera's off in VR for that reason.

**There is no device model to draw.** `VRController.GetModel` is a stub: it returns `Model.Cube`
(`models/dev/box.vmdl`) for every device, and never null. Drawn unscaled at the grip pose that is a
50-unit cube centred on the palm with the player's head inside it — every face a backface, so
nothing renders and nothing is occluded either. The room looks perfectly normal and the hands are
simply absent, which is the one failure that looks like no failure at all.

So the hands are primitives sized in world units, built the way every other primitive in this scene
is: the dev box normalised by its own `Bounds.Size` and tinted (`Streetlights.AddBox` is the
pattern). A grip-sized box per hand at the grip pose, a small bright tip at the aim pose. A
controller that stops reporting hides its grip rather than leaving it floating, because a grip left
in mid-air claims tracking that isn't happening.

**The beam is a stretched box too**, not a `LineRenderer`: one rendering path for everything the
rig draws, and it is the path the rest of this scene already proves. Its length is clamped, because
a ray that starts inside a collider traces zero, and a beam is only as useful as it is visible.

### The state board

**A headset has no console in it**, and every VR fact worth knowing is only true while one is being
worn. So `UI/VrStatusPanel.razor` puts the rig's state on a wall-board-scale panel in the room
(`gambit_vr_debug`, off by default — it is an instrument, and it covers the room it reports on):
whether the input system exists, whether the controllers report at all, what the sticks read,
whether the models arrived, how many times the hands have been placed. `gambit_vr_status` prints the same lines to the console and `gambit_vr_log <seconds>` dumps them once
a second, for reading after the headset comes off.

It also carries `VrRig.LastError` and `VrHands.LastError`. **An exception in a VR component is
invisible**: the hands are simply absent, which looks identical to never having been asked for, and
a throw in a per-frame placement leaves nothing behind at all. Both are caught and reported here
rather than only logged, because the log is not where the player is.

**`VrRig` builds and places it, and that is the point.** A tracked view proves `VrRig` is running, so
if the board is there, the lines on it can be believed — including the ones saying the input system
or the hands are missing. Hosted on either of those, it would be silent in exactly the case it
exists for.

`VrRig` also *builds* the hands, lazily, rather than `LobbyPlayer` building them once at spawn:
`OnStart` runs once per spawn, so a VR component added by a code hotload into a running session is
never asked for and fails silently — the same trap as a `GameObjectSystem` added mid-session.

`VrRig` places the hands, rather than them placing themselves: every one of those poses is the
playspace anchor's, so whatever sets the anchor has to place everything derived from it or the hands
trail the body by a frame.

**The cursor is forced hidden in VR**, every frame, because it is a global that the seat's aim
toggle and the music screen each set for their own reasons. A visible cursor sends `WorldInput` down
its camera-ray branch, so the hand would stop pointing the moment a panel with a button appeared.

### The hands other people see

**A headset player's hands go on the wire as a POSE.** The gesture machine animates a *move*: it
is fed a ply and a UCI string and every client derives the same reach from them. A pair of
controllers is not that — it is a pose that changes every frame and is known only to the machine
holding them. So `LobbyPlayer.PublishVrHands` syncs the two wrist poses (plus a tracked/grip flag
int) in the seat's **station-local** space, and `ApplyVrHands` writes them into the same two IK
chains the gesture machine uses, on whichever client is drawing that body.

- **Station-local, not world**: the ring slides when the admin changes the board count, and a
  world pose would slide with it.
- **Driven from `LobbyPlayer.OnUpdate`, above the proxy branch** — not from `SeatedTerry`, which
  early-returns on a table with no game. A player's hands are theirs from the moment they sit
  down, and reaching for the board is the first thing they do. `ApplyHandPose` yields while
  `VrHandsLive`, so the two can never both write to one arm.
- **Nothing clamps the ask.** The gesture path pulls its target inside the arm the solver really
  has; a controller is not a target we invented, it is where the player's hand is, and moving the
  wrist somewhere else would make every other screen disagree with the player about their own
  arm. Where the arm runs out the engine stops the wrist short, pointing the right way from as
  close as it gets — and the piece is not held by that hand anyway, it floats to the square the
  player names.
- **A controller that stops reporting publishes as absent**, which clears its chain. An arm
  frozen mid-reach on someone else's screen claims tracking that isn't happening.
- The bone and parameter names are the same on both citizen animgraphs, and the hand IK applies
  in `postprocess.vsubgrph` outside the holdtype branches, so none of this depends on which model
  is under it (`docs/terry.md`).

### The grip carries a piece

**A grab is not a second way to decide a move.** `ChessBoardView.UpdateCarry` runs the same seam
the click path runs: `Select` builds the legal (or premove) targets, a drop is refused unless the
square is one of them, and the move leaves through `IBoardGame.TryMakeMove`. The grip adds the
gesture — a lift, a carry, a release — and nothing about which moves exist. Promotion parks on the
picker exactly as a click does, and a premove carry queens silently for the same reason.

**Your hand if you can touch it, your beam if you cannot**, decided when the piece is picked up
and never changed mid-carry (`ChessBoardView.Carrier`):

- A grip closed within `GrabRadius` of one of your pieces picks up the nearest one, **wherever the
  beam is aimed** — a player reaching for a pawn is not also pointing at it — and the piece
  follows that hand, turning with the wrist, until it is let go.
- **Hand motion is MAGNIFIED onto the board** (`ReachGainX`, `gambit_vr_reach`). A real arm does
  not cover this board: at full stretch a seated player gets about as far as the 5th rank, and a
  game is played to the far back rank from the first move. The gain is applied to the hand's
  station-local offset from the centre line of the seat's own first rank — the one line the
  player is always sitting at, so the gain never moves the piece they just picked up — and the
  default puts the opponent's back rank inside about the 4th rank's worth of real reach. Across
  the board the gain is 1: the board is 39 units wide and a sideways gain multiplies tracking
  jitter into whole files. Height is never scaled; it is read against the bands, which are facts
  about the room.
- **Nothing magnifies the ARM.** The published wrist pose stays the real one, so other clients see
  the player's own reach and the piece floats out past it. The amplified point is where the hand
  is on the BOARD, not where the body claims to be.
- **A held piece lands on the square under the hand while the hand is low over the board
  (`PlaceBand`), and on the square the hand is POINTING at while it is raised.** Reaching and
  pointing are two motions and a hand holding a piece is doing one of them; the beam comes off
  the carrying hand (`VrInput.Pointing` follows the grip), so the line the player sees is the line
  that decides it, and the hovered highlight says what either zone will land on before the
  release commits it.
- A grip closed with the hand away from the board falls back to the square under the beam, and
  that piece rides the pointed square lifted clear of the board. Not a nicety: the board is about
  39 units across and the far rank some 60 from a seated shoulder, so nothing reaches it, and a
  piece over there can only be moved by pointing. Swapping authority mid-carry would be worse than
  either — a held piece landing where the player was looking, or a far piece yanked into their lap.
- Reach is horizontal distance to the square's centre inside a height band (`GrabBand`), not a 3D
  distance: a piece is 10 units tall, and a 3D radius would make a king easier to grab than a
  pawn. Below the surface is the tabletop, far above it is a hand up by the face — where a
  controller sits when it is pointing rather than reaching. `gambit_vr_grab <radius> <band>` and
  `gambit_vr_carry_offset x y z` dial both.
- **The grip is measured against a calibrated REST, never against zero.** Both shipped profiles
  bind `/input/squeeze/value`; on an Index that is the capacitive grip, which reads a real value
  for a hand merely holding the controller — fingers have to be round it to hold it at all.
  Against absolute thresholds that baseline is a grip that presses and then never releases: the
  value falls back to resting, which is still above the release line, and the piece never leaves
  the hand. `VrInput.GripRest` follows what each hand reads while NOT gripping (frozen while it
  is, or a long carry calibrates its own squeeze away and lets go by itself), and
  `gambit_vr_grip_press <press> <release>` are deltas on top of it. The state board prints value,
  rest and both deltas.

- **The piece is only ever moved, never re-parented or re-authored.** Every drop puts it back on
  the square it was lifted from first, and the FEN diff decides where it really goes — so a
  refused drop costs nothing and a legal one slides from the square it actually left. Its home is
  remembered as a position, not derived from the square: a piece mid-slide was not standing on its
  centre.
- **A diff that lands under a carry drops the carry first** (`SyncPieces`), before the pairing pass
  reads piece positions for its slide origins. A slide starting from a held piece is a piece
  hopping out of mid-air.
- **A frame the grip owns is not a click frame.** Otherwise a trigger pulled mid-carry selects the
  square under the piece being carried.
- **Every grip edge is logged to the console** (`VrInput.GripLog`, on, `gambit_vr_grip_log` to
  stop): which hand, which way, the raw value, the calibrated rest and the deltas, numbered. The
  number is the point — presses and releases alternate, so a trail of nothing but GRABs is the
  carry that can never be dropped. A game starting at the local table clears the trail and the
  drop verdict, so what is on screen describes the game being played.
- **A drop says what it did** (`ChessBoardView.DropStatus`, on the state board and in the log). A
  refused drop and a release that never arrived are identical from inside a headset — the piece is
  back on its square either way, with nothing said — and so are "that square is not a legal
  target", "dropped on its own square" and "the controller refused the move". The verdict names
  which.
- **The carry belongs to the hand that closed first** and passes to the other if that one is still
  holding when it opens: one gesture, two hands. The grip has hysteresis for the same reason —
  one that drifted around a single threshold mid-board would drop the piece and pick it up again.

### The board pick comes off the controller

`ChessBoardView.SquareUnderCursor` takes `VrInput.AimRay` when there is one, and the desktop's
`camera.ScreenPixelToRay( SeatAim.PickPixel() )` otherwise. In VR the player's hand is in the same
space as the board, so the device is a better description of where they are aiming than any screen
pixel; neither aim mode applies, and a seated camera ray would pick from the eye rather than from
where the beam is drawn. The pick is plane maths against the board surface with no colliders
involved, which is why a different ray drops straight in.

That also means the beam does not stop at the square: cells, pieces and panels have no colliders, so
it lands on the table or the floor behind them. The hovered square's own highlight is the reticle —
the same thing the desktop's look aim relies on.

## Known gaps

- **The headset player's own body is not drawn to them** (`LobbyPlayer.HideLocalAvatar`), and
  that is a decision rather than a gap: a wrist driven to where a real controller is lands
  outside a ~24-unit arm, and the engine's two-bone IK then stops it short along the
  shoulder→ask ray — fine to watch on someone else, wrong to wear. The rig's own primitives are
  the hands locally; the arms are for everyone else (above).
- World scale is the desktop's (`ChessRing.SeatEyeHeight` is a `[GUESS]`), unchecked against a
  real eye height — so how much of the board is inside a real arm's reach is unknown, and the
  piece floats rather than being carried there.
- Every hint still names a key: "Press E to sit", the info board's "E or Esc". The copy has no VR
  branch, so it describes controls the headset player is not using.
- `LocalMusicSystem` builds its own `ScreenPanel` on a `NotSaved` object, which `VrHud` does not
  adopt — the music HUD is still flat in both eyes.
- World scale is the desktop's (`ChessRing.SeatEyeHeight` is a `[GUESS]`), unchecked against a
  real eye height.
