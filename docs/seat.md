# Seat

Seated controls: which settings panel a row belongs on, the tabletop BOARD SETTINGS plate, and
cursor vs LOOK aim at the board.

## Where a setting lives: the room on the wall, the board in your hand

Which panel a row belongs on is a question about **audience**, not space:

- **WORLD SETTINGS**, the south-wall board (`SettingsStation` → `SettingsScreen`, summarised by
  `WallSettingsPanel`): the room. Theme, room and table light brightness, the checkerboard floor
  and its pop rate, both voice-range sliders.
- **BOARD SETTINGS** (`UI/Screens/BoardSettingsScreen.razor`): a chess board. BOARD SOUNDS, PLAY
  MODE, MOVE MODE, SHOW LEGAL MOVES, SPEAK MOVES AT MY BOARD, the TTS voice pill and volume.

**Both render `SettingsModel` rows**: `BuildLocalRows()` and `BuildBoardRows()`, one model, two
lists. A third consumer is a third `Build*Rows`, never a second copy of the row types. **Anything
that mutates goes through `Mutate`**: it bumps `SettingsVersion`, which is the repaint key for
both panels and the trigger for `ChessRing.ApplyPlayModeSetting`. A setter that skips it changes
a stored value and nothing in the world.

The wall panel does not scroll (scroll fights the sliders' drag) and has no local font scale (a
font size local to one board makes it quietly different from every other). A panel that
outgrows the screen splits by audience, as these two did.

**The wall board summarises only what it edits.** A status line for a setting that lives
elsewhere is a line nobody re-reads when that setting changes shape.

### Two doors, one editor

Seated: a **plate on the tabletop**, near-left corner of your own seat
(`World/SeatSettingsPlate.cs`). At the wall: a BOARD SETTINGS row on the world panel, so someone
not sitting down can reach these. Both call `BoardSettingsScreen.Open()`. **Nothing in the panel
may read `ChessStation.Active`**: every row is client-local and the wall door has no seat.

### The tabletop plate

- **It is a `WorldInput` control.** `LobbyPlayer` hangs `Sandbox.WorldInput` on the camera; that
  is the path for any world-space control. Do not hand-roll a tilted-plane hit test.
- It is client-local, so there is exactly **one**, moved to whichever seat the local player is
  in. It lies flat (0.3 thick) in the X margin behind the near rank; see `docs/world.md` for why
  the X margins are otherwise kept clear.
- **Unparented on purpose.** A `ChessStation` is NetworkSpawned, so a child of one rides the
  host's snapshot, transform and enabled state included. The plate is a `NotSaved | NotNetworked`
  GO at the scene root, placed in world space off the station's transform every frame, which
  also keeps it with the table when the ring slides on a board-count change.
- **One string, one plate, one font size.** Both states are 14 characters ("BOARD SETTINGS" /
  "ESC FOR CURSOR"), so the inert state is colour only, the same rule `TableClockTextPanel`
  keeps. A second string would mean a second plate.
- **Text size is fixed in pixel space, never in the span.** Shrinking `SettingsTextSpanLength`
  scales the panel and the glyphs together, so overhanging text still overhangs. The knobs are
  `SettingsCharAdvanceEm` / `SettingsTextFitFraction`: a wider pixel space means smaller glyphs
  on the same plate. Turn the span height with them, because the span's aspect is the panel's
  pixel aspect.
- **Its advance estimate is its own, not `ClockCharAdvanceEm`.** The clock measured digits; this
  draws bold caps in a proportional fallback face, and the formula has no term for
  `letter-spacing`. Round the advance up: understating it is the failure that shows.
- **In LOOK aim it stops offering a click and says `ESC FOR CURSOR`.** The pointer is hidden, and
  a live-looking control there reads as broken.

### Modal for `SeatAim`

The panel is modal for `SeatAim`, like the promotion picker. `LobbyPlayer.UpdateSeatAim` folds
`BoardSettingsScreen.IsOpen` into the modal flag rather than the panel touching `SeatAim`, which
is what makes it release the cursor and take aim back by itself on close without clearing a
suspend the player asked for with Escape. Escape is routed at it in `LobbyPlayer` (the one place
that reads `EscapePressed` while engaged), ahead of both the aim toggle and the stand-up.

**The wall's own panel hides itself while the board panel is open** (`SettingsScreen`), rather
than stacking under it: both cards are 620px and centred, the wall's is taller, and its rows
poke out past the board card. The station stays engaged underneath, so closing the board panel
brings the wall's straight back.

**The bottom-left column has two tenants**: `HudHints` at `bottom: 44px` and `VoicePanel`'s
roster at `100px`. Don't add a third.

## Cursor vs LOOK aim at the board

A BOARD SETTINGS picker, **MOVE MODE: CURSOR / LOOK**, chooses how a seated player picks a
square. CURSOR is the default. LOOK hides the pointer, turns the seated view with the mouse, and
picks whatever is under the **centre of the screen**. `World/SeatAim.cs` is the whole state
machine and the only thing that decides; `ChessBoardView` acts on it for the ray, `LobbyPlayer`
for the camera offset and Escape, and `GameHud` only draws it. `PlayerData.LookAimAtBoard` says
whether the player wants it at all.

- **The cursor is still the default state with LOOK on.** Aim engages only while a game is
  **Playing**, read off the `IBoardGame` seam so a lichess or relay game aims like a local one.
  An empty seat, the setup panel and a finished game all need a pointer and keep one.
- **The crosshair is part of the mechanism.** With the pointer hidden the pick point is
  invisible, so `GameHud` draws a dot-in-a-ring at dead centre (`.crosshair`), gated on
  `SeatAim.Aiming` alone and outside the HUD's own `Visible()` block, because it marks a point in
  the world. It sits at 50%/50% because that is what `SeatAim.PickPixel` returns, centred with
  negative margins rather than a `transform`. The ring keeps it visible on both square colours.
- **Three ways back to the cursor, and they differ.** The player's own (Escape) **sticks**; a
  **modal** (the promotion picker, the board settings panel, an offer standing against you)
  releases and **restores by itself** without undoing a suspend; the game **ending** clears
  everything, so the next game starts in aim.
- **Escape is the whole control, and it cycles.** While `SeatAim.Toggleable` (the setting on, a
  game live, no modal), Escape switches the pointer and **does not stand you up**; the HUD's
  Leave button does, and one Escape puts a cursor on screen to click it. Escape is the only key
  that works with the pointer hidden, so it belongs to that mode. Everywhere else (roaming, an
  idle seat, a finished game, the setting off, a picker open) Escape is the plain stand-up.
  `GameHud` says both halves ("Esc for the cursor" / "Esc to aim again, Leave to stand up"),
  because with one key doing two things and no pointer to explore with, an unsaid rule is
  unfindable. There is no world-space aim button, and there should not be: it would be a thing to
  find and aim at in the mode whose point is that you are not pointing.
- **The mechanism is one engine switch.** `Mouse.Visibility = Hidden` locks the pointer and is
  what makes `Input.AnalogLook` report movement (the engine zeroes it whenever a cursor is
  visible). **Never set `Visible`; set `Auto`**: `Auto` already shows a cursor while clickable UI
  is up, and this is a global, so a forgotten reset would leave a roaming player with no pointer
  and no mouselook. `Disengage` clears it first, and the roaming path re-asserts it, because not
  every way to stop being seated goes through `Disengage`.
- **The camera offset composes in Euler space**, not as a quaternion post-multiply: a seat anchor
  is already pitched steeply down, so turning about its tilted up-axis would roll the horizon. It
  is clamped to `SeatAim.MaxYaw` (45°) and `MaxPitch` (30°), and **persists** when the cursor
  comes back by Escape, so releasing hands you a pointer rather than snapping the view.
- **The offset is cleared when LOOK aim stops being available**: MOVE MODE switched to CURSOR, or
  the game finishing. Both would otherwise leave the view turned off the board with nothing left
  to turn it back. `SeatAim` watches the falling edge of `Enabled && playing` (not `Toggleable`,
  which a modal also clears), zeroes the offset and raises a one-shot `Recentred`;
  `LobbyPlayer.UpdateLockedCamera` consumes it through the same re-blend as an anchor swap, so
  the view eases back. **`TakeRecentred` is called even when the anchor also swapped** (2D + LOOK
  → 2D + CURSOR does both at once), or the one-shot fires on an unrelated later frame.
- **LOOK aim keeps the seat camera, even in 2D, and `LocalNadir` means the camera, not the render
  mode.** The Euler composition is degenerate at the straight-down 2D anchor: yaw about world up
  rolls the image, and pitch in camera space tips opposite ways for White and Black because the
  nadir anchor's axes come from a per-seat `farDir`. So `ChessStation.LocalNadir` is
  `Active && FlatMode && !SeatAim.Enabled`, and `UpdateLockedCamera` picks its anchor off that same
  property, so the two cannot disagree. **2D + LOOK is flat pieces, seated view**; `FlatMode` is a
  render gate independent of the anchor. Flat glyphs stay flat under it (no billboarding), the
  same sprites the north-wall board is read from at a steeper angle.
  → Gate on `SeatAim.Enabled` (the setting), never `SeatAim.Aiming`: aiming goes false on every
  modal, Escape and finished game, and an anchor following it would swing the camera mid-game.
  → `NameTagPanel` and `StationScreenPanel` hide themselves on `LocalNadir`, so a 2D + LOOK
  player sees them.
- **A mid-seat anchor change blends.** `_engageTime` is long past `CamBlendTime` by then, so the
  engage lerp alone would hard-cut. `_lastSeatAnchor` spots the swap and re-blends from the
  camera's live transform, which also keeps a non-zero `SeatAim.LookOffset` from snapping.
  `BeginEngage` and the seat-switch clear it so those adopt the new anchor silently.
- `scripts/seataim_harness/` runs `SeatAim`'s whole truth table, including which way Escape goes:
  `Toggleable` is true for a live game with the setting on, and false for a modal, a dead game,
  the setting off and standing up. Wrong either way traps the player in their seat or takes the
  toggle away. Whether the crosshair reads over both square colours is an in-editor check.
