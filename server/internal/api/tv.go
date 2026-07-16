package api

import (
	"context"
	"net/http"
	"strconv"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"go.uber.org/zap"
)

// lichess TV, fanned out.
//
// # The invariant
//
// ONE upstream stream per CHANNEL, no matter how many people are watching. 100
// players on blitz = 1 open feed to lichess. That is the entire reason TV is
// proxied here rather than hit directly from each client, and it is what makes
// per-client channel choice affordable: the cost to lichess is bounded by the
// channel count (6), not the player count.
//
// The stream opens on the first watcher and is dropped after tvIdleTTL once the
// last one stops polling. Ref-counted by POLLERS, not by lobbies — a lobby is not
// a thing this package knows about, and a player who walks away from the wall
// stops polling whether or not their lobby still exists.
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
// costs one local HMAC.

const (
	// How long a channel's upstream stays open after the last watcher stops
	// polling. Comfortably longer than one poll cycle, so a watcher who is merely
	// between polls — or reloading, or walking past the wall — does not cause a
	// close+reopen against lichess. Short enough that an empty lobby stops costing
	// them anything within a minute.
	tvIdleTTL = 45 * time.Second

	// How often the sweeper looks for channels past their TTL.
	tvSweepEvery = 15 * time.Second

	// A TV stream that drops while people are still watching is retried this
	// often. Mirrors streamRetryDelay for the game relay.
	tvRetryDelay = 2 * time.Second

	// How long to wait for "how did the last game end" before publishing the new
	// featured game without it. Short: this blocks the channel's stream reader, and a
	// fanfare missing its reason is a much smaller problem than a wall that stops
	// updating because lichess is slow to answer a side question.
	tvResultTimeout = 3 * time.Second
)

// TvState is the client-facing snapshot of a channel's featured game.
//
// The field list is exactly what the spectator wall wants — Fen, LastMoveUci,
// names, clocks, whose turn — because it exists to feed it and nothing else.
//
// Clocks are SECONDS, straight from lichess's fen frame. Note this differs from
// PlayState, which is milliseconds: two lichess endpoints, two units. Seconds is
// what TimeControl.Format takes, so the client converts nothing.
type TvState struct {
	Version uint64 `json:"version"`
	Channel string `json:"channel"`
	// Label is the human channel name ("Blitz"), so the client needn't keep its own
	// copy in sync with ours.
	Label string `json:"label"`

	// Error is set when we cannot currently serve this channel (lichess is backing
	// off, the stream dropped and hasn't recovered). The client shows it and keeps
	// polling — an error here is never fatal and never stops the wall from
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

	// ── What makes the wall's clock legal (M11) ──
	//
	// The house rule is that a live clock must never read HIGHER than the time
	// actually left. The wall broke it, and the reasoning that said otherwise was
	// written in three places: "counting down from a known-good value can only read
	// low" fails at its first step, because the value is already STALE when it
	// arrives. lichess stamps the clock at the move instant T0; by the time the
	// client has it, the player has burned the whole lichess -> gamchess -> client
	// chain, and the client was zeroing its age on arrival. It read HIGH by that
	// chain until the next move.
	//
	// These two let the client subtract it. Both are computed AT SEND, not stored.

	// ClockAgeMs is how long ago WE received the clock values in this state from
	// lichess — the gamchess-side staleness.
	//
	// An absolute timestamp would be useless here and it is worth saying why, since
	// "stamp receipt time and let the client subtract the elapsed" was the obvious
	// design and does not work: we do not share a wall clock with the client, and a
	// clock-skewed client would correct by minutes in either direction — including
	// UP, which is the one direction the house rule forbids. An age is a duration,
	// and durations survive skew.
	//
	// Usually ~0, and that is not a reason to drop it: the long poll wakes on the
	// frame, so the common path really is fresh. It is the path where a client's
	// poll arrives late — reconnect, backoff, a slow frame — that this covers, and
	// that is exactly when the clock is most wrong.
	ClockAgeMs int64 `json:"clock_age_ms,omitempty"`

	// HoldMs is how long we sat on this request before answering.
	//
	// Load-bearing, not diagnostics. The client measures its own poll round trip to
	// estimate the network leg, and a long poll's round trip is mostly US WAITING —
	// up to pollHold (5s). Without this the client would read a 5s hold as 5s of
	// network latency and subtract it from the clock, which is a far bigger lie than
	// the one being fixed, in the safe direction rather than the unsafe one but still
	// nonsense. Network time = round trip - HoldMs.
	HoldMs int64 `json:"hold_ms,omitempty"`

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
	// ClockAgeMs is derived from it at send time.
	clockAt time.Time
}

// ageAt fills in the two send-time fields on a COPY of the state. Called on the
// snapshot the handler is about to write, never on the shared state — these are
// per-response values, not channel state, and storing them would mean every waiter
// on one channel got whichever request's timings happened to be written last.
func (s *TvState) ageAt(now time.Time, reqStart time.Time) {
	if !s.clockAt.IsZero() {
		s.ClockAgeMs = now.Sub(s.clockAt).Milliseconds()
	}
	s.HoldMs = now.Sub(reqStart).Milliseconds()
}

// tvChannel is one channel's fanned-out upstream.
type tvChannel struct {
	mu      sync.Mutex
	state   TvState
	version uint64
	changed chan struct{}

	// lastPoll is when someone last asked for this channel. The ref count is
	// "recently polled", not a counter: a counter needs a decrement on every exit
	// path including the ones a dropped HTTP connection doesn't give us, and
	// getting that wrong leaks an upstream stream forever. A timestamp cannot leak.
	lastPoll time.Time

	cancel context.CancelFunc
}

func (c *tvChannel) snapshot() (TvState, <-chan struct{}) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state, c.changed
}

// update mutates the state and wakes every waiter. Same shape as play.update —
// close-and-replace is what makes the long poll race-free.
func (c *tvChannel) update(fn func(*TvState)) {
	c.mu.Lock()
	fn(&c.state)
	c.version++
	c.state.Version = c.version
	close(c.changed)
	c.changed = make(chan struct{})
	c.mu.Unlock()
}

// currentGame returns the featured game id and both names as they stand — read
// under the lock, before a featured swap overwrites them, so we know which game
// just ended and who was playing it.
func (c *tvChannel) currentGame() (id, white, black string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state.GameID, c.state.WhiteName, c.state.BlackName
}

func (c *tvChannel) touch() {
	c.mu.Lock()
	c.lastPoll = time.Now()
	c.mu.Unlock()
}

func (c *tvChannel) idleSince() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.lastPoll
}

// tv owns every live channel.
type tv struct {
	log *zap.Logger

	mu       sync.Mutex
	channels map[lichess.Channel]*tvChannel

	// sweeping guards the lazily-started sweeper goroutine.
	sweeping bool

	// idleTTL, streamTv and gameResult are fields, not constants, for the same reason
	// api.validateToken is a package var: they are the seams that let the
	// ref-counting, the frame state machine and the end-of-game fanfare be tested
	// without lichess and without a 45-second wait. Production uses the real values.
	idleTTL    time.Duration
	streamTv   func(context.Context, lichess.Channel, func(lichess.TvEvent)) error
	gameResult func(context.Context, string) (lichess.TvResult, error)
}

func newTv(log *zap.Logger) *tv {
	return &tv{
		log:        log,
		channels:   map[lichess.Channel]*tvChannel{},
		idleTTL:    tvIdleTTL,
		streamTv:   lichess.StreamTv,
		gameResult: lichess.GameResult,
	}
}

// watch returns the channel's fan-out, opening the upstream if this is the first
// watcher. Every call marks the channel as wanted.
func (t *tv) watch(c lichess.Channel) *tvChannel {
	t.mu.Lock()
	defer t.mu.Unlock()

	if ch, ok := t.channels[c]; ok {
		ch.touch()
		return ch
	}

	ch := &tvChannel{
		changed:  make(chan struct{}),
		lastPoll: time.Now(),
		state: TvState{
			Version: 1,
			Channel: string(c),
			Label:   lichess.ChannelLabel(c),
		},
	}
	ch.version = 1

	ctx, cancel := context.WithCancel(context.Background())
	ch.cancel = cancel
	t.channels[c] = ch

	t.log.Info("lichess tv: opening upstream", zap.String("channel", string(c)))
	go t.run(ctx, c, ch)
	t.startSweeper()
	return ch
}

// run holds the upstream open, retrying while anyone is still watching. Exits
// only when the context is cancelled — which is the sweeper's job, not its own.
func (t *tv) run(ctx context.Context, c lichess.Channel, ch *tvChannel) {
	for {
		err := t.streamTv(ctx, c, func(ev lichess.TvEvent) {
			switch {
			case ev.Featured != nil:
				f := ev.Featured
				w, b := f.White(), f.Black()

				// A NEW featured game means the old one just ended — that swap is the only
				// notice the feed ever gives. Go and ask how it went before publishing the
				// new game, so the client gets the ending and its replacement in one
				// atomic state and can never show the new game first.
				//
				// Synchronous on purpose. It blocks this stream's reader for one request,
				// once per game, and the frames behind it just wait in the socket — cheap,
				// and far simpler than a concurrent write-back racing the next fen. Never
				// fatal: a failure leaves LastStatus empty and the fanfare loses its detail.
				prev, prevW, prevB := ch.currentGame()
				var result lichess.TvResult
				if prev != "" && prev != f.ID {
					rctx, cancel := context.WithTimeout(ctx, tvResultTimeout)
					var err error
					result, err = t.gameResult(rctx, prev)
					cancel()
					if err != nil {
						t.log.Warn("lichess tv: couldn't read how the last game ended",
							zap.String("channel", string(c)), zap.String("game", prev), zap.Error(err))
					}
				}

				ch.update(func(s *TvState) {
					s.Error = ""
					if prev != "" && prev != f.ID {
						s.LastGameID = prev
						s.LastStatus = result.Status
						s.LastWinner = result.Winner
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
			case ev.Fen != nil:
				f := ev.Fen
				ch.update(func(s *TvState) {
					s.Error = ""
					s.Fen = f.Fen
					s.LastMoveUci = f.LM
					s.WhiteClock, s.BlackClock = f.WC, f.BC
					// The stamp that makes the clock legal: this is the closest we will
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

		// The stream dropped on its own. Surface it, then retry — people are still
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

// sweep drops channels nobody has polled for tvIdleTTL. This is the ONLY thing
// that closes an upstream — which is what makes "we never leak a stream" a
// property of one function rather than of every exit path in the handler.
func (t *tv) sweep() {
	t.mu.Lock()
	defer t.mu.Unlock()
	for c, ch := range t.channels {
		if time.Since(ch.idleSince()) < t.idleTTL {
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

// tvState is GET /api/v1/tv/{channel}?since=N — a long poll, the same shape as
// the game relay's.
//
// Session-gated (see the package comment). Costs one local HMAC; no DB, no
// Facepunch, no lichess call on the request path.
func (h *handler) tvState(w http.ResponseWriter, r *http.Request) {
	if _, ok := h.requireSteam(w, r); !ok {
		return
	}

	// The allowlist is the boundary: an arbitrary string off the wire must never
	// reach a lichess URL. 404 rather than 400 — an unknown channel is an unknown
	// resource, and it keeps the list of what we offer from being probeable.
	c, ok := lichess.ValidChannel(r.PathValue("channel"))
	if !ok {
		writeError(w, http.StatusNotFound, "unknown tv channel")
		return
	}

	ch := h.tv.watch(c)
	since, _ := strconv.ParseUint(r.URL.Query().Get("since"), 10, 64)

	// When the request landed — the baseline for HoldMs. Taken before the poll can
	// block, so it measures OUR wait and not the client's.
	reqStart := time.Now()

	state, changed := ch.snapshot()
	if state.Version > since {
		state.ageAt(time.Now(), reqStart)
		writeJSON(w, http.StatusOK, state)
		return
	}

	select {
	case <-changed:
		state, _ = ch.snapshot()
	case <-time.After(pollHold):
		// Nothing moved. Answer with what we have rather than holding longer — the
		// hold must stay under the client's 8s ceiling or every poll reads as a
		// timeout and trips its breaker.
	case <-r.Context().Done():
		return
	}
	state.ageAt(time.Now(), reqStart)
	writeJSON(w, http.StatusOK, state)
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
