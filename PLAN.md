# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

**M8 (lichess link + Board API relay), M9 (game sessions + lichess TV), M10 (draw, takeback
and premove at both kinds of table) and M11 (the design pass — trays, the table clock, the
material bar, sound) are built and merged.** Read CLAUDE.md's "Lichess" section for the custody
decision, the traps and the API-citizen rules; the M11 findings that outlived the milestone are
in CLAUDE.md too (the one-string WorldPanel rule, the tabletop margin budget, the tilted-edge
rule, the Sounds map). What remains of all of them is below.

---

## M11 leftovers — the pass shipped, these didn't

The design pass is **merged**: captured-piece trays, the table clock and material bar, the
sound pass, the scene/CLAUDE.md rot, the TV clock's HIGH read. The reasoning that outlived it
is in CLAUDE.md; the rest is in git. What follows is only what was decided-but-not-built, and
the calls that need someone standing in the room.

### Decided, not built

- **Chat: delete ours, use the engine's.** Still unbuilt — `ChatShowUI` is **false** and
  `ChatPanel.razor` is **288 lines**. `../terryball` is the reference, **not**
  `../rotaliate-client`: terryball built rotaliate's model, shipped it, and deleted it
  (`8ad9f4b`, *"Drop the rotaliate-copied custom chat box; use the built-in chat"*). Gambit is
  running the code terryball threw away, near-verbatim — the engine overlay is switched off
  purely so we can redraw it worse. The deliverable is ~73 lines of hint plus the config flag:
  flip `ChatShowUI` true, gut the feed/TextEntry/fade, take the keycap from
  `Input.GetButtonOrigin( "Chat" )` (never hardcoded), and keep `ChatPanel.IsOpen` as a stub
  returning false so `LobbyPlayer.cs:219`'s gate keeps compiling — that gate becomes dead and
  that is fine. Gambit's `Chat` action is already bound to **T** and the overlay reads it.
  - Two lies to clear out while in there: `ChatPanel.razor:16` says the key is "rebindable in
    Settings" — it isn't, and **nothing anywhere writes `PlayerData.Bindings`** (ChatPanel is
    its only reader, so the whole PlayerData branch is dead code guarding a feature that does
    not exist — `GamepadBindings` is real, don't confuse them). The header comment describes
    the feed this change deletes.
  - **Unrelated despite the word**: "no chat on lichess games" under Known gaps is about
    relaying a *lichess* game's chat. Different thing; untouched by this.
- **Split `GameHud.razor` (849 lines).** Extract the **promotion picker** (`:268-282`,
  `:439-474` — zero coupling, already a sibling of `.hud`) and the **setup block** (`:112-191`
  — sole owner of `_seekRated`/`_seekColor`, `TimeControl.All`, every `LichessTable` call, and
  the `.tc*`/`.chip`/`.seek-row` CSS). ~40% of the file. Stop there.
  - **What resists splitting, and shouldn't be forced**: the status line (`:37-68`) is where
    the lichess-vs-local and setup-vs-in-game axes genuinely cross;
    `Source()`/`OnLichess()`/`Lichess()`/`Controller()`/`View()`/`Station()` are a shared spine
    that becomes a static helper or gets re-resolved per panel.
  - **A live instance of the BuildHash hazard rides on this.** `LichessText()` renders
    `LichessLinkState.Username`, but the lichess nest is FULL and hashes only `.Linked` — a
    username changing while linked renders stale. It survives on luck (the username arrives as
    `Linked` flips). The split IS the fix: the setup block hashes those values flat and gives
    `Username` a home. Not separate work.
  - Design against **10** controls (Rapid/Classical linked); the default table shows **7**
    (`CanSeek` is false at 180s), which is why nobody has felt this yet.
- **Wall boards: one scale.** `WallSettingsPanel` shares the palette but none of the east-wall
  trio's scale (12/16 padding vs 9/12, 6 gap vs 4, 12px body ≈2.2×, 1.5px dividers vs 0.75) and
  is the only panel using `$wall-radius`. **The trio's scale is canonical** — it is the
  majority and it is what `WallBoardGeometry`'s "copyable px" promise assumes. Bring Settings
  onto it and **put the type scale in `WallTheme.scss`**, which today defines exactly two
  tokens.
  - **`WallTheme` is not a fixed palette**: every token derives from `Accent` =
    `PlayerData.WorldLightColor`, so **any hardcoded colour will not retint with the room**.
    `LichessBoardPanel`'s grey `.title` looks like an oversight; `DevNotesPanel`'s amber and
    `GameHud`'s lichess black/white are deliberate and stay. `AccentBg` is consumed by nothing.
  - **Exemptions to record, not fix**: `SpectatorSeatPanel` and `SpectatorFanfarePanel` bypass
    `WallBoardGeometry` on purpose — a plaque and a banner aren't wall boards. Note the retune
    cost: **`SpectatorWall` isn't in the scene at all**, so the north wall is an edit-and-hotload
    loop, not a scene-tweak loop.
- **The move list**: `MoveRows` caps at 12 and `.moves` at `max-height: 180px` — **two
  independent caps that agree by luck**. Derive one from the other and **relabel it "recent
  moves"**; a full list wants the wall or the archive viewer, not a 250px column.
  `overflow: scroll` stays ruled out (drag-scroll fights clicks). Low priority.
- **Dead code, all verified zero-reference**: `.status.wait` (`GameHud:402`),
  `.button.disabled` (`GameHud:431`), `.button.ghost` (`GameHud:436` — real in
  SpectatorScreen, never emitted here), `.note`/`.prizes-notice` (`InfoScreen:231, 326` —
  `prizes` is a rotaliate fossil), `.input` (`SpectatorScreen:161`), and the stale CSS comment
  at `InfoScreen:321` (`/* The 10+0-only caveat stands out in amber. */` above
  `.section-header.warn`, whose only markup today is **unlink** — a fossil of the exact
  "RIGHT NOW: 10+0 GAMES ONLY" lie this milestone existed to catch).
- **Pick one description**: `InfoScreen:118` says "the board next door", `GameHud:149` says
  "the east wall". Same board. Neither is false.

### Needs someone in the room

- **The four new sounds have never been heard** — all synthesized blind, each a `gen_*` away
  from a retune. **`panic` is the risky one**: the first per-second sound in the game, and ten
  seconds of it is either pressure or an alarm — numpy cannot tell you which. **`check` lands
  ON TOP of tick/tock**; two pips to stay separable by shape, or just clutter? **`gameover3d`
  at 45%** plays at the TV wall, where UltraBullet ends a game every ~30s. **The room with six
  tables is the whole gate.**
- **Wall board title sizes**: three on one wall (CenterInfo 13, Lichess 10, DevNotes 10, plus
  Settings' 15 on the south). `CenterInfoPanel`'s `.prompt` is 15px bold — *larger than its own
  title*. Arguably right (the instruction **is** the content), but nobody's decision on record.
- **A clock is urgent in two places out of three.** The table clock reddens under
  `TimeControl.PanicSeconds` and now beeps there; the spectator wall's clocks are green with
  **no panic state at all**.
- **One action, two buttons.** `GameHud` and `LobbyOverlay` both render a resign control while
  seated in a live game, both calling `RequestLeave()`, both off the same `LeaveArmed` — with
  **different labels** ("Resign & stand up" / "Sure? This resigns" vs "Stand Up" / "Resign &
  Stand Up?"). Not a bug: two vocabularies for one action.
- **Does the east wall still read as a wall?** Signpost (`YFrac 0.1`) → lichess (`-0.1`) → dev
  notes (`-0.3`), all at `FloorClearance 30` where other walls run 60. The signpost is three
  lines tall against two full boards, and every board bottom-anchors, so the tops are ragged by
  design. No recommendation.
- **Confirm the music board fix.** A **joined** instance pressing E at the music wall got a
  dead panel: `SkafinityMusicPanel.OnStart` resolved its player **once** via
  `GetAllComponents` (**enabled-only**), and joiners rebuild `/UI` and `/GameController` from
  the host's snapshot in a different order. `MusicBoardScreen` now retries the resolve every
  frame. **If the board still looks wrong, check for `SkafinityMusicPanel: no SkafinityPlayer
  found in the scene` on the joined instance — its ABSENCE means this diagnosis was wrong** and
  the snapshot is corrupting something else.

### Known gap: the panic beep barely fires at a lichess table

Inherited, not new. lichess only sends a clock when a **move** happens, so `LocalSeatClock` is
frozen through your whole think and the second never advances — the beep fires about once. This
is the same staleness the table clock's red already has there.

**The fix is not a local countdown.** That is what the TV wall does, and it read **HIGH** for
two milestones while three places claimed it read low; beeping at a player who has more time
than we think is that mistake with worse consequences. It wants the TV clock's shape (gamchess
stamps a duration, client subtracts), and it wants doing once for both.

### There is no proximity gate, and there must not be one

Recorded because a UI/UX pass is exactly when someone re-invents it. TV briefly only streamed
while a viewer was near the board. **That is gone.** It cost three attempts, each fine in a diff:
a range of 1200 in an 800-unit room (gated nothing while looking exactly like a gate); measuring
from the controller's own GO, which sits on **LobbyRoom** at the room centre, not at the wall;
and measuring in 3D against a board floating ~390 up, so a third of the distance was vertical
before the player moved. What it bought was a wall that went blank when you stepped back from
it. The cost it guarded is already bounded: TV polls only while it's the featured source on that
client, and gamchess holds one upstream per channel however many watch.

---

## M12 or later — proximity voice, copied from `../terryball`

A chess lobby is a social room and people sitting opposite each other should be able to talk.
**terryball has a complete, working proximity-voice implementation and it is the reference** —
the same rule as chat above: copy terryball, not rotaliate-client (which has no voice at all).

**It is small.** Four files, ~350 lines total, and one of them is 23 lines:

| File | Lines | What it is |
|---|---|---|
| `Code/TerryVoice.cs` | 23 | `sealed class TerryVoice : Voice` — the entire networking model |
| `Code/VoicePrefs.cs` | 61 | `Cookie`-backed, client-local prefs. Never networked. |
| `Code/VoiceScreen.cs` | 222 | client-local keyboard driver; pushes transmit config each frame |
| `Code/VoicePanel.razor` | 246 | the roster + speaking dots |
| `Code/AlleyServer.cs:88-95` | 8 | host-side: create the component on the avatar, then `NetworkSpawn` |

**The model, and why it's the elegant part: game code networks nothing.** Playback is gated on
the **receiver**, in the only two virtual hooks the engine gives you:

```csharp
protected override bool ShouldHearVoice( Connection c )
    => VoicePrefs.VoiceEnabled && !VoicePrefs.IsMuted( c.SteamId );
```

`ShouldHearVoice` runs on the **receiver** when a packet arrives, carrying the **sender's**
connection — so reading *our own* prefs there is exactly right, and mute needs no sync, no
authority, and no server state. Transmit is gated separately and owner-locally by driving
`Voice.Mode`; **never touch a networked Enabled flag.**

**Fits Gambit cleanly.** Voice hangs off the player avatar, and `LobbyNetworkManager` already
clones + `NetworkSpawn`s `PlayerTemplate` per connection — that is the same seam as terryball's
`SpawnAvatar`, so the component goes on there. Identity is SteamID, which Gambit already has
everywhere.

**The gotchas, which are the reason to copy rather than write:**

- **`Voice.OnUpdate` is sealed in the engine.** You cannot override it. Only the hear/exclude
  hooks are virtual — don't design around intercepting the update.
- **The default `Falloff` curve is a trap.** It is *savagely* front-loaded — down to ~4% volume
  by 20% of range — so voices go near-inaudible a couple of body-lengths apart, which reads as
  broken voice rather than distant voice. terryball replaces it with a **linear** curve
  (`new Curve( new Curve.Frame( 0f, 1f ), new Curve.Frame( 1f, 0f ) )`), plus a scale-appropriate
  `Distance` and `Volume = 2f` for headroom. **The engine's default `Distance` of 15,000u is also
  wrong for any room-sized scene** — Gambit's room is 800 units, so this needs its own number,
  not terryball's 3000.
- **`V` is the engine's default push-to-talk key**, so it cannot be your toggle. terryball uses
  `G`. Gambit's free keys need checking against `Input.config` — note `use` is E and `view` is C.
- **A never-bound sentinel action is how you hard-gate transmit closed.** `PushToTalkInput` set
  to an action that does not exist means `Input.Down` is always false. Cleaner than trusting a
  flag.
- **The engine has the last word on the mic.** `Voice.IsListening` honours the user's s&box
  `voip_mode`, which game code cannot change — so a UI here surfaces and gates our side of it and
  must not claim to be the switch.
- **Menu keys leak into the world.** While a roster is open, Enter would open the built-in chat
  overlay (it reads the action in a later UI tick — `Input.Clear( "chat" )`) and Space would jump
  (`UseInputControls = false`, which gates movement only; look and camera stay live). Track the
  restore with its own flag and undo it in `OnDisabled`/`OnDestroy`, so a destroy mid-roster still
  hands control back.
- **`WorldspacePlayback = true`** is what makes it positional; `Renderer = <the citizen renderer>`
  gets lip-sync visemes free.
- **Speaking indicator** is just `voice.LastPlayed < 0.3f`.
- **Master default is OFF.** Do not turn a chess lobby's mics on by default.

**Don't copy terryball's comments blindly — some are stale.** `VoiceScreen.cs:10`,
`VoicePanel.razor:17` and `LocalHud.cs:59` all say "V toggles voice" while the code binds **G**;
`AlleyServer.cs:93` sets `Distance = 3000f` while its own commit message claims 4000u. Read the
code, not the prose — the same rule this repo keeps re-learning.

**What to strip when copying:** the first-run help pop-up in `VoiceScreen` (`RequestOpenHelp`/
`HelpOpen`) is unrelated to voice; `LocalIsBowling()` exists only because G is overloaded as the
bowler's exit key; `TerryAvatar`/`ControlsOpen` lookups become Gambit's `LobbyPlayer`. Also note
terryball's warning that `INetworkListener` **fires on every component that implements it** — with
N lanes, each joiner got N avatars. Gambit has one `LobbyNetworkManager`, so this is a trap to
avoid re-introducing, not one to fix.

**And the HUD-parenting rule, which cost terryball real bugs:** a client-local HUD must not hang
off a networked object, or the host's mute/panel state rides its network snapshot onto every
client. But a hand-placed `NetworkMode.Never` object never reaches joiners either, because a
joining client rebuilds the scene from the host's snapshot and that **excludes `Never` objects** —
terryball solves it with a `GameObjectSystem`. Gambit's screens self-attach at runtime
(`LobbyPlayer` → `ScreenPanel`), which sidesteps this, but **anything voice-related that gets
parented into the scene needs this thought through.**

**Open before starting:** whether voice is proximity-only or also table-scoped (two players seated
opposite each other are ~50 units apart in an 800-unit room — proximity may already be exactly
right, or may make the whole ring one conversation). That is a taste call and a loud one, in the
same family as the sound decisions in M11.

---

## Still open from M8 / M9 — resolve, don't guess

- **`LICHESS_TOKEN_KEY` rotation.** No path exists. Changing the key orphans every link. Needs
  a re-encrypt migration before there are real users worth not annoying. **Do not forget this.**
- **`code_verifier` length.** lichess has a `CodeVerifierTooShort` error whose threshold is
  undocumented. Ours is 43 chars — exactly RFC 7636's floor. If linking fails at the exchange,
  this is the first suspect; widen to 64 bytes and re-test.
- **The upstream engine streaming fix.** `Http.RequestStreamAsync` is broken (it `using`s the
  response, then returns its stream) and `HttpCompletionOption` is off the whitelist. Fixing
  both upstream is what would let the token move to the client and delete the custody problem
  entirely. File the PR: drop the `using`, pass `ResponseHeadersRead`, add the missing test.
  Not a dependency for anything shipped.
- **Can a rogue lobby host read another client's `FileSystem.Data`?** Only matters if the token
  ever moves client-side. Facepunch platform question — confirm, don't assume.
- **Does the long poll hold up under real latency?** 5s server hold vs the client's 8s ceiling
  is not much headroom. If polls start reading as timeouts and tripping the breaker, shorten
  the hold before reaching for WebSocket.

### Talking to lichess about limits

**Discord `#lichess-api-support`** (`https://discord.gg/MS9MejQqha`), **not email** — there is
no API branch in their contact form. Do not ask pre-emptively. The ask is only credible with
real numbers, so: ship, measure actual 429s, and bring the specific limit hit. The one to watch
is the lobby seek — **5/min per IP, which is 5/min for the entire playerbase** (lila
`Limiters.setupPost`). Outcome is discretionary; there is no blessing process.

---

## Known gaps in what shipped

- **A relayed lichess game is never archived to gamchess.** It lives on lichess and nowhere
  else. The PGN + `%clk` writer already exists — wiring the relay's final state into
  `POST /api/v1/games` would put it in the web viewer too.
- **No berserk, no chat on lichess games.** Both exist on the Board API; neither is wired up —
  gamchess *can* send (`board.go`) but inbound `chatLine` is explicitly ignored (`relay.go`) with
  no client UI. **This is not lobby chat** and has nothing to do with the M11 chat change above:
  it is relaying a *lichess game's* chat between the two players of that game.
- **"Quick pairing" and blitz seeks are both behind the `web:mobile` scope — and we won't take
  it.** Re-derived from lila + lila-ws master 2026-07-16 (CLAUDE.md has the full chain): quick
  pairing is a **pool**, not a seek; pools have **no HTTP endpoint at all**; and lila-ws's
  bearer auth requires scope `web:mobile` or `web:polygon`, so a `board:play` token cannot
  authenticate there. Blitz seeks aren't universally refused either — `boardApiHook`'s
  `allowFastGames` skips the Rapid check for those same two scopes. `web:mobile`'s own
  description is **"Official Lichess mobile app"**. Taking it would mean claiming to be
  lichess's first-party client to get past a gate aimed at third-party board clients, on an API
  where our whole playerbase shares one IP and lichess can kill the app on `clientOrigin` — and
  it would force every linked player to re-link. **Don't re-open this without new facts from
  lichess's side**; the ask is "may we seek blitz", not a scope we help ourselves to.
- **No correspondence.** `SeekCorrespondence` exists in the lichess package and has no route:
  it's the one seek shape that costs the relay nothing (buffered, no held stream, no per-IP
  seek cap), but days-per-move doesn't fit sitting down at a table. Note it is also the one
  seek whose `ratingRange` really is unbounded by default — `Seek.scala` has no Gaussian
  fallback, unlike a real-time hook.
- **Variants can never be PLAYED** without replacing the vendored rules library, which is
  standard-only: `ChessGame` would have to parse the FEN and validate moves it has no rules
  for. Don't offer what can't be played.
  **Note the word.** This constraint is about *playing* and nothing else. It was carried over
  to lichess TV — which parses nothing, and just walks a FEN's placement field onto 64 squares
  — and cost M9 ten channels on a premise nobody checked. M9 now serves all 16. **"The board
  can't draw it" is a claim about whatever actually reads the FEN; go and look at that.**
- **The relay is in-memory.** A gamchess restart mid-game drops the relay's state and the board
  goes quiet, though the lichess game itself carries on (and can be finished on lichess.org).
  Acceptable; worth knowing.

---

## The web viewer needs a lot of work

`server/frontend/` (`index.html` / `app.js` / `chess.js` / `style.css`) — the archive viewer at
chess.gamah.net. It works, but it has never had a design pass, and **nobody has ever looked at
it on anything but a desktop browser**. Treat the list below as observations, not a spec — the
real first step is to open it and decide what it should be.

**M11 does the client only, and the viewer inherits afterwards — that ordering is a
constraint, not a preference.** "Inherit the client's vocabulary" **cannot mean inheriting its
colours**: the client's `WallTheme` is *dynamic* (every colour derives from the player's room
theme) and the viewer's palette is fixed gold `#c9a227` on `#14151a`. The client has no fixed
colours to inherit. It can only inherit the **type scale, spacing and radius** — which is
exactly the part `WallTheme.scss` doesn't define yet. **So landing that type scale is the
prerequisite**, and doing both halves at once would mean designing a vocabulary and applying it
twice before anyone has looked at either.

What's already known to be weak:

- **The CSS has barely been exercised.** The board squares resized to fit whichever piece stood
  on them until 2026-07-15 — an 8×8 grid that wasn't actually holding a grid. That bug
  surviving this long says the styling has had no real scrutiny.
- **Never checked narrow / mobile — and there is literally no narrow branch to check.** All 217
  lines of `style.css` contain **zero `@media` queries**. `tbody td { white-space: nowrap }`
  across four fixed columns means the games table will **overflow horizontally rather than
  reflow** under 400px. The board itself is fine (`width: min(28rem, 100%)`, and `.piece` uses
  `clamp(1rem, 6.5cqw, 2.4rem)` container-query units) — so someone did do responsive work
  there, just not on the table.
- **`color-scheme: light dark` is declared but every var is a fixed dark value** — the
  declaration is inert and misleading.
- **The games list shows Played / White / Black / Result only.** No time control, though the PGN
  now carries one. Adding a column means touching `index.html`'s `<thead>` too.
- **Game meta is one text line** — date, then the time control tacked on after a `·`. Fine as a
  stopgap; not a design.
- **The per-move `%clk` display is brand new and unseen.** `shortClk` trims the leading `0:` so a
  bullet clock reads `0:51.63`; that call was made without ever seeing it rendered next to the SAN.
- **Sign-in is a bare button.** The Steam OpenID round trip works, but the signed-out and error
  states have had no thought.
- **The lichess pages are server-rendered and unstyled beyond `/style.css`.** `/lichess/link` and
  the callback page are `html/template` in `internal/api/lichess_pages.go`, deliberately (the
  callback has to name the account it just linked). They reuse the viewer's stylesheet and a small
  inline block. If the viewer gets a design pass, they should come with it — and the disclosure
  copy in them is load-bearing, not decoration: **do not trim the two warnings** (a lichess
  password change does NOT unlink; `/account/oauth/token` does NOT list this grant).
- **The viewer says nothing about lichess.** A linked player has no way to see or manage the link
  from the web except by knowing the `/lichess/link` URL.

Constraints worth knowing before starting:

- **The frontend is baked into the Docker image** (`COPY --from=builder /src/frontend /frontend`).
  A restart won't pick up CSS changes — the server needs `git pull && make rebuild`.
- **Zero image assets, and it should stay that way.** Pieces are Unicode glyphs with U+FE0E
  forcing text presentation; that's what keeps the viewer CC0-clean with nothing to attribute.
  The s&box client can't render these glyphs at all (they come out as colour emoji — see
  CLAUDE.md), but a browser can, so this is the one place they're allowed.
- **`chess.js` is rules code, not view code.** It is gated by `node scripts/chess_js_perft.mjs`,
  which runs on the dev host. Re-run it after touching that file — it holds the viewer's rules and
  PGN parsing to the same reference positions and real C# writer output as the client.
