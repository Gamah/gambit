# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

**This is a list of things that need doing, ranked 1–100, highest first. Nothing else.**

- **The number is a priority, not a schedule and not an order.** It says what would hurt most
  to leave undone, not what to do next. Nothing here blocks on the item above it unless the row
  says so.
- **Rows are not branches.** Group them into whatever branches make sense when you pick them
  up — several small rows on one wall or one panel are usually one branch, and one big row
  (chat, voice) is usually its own. The table is deliberately flat so it can be regrouped
  freely; do not read the ranking as a milestone plan.
- **Delete a row when it ships.** The reasoning that outlives the work belongs in CLAUDE.md;
  everything else belongs in git. If a row is only *recorded* — a decision not to do something —
  it says so, and it stays.
- Rows marked **room** need someone standing in the game. Nothing on this host can render or
  make a sound, so those cannot be closed by review.

**The table is empty: M12 shipped both efforts — chat (engine overlay) and proximity voice.** The
durable reasoning that outlived the work — why chat is the engine's overlay now, why voice range is
a receive-side per-client value on the world board, the sealed-`OnUpdate`/falloff/key-choice gotchas
— lives in **`CLAUDE.md`** under "Lobby chat and proximity voice (M12)". Everything else is in git.

| Pri | Item | What needs doing, and the trap in it |
|---|---|---|
| 55 | Draw offer: the offerer isn't told when it's DECLINED | Reported live (M12): offered a draw, opponent declined, the offerer's "waiting" never cleared. **The relay and client are both correct** — `applyState` folds `Wdraw`/`Bdraw` from every gameState and `update()` bumps the poll version on every change, so the moment lichess sends a cleared flag the held long-poll returns it. The gap is upstream: **lichess's Board API appears not to emit a gameState when a draw is DECLINED** — `board.go`'s own note says the only truth is "`Wdraw`/`Bdraw` on the NEXT gameState", and the next one arrives on the next MOVE, so between offer and move the offerer sees a stuck "pending". **This is RECORDED, not diagnosed** — CLAUDE.md's rule is re-derive from lila, never guess, and that couldn't be done from the dev host (no lila source, no live lichess). Before "fixing" anything: confirm against lila master whether `Round.Draw.no` pushes a `gameState`. If it genuinely doesn't, there is nothing on the wire to react to and the honest fix is UI (say "offer sent", not "waiting for a reply") — do NOT invent a phantom decline signal. |
| 45 | Resign felt slow to reach the lichess opponent | Reported live (M12): resigned by standing up, the Gambit board did the right thing, but the lichess opponent said the resignation reached them late. **The client POSTs the resign immediately** (`ResignLocal` → `LichessApi.Resign`, fire-and-forget through the same authed path as everything else) and gamchess relays it straight to `/api/board/game/{id}/resign` — no queue, no batching, no client-side serialisation. So the delay is either the gamchess→lichess POST or lichess→opponent propagation, neither of which this code controls, OR a real timing bug that only shows up live. **Needs instrumentation in a real game** (log the wall-clock from `ResignLocal` to the relay's POST return) to tell "our latency" from "lichess's" — can't be reproduced by review on a host with no s&box and no live token. Rank low until it's seen a second time: one report, cause unproven. |
