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
| — | _(nothing outstanding)_ | Add the next branch's rows here when you pick up new work. |
