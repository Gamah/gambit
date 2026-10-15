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
`WorldPanel` welded in front of the eye. Panel pixels stay panel pixels (the quad's layout space is
1920x1080, what the desktop's `ConsistentHeight` strategy gives them), so edge-anchored rows land on
the quad's edges. `gambit_vr_hud_distance` and `gambit_vr_hud_scale` tune it from inside the
headset.

`LobbyPlayer.HudHost()` is the one seam that knows about this: the self-attaching screens hang on
the UI **GameObject**, whichever root panel it carries. `EnsureVr` runs before them so the world
panel is in place when they are built, and it calls `VrHud.Adopt` directly rather than leaving it to
a component's own `OnStart` for the same reason.

**Head-locked is a staging post.** A panel welded to the view is the thing VR players complain
about, and it still covers the board. It stands until the UI is something you point a controller at.

## The controllers, bridged onto the controls that already exist

VR input is a parallel API: nothing in the engine's VR code writes the action system, so
`Input.Pressed("Select")` never hears a trigger. But `Input.SetAction`, `Input.EscapePressed`,
`Input.AnalogMove` and `Input.AnalogLook` are all public and settable, so `World/VrInput.cs` — a
`GameObjectSystem` at `Stage.StartUpdate`, after the frame's real keyboard and mouse state is
computed and before any component runs — writes them, and **nothing downstream knows VR exists**.

| control | does |
| --- | --- |
| either trigger | `Select` (pick a square) and, through `WorldInput`'s own VR branch, a panel click |
| A | `use` — sit down, engage a wall board |
| B | `Input.EscapePressed` — the cursor toggle while seated, standing up otherwise |
| left stick | `Input.AnalogMove` |
| right stick | `Input.AnalogLook` yaw, smooth, `VrInput.TurnRate` degrees per second |
| grip | nothing, deliberately: it is what grabbing a piece will want |

Two rules hold this together:

- **Actions are pulsed true on the press and never written false.** `Input.Pressed` compares
  against a previous-frame bitfield captured before this write (`Input.Context.Flip`), so an action
  held true every frame would fire `Pressed` every frame — a held trigger picking a square per
  frame. Writing false is equally wrong, because the same bit carries the keyboard's state. One
  true on the rising edge, and the engine's own flip clears it.
- **The sticks write only off centre**, which is what leaves mouse look and WASD working exactly as
  they do on the desktop.

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
- `LocalMusicSystem` builds its own `ScreenPanel` on a `NotSaved` object, which `VrHud` does not
  adopt — the music HUD is still flat in both eyes.
- The seated body is hidden locally in VR (`LobbyPlayer.HideLocalAvatar`); remote players still
  see the citizen sitting.
- World scale is the desktop's (`ChessRing.SeatEyeHeight` is a `[GUESS]`), unchecked against a
  real eye height.
