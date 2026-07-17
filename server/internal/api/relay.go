package api

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gamah/gambit/server/internal/store"
	"github.com/jackc/pgx/v5/pgxpool"
	"go.uber.org/zap"
)

// The lichess relay: gamchess plays a real lichess game on behalf of the two
// players sitting at a Gambit table.
//
// # Why this exists at all
//
// The s&box client cannot read a long-lived HTTP stream (Sandbox.Http buffers
// the whole body; HttpCompletionOption is off the API whitelist), and playing a
// lichess game REQUIRES holding a game stream open — lichess has no polling
// substitute and says so with a 429. So the stream reader has to live server
// side, which is why gamchess holds the token. That is the "position 2" custody
// decision, and this file is what it buys.
//
// # Mutual consent, and why it is not optional
//
// A challenge is only ever issued when BOTH seats have independently POSTed an
// intent for the same client_game_id, each authenticated with their own
// Facepunch token, each naming the same two seats and the same clock.
//
// This is the whole authorisation story, so it's worth being explicit about the
// attack it stops. gamchess holds a board:play token for every linked player. If
// one player could say "challenge SteamID X" and have gamchess auto-accept on
// X's behalf, then any linked player could drag any other linked player into a
// lichess game at will, from anywhere, without them being in the lobby. Pairing
// two independently-authenticated intents means a game can only start if both
// people actually asked for it. client_game_id is NOT a secret and carries no
// authority on its own — it is [Sync]ed to the whole lobby — it is only the
// rendezvous key. The authority is the two FP tokens.
//
// # No event stream
//
// gamchess holds both seats' tokens, so it issues the challenge with White's
// token and immediately accepts it by id with Black's. It never has to watch
// /api/stream/event for the challenge to show up, which is why nothing here is
// subject to lichess's one-event-stream-per-token rule. Exactly one stream runs
// per LIVE GAME, not per user.

// Relay timings.
const (
	// How long an unpaired intent waits for the other seat before it's swept.
	// Generous: it only has to outlive the gap between two clients noticing the
	// same table state, and a real-time lichess challenge expires in 20s anyway.
	playIntentTTL = 2 * time.Minute

	// How long a finished game's final state is kept readable. Long enough for
	// both clients to see the result and archive it; short enough to bound memory.
	playDoneTTL = 10 * time.Minute

	// How long a long-poll hangs before answering with the current state. MUST
	// stay under the client's 8s HTTP ceiling (GamchessApi.Timeout) or every poll
	// looks like a timeout and trips the circuit breaker.
	pollHold = 5 * time.Second

	// A game stream that drops while the game is still live is retried this often.
	streamRetryDelay = 2 * time.Second

	// How long a direct challenge waits for its opponent to answer.
	//
	// This is a HAZARD BOUND, not a UX timeout. A keep-alive challenge does not
	// expire on its own — lila sweeps an unseen real-time challenge after 20s,
	// but our stream pings it every 15s precisely to stop that, so it lives for
	// as long as we hold on. Without a ceiling here, a client that crashes or
	// drops off without calling DELETE would leave an invitation open
	// indefinitely, acceptable by a stranger long after the player walked away.
	//
	// relay.Cancel covers the player who stands up properly; this covers the one
	// who doesn't. Two minutes is far longer than a person takes to answer a
	// challenge they are awake for.
	challengeAnswerTTL = 2 * time.Minute
)

// Play status values, as seen by the client.
const (
	playWaiting     = "waiting"     // one seat has asked; waiting for the other
	playChallenging = "challenging" // both in; talking to lichess
	playLive        = "live"        // a real game is running
	playOver        = "over"        // it finished
	playFailed      = "failed"      // lichess said no; Error explains
)

// PlayState is the client-facing snapshot of a relayed game.
//
// Moves is lichess's own full UCI list from the start position — never a delta.
// The client replays it into its own ChessGame, which is why a dropped or
// duplicated poll costs nothing and why there is no reconciliation logic here.
//
// SteamIDs are STRINGS, for the same reason as everywhere else in this API: a
// SteamID64 is past JavaScript's 2^53.
type PlayState struct {
	Status  string `json:"status"`
	Error   string `json:"error,omitempty"`
	Version uint64 `json:"version"`

	GameID string `json:"game_id,omitempty"`
	URL    string `json:"url,omitempty"`

	WhiteSteamID string `json:"white_steam_id"`
	BlackSteamID string `json:"black_steam_id"`
	WhiteName    string `json:"white_name,omitempty"`
	BlackName    string `json:"black_name,omitempty"`

	Moves string `json:"moves"`

	// Milliseconds, straight from lichess. lichess is the only authority on its own
	// clocks — but it only SENDS a clock when a move happens, so the client runs the
	// side-to-move's value down locally between moves and snaps back to these on the
	// next state. That is the same shape TV uses, and it needs the two staleness
	// fields below to do it without reading HIGH.
	WhiteTimeMs int64 `json:"white_time_ms"`
	BlackTimeMs int64 `json:"black_time_ms"`
	WhiteIncMs  int64 `json:"white_inc_ms"`
	BlackIncMs  int64 `json:"black_inc_ms"`

	// ClockAgeMs / HoldMs let the client subtract this frame's staleness before it
	// runs a clock down — the identical machinery to TvState's, for the identical
	// reason (a live clock must never read HIGHER than the time actually left). Both
	// are computed AT SEND on a copy, never stored: ClockAgeMs from clockAt (below),
	// HoldMs from when the request landed. Omitted (0) means "no correction", so a
	// client talking to an older gamchess just gets the old frozen-between-moves
	// behaviour rather than a broken clock.
	ClockAgeMs int64 `json:"clock_age_ms,omitempty"`
	HoldMs     int64 `json:"hold_ms,omitempty"`

	// Seek marks a game whose opponent is a lichess stranger rather than the
	// player sitting opposite you — a lobby seek OR a direct challenge to a
	// username. The stranger has no SteamID, so one of the seat ids is empty and
	// the client must read YourColor rather than matching SteamIDs.
	//
	// It is deliberately the STRANGER-OPPOSITE flag rather than literally "this
	// was a seek": every client-side use of it asks "is there a Gambit player in
	// the other seat?", and a challenge answers that the same way a seek does.
	// Opponent is what tells the two apart.
	Seek bool `json:"seek"`
	// Opponent is the lichess username we challenged directly, when that is how
	// this game started. Empty for a seek (nobody was named) and for the paired
	// flow (the other seat is a Gambit player, in the *Name fields).
	Opponent string `json:"opponent,omitempty"`
	// YourColor is stamped PER CALLER at write time ("white"/"black"/""), because
	// it is the only per-caller field in an otherwise shared snapshot.
	YourColor string `json:"your_color,omitempty"`

	LichessStatus string `json:"lichess_status,omitempty"`
	Winner        string `json:"winner,omitempty"`
	Finished      bool   `json:"finished"`
	WhiteDraw     bool   `json:"white_draw"`
	BlackDraw     bool   `json:"black_draw"`
	// Standing takeback proposals, same shape as the draw pair above. These are
	// the only honest answer to "did my takeback land?" — the POST that offered
	// it always returns 200 whether lichess took it or dropped it.
	WhiteTakeback bool `json:"white_takeback"`
	BlackTakeback bool `json:"black_takeback"`

	// clockAt is when WhiteTimeMs/BlackTimeMs last CHANGED — i.e. when the move that
	// produced them reached us from lichess. Unexported so it never hits the wire:
	// it is in OUR clock's frame of reference and means nothing in a client's.
	// ClockAgeMs is derived from it at send time. Only set on a real clock change so
	// a draw offer or takeback (a gameState with no move, hence the same clocks)
	// doesn't wrongly refresh it and understate the age.
	clockAt time.Time
}

// ageAt fills the two send-time staleness fields on a COPY of the state — never on
// the shared state, since these are per-response values (one request's timings must
// not leak onto another waiter's answer). Mirrors TvState.ageAt exactly.
func (s *PlayState) ageAt(now, reqStart time.Time) {
	if !s.clockAt.IsZero() {
		s.ClockAgeMs = now.Sub(s.clockAt).Milliseconds()
	}
	s.HoldMs = now.Sub(reqStart).Milliseconds()
}

// PlayRequest is an intent to put a game on lichess. Three shapes:
//
//	paired    — two seated players challenge each other. Both seats must ask;
//	            White and Black are both known up front.
//	Seek      — the lobby flow: ONE player seeks a RANDOM opponent. Only
//	            SoloSteamID is known, and the opponent is a lichess account that
//	            has nothing to do with this lobby.
//	Challenge — ONE player challenges a NAMED lichess user (Opponent). Same
//	            shape as a seek in every way that matters here: one Gambit
//	            player, one stranger, one intent.
type PlayRequest struct {
	ClientGameID string
	LimitSeconds int
	IncrementSec int
	Unlimited    bool

	// Paired flow only.
	WhiteSteamID int64
	BlackSteamID int64

	// Solo flows (Seek and Challenge) — see solo().
	Seek bool
	// Challenge names a lichess user directly rather than asking the lobby for
	// anyone. It reaches BLITZ, which a seek cannot, and it spends the per-user
	// challenge budget rather than the 5/min-per-IP lobby budget the whole
	// playerbase shares — so it is the cheaper of the two for us.
	Challenge bool
	// Opponent is the lichess username to challenge. Challenge flow only.
	Opponent string
	// SoloSteamID is the ONE Gambit player in a solo flow — the seeker, or the
	// challenger. There is no second seat: the opponent is a lichess account.
	SoloSteamID int64
	Rated       bool
	// RatingRange is "1500-1800" (absolute, never a delta). Empty does NOT mean
	// "any" — lila reads an omitted range as "no preference" and substitutes a
	// Gaussian band around the seeker's real rating. Gambit always sends empty;
	// see CLAUDE.md's ratingRange trap. The field stays on the wire because the
	// endpoint is a faithful mirror of lichess's, not because a caller sets it.
	RatingRange string
	Color       string // "white" | "black" | "random"/"" — lichess default is 50/50
}

// matches reports whether two intents describe the same game. Both seats must
// agree on everything, or we'd be picking one client's terms over the other's.
// Only meaningful for the paired flow — a seek has nobody to agree with.
func (r PlayRequest) matches(o PlayRequest) bool {
	return r.Seek == o.Seek &&
		r.Challenge == o.Challenge &&
		r.Opponent == o.Opponent &&
		r.WhiteSteamID == o.WhiteSteamID &&
		r.BlackSteamID == o.BlackSteamID &&
		r.SoloSteamID == o.SoloSteamID &&
		r.LimitSeconds == o.LimitSeconds &&
		r.IncrementSec == o.IncrementSec &&
		r.Unlimited == o.Unlimited
}

// solo reports a flow with exactly ONE Gambit player and one lichess stranger:
// a lobby seek, or a direct challenge to a username. The paired flow is the only
// one with two.
func (r PlayRequest) solo() bool { return r.Seek || r.Challenge }

// intentsNeeded is how many independent, separately-authenticated players must
// ask before a game is started.
//
// TWO for the paired flow, and that is the whole authorisation story (see the
// file header). ONE for a solo flow, which needs no consent from anyone else:
// you are spending your own token to play a stranger who opts in on lichess's
// side, by their own choice, in their own client. Nobody is dragged anywhere.
//
// A direct CHALLENGE is one intent for exactly that reason, and the distinction
// is worth being precise about, because "challenge" is also the paired flow's
// mechanism. What makes the paired flow need two intents is that gamchess holds
// BOTH tokens and would ACCEPT for the other player — so one intent could drag a
// linked player into a game. Here gamchess holds only the challenger's token and
// accepts nothing: the named opponent is a lichess user who must click Accept in
// their own client, which is consent given directly to lichess. If they never
// do, no game happens.
func (r PlayRequest) intentsNeeded() int {
	if r.solo() {
		return 1
	}
	return 2
}

// play is one table's relayed game, keyed by client_game_id.
type play struct {
	req     PlayRequest
	created time.Time

	// Which seats have actually asked. A game starts only at len(intents) == 2.
	intents map[int64]bool

	mu      sync.Mutex
	state   PlayState
	version uint64
	// changed is closed (and replaced) on every state change — the long-poll
	// wakeup. A closed channel wakes every waiter at once, which is what we want:
	// both seats are watching the same game.
	changed  chan struct{}
	started  bool
	finished time.Time
	cancel   context.CancelFunc

	// soloColor is which side lichess gave our one player, learned from gameFull.
	// Empty until then — a seek gets 50/50 and we don't get to choose.
	// soloLichessID is what we match gameFull's players against. Both mu-guarded.
	soloColor     string
	soloLichessID string

	// challengeID is the lichess challenge we opened and have not yet seen
	// answered. Held so Cancel can POST /cancel against it.
	//
	// Not merely hanging up the keep-alive stream: closing that stream only stops
	// lila's 15s ping, after which the challenge is swept to Status.Offline —
	// which survives THREE HOURS and stays acceptable. See lichess.ChallengeKeepAlive.
	challengeID string
}

func newPlay(req PlayRequest) *play {
	p := &play{
		req:     req,
		created: time.Now(),
		intents: map[int64]bool{},
		changed: make(chan struct{}),
		state:   PlayState{Status: playWaiting, Seek: req.solo(), Opponent: req.Opponent},
	}
	// A solo flow has one known player and one stranger; which colour they get is
	// lichess's to confirm, so neither seat is filled in until gameFull lands.
	if !req.solo() {
		p.state.WhiteSteamID = strconv.FormatInt(req.WhiteSteamID, 10)
		p.state.BlackSteamID = strconv.FormatInt(req.BlackSteamID, 10)
	}
	p.state.Version = 1
	p.version = 1
	return p
}

// snapshot returns the current state, plus a channel that closes when it next
// changes. Taking both under one lock is what makes the long-poll race-free:
// there is no window between reading the version and starting to wait in which
// an update could be missed.
func (p *play) snapshot() (PlayState, <-chan struct{}) {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.state, p.changed
}

// update mutates the state and wakes every waiter.
func (p *play) update(fn func(*PlayState)) {
	p.mu.Lock()
	fn(&p.state)
	p.version++
	p.state.Version = p.version
	if p.state.Finished || p.state.Status == playFailed {
		p.finished = time.Now()
	}
	close(p.changed)
	p.changed = make(chan struct{})
	p.mu.Unlock()
}

func (p *play) fail(format string, args ...any) {
	msg := fmt.Sprintf(format, args...)
	p.update(func(s *PlayState) {
		s.Status = playFailed
		s.Error = msg
	})
}

// seatOf returns the colour a SteamID plays, or ("", false) if they aren't in
// this game. Every write action goes through this — a caller may only act for
// the seat they hold.
//
// For a SOLO flow there is one Gambit player and one stranger, and we don't know
// which colour lichess gave us until gameFull arrives — so the colour may be ""
// while ok is true. Callers must not read the colour as "they're white".
//
// A challenge asks for a colour and lichess honours it, but this still waits for
// gameFull to CONFIRM rather than trusting what we asked for: lichess is the
// authority on its own game, and a board that assumed would be lying if they
// ever disagreed.
func (p *play) seatOf(steamID int64) (string, bool) {
	if steamID == 0 {
		return "", false // 0 is "empty seat" everywhere in this codebase
	}
	if p.req.solo() {
		if steamID != p.req.SoloSteamID {
			return "", false
		}
		p.mu.Lock()
		defer p.mu.Unlock()
		return p.soloColor, true
	}
	switch steamID {
	case p.req.WhiteSteamID:
		return "white", true
	case p.req.BlackSteamID:
		return "black", true
	}
	return "", false
}

// done reports whether this play can be swept.
func (p *play) done(now time.Time) bool {
	p.mu.Lock()
	defer p.mu.Unlock()

	// Never paired up — the other seat never came.
	if !p.started && now.Sub(p.created) > playIntentTTL {
		return true
	}
	if !p.finished.IsZero() && now.Sub(p.finished) > playDoneTTL {
		return true
	}
	return false
}

// relay owns every live relayed game. One per process.
type relay struct {
	log    *zap.Logger
	db     *pgxpool.Pool
	cipher *lichess.Cipher

	mu    sync.Mutex
	plays map[string]*play
	// pending is steamID → the client_game_id of that player's outstanding SOLO
	// request: a lobby seek, or a direct challenge waiting to be answered. One
	// per user.
	//
	// The two flows land in the same slot for different reasons, and only one of
	// them is technical:
	//
	//   - A SEEK must be alone because it needs an event stream, and lichess
	//     allows one per token: a second would silently close the first, leaving
	//     that seek unable to learn its own game had started.
	//   - A CHALLENGE has no such constraint (its verdict arrives on the
	//     challenge's own stream, not the event stream). It shares the slot as a
	//     POLICY: a player sits at ONE board, so a second outstanding request is
	//     one they cannot be waiting at — and both must be cancelled when they
	//     stand up. One slot is one thing to cancel.
	pending map[int64]string
}

func newRelay(log *zap.Logger, db *pgxpool.Pool, c *lichess.Cipher) *relay {
	return &relay{
		log:     log,
		db:      db,
		cipher:  c,
		plays:   map[string]*play{},
		pending: map[int64]string{},
	}
}

// claimPending reserves this player's single solo-request slot, or explains why
// not. Re-asking for the SAME one is fine — that's just the client re-posting.
func (r *relay) claimPending(steamID int64, clientGameID string) error {
	r.mu.Lock()
	defer r.mu.Unlock()

	if existing, ok := r.pending[steamID]; ok && existing != clientGameID {
		// Is the old one actually still going? A finished/failed request shouldn't
		// block a new one.
		if p, live := r.plays[existing]; live {
			if st, _ := p.snapshot(); st.Status != playOver && st.Status != playFailed {
				return errors.New("you already have a lichess game waiting — cancel that one first")
			}
		}
	}
	r.pending[steamID] = clientGameID
	return nil
}

// releasePending frees a player's solo-request slot.
func (r *relay) releasePending(steamID int64, clientGameID string) {
	r.mu.Lock()
	if r.pending[steamID] == clientGameID {
		delete(r.pending, steamID)
	}
	r.mu.Unlock()
}

// Cancel stops a play the caller owns — a seeker giving up on waiting, a
// challenger walking away before their opponent answers, or a player standing
// up. Only a participant may do it.
//
// Cancelling matters more than it looks, and for a DIFFERENT reason per flow:
//
//   - A SEEK is the held connection, so dropping our context is itself the
//     withdrawal. Without it a player who walked away stays pairable and gets
//     dropped into a game nobody is sitting at.
//   - A CHALLENGE is NOT withdrawn by hanging up, and this is the trap. Closing
//     the keep-alive stream only stops lila's 15s ping; the challenge is then
//     swept to Status.Offline, where it lives for THREE HOURS and is still
//     acceptable (a ping revives it via setSeenAgain). So a challenge needs an
//     explicit POST /cancel, or standing up leaves a live invitation a stranger
//     can accept long after the player has gone — starting a real game on their
//     account, at a board nobody is sitting at, which then loses on time. That
//     is the exact harm the two-intent rule exists to prevent, self-inflicted.
//
// The POST is best-effort and never fatal: the caller is already leaving, and
// there is nothing useful to tell them if lichess is unreachable. It runs on a
// fresh context because p.cancel has just killed this play's own.
func (r *relay) Cancel(p *play, steamID int64) error {
	if _, ok := p.seatOf(steamID); !ok {
		return errors.New("you are not in this game")
	}

	state, _ := p.snapshot()
	// Once a real game exists, "cancel" is not a thing lichess offers — resigning
	// is. Say so rather than pretending.
	if state.Status == playLive {
		return errors.New("the game has already started — resign it instead")
	}

	if p.cancel != nil {
		p.cancel()
	}
	if p.req.solo() {
		r.releasePending(p.req.SoloSteamID, p.req.ClientGameID)
	}

	p.mu.Lock()
	challengeID := p.challengeID
	p.mu.Unlock()
	if challengeID != "" {
		go r.cancelChallenge(p.req.SoloSteamID, challengeID)
	}

	p.update(func(s *PlayState) {
		s.Status = playFailed
		s.Error = "cancelled"
	})
	return nil
}

// cancelChallenge withdraws an outstanding lichess challenge. Best-effort: it is
// tidy-up on a path whose caller has already walked away.
func (r *relay) cancelChallenge(steamID int64, challengeID string) {
	ctx, done := context.WithTimeout(context.Background(), 10*time.Second)
	defer done()

	token, _, err := r.credentials(ctx, steamID)
	if err != nil {
		r.log.Warn("could not load credentials to cancel a challenge",
			zap.String("challenge_id", challengeID), zap.Error(err))
		return
	}
	if err := lichess.CancelChallenge(ctx, token, challengeID); err != nil {
		r.log.Warn("could not cancel an abandoned lichess challenge",
			zap.String("challenge_id", challengeID), zap.Error(err))
	}
}

// Enabled reports whether lichess is CONFIGURED. No key ⇒ no tokens can be
// decrypted ⇒ no lichess. Feature-off, never fatal, never plaintext.
//
// Deliberately says nothing about the database: DATABASE_URL is the one fatal
// config key, so a live server always has a pool, and folding "db != nil" in
// here would only ever change behaviour under test — where it would answer 501
// to requests that should have been refused as unauthorised first.
func (r *relay) Enabled() bool { return r != nil && r.cipher != nil }

var errNotLinked = errors.New("that player has not linked a lichess account")

// credentials decrypts a player's lichess token. This is the ONLY place a
// plaintext token exists in the process, and it never leaves the relay.
func (r *relay) credentials(ctx context.Context, steamID int64) (token, username string, err error) {
	link, err := store.LichessLinkBySteamID(ctx, r.db, steamID)
	if errors.Is(err, store.ErrNotFound) {
		return "", "", errNotLinked
	}
	if err != nil {
		return "", "", err
	}
	token, err = r.cipher.Open(link.TokenEnc, link.TokenNonce)
	if err != nil {
		// The row exists but won't decrypt: the key changed under us (rotation is
		// an open spike with no path today). Say so plainly — it means re-link.
		return "", "", fmt.Errorf("stored token will not decrypt — re-link required: %w", err)
	}
	return token, link.Username, nil
}

// Join records one player's intent and starts the game once enough players have
// asked — both seats for the paired flow, just the seeker for a lobby seek.
// Returns the play so the caller can answer with its current state.
func (r *relay) Join(ctx context.Context, steamID int64, req PlayRequest) (*play, error) {
	r.sweep()

	// One outstanding solo request per user — see relay.pending for why a seek
	// must be alone (lichess's one-event-stream-per-token rule) and why a
	// challenge shares the slot anyway (a player sits at one board).
	if req.solo() {
		if err := r.claimPending(steamID, req.ClientGameID); err != nil {
			return nil, err
		}
	}

	r.mu.Lock()
	p, ok := r.plays[req.ClientGameID]
	if !ok {
		p = newPlay(req)
		r.plays[req.ClientGameID] = p
	}
	r.mu.Unlock()

	// Both seats must describe the same game. A mismatch means two different
	// tables collided on one id, or a client is lying about the terms — either
	// way, refuse rather than let one seat's clock win.
	if !p.req.matches(req) {
		return nil, errors.New("the other seat asked for a different game")
	}
	if _, ok := p.seatOf(steamID); !ok {
		return nil, errors.New("you are not seated in this game")
	}

	p.mu.Lock()
	p.intents[steamID] = true
	ready := len(p.intents) >= req.intentsNeeded() && !p.started
	if ready {
		p.started = true // claim before unlocking, so only one goroutine starts it
	}
	p.mu.Unlock()

	if ready {
		p.update(func(s *PlayState) { s.Status = playChallenging })
		// Detached from the request context on purpose: the game outlives the HTTP
		// call that started it. Cancelled by sweep, never by a client hanging up.
		gameCtx, cancel := context.WithCancel(context.Background())
		p.cancel = cancel
		go r.run(gameCtx, p)
	}
	return p, nil
}

// Lookup finds an existing play. No creation — polling must not conjure one.
func (r *relay) Lookup(clientGameID string) (*play, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	p, ok := r.plays[clientGameID]
	return p, ok
}

// run drives one game start to finish.
func (r *relay) run(ctx context.Context, p *play) {
	switch {
	case p.req.Seek:
		r.runSeek(ctx, p)
	case p.req.Challenge:
		r.runChallenge(ctx, p)
	default:
		r.runPaired(ctx, p)
	}
}

// runChallenge challenges a named lichess user and relays the game if they
// accept.
//
// Simpler than the seek in the one way that counts: a challenge's id IS its
// game's id, so there is nothing to learn from /api/stream/event and no race
// between two streams. The keep-alive stream both holds the challenge open and
// delivers the verdict.
//
// The colour is not random. The player is already sitting on one side of a
// physical board, and the whole point of the relay is that the lichess game
// mirrors that board — the same reasoning that makes the paired flow challenge
// with "white" rather than letting lichess pick.
func (r *relay) runChallenge(ctx context.Context, p *play) {
	token, _, err := r.credentials(ctx, p.req.SoloSteamID)
	if err != nil {
		p.fail("%s", err)
		return
	}

	// Our own lichess id, so gameFull can confirm which side we were given.
	link, err := store.LichessLinkBySteamID(ctx, r.db, p.req.SoloSteamID)
	if err != nil {
		p.fail("%s", err)
		return
	}

	p.mu.Lock()
	p.soloLichessID = link.LichessID
	p.mu.Unlock()

	// Blocks until the opponent answers, we hang up, or the answer TTL expires.
	// onOpen fires as soon as lichess mints the challenge — before anyone has
	// answered — which is what gives Cancel something to withdraw.
	waitCtx, stopWaiting := context.WithTimeout(ctx, challengeAnswerTTL)
	defer stopWaiting()

	done, err := lichess.ChallengeKeepAlive(waitCtx, token, p.req.Opponent, lichess.ChallengeParams{
		LimitSeconds:     p.req.LimitSeconds,
		IncrementSeconds: p.req.IncrementSec,
		Unlimited:        p.req.Unlimited,
		Rated:            p.req.Rated,
		Color:            p.req.Color,
	}, func(res lichess.ChallengeResult) {
		p.mu.Lock()
		p.challengeID = res.ID
		p.mu.Unlock()
		p.update(func(s *PlayState) {
			s.Status = playChallenging
			s.URL = res.URL
		})
	})

	if ctx.Err() != nil {
		return // cancelled: the player stood up, and Cancel has published why
	}
	// The answer TTL fired: the opponent never responded. Withdraw the invitation
	// we're still holding open — hanging up alone leaves it acceptable for hours
	// (see Cancel) — and say so. waitCtx, not ctx, so this is distinct from the
	// player standing up (handled above).
	if waitCtx.Err() != nil {
		p.mu.Lock()
		challengeID := p.challengeID
		p.mu.Unlock()
		if challengeID != "" {
			go r.cancelChallenge(p.req.SoloSteamID, challengeID)
		}
		p.fail("%s didn't answer the challenge.", p.req.Opponent)
		return
	}
	if err != nil {
		var apiErr *lichess.APIError
		if errors.As(err, &apiErr) && apiErr.RateLimited() {
			p.fail("lichess is rate-limiting challenges right now. Try again in a minute.")
			return
		}
		// lichess's own words are the useful ones here: "No such user: bob",
		// "You cannot challenge yourself", "bob does not accept challenges".
		p.fail("lichess wouldn't send the challenge: %s", err)
		return
	}

	switch done {
	case lichess.ChallengeAccepted:
	case lichess.ChallengeDeclined:
		p.fail("%s declined the challenge.", p.req.Opponent)
		return
	case lichess.ChallengeCanceled:
		p.fail("the challenge was cancelled.")
		return
	default:
		p.fail("lichess answered the challenge with %q, which we don't understand.", done)
		return
	}

	p.mu.Lock()
	gameID := p.challengeID
	p.mu.Unlock()
	if gameID == "" {
		// Accepted a challenge we never saw an id for. Nothing to stream.
		p.fail("lichess accepted a challenge we have no id for")
		return
	}

	// Answered, so there is nothing left to withdraw — and leaving the id set
	// would have a later Cancel POST /cancel against a LIVE GAME. lichess honours
	// that before either side has moved (its cancel doubles as an abort), so this
	// is not cosmetic: standing up would abort the game instead of resigning it.
	p.mu.Lock()
	p.challengeID = ""
	p.mu.Unlock()

	// The challenge id IS the game id once accepted.
	p.update(func(s *PlayState) {
		s.Status = playLive
		s.GameID = gameID
		s.URL = "https://lichess.org/" + gameID
	})

	r.streamGame(ctx, p, token, gameID)
}

// runSeek finds a random opponent on lichess and then relays the game.
//
// Unlike the paired flow, this one DOES need /api/stream/event: a seek's response
// stream carries no information at all — not even the game id — and lichess's own
// instruction is to learn about the game from a gameStart event. So the order
// here is theirs and matters: open the event stream FIRST, then the seek, or an
// instant pairing is missed.
//
// The event stream is one-per-token server-side (a second closes the first), so
// exactly one seek per user may run at a time — enforced by relay.seeks.
func (r *relay) runSeek(ctx context.Context, p *play) {
	token, _, err := r.credentials(ctx, p.req.SoloSteamID)
	if err != nil {
		p.fail("%s", err)
		return
	}

	// The seek's own lichess id, so we can tell which colour gameFull gave us.
	link, err := store.LichessLinkBySteamID(ctx, r.db, p.req.SoloSteamID)
	if err != nil {
		p.fail("%s", err)
		return
	}
	soloLichessID := link.LichessID

	gameCh := make(chan string, 1)
	eventCtx, stopEvents := context.WithCancel(ctx)
	defer stopEvents()

	go func() {
		err := lichess.StreamEvents(eventCtx, token, func(e lichess.Event) {
			if e.Type == "gameStart" && e.Game != nil && e.Game.GameID != "" {
				select {
				case gameCh <- e.Game.GameID:
				default: // already have one
				}
			}
		})
		if err != nil && eventCtx.Err() == nil {
			r.log.Warn("lichess event stream ended during a seek", zap.Error(err))
		}
	}()

	// Now the seek itself. This call BLOCKS for as long as the player is waiting:
	// the connection is the seek, and hanging up cancels it. lichess closes it
	// when we're matched.
	seekCtx, stopSeek := context.WithCancel(ctx)
	defer stopSeek()

	seekErr := make(chan error, 1)
	go func() {
		seekErr <- lichess.SeekRealtime(seekCtx, token, lichess.SeekParams{
			TimeMinutes:      float64(p.req.LimitSeconds) / 60,
			IncrementSeconds: p.req.IncrementSec,
			Rated:            p.req.Rated,
			RatingRange:      p.req.RatingRange,
			Color:            p.req.Color,
		})
	}()

	var gameID string
	select {
	case gameID = <-gameCh:
		// Matched. Drop the seek connection — lichess has probably closed it
		// already, but we must not leave a second seek pending.
		stopSeek()

	case err := <-seekErr:
		// The seek ended without a game. Either lichess refused it (rate limit,
		// bad clock) or it expired.
		if err != nil && ctx.Err() == nil {
			var apiErr *lichess.APIError
			if errors.As(err, &apiErr) && apiErr.RateLimited() {
				// The 5/min cap is per IP and shared by every Gambit player, so
				// this is a real and expected outcome — say so in those terms.
				p.fail("lichess's lobby is rate-limited right now (it allows only a few seeks a minute across all of Terry's Gambit). Try again in a minute.")
				return
			}
			p.fail("lichess wouldn't post the seek: %s", err)
			return
		}
		// Clean close with no gameStart yet — wait briefly for the event to catch
		// up before calling it expired; the two streams race by design.
		select {
		case gameID = <-gameCh:
		case <-time.After(3 * time.Second):
			if ctx.Err() == nil {
				p.fail("nobody took the game. Try again.")
			}
			return
		case <-ctx.Done():
			return
		}

	case <-ctx.Done():
		return
	}

	// We know the game; the event stream has done its job.
	stopEvents()

	p.update(func(s *PlayState) {
		s.Status = playLive
		s.GameID = gameID
		s.URL = "https://lichess.org/" + gameID
	})

	p.mu.Lock()
	p.soloLichessID = soloLichessID
	p.mu.Unlock()

	r.streamGame(ctx, p, token, gameID)
}

// runPaired drives the two-seat flow: challenge → accept → stream.
func (r *relay) runPaired(ctx context.Context, p *play) {
	whiteTok, _, err := r.credentials(ctx, p.req.WhiteSteamID)
	if err != nil {
		p.fail("white seat: %s", err)
		return
	}
	blackTok, blackName, err := r.credentials(ctx, p.req.BlackSteamID)
	if err != nil {
		p.fail("black seat: %s", err)
		return
	}

	// White challenges Black, and White gets White — colour is not random,
	// because the players are already sitting on their sides of a physical board
	// and the whole point is that the lichess game mirrors that board.
	res, err := lichess.ChallengeUserByName(ctx, whiteTok, blackName, lichess.ChallengeParams{
		LimitSeconds:     p.req.LimitSeconds,
		IncrementSeconds: p.req.IncrementSec,
		Unlimited:        p.req.Unlimited,
		Rated:            false, // Gambit never plays for someone's rating without asking
		Color:            "white",
	})
	if err != nil {
		p.fail("lichess refused the challenge: %s", err)
		return
	}

	// Accept it with the other seat's own token. gamchess holds both, so it never
	// needs to watch an event stream for the challenge to arrive — which is why
	// nothing here is bound by the one-event-stream-per-token rule.
	if err := lichess.AcceptChallenge(ctx, blackTok, res.ID); err != nil {
		p.fail("the challenge could not be accepted: %s", err)
		// Best-effort tidy-up: a challenge nobody accepts would otherwise sit in
		// White's lichess notifications for 20s.
		cancelCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if cerr := lichess.CancelChallenge(cancelCtx, whiteTok, res.ID); cerr != nil {
			r.log.Debug("could not cancel the dead challenge", zap.Error(cerr))
		}
		return
	}

	// The challenge id IS the game id once accepted.
	p.update(func(s *PlayState) {
		s.Status = playLive
		s.GameID = res.ID
		s.URL = "https://lichess.org/" + res.ID
	})

	r.streamGame(ctx, p, whiteTok, res.ID)
}

// streamGame holds the game stream open, republishing every state, and retries
// while the game is still live.
//
// A stream can drop for reasons that have nothing to do with the game (a proxy
// hiccup, a lichess deploy). Since every gameState carries the complete move
// list, reconnecting costs nothing and loses nothing — so a drop must never end
// a game that lichess still considers live.
func (r *relay) streamGame(ctx context.Context, p *play, token, gameID string) {
	for {
		err := lichess.StreamGame(ctx, token, gameID, func(e lichess.GameEvent) {
			switch {
			case e.Full != nil:
				full := e.Full
				p.resolveSoloColor(full)
				now := time.Now()
				p.update(func(s *PlayState) {
					s.WhiteName = full.White.Name
					s.BlackName = full.Black.Name
					applyState(s, &full.State, now)
				})
			case e.State != nil:
				st := e.State
				now := time.Now()
				p.update(func(s *PlayState) { applyState(s, st, now) })
			}
			// chatLine / opponentGone are ignored: Gambit has voice and a table.
		})

		if ctx.Err() != nil {
			return // we stopped it (sweep or shutdown)
		}

		state, _ := p.snapshot()
		if state.Finished {
			return
		}

		if err != nil {
			r.log.Warn("lichess game stream dropped; retrying",
				zap.String("game_id", gameID), zap.Error(err))
		}

		select {
		case <-ctx.Done():
			return
		case <-time.After(streamRetryDelay):
		}
	}
}

// resolveSoloColor learns which side our one player was given, by matching their
// lichess id against the game's players.
//
// A seek is 50/50 and lichess decides, so this is the only way to know there. A
// challenge asked for a colour, but this confirms it from the game rather than
// assuming the ask was honoured — lichess is the authority.
//
// It also fills in that player's SteamID on the right seat, which is what makes
// seatOf — and therefore every write action — work for a solo flow.
//
// No-op for the paired flow, where both colours were fixed by the challenge.
func (p *play) resolveSoloColor(full *lichess.GameFull) {
	if !p.req.solo() || full == nil {
		return
	}

	p.mu.Lock()
	color := ""
	switch p.soloLichessID {
	case full.White.ID:
		color = "white"
	case full.Black.ID:
		color = "black"
	}
	p.soloColor = color
	p.mu.Unlock()

	if color == "" {
		// Neither player is us. Should be impossible — it's our token's game —
		// but guessing a colour here would hand a stranger's seat to our player.
		p.fail("lichess started a game we don't seem to be in")
		return
	}

	solo := strconv.FormatInt(p.req.SoloSteamID, 10)
	p.update(func(s *PlayState) {
		if color == "white" {
			s.WhiteSteamID = solo
			s.BlackSteamID = "" // a stranger: no SteamID exists for them
		} else {
			s.BlackSteamID = solo
			s.WhiteSteamID = ""
		}
	})
}

// applyState folds a lichess gameState into the client-facing snapshot. now is
// when this frame reached us, stamped onto clockAt whenever the clocks actually
// change so the client can age them — passed in rather than read here so a test
// can pin it.
func applyState(s *PlayState, st *lichess.GameState, now time.Time) {
	// Stamp BEFORE overwriting the old values: a real move changes at least one
	// clock (think time out, increment in), while a draw/takeback gameState carries
	// the same clocks and must leave clockAt where it was, or the age reads ~0 for a
	// value that is really as old as the current think.
	if st.Wtime != s.WhiteTimeMs || st.Btime != s.BlackTimeMs {
		s.clockAt = now
	}
	s.Moves = st.Moves
	s.WhiteTimeMs = st.Wtime
	s.BlackTimeMs = st.Btime
	s.WhiteIncMs = st.Winc
	s.BlackIncMs = st.Binc
	s.LichessStatus = st.Status
	s.Winner = st.Winner
	s.WhiteDraw = st.Wdraw
	s.BlackDraw = st.Bdraw
	s.WhiteTakeback = st.Wtakeback
	s.BlackTakeback = st.Btakeback

	if lichess.Finished(st.Status) {
		s.Finished = true
		s.Status = playOver
	}
}

// Act performs a write on a live game as the given player, with that player's
// own token. steamID has been FP-verified by the handler; seatOf is what stops
// one seat resigning for the other.
func (r *relay) Act(ctx context.Context, p *play, steamID int64, action, uci string) error {
	if _, ok := p.seatOf(steamID); !ok {
		return errors.New("you are not seated in this game")
	}

	state, _ := p.snapshot()
	if state.GameID == "" {
		return errors.New("this game has not started on lichess yet")
	}
	if state.Finished {
		return errors.New("this game is already over")
	}

	// Each seat acts with their OWN token. Playing White's move with Black's
	// token would be rejected by lichess anyway, but the real point is that
	// gamchess never uses one player's credential to act as another.
	token, _, err := r.credentials(ctx, steamID)
	if err != nil {
		return err
	}

	switch action {
	case "move":
		if uci == "" {
			return errors.New("a move needs a uci")
		}
		return lichess.Move(ctx, token, state.GameID, uci, false)
	case "resign":
		return lichess.Resign(ctx, token, state.GameID)
	case "draw":
		return lichess.Draw(ctx, token, state.GameID, true)
	case "draw-decline":
		return lichess.Draw(ctx, token, state.GameID, false)
	case "takeback":
		return lichess.Takeback(ctx, token, state.GameID, true)
	case "takeback-decline":
		return lichess.Takeback(ctx, token, state.GameID, false)
	case "abort":
		return lichess.Abort(ctx, token, state.GameID)
	default:
		return fmt.Errorf("unknown action %q", action)
	}
}

// Wait blocks until the play's version passes since, or pollHold elapses, or the
// client hangs up. This is the transport: a long-poll over our own API.
//
// Not a WebSocket, deliberately. s&box CAN speak WebSocket (Sandbox.WebSocket
// streams fine and its Connect goes through the same URL policy as Http), but
// the Go side would need a WebSocket library — a new dependency this repo cannot
// add today, because the machine that writes this server has neither Go nor
// Docker to regenerate go.sum with. A long poll needs no dependency, and it
// suits the client we actually have: Sandbox.Http buffers whole responses, which
// is useless for a stream but exactly right for one small JSON per state change.
// Latency is a round trip, which is fine for blitz.
//
// If a WS transport ever lands, it replaces this one function and the client's
// poll loop — nothing else here knows the difference.
func (p *play) Wait(ctx context.Context, since uint64) PlayState {
	state, changed := p.snapshot()
	if state.Version > since {
		return state
	}

	select {
	case <-changed:
		state, _ = p.snapshot()
		return state
	case <-time.After(pollHold):
		return state
	case <-ctx.Done():
		return state
	}
}

// sweep drops finished and abandoned plays, and stops their streams. Called on
// the write path (Join) rather than from a ticker — the same lazy-sweep pattern
// as nonceStore, and for the same reason: no goroutine to own, and a server
// nobody is using has nothing to clean up.
func (r *relay) sweep() {
	now := time.Now()

	r.mu.Lock()
	var dead []*play
	for id, p := range r.plays {
		if p.done(now) {
			dead = append(dead, p)
			delete(r.plays, id)
			// Free the player's solo slot with the play, or one abandoned seek or
			// challenge would lock them out of asking again forever.
			if p.req.solo() && r.pending[p.req.SoloSteamID] == id {
				delete(r.pending, p.req.SoloSteamID)
			}
		}
	}
	r.mu.Unlock()

	for _, p := range dead {
		if p.cancel != nil {
			p.cancel()
		}
	}
}
