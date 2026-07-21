// Archive viewer.
//
// The archive is PRIVATE: every read needs a session, and the server only ever
// returns games you sat in. There is no SteamID input on purpose — identity comes
// from the session cookie, never from anything typed here. Sending a SteamID would
// make it a request rather than a fact, and the server ignores it either way.
import { replayPgn } from './chess.js';

// U+FE0E VARIATION SELECTOR-15 forces TEXT presentation. Without it the pawn
// (U+265F) in particular renders as a colour emoji on several platforms — the
// same trap the s&box client hit, which is why its board is real 3D meshes.
const VS = '︎';
const GLYPH = { k: '♚', q: '♛', r: '♜', b: '♝', n: '♞', p: '♟' };

const $ = (id) => document.getElementById(id);
const el = { status: $('status'), list: $('list'), game: $('game'), signin: $('signin') };

let me = null;   // { steam_id } once signed in

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

// "0:02:58" → "2:58" and "0:00:09.7" → "0:09.7", but "1:04:00" stays whole. The optional
// fraction is bullet's: %clk carries up to 3 decimals and we write centiseconds, so it
// has to survive the trim. Anything not shaped H:MM:SS[.f] passes through untouched — a
// foreign PGN may carry a %clk we don't write.
function shortClk(clk) {
  const m = /^0:(\d{2}:\d{2}(?:\.\d+)?)$/.exec(clk);
  if (!m) return clk;
  return m[1].replace(/^0/, '');
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
    // {[%clk]} from the PGN, when the game was played on a clock. Trimmed to m:ss:
    // the tag is H:MM:SS, and the leading "0:" on every move of a sub-hour game is
    // noise in a list this narrow.
    if (pos.clock) {
      const clk = document.createElement('span');
      clk.className = 'clk';
      clk.textContent = shortClk(pos.clock);
      li.appendChild(clk);
    }
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
  if (which) el.signin.hidden = true;
};

async function api(path) {
  const res = await fetch(path, { headers: { Accept: 'application/json' } });
  if (res.status === 401) {
    // Session expired mid-visit — fall back to the gate rather than a raw error.
    me = null;
    show(null);
    el.signin.hidden = false;
    throw new Error('Your session expired \u2014 sign in again.');
  }
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

// PGN [TimeControl] is "seconds+increment" (PGN spec §9.6), but chess convention shows
// the initial bank in minutes — so the "180+2" Gambit writes reads back as "3+2".
// Anything unparseable is shown verbatim rather than guessed at: the archive is durable
// and may hold PGNs that predate our writer, or that came from elsewhere entirely.
function fmtTimeControl(spec) {
  if (!spec) return null;
  const s = spec.trim();
  if (s === '-') return 'Untimed';

  const m = /^(\d+)\+(\d+)$/.exec(s);
  if (!m) return s;

  const secs = Number(m[1]);
  const inc = Number(m[2]);
  // Whole minutes stay integers (600 → 10); anything else keeps one decimal (90 → 1.5)
  // rather than rounding a 90-second bullet game to "2+0".
  const mins = secs % 60 === 0 ? secs / 60 : Math.round((secs / 60) * 10) / 10;
  return `${mins}+${inc}`;
}

// PGN headers carry the display names; the archive itself only keys on SteamID64
// (gamchess has no username of its own — names come from Steam).
function namesFrom(pgn) {
  const w = pgn.match(/^\s*\[White\s+"([^"]*)"\]/m);
  const b = pgn.match(/^\s*\[Black\s+"([^"]*)"\]/m);
  return { white: w?.[1] || 'Anonymous', black: b?.[1] || 'Anonymous' };
}

// The raw [TimeControl] header, or null. The list has the full PGN in hand, so it
// reads the tag with a regex rather than replaying the whole game (loadGame parses
// it properly, but a list of 200 games shouldn't). Absent on games archived before
// Gambit wrote the tag — those show "—", same as a game with no result.
function tcFrom(pgn) {
  const m = pgn.match(/^\s*\[TimeControl\s+"([^"]*)"\]/m);
  return m?.[1] || null;
}

async function loadList() {
  show(null);
  status('Loading your games\u2026');
  try {
    const { games } = await api('/api/v1/games?limit=200');
    status('');
    $('list-title').textContent = games.length
      ? `Your games (${games.length})`
      : 'Your games';

    const tbody = $('games').querySelector('tbody');
    tbody.replaceChildren();
    for (const g of games) {
      const { white, black } = namesFrom(g.pgn);
      const tr = document.createElement('tr');
      for (const [text, cls] of [[fmtDate(g.played_at), ''], [fmtTimeControl(tcFrom(g.pgn)) || '—', 'tc'], [white, ''], [black, ''], [g.result, 'r result']]) {
        const td = document.createElement('td');
        td.className = cls;
        td.textContent = text;
        tr.appendChild(td);
      }
      tr.onclick = () => navigate({ game: g.id });
      tbody.appendChild(tr);
    }
    if (!games.length) status('No games archived yet — play one in Terry\u2019s Gambit.');
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
    $('pgn').textContent = g.pgn;

    const replay = replayPgn(g.pgn);
    view = { positions: replay.positions, at: 0, error: replay.error };

    // Replay parses every header, so the time control rides along for free. Games
    // archived before Gambit wrote the tag simply have no time control to show.
    const tc = fmtTimeControl(replay.headers?.TimeControl);
    $('game-meta').textContent = tc ? `${fmtDate(g.played_at)} · ${tc}` : fmtDate(g.played_at);

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

/** Resolve who we are. 401 simply means signed out — not an error to shout about. */
async function loadMe() {
  try {
    const res = await fetch('/api/v1/me', { headers: { Accept: 'application/json' } });
    me = res.ok ? await res.json() : null;
  } catch { me = null; }

  const on = !!me;
  $('who').hidden = !on;
  $('signout').hidden = !on;
  if (on) $('who').textContent = me.steam_id;
  return on;
}

async function route() {
  const q = new URLSearchParams(location.search);

  if (!(await loadMe())) {
    show(null);
    el.signin.hidden = false;
    // The server bounces a failed OpenID return here rather than explaining why.
    status(q.get('error') === 'signin' ? 'Steam sign-in failed \u2014 try again.' : '', true);
    return;
  }
  el.signin.hidden = true;

  const game = q.get('game');
  if (game) { loadGame(game); return; }
  loadList();
}

// ── Wiring ──

$('signout').addEventListener('click', async () => {
  // POST, not a link: a GET logout can be fired by any stray <img> or prefetch.
  await fetch('/auth/steam/logout', { method: 'POST' });
  location.href = '/';
});

$('back').addEventListener('click', (e) => {
  e.preventDefault();
  navigate({});
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
