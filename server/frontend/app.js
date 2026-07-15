// Archive viewer. Talks only to the public endpoints (GET /api/v1/games*), so
// there is no auth here and nothing to leak: PGNs are public chess.
import { replayPgn } from './chess.js';

// U+FE0E VARIATION SELECTOR-15 forces TEXT presentation. Without it the pawn
// (U+265F) in particular renders as a colour emoji on several platforms — the
// same trap the s&box client hit, which is why its board is real 3D meshes.
const VS = '︎';
const GLYPH = { k: '♚', q: '♛', r: '♜', b: '♝', n: '♞', p: '♟' };

const $ = (id) => document.getElementById(id);
const el = { status: $('status'), list: $('list'), game: $('game') };

let view = { positions: [], at: 0, error: null };

// ── Rendering ──

function renderBoard(fen, from = -1, to = -1) {
  const rows = fen.split(' ')[0].split('/');
  const board = $('board');
  board.replaceChildren();

  for (let r = 0; r < 8; r++) {
    // Expand the FEN row ("r1bk3r" / "3q4") into 8 explicit cells.
    const cells = [];
    for (const ch of rows[r]) {
      if (/[1-8]/.test(ch)) { for (let i = 0; i < +ch; i++) cells.push(null); }
      else cells.push(ch);
    }
    for (let f = 0; f < 8; f++) {
      const rank = 7 - r;               // rows run 8→1; our squares run rank 0 = rank 1
      const sq = rank * 16 + f;         // 0x88, matching chess.js
      const d = document.createElement('div');
      d.className = 'sq ' + ((r + f) % 2 === 0 ? 'light' : 'dark');
      if (sq === from || sq === to) d.classList.add('hl');

      const p = cells[f];
      if (p) {
        const span = document.createElement('span');
        const white = p === p.toUpperCase();
        span.className = 'pc ' + (white ? 'w' : 'b');
        span.textContent = GLYPH[p.toLowerCase()] + VS;
        d.appendChild(span);
      }
      board.appendChild(d);
    }
  }
}

function renderMoves() {
  const ol = $('moves');
  ol.replaceChildren();

  view.positions.forEach((pos, i) => {
    if (i === 0) return;
    if (i % 2 === 1) {
      const num = document.createElement('li');
      num.className = 'num';
      num.textContent = Math.ceil(i / 2) + '.';
      ol.appendChild(num);
    }
    const li = document.createElement('li');
    li.textContent = pos.san;
    if (i === view.at) li.classList.add('on');
    li.onclick = () => goTo(i);
    ol.appendChild(li);
  });
}

function goTo(i) {
  view.at = Math.max(0, Math.min(i, view.positions.length - 1));
  const pos = view.positions[view.at];
  renderBoard(pos.fen, pos.from, pos.to);
  renderMoves();

  $('ply').textContent = view.at === 0
    ? 'Start position'
    : `Move ${view.at} of ${view.positions.length - 1}`;

  $('first').disabled = $('prev').disabled = view.at === 0;
  $('next').disabled = $('last').disabled = view.at === view.positions.length - 1;

  const on = ol_active();
  if (on) on.scrollIntoView({ block: 'nearest' });
}

const ol_active = () => $('moves').querySelector('li.on');

// ── Data ──

function status(msg, isErr = false) {
  el.status.textContent = msg;
  el.status.classList.toggle('err', isErr);
  el.status.hidden = !msg;
}

const show = (which) => {
  el.list.hidden = which !== 'list';
  el.game.hidden = which !== 'game';
};

async function api(path) {
  const res = await fetch(path, { headers: { Accept: 'application/json' } });
  if (!res.ok) {
    let msg = `Server returned ${res.status}`;
    try { msg = (await res.json()).error || msg; } catch { /* not JSON — keep the status */ }
    throw new Error(msg);
  }
  return res.json();
}

const fmtDate = (iso) => {
  const d = new Date(iso);
  return Number.isNaN(+d) ? '—' : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
};

// PGN headers carry the display names; the archive itself only keys on SteamID64
// (gamchess has no username of its own — names come from Steam and lichess).
function namesFrom(pgn) {
  const w = pgn.match(/^\s*\[White\s+"([^"]*)"\]/m);
  const b = pgn.match(/^\s*\[Black\s+"([^"]*)"\]/m);
  return { white: w?.[1] || 'Anonymous', black: b?.[1] || 'Anonymous' };
}

async function loadList(steamId) {
  show(null);
  status('Loading games…');
  try {
    const { games } = await api(`/api/v1/games?steam_id=${encodeURIComponent(steamId)}&limit=200`);
    status('');
    $('list-title').textContent = `${games.length} game${games.length === 1 ? '' : 's'} for ${steamId}`;

    const tbody = $('games').querySelector('tbody');
    tbody.replaceChildren();
    for (const g of games) {
      const { white, black } = namesFrom(g.pgn);
      const tr = document.createElement('tr');
      for (const [text, cls] of [[fmtDate(g.played_at), ''], [white, ''], [black, ''], [g.result, 'r result']]) {
        const td = document.createElement('td');
        td.className = cls;
        td.textContent = text;
        tr.appendChild(td);
      }
      tr.onclick = () => navigate({ game: g.id });
      tbody.appendChild(tr);
    }
    if (!games.length) status('No games archived for that SteamID yet.');
    show('list');
  } catch (e) {
    status(e.message, true);
  }
}

async function loadGame(id) {
  show(null);
  status('Loading game…');
  try {
    const g = await api(`/api/v1/games/${encodeURIComponent(id)}`);
    status('');

    const { white, black } = namesFrom(g.pgn);
    $('game-title').textContent = `${white} vs ${black} — ${g.result}`;
    $('game-meta').textContent = fmtDate(g.played_at)
      + (g.lichess_game_id ? ` · imported to lichess as ${g.lichess_game_id}` : '');
    $('pgn').textContent = g.pgn;

    const replay = replayPgn(g.pgn);
    view = { positions: replay.positions, at: 0, error: replay.error };

    const err = $('replay-error');
    err.hidden = !replay.error;
    // A PGN we can't fully replay still shows every move we DID understand —
    // a partial board beats a blank one.
    if (replay.error) err.textContent = `${replay.error} — showing the moves up to that point.`;

    goTo(replay.positions.length - 1);   // open on the final position
    show('game');
  } catch (e) {
    status(e.message, true);
  }
}

// ── Routing (query string, so a game is linkable) ──

function navigate(params, replace = false) {
  const url = new URL(location.href);
  url.search = new URLSearchParams(params).toString();
  history[replace ? 'replaceState' : 'pushState']({}, '', url);
  route();
}

function route() {
  const q = new URLSearchParams(location.search);
  const game = q.get('game');
  const steamId = q.get('steam_id');

  if (game) { loadGame(game); return; }
  if (steamId) { $('steamid').value = steamId; loadList(steamId); return; }

  show(null);
  status('Enter a SteamID64 to see that player’s archived games.');
}

// ── Wiring ──

$('lookup').addEventListener('submit', (e) => {
  e.preventDefault();
  const v = $('steamid').value.trim();
  if (!/^[1-9][0-9]{0,19}$/.test(v)) { status('That doesn’t look like a SteamID64.', true); return; }
  navigate({ steam_id: v });
});

$('back').addEventListener('click', (e) => {
  e.preventDefault();
  const sid = $('steamid').value.trim();
  navigate(sid ? { steam_id: sid } : {});
});

$('first').onclick = () => goTo(0);
$('prev').onclick = () => goTo(view.at - 1);
$('next').onclick = () => goTo(view.at + 1);
$('last').onclick = () => goTo(view.positions.length - 1);

document.addEventListener('keydown', (e) => {
  if (el.game.hidden) return;
  if (e.target.tagName === 'INPUT') return;
  const jump = { ArrowLeft: view.at - 1, ArrowRight: view.at + 1, Home: 0, End: view.positions.length - 1 }[e.key];
  if (jump === undefined) return;
  e.preventDefault();
  goTo(jump);
});

addEventListener('popstate', route);
route();
