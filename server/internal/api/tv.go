package api

import (
	"context"
	"net/http"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gorilla/websocket"
	"go.uber.org/zap"
)

// lichess TV, fanned out over a WebSocket push (M18).
//
// # The invariant
//
// ONE upstream stream per CHANNEL, no matter how many people are watching. 100
// players on blitz = 1 open feed to lichess. That is the entire reason TV is
// proxied here rather than hit directly from each client, and it is what makes
// per-client channel choice affordable: the cost to lichess is bounded by the
// channel count (15), not the player count.
//
// The stream opens on the first watcher and is dropped tvLingerTTL after the last
// connection leaves. Ref-counted by a CONNECTION COUNT, incremented in watch and
// decremented in leave — and leave runs from a `defer` on every exit path of the
// socket handler, so a rude TCP close cannot leak a stream. (Before M18 this was a
// last-polled timestamp, because a long poll's HTTP handler had exit paths a
// dropped connection never ran; a WebSocket handler holds the connection for its
// whole life, so a guaranteed `defer` decrement is both possible and simpler.)
//
// # The transport
//
// gamchess still reads the lichess ndjson feed (unchanged, anonymous upstream) and
// stays the sole stream holder. What changed in M18 is the CLIENT-facing hop: it
// was a version-gated long poll over a single latest-state slot, with a stack of
// machinery (a since/version cursor, a hold_ms field, sawtooth guards, a full
// clock latency-compensation apparatus) layered on to make a 5s-held poll feel
// like a live stream. It is now a WebSocket that pushes one full, self-contained
// snapshot whenever a channel's state actually changes — no deltas, no cursor,
// latest-wins. Every message is the whole TvState.
//
// # Why it is gated
//
// /api/tv/{channel}/feed is anonymous upstream, so a proxy of it needs no
// identity to function. It is session-gated anyway, for two reasons that have
// nothing to do with cost:
//
//  1. An open /api/v1/tv is a free CDN for someone else's content, pointable by
//     any script, costing us bandwidth to serve lichess's feed to people who have
//     never touched Gambit.
//  2. lichess sees our IP and our User-Agent. We went out of our way to make that
//     traffic attributable (etiquette.go) so they CAN attribute it — which means
//     anything done through an open relay is done as Gambit, against the one IP
//     whose limits every real player shares. Being identifiable and being an open
//     relay is a bad combination.
//
// A Steam identity to watch TV is a trivial ask for a Steam-gated game, and it
// costs one local HMAC — checked BEFORE the upgrade, so a failed auth is a plain
// 401 with no socket and no upstream.

const (
	// How long a channel's upstream stays open after its LAST connection leaves.
	// A short A→B→A channel switch reconnects within a round trip, so a linger of a
	// few seconds keeps that from flapping the upstream (close+reopen against
	// lichess) while still costing them nothing within a few seconds of a wall
	// going empty. Correctness does not depend on this value — the defer decrement
	// guarantees we never leak — only cost does.
	tvLingerTTL = 10 * time.Second

	// How often the sweeper looks for channels whose last connection left more than
	// tvLingerTTL ago.
	tvSweepEvery = 5 * time.Second

	// A TV stream that drops while people are still watching is retried this
	// often. Mirrors streamRetryDelay for the game relay.
	tvRetryDelay = 2 * time.Second

	// How long to wait for "how did the last game end" before giving up on it. The
	// swap is already published (see run); this only fills in the fanfare's reason,
	// so a slow lichess export costs the detail, never the announcement.
	tvResultTimeout = 3 * time.Second

	// WebSocket keepalive. A classical-channel game can idle for minutes between
	// moves, and an intermediary (Caddy) may cut a silent socket — so gamchess
	// pings every tvPingEvery and drops a client that hasn't ponged within
	// tvPongWait. tvWriteWait bounds a single frame write so a wedged client can't
	// block the writer forever.
	tvPingEvery = 30 * time.Second
	tvPongWait  = 70 * time.Second
	tvWriteWait = 10 * time.Second
)

// TvState is the bespoke wire message: one full, self-contained snapshot of a
// channel's featured game, pushed to every connected client on each change.
//
// The field list is exactly what the spectator wall wants — Fen, LastMoveUci,
// names, clocks, whose turn — because it exists to feed it and nothing else.
//
// Clocks are SECONDS, straight from lichess's fen frame. Note this differs from
// PlayState, which is milliseconds: two lichess endpoints, two units. Seconds is
// what TimeControl.Format takes, so the client converts nothing.
type TvState struct {
	Channel string `json:"channel"`
	// Label is the human channel name ("Blitz"), so the client needn't keep its own
	// copy in sync with ours.
	Label string `json:"label"`

	// Error is set when we cannot currently serve this channel (lichess is backing
	// off, the stream dropped and hasn't recovered). The client shows it and keeps
	// the socket open — an error here is never fatal and never stops the wall from
	// mirroring real tables.
	Error string `json:"error,omitempty"`

	GameID string `json:"game_id,omitempty"`
	URL    string `json:"url,omitempty"`

	Fen         string `json:"fen,omitempty"`
	LastMoveUci string `json:"last_move_uci,omitempty"`

	WhiteName   string `json:"white_name,omitempty"`
	BlackName   string `json:"black_name,omitempty"`
	WhiteTitle  string `json:"white_title,omitempty"`
	BlackTitle  string `json:"black_title,omitempty"`
	WhiteRating int    `json:"white_rating,omitempty"`
	BlackRating int    `json:"black_rating,omitempty"`

	WhiteClock int `json:"white_clock"`
	BlackClock int `json:"black_clock"`

	// ── What keeps the wall's clock legal (M18) ──
	//
	// The house rule is that a live clock must never read HIGHER than the time
	// actually left; reading low is fine. M18 favours reading low deliberately and
	// keeps the mechanism tiny.
	//
	// The steady-state push is fresh — a change wakes the writer and it sends
	// immediately — so sub-second transport latency is absorbed for free by the
	// client flooring the displayed second (TimeControl.Format truncates). The one
	// case the floor cannot cover is a client that CONNECTS mid-think: it is handed
	// the stored frame, which is already however-many-seconds stale. AgeMs is that
	// staleness, and the client subtracts it so the replayed frame reads low, not
	// high.
	//
	// A duration, not a timestamp, and that is the point: we do not share a wall
	// clock with the client, so an absolute stamp would be corrected by whatever the
	// client's skew happens to be — including UP, the one direction the house rule
	// forbids. Computed AT SEND on the response copy (see stamp), never stored.
	// ~0 on a live push; nonzero only on the connect/replay path.
	AgeMs int64 `json:"age_ms,omitempty"`

	// TickingSeat is "white"/"black" — whose clock is running, derived from the
	// FEN's side-to-move. lichess doesn't send it; the FEN already says it.
	TickingSeat string `json:"ticking_seat,omitempty"`

	// How the PREVIOUS featured game ended, so the wall can hold on it for a beat
	// instead of cutting straight to the next one.
	//
	// The TV feed says NOTHING about a game ending — it just swaps to a new featured
	// game. So these are fetched from the (anonymous) game export when we notice the
	// swap, and they describe the game the client was probably still watching, not the
	// one now in Fen.
	//
	// LastGameID is what the client matches against: "the game I am showing just
	// ended, and here is how". It stays set until the next swap. Empty until the first
	// one, and LastStatus stays empty if the fetch failed — the fanfare then says a
	// game ended without saying how, which is worse than the detail but better than
	// waiting on lichess to tell the wall anything at all.
	LastGameID string `json:"last_game_id,omitempty"`
	// LastStatus is lichess's own vocabulary: mate, resign, outoftime, stalemate,
	// draw, timeout, aborted, variantEnd… The CLIENT turns it into English, because
	// that is the half a human ever reads.
	LastStatus string `json:"last_status,omitempty"`
	// LastWinner is "white", "black", or "" for a draw — lichess omits the field
	// rather than sending a third value, so empty is an answer.
	LastWinner string `json:"last_winner,omitempty"`
	// The names as they were, so the fanfare can say who won without the client
	// having to have kept them.
	LastWhiteName string `json:"last_white_name,omitempty"`
	LastBlackName string `json:"last_black_name,omitempty"`

	// clockAt is when WhiteClock/BlackClock were received from lichess. Unexported,
	// so it never reaches the wire and cannot be mistaken for something a client may
	// read: it is in OUR clock's frame of reference and means nothing in theirs.
	// AgeMs is derived from it at send time.
	clockAt time.Time
}

// stamp fills AgeMs on a COPY of the state, right before the handler writes it —
// never on the shared state, or two clients on one channel would age it
// cumulatively and every push would read staler than the last. On a live push
// clockAt was set moments ago, so AgeMs ≈ 0; on the frame a fresh connection is
// handed, it is the real staleness the client must subtract.
func (s *TvState) stamp(now time.Time) {
	if !s.clockAt.IsZero() {
		s.AgeMs = now.Sub(s.clockAt).Milliseconds()
	}
}

// tvChannel is one channel's fanned-out upstream.
type tvChannel struct {
	mu    sync.Mutex
	state TvState
	// changed is the close-and-replace wake primitive: every writer selects on it,
	// and update/setLastResult close it to wake them all. It is the real internal
	// signal — the old client-facing version cursor was only ever a poll's "is this
	// news" test, which a push does not need (a push only ever fires ON news).
	changed chan struct{}

	cancel context.CancelFunc

	// conns is how many sockets are currently attached, and lastEmpty is when it
	// last fell to zero. Both are guarded by tv.mu (not this mutex): watch, leave
	// and sweep all take tv.mu, so the ref count lives at the same level as the map
	// it gates. The count can only leak if a decrement is missed — which is why
	// leave is called from a defer, not from a code path.
	conns     int
	lastEmpty time.Time
}

func (c *tvChannel) snapshot() (TvState, <-chan struct{}) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state, c.changed
}

// update mutates the state and wakes every waiter. Close-and-replace is what makes
// the fan-out race-free: a writer that grabbed `changed` before we closed it still
// sees the close.
func (c *tvChannel) update(fn func(*TvState)) {
	c.mu.Lock()
	fn(&c.state)
	close(c.changed)
	c.changed = make(chan struct{})
	c.mu.Unlock()
}

// setLastResult folds a finished game's reason into the current state and wakes
// every waiter — but ONLY if that game is still the one the state says just ended.
//
// It runs from the background fetch kicked off on a featured swap, and a fast
// channel can swap AGAIN before the fetch returns: by then LastGameID names a newer
// ending and this answer is stale, so we drop it WITHOUT closing `changed`. A
// spurious wake would re-push the full snapshot to every client and make them
// re-snap their locally-run clocks — so a no-op must stay a no-op on the wire.
func (c *tvChannel) setLastResult(gameID, status, winner string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.state.LastGameID != gameID {
		return
	}
	c.state.LastStatus = status
	c.state.LastWinner = winner
	close(c.changed)
	c.changed = make(chan struct{})
}

// currentGame returns the featured game id and both names as they stand — read
// under the lock, before a featured swap overwrites them, so we know which game
// just ended and who was playing it.
func (c *tvChannel) currentGame() (id, white, black string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state.GameID, c.state.WhiteName, c.state.BlackName
}

// tv owns every live channel.
type tv struct {
	log *zap.Logger

	mu       sync.Mutex
	channels map[lichess.Channel]*tvChannel

	// sweeping guards the lazily-started sweeper goroutine.
	sweeping bool

	// lingerTTL, streamTv and gameResult are fields, not constants, for the same
	// reason api.validateToken is a package var: they are the seams that let the
	// ref-counting, the frame state machine and the end-of-game fanfare be tested
	// without lichess and without a 10-second wait. Production uses the real values.
	lingerTTL  time.Duration
	streamTv   func(context.Context, lichess.Channel, func(lichess.TvEvent)) error
	gameResult func(context.Context, string) (lichess.TvResult, error)
}

func newTv(log *zap.Logger) *tv {
	return &tv{
		log:        log,
		channels:   map[lichess.Channel]*tvChannel{},
		lingerTTL:  tvLingerTTL,
		streamTv:   lichess.StreamTv,
		gameResult: lichess.GameResult,
	}
}

// watch returns the channel's fan-out, opening the upstream if this is the first
// connection. Every call increments the connection count; leave decrements it.
func (t *tv) watch(c lichess.Channel) *tvChannel {
	t.mu.Lock()
	defer t.mu.Unlock()

	if ch, ok := t.channels[c]; ok {
		ch.conns++
		return ch
	}

	ch := &tvChannel{
		changed: make(chan struct{}),
		conns:   1,
		state: TvState{
			Channel: string(c),
			Label:   lichess.ChannelLabel(c),
		},
	}

	ctx, cancel := context.WithCancel(context.Background())
	ch.cancel = cancel
	t.channels[c] = ch

	t.log.Info("lichess tv: opening upstream", zap.String("channel", string(c)))
	go t.run(ctx, c, ch)
	t.startSweeper()
	return ch
}

// leave drops one connection's claim on a channel. It NEVER cancels the upstream —
// teardown stays the sweeper's single job, so "we never leak a stream" is a
// property of one function rather than of every exit path here. Called from a
// defer in the socket handler, so it runs on every exit including a rude close.
func (t *tv) leave(c lichess.Channel, ch *tvChannel) {
	t.mu.Lock()
	defer t.mu.Unlock()
	ch.conns--
	if ch.conns <= 0 {
		ch.conns = 0
		ch.lastEmpty = time.Now()
	}
}

// run holds the upstream open, retrying while the channel is still registered.
// Exits only when the context is cancelled — which is the sweeper's job, not its
// own.
func (t *tv) run(ctx context.Context, c lichess.Channel, ch *tvChannel) {
	for {
		err := t.streamTv(ctx, c, func(ev lichess.TvEvent) {
			switch {
			case ev.Featured != nil:
				f := ev.Featured
				w, b := f.White(), f.Black()

				// A NEW featured game means the old one just ended — that swap is the only
				// notice the feed ever gives.
				prev, prevW, prevB := ch.currentGame()
				ended := prev != "" && prev != f.ID

				// Publish the swap IMMEDIATELY, without the ending's reason.
				//
				// The client decides a game ended purely from the game_id changing (see the
				// client's LichessTvSource) and starts its fanfare the instant it sees the
				// swap — so it must not wait on a side fetch to lichess. Blocking the publish
				// on GameResult here froze the wall for up to tvResultTimeout with no fanfare
				// at all: the ending was on the WRONG side of the publish. LastGameID is set
				// now (that's what the client matches "the game I'm showing just ended"
				// against); LastStatus/LastWinner are filled in by the async fetch below and
				// pushed in a later message while the client is still holding on the finished
				// position.
				ch.update(func(s *TvState) {
					s.Error = ""
					if ended {
						s.LastGameID = prev
						s.LastStatus = ""
						s.LastWinner = ""
						s.LastWhiteName, s.LastBlackName = prevW, prevB
					}
					s.GameID = f.ID
					s.URL = "https://lichess.org/" + f.ID
					s.Fen = f.Fen
					// A new featured game has no last move to highlight — and keeping the
					// old one would draw a highlight from a different game entirely.
					s.LastMoveUci = ""
					s.WhiteName, s.WhiteTitle, s.WhiteRating = w.Name(), w.Title(), w.Rating
					s.BlackName, s.BlackTitle, s.BlackRating = b.Name(), b.Title(), b.Rating
					// featured carries the STARTING clock per side; fen frames correct it
					// on the very next move. Without this the wall shows 0:00 until then.
					s.WhiteClock, s.BlackClock = w.Seconds, b.Seconds
					s.clockAt = time.Now()
					s.TickingSeat = sideToMove(f.Fen)
				})

				// Now go ask how the old game ended, OFF the reader's path — the TV feed
				// says nothing, so this is the only way to get "White wins — out of time".
				// One request per game end per channel, through the same governor as
				// everything else. Never fatal: on failure the fanfare simply keeps its
				// bare "Game over". setLastResult drops the answer if a newer game has since
				// ended, so a fast channel that swaps again mid-fetch can't get a stale
				// reason pinned to it.
				if ended {
					go func(prev string) {
						rctx, cancel := context.WithTimeout(ctx, tvResultTimeout)
						defer cancel()
						result, err := t.gameResult(rctx, prev)
						if err != nil {
							t.log.Warn("lichess tv: couldn't read how the last game ended",
								zap.String("channel", string(c)), zap.String("game", prev), zap.Error(err))
							return
						}
						ch.setLastResult(prev, result.Status, result.Winner)
					}(prev)
				}
			case ev.Fen != nil:
				f := ev.Fen
				ch.update(func(s *TvState) {
					s.Error = ""
					s.Fen = f.Fen
					s.LastMoveUci = f.LM
					s.WhiteClock, s.BlackClock = f.WC, f.BC
					// The stamp that keeps the clock legal: this is the closest we will
					// ever get to lichess's T0. The lichess -> gamchess leg is already
					// spent and nothing downstream can recover it — see the client.
					s.clockAt = time.Now()
					s.TickingSeat = sideToMove(f.Fen)
				})
			}
		})

		if ctx.Err() != nil {
			t.log.Info("lichess tv: upstream closed", zap.String("channel", string(c)))
			return
		}

		// The stream dropped on its own. Surface it, then retry — someone is still
		// watching or the sweeper would have cancelled us.
		t.log.Warn("lichess tv: stream dropped, retrying",
			zap.String("channel", string(c)), zap.Error(err))
		ch.update(func(s *TvState) { s.Error = "lichess tv is unavailable right now" })

		select {
		case <-ctx.Done():
			return
		case <-time.After(tvRetryDelay):
		}
	}
}

// startSweeper launches the idle sweeper once. Lazily started so a deployment
// that never serves TV never runs a goroutine for it.
func (t *tv) startSweeper() {
	if t.sweeping {
		return
	}
	t.sweeping = true
	go func() {
		for range time.Tick(tvSweepEvery) {
			t.sweep()
		}
	}()
}

// sweep drops channels whose last connection left more than lingerTTL ago. This is
// the ONLY thing that closes an upstream — which is what makes "we never leak a
// stream" a property of one function rather than of every exit path in the handler.
func (t *tv) sweep() {
	t.mu.Lock()
	defer t.mu.Unlock()
	for c, ch := range t.channels {
		if ch.conns > 0 || time.Since(ch.lastEmpty) < t.lingerTTL {
			continue
		}
		t.log.Info("lichess tv: dropping idle upstream", zap.String("channel", string(c)))
		ch.cancel()
		delete(t.channels, c)
	}
}

// sideToMove reads the active colour out of a FEN's second field. Returns "" for
// anything that isn't one — a malformed FEN must not be reported as white's turn.
func sideToMove(fen string) string {
	// A FEN is space-separated; field 1 is the side to move. Hand-split rather than
	// strings.Fields to avoid allocating for a single byte.
	i := 0
	for ; i < len(fen) && fen[i] != ' '; i++ {
	}
	if i+1 >= len(fen) {
		return ""
	}
	switch fen[i+1] {
	case 'w':
		return "white"
	case 'b':
		return "black"
	}
	return ""
}

// ── the route ──

// tvUpgrader turns a TV GET into a WebSocket. CheckOrigin returns true on purpose:
// the client is the s&box game, not a browser, so there is no cookie for a
// cross-site page to ride and Origin is not a boundary here — the session bearer
// checked before the upgrade is the real gate. permessage-deflate is enabled
// because TV data is public (the anonymous lichess feed, no secrets) and the full
// snapshots compress well, cutting egress with nothing to leak.
var tvUpgrader = websocket.Upgrader{
	CheckOrigin:       func(*http.Request) bool { return true },
	EnableCompression: true,
}

// tvSocket is GET /api/v1/tv/{channel}, upgraded to a WebSocket that pushes one
// full TvState snapshot per change.
//
// Session-gated (see the package comment), and the gate runs BEFORE the upgrade —
// a failed auth is a normal 401 with no socket and no upstream. Costs one local
// HMAC; no DB, no Facepunch, no lichess call on the request path.
func (h *handler) tvSocket(w http.ResponseWriter, r *http.Request) {
	if _, ok := h.requireSteam(w, r); !ok {
		return
	}

	// The allowlist is the boundary: an arbitrary string off the wire must never
	// reach a lichess URL. 404 rather than 400 — an unknown channel is an unknown
	// resource, and it keeps the list of what we offer from being probeable. Checked
	// before the upgrade so a junk channel opens nothing.
	c, ok := lichess.ValidChannel(r.PathValue("channel"))
	if !ok {
		writeError(w, http.StatusNotFound, "unknown tv channel")
		return
	}

	conn, err := tvUpgrader.Upgrade(w, r, nil)
	if err != nil {
		// Upgrade has already written an error response; nothing more to do.
		h.log.Warn("lichess tv: websocket upgrade failed",
			zap.String("channel", string(c)), zap.Error(err))
		return
	}
	defer conn.Close()

	// The leak-proof core: watch increments the connection count, and this defer
	// decrements it on EVERY exit path — a clean close, a write error, a rude TCP
	// drop. The sweeper drops the upstream lingerTTL after the count reaches zero.
	ch := h.tv.watch(c)
	defer h.tv.leave(c, ch)

	ctx, cancel := context.WithCancel(r.Context())
	defer cancel()

	// gorilla requires a reader to be draining the connection for control frames
	// (pong replies) to be processed and for a closed socket to surface. We send no
	// application messages upstream, so this reader's only jobs are to answer pings
	// via the pong handler and to cancel the writer when the socket dies.
	go func() {
		defer cancel()
		conn.SetReadLimit(512)
		_ = conn.SetReadDeadline(time.Now().Add(tvPongWait))
		conn.SetPongHandler(func(string) error {
			return conn.SetReadDeadline(time.Now().Add(tvPongWait))
		})
		for {
			if _, _, err := conn.ReadMessage(); err != nil {
				return
			}
		}
	}()

	ping := time.NewTicker(tvPingEvery)
	defer ping.Stop()

	// Snapshot-then-select, looped: grab the current state AND the wake channel
	// under one lock, push the snapshot, then wait for the next change. Grabbing
	// `changed` before writing is what makes it missed-wakeup-free — a change
	// between the write and the select still closes the channel we are about to
	// select on. Coalescing is automatic: a slow writer re-snapshots the latest and
	// silently drops the intermediates, which is exactly what a full-snapshot,
	// latest-wins wire wants.
writeLoop:
	for {
		st, changed := ch.snapshot()
		st.stamp(time.Now())
		_ = conn.SetWriteDeadline(time.Now().Add(tvWriteWait))
		if err := conn.WriteJSON(st); err != nil {
			return
		}

		for {
			select {
			case <-changed:
				continue writeLoop
			case <-ping.C:
				_ = conn.SetWriteDeadline(time.Now().Add(tvWriteWait))
				if err := conn.WriteMessage(websocket.PingMessage, nil); err != nil {
					return
				}
			case <-ctx.Done():
				return
			}
		}
	}
}

// TvChannelsResponse lists what a client may ask for. Served so the client's own
// list can be checked against ours rather than assumed to match.
type TvChannelsResponse struct {
	Default  string      `json:"default"`
	Channels []TvChannel `json:"channels"`
}

type TvChannel struct {
	Key   string `json:"key"`
	Label string `json:"label"`
}

func (h *handler) tvChannels(w http.ResponseWriter, r *http.Request) {
	if _, ok := h.requireSteam(w, r); !ok {
		return
	}
	out := TvChannelsResponse{Default: string(lichess.ChannelDefault)}
	for _, c := range lichess.ChannelOrder {
		out.Channels = append(out.Channels, TvChannel{Key: string(c), Label: lichess.ChannelLabel(c)})
	}
	writeJSON(w, http.StatusOK, out)
}
