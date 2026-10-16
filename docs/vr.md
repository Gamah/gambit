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
| at a chess seat | the 3D seat anchor's position and yaw, offset by the head pose captured at sit-down |
| at a wall board | left where it was |

The seated capture is what makes the board land in front of a player whose chair is anywhere in
the room and at any height: position so their seated head is on the seat's eye point, yaw so
facing forward in that chair faces the board. It happens once per seat, so leaning in is real
movement afterwards. `gambit_vr_recentre` captures again.

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
the quad's edges. `gambit_vr_hud_distance`, `gambit_vr_hud_height` and `gambit_vr_hud_scale` tune it
from inside the headset.

**Nothing about the quad follows head rotation.** `VrRig.ViewAnchor` is the one place that decides
where "in front of the player" is — the seat anchor while seated, the controller's eye angles while
roaming, never the headset's — and both the quad and the state board hang off it, so they cannot
disagree. The quad takes that seated direction with its **pitch**, which is what centres it on the
board: the seat anchor is aimed at the board centre, so the panels composed against a 1920x1080
screen sit around the board the way they do on the monitor. The state board takes the yaw alone,
because it hangs at a fixed height in the room. `gambit_vr_hud_height` lifts the quad off the pieces
from there, and gives up the centring to do it.

**The adopted desktop UI is drawn, because it is the only way to drive a game.** It is a 1920x1080
screen layout with edge-anchored rows, a crosshair and keyboard hints, and no amount of scaling
makes that a VR UI — but every control for starting a game, offering a draw or standing up lives on
it, and the beam can click it. `gambit_vr_hud` parks it (below the room, never disabled: disabling
the world panel deletes the root its panels are children of while they go on building trees against
it). **A VR panel is wall-board sized and built the way the wall boards are**
(`WallBoardGeometry`: default `PanelSize`, content `height:auto`, `BoardScale` as the GO scale); the
state board below is the first one, and the pattern to copy.

`LobbyPlayer.HudHost()` is the one seam that knows about this: the self-attaching screens hang on
the UI **GameObject**, whichever root panel it carries. `EnsureVr` runs before them so the world
panel is in place when they are built, and it calls `VrHud.Adopt` directly rather than leaving it to
a component's own `OnStart` for the same reason.

## The controllers, bridged onto the controls that already exist

VR input is a parallel API: nothing in the engine's VR code writes the action system, so
`Input.Pressed("Select")` never hears a trigger. But `Input.SetAction`, `Input.EscapePressed`,
`Input.AnalogMove` and `Input.AnalogLook` are all public and settable, so `World/VrInput.cs` — a
`GameObjectSystem` at `Stage.StartUpdate`, after the frame's real keyboard and mouse state is
computed and before any component runs — writes them, and **nothing downstream knows VR exists**.

| control | does |
| --- | --- |
| either trigger | `Select` (pick a square), `use` (sit down, engage a board) while roaming, and a panel click through `WorldInput`'s own VR branch — which watches **one named hand**, `WorldInput.VRHandSource`, defaulting to Left: `VrHands` writes it from `VrInput.Pointing` every frame, or the beam hovers a button that no trigger can press |
| B, and A | `Input.EscapePressed` — the cursor toggle while seated, standing up otherwise |
| left stick | `Input.AnalogMove`, in the direction the player is LOOKING |
| right stick | `Input.AnalogLook` yaw, smooth, `VrInput.TurnRate` degrees per second |
| grip | nothing, deliberately: it is what grabbing a piece will want |

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

- No grab: a piece is still picked square-by-square, not lifted. Grip is reserved for it.
- No IK and no arms — the controller model floats where the device is. The intended end is the
  citizen's own arm and fingers driven by IK, with the ray still cast from the controller transform
  regardless of where the animation puts the hand.
- Every hint still names a key: "Press E to sit", the info board's "E or Esc". The copy has no VR
  branch, so it describes controls the headset player is not using.
- `LocalMusicSystem` builds its own `ScreenPanel` on a `NotSaved` object, which `VrHud` does not
  adopt — the music HUD is still flat in both eyes.
- The seated body is hidden locally in VR (`LobbyPlayer.HideLocalAvatar`); remote players still
  see the citizen sitting.
- World scale is the desktop's (`ChessRing.SeatEyeHeight` is a `[GUESS]`), unchecked against a
  real eye height.
