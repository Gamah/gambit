// Minimal chess rules for the gamchess archive viewer: enough to replay a PGN
// and show each position. No engine, no evaluation — Gambit ships no engine, ever,
// and a viewer is not the place to start.
//
// This is NOT a port of the client's vendored Gera library; it's a second,
// independent implementation for the browser. That's a duplication worth naming:
// it's justified only because it's held to the SAME correctness gate as the C#
// side — `node scripts/chess_js_perft.mjs` runs the identical reference positions
// and node counts as Code/Chess/PerftCommand.cs. If those disagree, this is wrong.
//
// 0x88 board: sq = rank * 16 + file, rank 0 = rank 1, file 0 = 'a'. A square is
// off-board iff (sq & 0x88) — the whole reason for the layout.

const WHITE = 'w', BLACK = 'b';

const KNIGHT_OFFSETS = [-33, -31, -18, -14, 14, 18, 31, 33];
const BISHOP_OFFSETS = [-17, -15, 15, 17];
const ROOK_OFFSETS = [-16, -1, 1, 16];
const KING_OFFSETS = [-17, -16, -15, -1, 1, 15, 16, 17];

const isOffBoard = (sq) => (sq & 0x88) !== 0;
const rankOf = (sq) => sq >> 4;
const fileOf = (sq) => sq & 7;
const colorOf = (p) => (p === p.toUpperCase() ? WHITE : BLACK);
const typeOf = (p) => p.toLowerCase();

export const squareName = (sq) => 'abcdefgh'[fileOf(sq)] + (rankOf(sq) + 1);

export function parseFen(fen) {
  const [placement, turn, castling, ep, half, full] = fen.trim().split(/\s+/);
  const board = new Array(128).fill(null);

  let rank = 7, file = 0;
  for (const ch of placement) {
    if (ch === '/') { rank--; file = 0; continue; }
    if (/[1-8]/.test(ch)) { file += +ch; continue; }
    board[rank * 16 + file] = ch;
    file++;
  }

  return {
    board,
    turn: turn === 'b' ? BLACK : WHITE,
    castling: {
      K: castling.includes('K'), Q: castling.includes('Q'),
      k: castling.includes('k'), q: castling.includes('q'),
    },
    ep: ep && ep !== '-' ? algebraicToSquare(ep) : -1,
    half: +(half ?? 0),
    full: +(full ?? 1),
  };
}

export const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';
export const startPosition = () => parseFen(START_FEN);

function algebraicToSquare(s) {
  return ('abcdefgh'.indexOf(s[0])) + (+s[1] - 1) * 16;
}

export function toFen(st) {
  let placement = '';
  for (let rank = 7; rank >= 0; rank--) {
    let empty = 0;
    for (let file = 0; file < 8; file++) {
      const p = st.board[rank * 16 + file];
      if (!p) { empty++; continue; }
      if (empty) { placement += empty; empty = 0; }
      placement += p;
    }
    if (empty) placement += empty;
    if (rank) placement += '/';
  }
  const c = (st.castling.K ? 'K' : '') + (st.castling.Q ? 'Q' : '')
    + (st.castling.k ? 'k' : '') + (st.castling.q ? 'q' : '');
  return `${placement} ${st.turn} ${c || '-'} ${st.ep >= 0 ? squareName(st.ep) : '-'} ${st.half} ${st.full}`;
}

function cloneState(st) {
  return {
    board: st.board.slice(),
    turn: st.turn,
    castling: { ...st.castling },
    ep: st.ep,
    half: st.half,
    full: st.full,
  };
}

function findKing(st, color) {
  const target = color === WHITE ? 'K' : 'k';
  for (let sq = 0; sq < 128; sq++) {
    if (isOffBoard(sq)) { sq += 7; continue; }
    if (st.board[sq] === target) return sq;
  }
  return -1;
}

/** Is `sq` attacked by any `byColor` piece? Used for legality and for check. */
export function isAttacked(st, sq, byColor) {
  // Pawns. A white pawn on sq-15/sq-17 attacks sq (it captures "upward").
  const pawn = byColor === WHITE ? 'P' : 'p';
  for (const d of byColor === WHITE ? [-15, -17] : [15, 17]) {
    const from = sq + d;
    if (!isOffBoard(from) && st.board[from] === pawn) return true;
  }

  const knight = byColor === WHITE ? 'N' : 'n';
  for (const d of KNIGHT_OFFSETS) {
    const from = sq + d;
    if (!isOffBoard(from) && st.board[from] === knight) return true;
  }

  const king = byColor === WHITE ? 'K' : 'k';
  for (const d of KING_OFFSETS) {
    const from = sq + d;
    if (!isOffBoard(from) && st.board[from] === king) return true;
  }

  // Sliders.
  const sliders = [
    { offsets: BISHOP_OFFSETS, pieces: byColor === WHITE ? ['B', 'Q'] : ['b', 'q'] },
    { offsets: ROOK_OFFSETS, pieces: byColor === WHITE ? ['R', 'Q'] : ['r', 'q'] },
  ];
  for (const { offsets, pieces } of sliders) {
    for (const d of offsets) {
      for (let from = sq + d; !isOffBoard(from); from += d) {
        const p = st.board[from];
        if (!p) continue;
        if (pieces.includes(p)) return true;
        break; // blocked
      }
    }
  }
  return false;
}

export const inCheck = (st, color = st.turn) => isAttacked(st, findKing(st, color), color === WHITE ? BLACK : WHITE);

/** Pseudo-legal moves — may leave own king in check; filtered by generateMoves. */
function pseudoMoves(st) {
  const moves = [];
  const us = st.turn, them = us === WHITE ? BLACK : WHITE;

  const add = (from, to, extra = {}) => moves.push({ from, to, piece: st.board[from], ...extra });

  for (let from = 0; from < 128; from++) {
    if (isOffBoard(from)) { from += 7; continue; }
    const p = st.board[from];
    if (!p || colorOf(p) !== us) continue;
    const t = typeOf(p);

    if (t === 'p') {
      const dir = us === WHITE ? 16 : -16;
      const startRank = us === WHITE ? 1 : 6;
      const promoRank = us === WHITE ? 7 : 0;

      const one = from + dir;
      if (!isOffBoard(one) && !st.board[one]) {
        if (rankOf(one) === promoRank) for (const q of 'qrbn') add(from, one, { promotion: q });
        else {
          add(from, one);
          const two = from + dir * 2;
          if (rankOf(from) === startRank && !st.board[two]) add(from, two, { double: true });
        }
      }
      for (const d of [dir - 1, dir + 1]) {
        const to = from + d;
        if (isOffBoard(to)) continue;
        const target = st.board[to];
        if (target && colorOf(target) === them) {
          if (rankOf(to) === promoRank) for (const q of 'qrbn') add(from, to, { promotion: q, captured: target });
          else add(from, to, { captured: target });
        } else if (to === st.ep) {
          add(from, to, { ep: true, captured: us === WHITE ? 'p' : 'P' });
        }
      }
      continue;
    }

    if (t === 'n' || t === 'k') {
      for (const d of t === 'n' ? KNIGHT_OFFSETS : KING_OFFSETS) {
        const to = from + d;
        if (isOffBoard(to)) continue;
        const target = st.board[to];
        if (target && colorOf(target) === us) continue;
        add(from, to, target ? { captured: target } : {});
      }
      continue;
    }

    const offsets = t === 'b' ? BISHOP_OFFSETS : t === 'r' ? ROOK_OFFSETS : [...BISHOP_OFFSETS, ...ROOK_OFFSETS];
    for (const d of offsets) {
      for (let to = from + d; !isOffBoard(to); to += d) {
        const target = st.board[to];
        if (!target) { add(from, to); continue; }
        if (colorOf(target) === them) add(from, to, { captured: target });
        break;
      }
    }
  }

  // Castling. Squares between must be empty, and the king must not start in,
  // pass through, or land on an attacked square.
  const backRank = us === WHITE ? 0 : 7;
  const kingHome = backRank * 16 + 4;
  if (st.board[kingHome] === (us === WHITE ? 'K' : 'k') && !isAttacked(st, kingHome, them)) {
    const rights = us === WHITE ? [st.castling.K, st.castling.Q] : [st.castling.k, st.castling.q];
    const rook = us === WHITE ? 'R' : 'r';
    if (rights[0] && st.board[backRank * 16 + 7] === rook
      && !st.board[kingHome + 1] && !st.board[kingHome + 2]
      && !isAttacked(st, kingHome + 1, them) && !isAttacked(st, kingHome + 2, them)) {
      add(kingHome, kingHome + 2, { castle: 'k' });
    }
    if (rights[1] && st.board[backRank * 16] === rook
      && !st.board[kingHome - 1] && !st.board[kingHome - 2] && !st.board[kingHome - 3]
      && !isAttacked(st, kingHome - 1, them) && !isAttacked(st, kingHome - 2, them)) {
      add(kingHome, kingHome - 2, { castle: 'q' });
    }
  }

  return moves;
}

export function makeMove(st, m) {
  const next = cloneState(st);
  const us = st.turn;
  const piece = st.board[m.from];
  const t = typeOf(piece);

  next.board[m.from] = null;
  next.board[m.to] = m.promotion
    ? (us === WHITE ? m.promotion.toUpperCase() : m.promotion)
    : piece;

  if (m.ep) next.board[m.to + (us === WHITE ? -16 : 16)] = null;

  if (m.castle) {
    const backRank = us === WHITE ? 0 : 7;
    if (m.castle === 'k') {
      next.board[backRank * 16 + 5] = next.board[backRank * 16 + 7];
      next.board[backRank * 16 + 7] = null;
    } else {
      next.board[backRank * 16 + 3] = next.board[backRank * 16];
      next.board[backRank * 16] = null;
    }
  }

  // Castling rights: lost by moving the king or a rook, or by the rook's home
  // square being captured on.
  if (t === 'k') {
    if (us === WHITE) { next.castling.K = next.castling.Q = false; }
    else { next.castling.k = next.castling.q = false; }
  }
  const clearRookRight = (sq) => {
    if (sq === 0) next.castling.Q = false;
    else if (sq === 7) next.castling.K = false;
    else if (sq === 112) next.castling.q = false;
    else if (sq === 119) next.castling.k = false;
  };
  clearRookRight(m.from);
  clearRookRight(m.to);

  next.ep = m.double ? m.from + (us === WHITE ? 16 : -16) : -1;
  next.half = (t === 'p' || m.captured) ? 0 : st.half + 1;
  if (us === BLACK) next.full = st.full + 1;
  next.turn = us === WHITE ? BLACK : WHITE;

  return next;
}

/** Fully legal moves. */
export function generateMoves(st) {
  const us = st.turn, them = us === WHITE ? BLACK : WHITE;
  return pseudoMoves(st).filter((m) => {
    const after = makeMove(st, m);
    return !isAttacked(after, findKing(after, us), them);
  });
}

/**
 * SAN for a move, WITHOUT the +/# suffix.
 *
 * We generate SAN and compare, rather than parsing SAN. Parsing means
 * reimplementing disambiguation rules and getting every edge case right;
 * generating means being right once. Replay then just matches strings.
 */
export function moveToSan(st, m) {
  if (m.castle) return m.castle === 'k' ? 'O-O' : 'O-O-O';

  const t = typeOf(m.piece);
  const to = squareName(m.to);

  if (t === 'p') {
    let san = m.captured ? `${'abcdefgh'[fileOf(m.from)]}x${to}` : to;
    if (m.promotion) san += '=' + m.promotion.toUpperCase();
    return san;
  }

  // Disambiguate against other same-type pieces that could legally reach `to`:
  // by file if that's unique, else by rank, else by full square.
  const rivals = generateMoves(st).filter((o) =>
    o.to === m.to && o.from !== m.from && typeOf(o.piece) === t);

  let disambig = '';
  if (rivals.length) {
    const sameFile = rivals.some((o) => fileOf(o.from) === fileOf(m.from));
    const sameRank = rivals.some((o) => rankOf(o.from) === rankOf(m.from));
    if (!sameFile) disambig = 'abcdefgh'[fileOf(m.from)];
    else if (!sameRank) disambig = String(rankOf(m.from) + 1);
    else disambig = squareName(m.from);
  }

  return t.toUpperCase() + disambig + (m.captured ? 'x' : '') + to;
}

/** Node count of the legal-move tree — the correctness gate. */
export function perft(st, depth) {
  if (depth === 0) return 1;
  const moves = generateMoves(st);
  if (depth === 1) return moves.length;
  let n = 0;
  for (const m of moves) n += perft(makeMove(st, m), depth - 1);
  return n;
}

// ── PGN ──

/** Strip SAN decoration so a generated SAN can be matched against a PGN token. */
const bareSan = (s) => s.replace(/[+#?!]+$/g, '');

export function parsePgn(pgn) {
  const headers = {};
  for (const m of pgn.matchAll(/^\s*\[(\w+)\s+"([^"]*)"\]\s*$/gm)) headers[m[1]] = m[2];

  // Everything after the header block is movetext. Rest-of-line (;) comments are removed
  // up front: they run to a newline and can't contain braces, so dropping them can't
  // disturb the brace comments the tokenizer below is about to read.
  const movetext = pgn
    .replace(/^\s*\[[^\]]*\]\s*$/gm, ' ')
    .replace(/;[^\n]*/g, ' ');

  // One pass, brace comments intact. This used to strip comments and move numbers with
  // separate global replaces, but the move-number pattern (\d+\.) happily matches inside
  // a comment body — "{[%eval 1.5]}" contains "1." — so a comment could be corrupted
  // before anything got to read it. Matching comments as whole tokens avoids that.
  // Order matters: comment, then move number, then NAG, then anything else.
  const TOKEN = /\{([^}]*)\}|\d+\s*\.(?:\.\.)?|\$\d+|([^\s{}]+)/g;
  const RESULTS = ['1-0', '0-1', '1/2-1/2', '*'];

  const moves = [];
  const clocks = [];    // clocks[i] = %clk left by the mover of moves[i], or undefined
  let result = headers.Result || '*';

  for (let m; (m = TOKEN.exec(movetext)); ) {
    if (m[1] !== undefined) {
      // A comment annotates the move it follows (PGN spec §8.2.5).
      const clk = /\[%clk\s+([\d:.]+)\]/.exec(m[1]);
      if (clk && moves.length) clocks[moves.length - 1] = clk[1];
      continue;
    }
    const tok = m[2];
    if (!tok) continue;                                    // a move number or a $NAG
    if (RESULTS.includes(tok)) { result = tok; continue; }
    moves.push(tok);
  }

  return { headers, moves, clocks, result };
}

/**
 * Replay a PGN into a list of positions.
 * Returns { headers, result, positions: [{ fen, san, from, to, clock }], error }.
 * Position 0 is the start; position i is after the i-th move. `clock` is the mover's
 * remaining time from a {[%clk]} comment, or null when the PGN carries none.
 *
 * Never throws: a viewer showing a truncated game beats a viewer showing an
 * exception. `error` names the first token that didn't match a legal move.
 */
export function replayPgn(pgn) {
  const { headers, moves, clocks, result } = parsePgn(pgn);
  let st = headers.FEN ? parseFen(headers.FEN) : startPosition();

  const positions = [{ fen: toFen(st), san: null, from: -1, to: -1, clock: null }];
  let error = null;

  for (let i = 0; i < moves.length; i++) {
    const token = moves[i];
    const want = bareSan(token);
    const legal = generateMoves(st);
    const match = legal.find((m) => moveToSan(st, m) === want);
    if (!match) { error = `Couldn't play "${token}"`; break; }

    st = makeMove(st, match);
    positions.push({
      fen: toFen(st), san: token, from: match.from, to: match.to,
      clock: clocks[i] ?? null,
    });
  }

  return { headers, result, positions, error };
}
