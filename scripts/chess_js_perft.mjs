#!/usr/bin/env node
// Correctness gate for the archive viewer's JS chess rules.
//
// This is the SAME suite, positions and node counts as the client's
// Code/Chess/PerftCommand.cs (which in turn matches chessprogramming.org's
// published Perft_Results). The viewer has its own chess implementation because
// the C# vendored library can't run in a browser — the duplication is only
// defensible while both are held to this identical gate.
//
//   node scripts/chess_js_perft.mjs [depth]
//
// Unlike almost everything else in this repo, this actually EXECUTES on the dev
// host — no s&box, no Go needed.

import { startPosition, parseFen, perft, replayPgn } from '../server/frontend/chess.js';

const CASES = [
  { name: 'startpos', fen: null, expected: [20, 400, 8902, 197281] },
  { name: 'kiwipete', fen: 'r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1', expected: [48, 2039, 97862] },
  { name: 'position3', fen: '8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1', expected: [14, 191, 2812, 43238] },
  { name: 'position4', fen: 'r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1', expected: [6, 264, 9467] },
  { name: 'position5', fen: 'rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8', expected: [44, 1486, 62379] },
  { name: 'position6', fen: 'r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10', expected: [46, 2079, 89890] },
];

const maxDepth = Number(process.argv[2] ?? 3);
let failed = 0;

for (const c of CASES) {
  for (let depth = 1; depth <= Math.min(maxDepth, c.expected.length); depth++) {
    const st = c.fen ? parseFen(c.fen) : startPosition();
    const t0 = Date.now();
    const got = perft(st, depth);
    const want = c.expected[depth - 1];
    const ok = got === want;
    if (!ok) failed++;
    const secs = ((Date.now() - t0) / 1000).toFixed(2);
    console.log(`${ok ? 'PASS' : 'FAIL'} ${c.name} depth ${depth}: ${got} nodes (${secs}s)` +
      (ok ? '' : `  ← expected ${want}`));
  }
}

// Replay a real game end-to-end: perft proves move generation, this proves SAN
// generation, disambiguation, promotion and the PGN parser hang together.
const PGN = `[Event "Terry's Gambit casual game"]
[Site "Terry's Gambit (s&box)"]
[White "Anonymous"]
[Black "Anonymous"]
[Result "1-0"]

1. e4 e5 2. Nf3 Nc6 3. Bc4 Bc5 4. b4 Bxb4 5. c3 Ba5 6. d4 exd4 7. O-O d3 8. Qb3 Qf6
9. e5 Qg6 10. Re1 Nge7 11. Ba3 b5 12. Qxb5 Rb8 13. Qa4 Bb6 14. Nbd2 Bb7 15. Ne4 Qf5
16. Bxd3 Qh5 17. Nf6+ gxf6 18. exf6 Rg8 19. Rad1 Qxf3 20. Rxe7+ Nxe7 21. Qxd7+ Kxd7
22. Bf5+ Ke8 23. Bd7+ Kf8 24. Bxe7# 1-0`;

const { positions, result, error, headers } = replayPgn(PGN);
const expectedPlies = 47; // 23 full moves + white's 24th (Bxe7#)
const replayOk = !error && positions.length === expectedPlies + 1;
if (!replayOk) failed++;
console.log(`${replayOk ? 'PASS' : 'FAIL'} replay "Immortal Game" (${headers.Result}): ` +
  `${positions.length - 1} plies, result ${result}` + (error ? `  ← ${error}` : ''));

// Disambiguation, promotion and en passant in one line.
const TRICKY = `1. e4 c5 2. Nf3 Nc6 3. d4 cxd4 4. Nxd4 Nf6 5. Nc3 e5 6. Ndb5 d6 7. Bg5 a6 8. Na3 b5 9. Nd5 Be7 10. Bxf6 Bxf6 11. c3 O-O 12. Nc2 Bg5 13. a4 bxa4 14. Rxa4 a5 15. Bc4 Rb8 16. b3 Kh8 17. Nce3 g6 18. O-O f5 19. exf5 gxf5 *`;
const t = replayPgn(TRICKY);
const trickyOk = !t.error;
if (!trickyOk) failed++;
console.log(`${trickyOk ? 'PASS' : 'FAIL'} replay disambiguation/castling/ep game: ` +
  `${t.positions.length - 1} plies` + (t.error ? `  ← ${t.error}` : ''));

// A malformed PGN must degrade, not throw — the viewer shows what it can.
const broken = replayPgn('1. e4 e5 2. Nf3 Zz9 3. Bc4 *');
const brokenOk = broken.error !== null && broken.positions.length === 4;
if (!brokenOk) failed++;
console.log(`${brokenOk ? 'PASS' : 'FAIL'} malformed PGN degrades instead of throwing` +
  (brokenOk ? ` ("${broken.error}", kept ${broken.positions.length - 1} plies)` : ''));

// ── {[%clk]} annotations ────────────────────────────────────────────────────
// This block is the cross-language seam: LocalGameController writes the PGN below via
// the patched vendored writer, and this parser is the only thing that reads it back.
// CLKED is real output captured from the dotnet harness — if the C# writer's format
// drifts, this is what catches it. Regenerate it there, don't hand-edit it here.
const CLKED = `[Event "Terry's Gambit casual game"]
[Site "Terry's Gambit (s&box)"]
[Date "2026.07.15"]
[White "Alice"]
[Black "Bob"]
[Result "1-0"]
[TimeControl "180+2"]

1. e4 {[%clk 0:02:54]} e5 {[%clk 0:02:50]} 2. Bc4 {[%clk 0:02:49]} Nc6 {[%clk 0:02:41]} 3. Qh5 {[%clk 0:02:43]} Nf6 {[%clk 0:02:32]} 4. Qxf7# {[%clk 0:02:38]} 1-0`;
const c = replayPgn(CLKED);
// Clocks must land on their own move and descend per side, not run together.
const clkOk = !c.error
  && c.positions.length === 8
  && c.headers.TimeControl === '180+2'
  && c.positions[0].clock === null
  && c.positions[1].clock === '0:02:54'   // White's 1st
  && c.positions[2].clock === '0:02:50'   // Black's 1st
  && c.positions[7].clock === '0:02:38'   // the mate
  && c.positions[7].san === 'Qxf7#';      // no comment leaked into the SAN
if (!clkOk) failed++;
console.log(`${clkOk ? 'PASS' : 'FAIL'} {[%clk]} parses onto the right moves` +
  (clkOk ? ` (${c.positions.length - 1} plies, TimeControl ${c.headers.TimeControl})` : `  ← ${c.error ?? 'clock mismatch'}`));

// A comment body containing "1." must not be mistaken for a move number and swallow
// the move after it — the reason the parser tokenizes rather than strip-then-splits.
const evil = replayPgn('1. e4 {[%eval 1.5] 1... best} e5 {[%clk 0:01:00]} *');
const evilOk = !evil.error && evil.positions.length === 3 && evil.positions[2].clock === '0:01:00';
if (!evilOk) failed++;
console.log(`${evilOk ? 'PASS' : 'FAIL'} a comment containing "1." doesn't eat the next move` +
  (evilOk ? '' : `  ← ${evil.error ?? 'lost a ply or a clock'}`));

// An un-annotated PGN must stay clock-free rather than inventing one.
const bare = replayPgn('1. e4 e5 *');
const bareOk = !bare.error && bare.positions.length === 3 && bare.positions[1].clock === null;
if (!bareOk) failed++;
console.log(`${bareOk ? 'PASS' : 'FAIL'} PGN without clocks replays with clock === null`);

// Bullet: sub-second clocks must survive the round trip. Also captured from the dotnet
// harness. ChessGame.ClkField emits centiseconds with trailing zeros stripped, matching
// what lichess's own dartchess and python-chess read — if the fraction ever stops
// parsing, bullet games silently lose their clocks and this is the only thing watching.
const BULLET = `[Result "1-0"]
[TimeControl "60+0"]

1. e4 {[%clk 0:00:51.63]} e5 {[%clk 0:00:45.38]} 2. Bc4 {[%clk 0:00:43.26]} Nc6 {[%clk 0:00:30.76]} 3. Qh5 {[%clk 0:00:34.89]} Nf6 {[%clk 0:00:16.14]} 4. Qxf7# {[%clk 0:00:26.52]} 1-0`;
const bl = replayPgn(BULLET);
const bulletOk = !bl.error
  && bl.positions.length === 8
  && bl.positions[1].clock === '0:00:51.63'
  && bl.positions[2].clock === '0:00:45.38'
  && bl.positions[7].clock === '0:00:26.52';
if (!bulletOk) failed++;
console.log(`${bulletOk ? 'PASS' : 'FAIL'} sub-second {[%clk]} survives (bullet)` +
  (bulletOk ? ` (${bl.positions[1].clock})` : `  ← ${bl.error ?? 'lost the fraction'}`));

// A stripped trailing zero (.7, not .70) is what both reference writers emit, so the
// parser has to take a one-digit fraction as readily as two.
const tenth = replayPgn('1. e4 {[%clk 0:00:09.7]} *');
const tenthOk = !tenth.error && tenth.positions[1].clock === '0:00:09.7';
if (!tenthOk) failed++;
console.log(`${tenthOk ? 'PASS' : 'FAIL'} one-digit fraction (trailing zero stripped) parses`);

console.log(failed
  ? `\n${failed} FAILURE(S) — the viewer's rules do NOT match the client's.`
  : `\nALL PASS — viewer rules agree with Code/Chess/PerftCommand.cs`);
process.exit(failed ? 1 : 0);
