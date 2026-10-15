# VR

A headset on the existing desktop rig. Controls are unchanged: mouse, keyboard, cursor and every
panel work exactly as they do without a headset, and no VR controller is read.

`Game.IsRunningInVR` is true when a headset is connected and active at launch, so there is no
setting and no second scene: `LobbyPlayer.OnStart` adds `World/VrRig.cs` to the local player and
nothing else in the client knows about VR.

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

## Known gaps

- No tracked hands, no controller input, no pointing at a square: moves are made with the mouse.
- The seated body is hidden locally in VR (`LobbyPlayer.HideLocalAvatar`); remote players still
  see the citizen sitting.
- World scale is the desktop's (`ChessRing.SeatEyeHeight` is a `[GUESS]`), unchecked against a
  real eye height.
