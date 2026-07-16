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

	// Milliseconds, straight from lichess. The client renders these instead of
	// running its own clock — the same rule as the local game, where only the
	// host's tick is authoritative.
	WhiteTimeMs int64 `json:"white_time_ms"`
	BlackTimeMs int64 `json:"black_time_ms"`
	WhiteIncMs  int64 `json:"white_inc_ms"`
	BlackIncMs  int64 `json:"black_inc_ms"`

	// Seek marks a game against a random lichess opponent rather than the player
	// opposite you. The stranger has no SteamID, so one of the seat ids is empty
	// and the client must read YourColor rather than matching SteamIDs.
	Seek bool `json:"seek"`
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
}

// PlayRequest is an intent to put a game on lichess. Two shapes:
//
//	Seek == false — the paired flow: two seated players challenge each other.
//	                Both seats must ask; White and Black are both known up front.
//	Seek == true  — the lobby flow: ONE player seeks a random opponent. Only
//	                SeekerSteamID is known, and the opponent is a lichess account
//	                that has nothing to do with this lobby.
type PlayRequest struct {
	ClientGameID string
	LimitSeconds int
	IncrementSec int
	Unlimited    bool

	// Paired flow only.
	WhiteSteamID int64
	BlackSteamID int64

	// Seek flow only.
	Seek          bool
	SeekerSteamID int64
	Rated         bool
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
		r.WhiteSteamID == o.WhiteSteamID &&
		r.BlackSteamID == o.BlackSteamID &&
		r.SeekerSteamID == o.SeekerSteamID &&
		r.LimitSeconds == o.LimitSeconds &&
		r.IncrementSec == o.IncrementSec &&
		r.Unlimited == o.Unlimited
}

// intentsNeeded is how many independent, separately-authenticated players must
// ask before a game is started.
//
// TWO for the paired flow, and that is the whole authorisation story (see the
// file header). ONE for a seek, which needs no consent from anyone else: you are
// spending your own token to play a stranger who opts in on lichess's side, by
// their own choice, in their own client. Nobody is dragged anywhere.
func (r PlayRequest) intentsNeeded() int {
	if r.Seek {
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

	// seekerColor is which side lichess gave the seeker, learned from gameFull.
	// Empty until then — a seek gets 50/50 and we don't get to choose.
	// seekerLichessID is what we match gameFull's players against. Both mu-guarded.
	seekerColor     string
	seekerLichessID string
}

func newPlay(req PlayRequest) *play {
	p := &play{
		req:     req,
		created: time.Now(),
		intents: map[int64]bool{},
		changed: make(chan struct{}),
		state:   PlayState{Status: playWaiting, Seek: req.Seek},
	}
	// A seek has one known player and one stranger; which colour they get is
	// lichess's to decide, so neither seat is filled in until gameFull lands.
	if !req.Seek {
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
// For a SEEK there is one Gambit player and one stranger, and we don't know
// which colour lichess gave us until gameFull arrives — so the colour may be ""
// while ok is true. Callers must not read the colour as "they're white".
func (p *play) seatOf(steamID int64) (string, bool) {
	if steamID == 0 {
		return "", false // 0 is "empty seat" everywhere in this codebase
	}
	if p.req.Seek {
		if steamID != p.req.SeekerSteamID {
			return "", false
		}
		p.mu.Lock()
		defer p.mu.Unlock()
		return p.seekerColor, true
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
	// seeks is steamID → the client_game_id of that player's active seek. One per
	// user, because a seek needs an event stream and lichess allows one of those
	// per token.
	seeks map[int64]string
}

func newRelay(log *zap.Logger, db *pgxpool.Pool, c *lichess.Cipher) *relay {
	return &relay{
		log:    log,
		db:     db,
		cipher: c,
		plays:  map[string]*play{},
		seeks:  map[int64]string{},
	}
}

// claimSeek reserves this player's single seek slot, or explains why not.
// Re-asking for the SAME seek is fine — that's just the client re-posting.
func (r *relay) claimSeek(steamID int64, clientGameID string) error {
	r.mu.Lock()
	defer r.mu.Unlock()

	if existing, ok := r.seeks[steamID]; ok && existing != clientGameID {
		// Is the old one actually still going? A finished/failed seek shouldn't
		// block a new one.
		if p, live := r.plays[existing]; live {
			if st, _ := p.snapshot(); st.Status != playOver && st.Status != playFailed {
				return errors.New("you already have a lichess seek waiting — cancel that one first")
			}
		}
	}
	r.seeks[steamID] = clientGameID
	return nil
}

// releaseSeek frees a player's seek slot.
func (r *relay) releaseSeek(steamID int64, clientGameID string) {
	r.mu.Lock()
	if r.seeks[steamID] == clientGameID {
		delete(r.seeks, steamID)
	}
	r.mu.Unlock()
}

// Cancel stops a play the caller owns — a seeker giving up on waiting, or a
// player standing up. Only a participant may do it.
//
// Cancelling a seek matters more than it looks: the seek IS the held connection,
// so this is what actually withdraws it from lichess's lobby. Without it, a
// player who walked away would stay pairable and get dropped into a game nobody
// is sitting at.
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
	if p.req.Seek {
		r.releaseSeek(p.req.SeekerSteamID, p.req.ClientGameID)
	}
	p.update(func(s *PlayState) {
		s.Status = playFailed
		s.Error = "cancelled"
	})
	return nil
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

	// One seek per user, ever. Not a nicety: a seek needs an event stream, and
	// lichess allows ONE per token — opening a second silently closes the first,
	// so two concurrent seeks would leave the earlier one unable to learn its own
	// game started. lichess independently caps concurrent seeks per user too.
	if req.Seek {
		if err := r.claimSeek(steamID, req.ClientGameID); err != nil {
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
	if p.req.Seek {
		r.runSeek(ctx, p)
		return
	}
	r.runPaired(ctx, p)
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
	token, _, err := r.credentials(ctx, p.req.SeekerSteamID)
	if err != nil {
		p.fail("%s", err)
		return
	}

	// The seek's own lichess id, so we can tell which colour gameFull gave us.
	link, err := store.LichessLinkBySteamID(ctx, r.db, p.req.SeekerSteamID)
	if err != nil {
		p.fail("%s", err)
		return
	}
	seekerLichessID := link.LichessID

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
	p.seekerLichessID = seekerLichessID
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
				p.resolveSeekColor(full)
				p.update(func(s *PlayState) {
					s.WhiteName = full.White.Name
					s.BlackName = full.Black.Name
					applyState(s, &full.State)
				})
			case e.State != nil:
				st := e.State
				p.update(func(s *PlayState) { applyState(s, st) })
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

// resolveSeekColor learns which side a seeker was given, by matching their
// lichess id against the game's players.
//
// A seek is 50/50 and lichess decides, so this is the only way to know. It also
// fills in the seeker's SteamID on the right seat, which is what makes seatOf —
// and therefore every write action — work for a seek.
//
// No-op for the paired flow, where both colours were fixed by the challenge.
func (p *play) resolveSeekColor(full *lichess.GameFull) {
	if !p.req.Seek || full == nil {
		return
	}

	p.mu.Lock()
	color := ""
	switch p.seekerLichessID {
	case full.White.ID:
		color = "white"
	case full.Black.ID:
		color = "black"
	}
	p.seekerColor = color
	p.mu.Unlock()

	if color == "" {
		// Neither player is us. Should be impossible — it's our token's game —
		// but guessing a colour here would hand a stranger's seat to our player.
		p.fail("lichess started a game we don't seem to be in")
		return
	}

	seeker := strconv.FormatInt(p.req.SeekerSteamID, 10)
	p.update(func(s *PlayState) {
		if color == "white" {
			s.WhiteSteamID = seeker
			s.BlackSteamID = "" // a stranger: no SteamID exists for them
		} else {
			s.BlackSteamID = seeker
			s.WhiteSteamID = ""
		}
	})
}

// applyState folds a lichess gameState into the client-facing snapshot.
func applyState(s *PlayState, st *lichess.GameState) {
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
			// Free the seeker's slot with the play, or one abandoned seek would
			// lock them out of seeking again forever.
			if p.req.Seek && r.seeks[p.req.SeekerSteamID] == id {
				delete(r.seeks, p.req.SeekerSteamID)
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
