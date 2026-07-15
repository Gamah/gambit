package lichess

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
)

// The Board API: the half of lichess that actually plays a game.
//
// THIS FILE IS THE ONE PLACE IN GAMBIT THAT READS A LONG-LIVED STREAM, and that
// is the entire reason gamchess holds the token at all. Playing a lichess game
// requires two ndjson streams held open (/api/stream/event and
// /api/board/game/stream/{id}); lichess has no polling substitute and answers a
// poller with a literal "Please don't poll this endpoint, it is intended to be
// streamed" 429. The s&box client cannot read a stream — Sandbox.Http buffers
// the whole body before returning and HttpCompletionOption is off the API
// whitelist — so the reader must be here. Go's http.Client streams natively:
// resp.Body + bufio.Scanner, no client timeout, cancelled via context.
//
// Everything that is not a GET .../stream... is an ordinary buffered call.

// streamClient has NO Timeout, unlike client. A client-level timeout applies to
// the whole request including the body read, so it would kill a healthy stream
// mid-game. Cancellation is the caller's context (unseat, game over, shutdown).
var streamClient = &http.Client{}

// maxStreamLine bounds one ndjson line. A gameFull with a long move list is a
// few KB; a megabyte is slack.
const maxStreamLine = 1 << 20

// ── Event shapes (re-derived from the OpenAPI spec, 2026-07-15) ──

// Event is one line of /api/stream/event. Type is one of gameStart, gameFinish,
// challenge, challengeCanceled, challengeDeclined. Only the fields Gambit acts
// on are modelled; lichess may add more and we ignore them.
type Event struct {
	Type      string     `json:"type"`
	Game      *EventGame `json:"game"`
	Challenge *Challenge `json:"challenge"`
}

// EventGame rides gameStart/gameFinish.
type EventGame struct {
	GameID string `json:"gameId"`
	FullID string `json:"fullId"`
	Fen    string `json:"fen"`
	Color  string `json:"color"`
	Speed  string `json:"speed"`
}

// Challenge rides challenge/challengeCanceled/challengeDeclined. The id doubles
// as the game id once accepted.
type Challenge struct {
	ID         string         `json:"id"`
	Status     string         `json:"status"`
	Challenger *ChallengeUser `json:"challenger"`
	DestUser   *ChallengeUser `json:"destUser"`
	Speed      string         `json:"speed"`
	Rated      bool           `json:"rated"`
	Direction  string         `json:"direction"` // "in" = to us, "out" = from us
}

type ChallengeUser struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

// GameState is a gameState line: the whole game so far, not a delta.
//
// Moves is the FULL space-separated UCI list from the start position every time,
// which is what lets us rebuild a position without tracking deltas — a dropped
// or duplicated line costs nothing. Times are MILLISECONDS.
type GameState struct {
	Type   string `json:"type"`
	Moves  string `json:"moves"`
	Wtime  int64  `json:"wtime"`
	Btime  int64  `json:"btime"`
	Winc   int64  `json:"winc"`
	Binc   int64  `json:"binc"`
	Status string `json:"status"`
	Winner string `json:"winner"` // "white" | "black" | "" (no winner / unfinished)
	Wdraw  bool   `json:"wdraw"`
	Bdraw  bool   `json:"bdraw"`
}

// GameFull is the gameFull line — always the first line of a game stream.
type GameFull struct {
	Type       string      `json:"type"`
	ID         string      `json:"id"`
	Speed      string      `json:"speed"`
	Rated      bool        `json:"rated"`
	White      GamePlayer  `json:"white"`
	Black      GamePlayer  `json:"black"`
	InitialFen string      `json:"initialFen"`
	Clock      *ClockSetup `json:"clock"`
	State      GameState   `json:"state"`
}

type GamePlayer struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

type ClockSetup struct {
	Initial   int64 `json:"initial"`   // milliseconds
	Increment int64 `json:"increment"` // milliseconds
}

// GameEvent is one line of a game stream, already discriminated by type. Exactly
// one of Full/State is non-nil for the lines we act on; chatLine and opponentGone
// arrive as Type with both nil.
type GameEvent struct {
	Type  string
	Full  *GameFull
	State *GameState
}

// Finished reports whether a status means the game is over. "created" and
// "started" are the only live ones — everything else in lichess's status enum
// (mate, resign, stalemate, timeout, draw, outoftime, aborted, cheat, noStart,
// unknownFinish, insufficientMaterialClaim, variantEnd) is terminal.
func Finished(status string) bool {
	switch status {
	case "", "created", "started":
		return false
	default:
		return true
	}
}

// ── Streams ──

// StreamEvents holds /api/stream/event open, calling fn for each event until the
// context is cancelled or lichess closes the stream. Returns the reason it
// stopped; a cancelled context yields ctx.Err().
//
// ONE ACTIVE STREAM PER TOKEN: opening a second closes the first, server-side.
// So exactly one of these may run per linked user, ever — see api.relay, which
// is the only caller and enforces it with a per-user map.
//
// A blank line arrives every ~7s as a keepalive and is skipped.
func StreamEvents(ctx context.Context, token string, fn func(Event)) error {
	return stream(ctx, token, apiBase+"/api/stream/event", func(line []byte) error {
		var e Event
		if err := json.Unmarshal(line, &e); err != nil {
			// One malformed line must not kill a live game's stream. Skip it: the
			// next gameState carries the whole position anyway.
			return nil
		}
		fn(e)
		return nil
	})
}

// StreamGame holds /api/board/game/stream/{gameId} open, calling fn per event.
// The first line is always gameFull; subsequent gameState lines each carry the
// complete move list.
func StreamGame(ctx context.Context, token, gameID string, fn func(GameEvent)) error {
	u := apiBase + "/api/board/game/stream/" + url.PathEscape(gameID)
	return stream(ctx, token, u, func(line []byte) error {
		// Peek at the discriminator before committing to a shape.
		var head struct {
			Type string `json:"type"`
		}
		if err := json.Unmarshal(line, &head); err != nil {
			return nil
		}

		switch head.Type {
		case "gameFull":
			var full GameFull
			if err := json.Unmarshal(line, &full); err != nil {
				return nil
			}
			fn(GameEvent{Type: head.Type, Full: &full})
		case "gameState":
			var st GameState
			if err := json.Unmarshal(line, &st); err != nil {
				return nil
			}
			fn(GameEvent{Type: head.Type, State: &st})
		default:
			// chatLine, opponentGone, and anything lichess adds later.
			fn(GameEvent{Type: head.Type})
		}
		return nil
	})
}

// stream is the shared ndjson reader. Deliberately tiny and shared: the two
// callers differ only in URL and line shape, and a streaming bug is the kind
// that only shows up mid-game.
func stream(ctx context.Context, token, u string, onLine func([]byte) error) error {
	if err := guard(ctx); err != nil {
		return err
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return fmt.Errorf("lichess: build stream request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/x-ndjson")

	resp, err := streamClient.Do(req)
	if err != nil {
		return fmt.Errorf("lichess: stream request: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("lichess: stream status %d", resp.StatusCode)
	}

	sc := bufio.NewScanner(resp.Body)
	sc.Buffer(make([]byte, 0, 8<<10), maxStreamLine)
	for sc.Scan() {
		line := sc.Bytes()
		if len(strings.TrimSpace(string(line))) == 0 {
			continue // ~7s keepalive
		}
		if err := onLine(line); err != nil {
			return err
		}
	}
	if err := sc.Err(); err != nil {
		// Context cancellation surfaces here as a read error; report the cause so
		// callers can tell "we stopped it" from "lichess dropped us".
		if ctxErr := ctx.Err(); ctxErr != nil {
			return ctxErr
		}
		return fmt.Errorf("lichess: stream read: %w", err)
	}
	// A clean EOF means lichess closed it — game over, or the token opened a
	// second event stream elsewhere.
	if ctxErr := ctx.Err(); ctxErr != nil {
		return ctxErr
	}
	return nil
}

// ── Buffered writers ──

// Move plays a UCI move. offeringDraw rides along rather than needing a second
// call — that's lichess's own shape for "move and offer a draw".
func Move(ctx context.Context, token, gameID, uci string, offeringDraw bool) error {
	p := fmt.Sprintf("/api/board/game/%s/move/%s",
		url.PathEscape(gameID), url.PathEscape(uci))
	if offeringDraw {
		p += "?offeringDraw=true"
	}
	return post(ctx, token, p, nil)
}

// Resign the game.
func Resign(ctx context.Context, token, gameID string) error {
	return post(ctx, token, "/api/board/game/"+url.PathEscape(gameID)+"/resign", nil)
}

// Draw offers a draw (accept=true) or declines/retracts one (accept=false).
func Draw(ctx context.Context, token, gameID string, accept bool) error {
	verb := "no"
	if accept {
		verb = "yes"
	}
	return post(ctx, token, "/api/board/game/"+url.PathEscape(gameID)+"/draw/"+verb, nil)
}

// Abort a game that hasn't left the opening. Only legal before both sides have
// moved; lichess 400s otherwise.
func Abort(ctx context.Context, token, gameID string) error {
	return post(ctx, token, "/api/board/game/"+url.PathEscape(gameID)+"/abort", nil)
}

// Chat posts to a game's chat. room is "player" or "spectator".
func Chat(ctx context.Context, token, gameID, room, text string) error {
	return post(ctx, token, "/api/board/game/"+url.PathEscape(gameID)+"/chat",
		url.Values{"room": {room}, "text": {text}})
}

// AcceptChallenge accepts an incoming challenge. board:play covers this — the
// spec lists challenge:write/bot:play/board:play as alternatives.
func AcceptChallenge(ctx context.Context, token, challengeID string) error {
	return post(ctx, token, "/api/challenge/"+url.PathEscape(challengeID)+"/accept", nil)
}

// DeclineChallenge declines an incoming challenge. reason is a lichess reason
// key ("generic", "later", "tooFast", …); blank sends none.
func DeclineChallenge(ctx context.Context, token, challengeID, reason string) error {
	form := url.Values{}
	if reason != "" {
		form.Set("reason", reason)
	}
	return post(ctx, token, "/api/challenge/"+url.PathEscape(challengeID)+"/decline", form)
}

// CancelChallenge withdraws a challenge we issued.
func CancelChallenge(ctx context.Context, token, challengeID string) error {
	return post(ctx, token, "/api/challenge/"+url.PathEscape(challengeID)+"/cancel", nil)
}

// ChallengeParams describes the game to propose. A zero LimitSeconds with
// Unlimited=false is not "no clock" — set Unlimited explicitly, because omitting
// the clock fields is how lichess is told to make an unlimited game and a
// zero-valued 0+0 clock is a real (and rejected) thing to ask for.
type ChallengeParams struct {
	LimitSeconds     int  // initial clock, seconds
	IncrementSeconds int  // Fischer increment, seconds
	Unlimited        bool // no clock at all — omits both fields
	Rated            bool
	Color            string // "white" | "black" | "random"
}

// ChallengeResult is the accepted subset of lichess's ChallengeJson. The id
// becomes the GAME id once the challenge is accepted — same value, which is what
// lets the challenger start streaming before the acceptance lands.
type ChallengeResult struct {
	ID     string `json:"id"`
	Status string `json:"status"`
	URL    string `json:"url"`
}

// ChallengeUserByName issues a direct challenge to a lichess username.
//
// This is Gambit's PRIMARY path into a game, for one reason: it reaches BLITZ.
// A lobby seek cannot (real-time seeks are Rapid/Classical only), and Gambit's
// default table is Blitz 3+2. It also dodges the seek's 5/min-per-IP cap, which
// is shared across the whole playerbase and is the one real relay bottleneck.
//
// A real-time challenge EXPIRES AFTER 20 SECONDS if not accepted. We don't set
// keepAliveStream (which would hold a third stream open per pending challenge) —
// the opposite seat is already sitting at the table with a live event stream and
// auto-accepts in well under a second. If that ever stops being true, revisit.
func ChallengeUserByName(ctx context.Context, token, username string, p ChallengeParams) (ChallengeResult, error) {
	if err := p.validate(); err != nil {
		return ChallengeResult{}, err
	}

	form := url.Values{}
	// Omitting BOTH clock fields is what asks for an unlimited game — sending
	// "clock.limit=0&clock.increment=0" would ask for a 0+0 clock instead.
	if !p.Unlimited {
		form.Set("clock.limit", fmt.Sprint(p.LimitSeconds))
		form.Set("clock.increment", fmt.Sprint(p.IncrementSeconds))
	}
	form.Set("rated", fmt.Sprint(p.Rated))
	if p.Color != "" {
		form.Set("color", p.Color)
	}

	body, err := postBody(ctx, token, "/api/challenge/"+url.PathEscape(username), form)
	if err != nil {
		return ChallengeResult{}, err
	}
	var out ChallengeResult
	if err := json.Unmarshal(body, &out); err != nil {
		return ChallengeResult{}, fmt.Errorf("lichess: decode challenge: %w", err)
	}
	if out.ID == "" {
		return ChallengeResult{}, fmt.Errorf("lichess: challenge response carried no id")
	}
	return out, nil
}

// validate enforces lichess's documented clock domain before we spend a request
// on it, and enforces the Board API's own speed floor.
func (p ChallengeParams) validate() error {
	if p.Unlimited {
		return nil // no clock → Correspondence speed → board-compatible
	}
	if !ValidClockLimit(p.LimitSeconds) {
		return fmt.Errorf("lichess: clock.limit %d is not one of 0/15/30/45/60/90 or a multiple of 60 up to 10800",
			p.LimitSeconds)
	}
	if p.IncrementSeconds < 0 || p.IncrementSeconds > 60 {
		return fmt.Errorf("lichess: clock.increment %d is outside 0..60", p.IncrementSeconds)
	}
	if !ChallengeCompatible(p.LimitSeconds, p.IncrementSeconds) {
		return fmt.Errorf("lichess: %d+%d is faster than blitz — the Board API refuses to challenge with it",
			p.LimitSeconds, p.IncrementSeconds)
	}
	return nil
}

// ValidClockLimit mirrors lichess's documented clock.limit domain: 0, 15, 30,
// 45, 60, 90, or any multiple of 60 up to 10800 (3 hours).
func ValidClockLimit(limit int) bool {
	switch limit {
	case 0, 15, 30, 45, 60, 90:
		return true
	}
	return limit > 0 && limit <= 10800 && limit%60 == 0
}

// The Board API has TWO different speed floors, and lila implements them in two
// functions with the SAME NAME. This bit them into us and is worth spelling out:
//
//	Challenge.isBoardCompatible          speed >= Speed.Blitz   (180s)
//	  modules/challenge/src/main/Challenge.scala — gates a direct CHALLENGE
//
//	lila.core.game.isBoardCompatible     Speed(clock) >= Speed.Rapid  (480s)
//	  modules/core/src/main/game/misc.scala — gates a board SEEK, via
//	  SetupForm.boardApiHook's "Invalid time control" verification
//
// So a direct challenge accepts blitz and a lobby seek does not. Do not collapse
// these two, and do not trust a memory of "the Board API floor" — there isn't
// one floor.
//
// Both bands come from scalachess's Speed.byTime(limit + 40*increment):
// Blitz is 180..479, Rapid is 480..1499.
//
// [SOURCE] read from lila/scalachess master on 2026-07-15, not from a documented
// contract — it can change without notice. Re-check before trusting it.
const (
	// ChallengeFloorSeconds is the estimated total a direct challenge needs.
	ChallengeFloorSeconds = 180
	// SeekFloorSeconds is the estimated total a real-time lobby seek needs.
	SeekFloorSeconds = 480
)

// ChallengeCompatible reports whether a clock is slow enough to be challenged
// with (blitz or slower).
//
// BULLET CAN NEVER REACH LICHESS, from any path — Gambit's Bullet 1+0 estimates
// at 60 and lands in the Bullet band. That is a lichess rule, not ours, and it
// is why the play flow is never offered at a bullet table.
func ChallengeCompatible(limitSeconds, incrementSeconds int) bool {
	return EstimateTotalSeconds(limitSeconds, incrementSeconds) >= ChallengeFloorSeconds
}

// SeekCompatible reports whether a clock is slow enough for a REAL-TIME lobby
// seek (rapid or slower).
//
// Stricter than ChallengeCompatible, and that difference is the whole reason a
// direct challenge is Gambit's primary path: the default table is Blitz 3+2,
// which estimates at 260 — fine to challenge with, refused as a seek.
func SeekCompatible(limitSeconds, incrementSeconds int) bool {
	return EstimateTotalSeconds(limitSeconds, incrementSeconds) >= SeekFloorSeconds
}

// EstimateTotalSeconds is scalachess's clock-to-speed estimate: the initial bank
// plus increment over an assumed 40-move game.
func EstimateTotalSeconds(limitSeconds, incrementSeconds int) int {
	return limitSeconds + 40*incrementSeconds
}

// SeekRealtime opens a public seek for a random opponent and HOLDS IT OPEN.
//
// The connection IS the seek: lichess cancels it the moment we hang up, which is
// deliberate on their part (if the client dies, the user isn't paired into a game
// they won't play). So this blocks until the context is cancelled or lichess
// closes the stream, and the caller must keep it running for as long as the
// player is waiting.
//
// The stream carries NO information — not even the game id. lichess's own
// instruction is to have an event stream open first and learn about the game from
// gameStart there. api.relay does exactly that, in that order.
//
// NOTE THE UNITS. limitMinutes is MINUTES (0..180, fractional allowed), while
// ChallengeParams.LimitSeconds is SECONDS. That asymmetry is lichess's, not ours,
// and it is an easy way to ask for a 10-second game while meaning ten minutes.
//
// Rate limit: 5 per minute PER IP (lila Limiters.setupPost), which for gamchess
// means 5 per minute for every Gambit player combined. The caller is expected to
// gate on that before spending one.
// SeekParams is every control lichess offers on a real-time seek.
//
// Variant is deliberately absent, and that is a Gambit constraint rather than a
// lichess one: our board plays standard chess and nothing else (the vendored
// rules library is standard-only), so a Crazyhouse or Atomic game would arrive
// as a stream of moves we could not render or validate. Offering a variant we
// cannot draw would be worse than not offering it.
type SeekParams struct {
	// TimeMinutes is MINUTES (0..180, fractional allowed) — lichess's unit for a
	// seek. ChallengeParams.LimitSeconds is SECONDS. The asymmetry is theirs.
	TimeMinutes      float64
	IncrementSeconds int // 0..180

	// Rated puts the game on the player's real lichess rating. Their choice, and
	// the reason the UI asks rather than assumes.
	Rated bool

	// RatingRange narrows the opponent pool, e.g. "1500-1800". lichess's own
	// advice is "better left empty" — a narrow range on a small pool means a seek
	// that never pairs, and Gambit's seeks are already scarce.
	RatingRange string

	// Color is "white", "black", or ""/"random". lichess's advice is again to
	// leave it empty for an even split; asking for a colour halves the pool.
	Color string
}

func SeekRealtime(ctx context.Context, token string, p SeekParams) error {
	if p.TimeMinutes < 0 || p.TimeMinutes > 180 {
		return fmt.Errorf("lichess: seek time %.1f is outside 0..180 minutes", p.TimeMinutes)
	}
	if p.IncrementSeconds < 0 || p.IncrementSeconds > 180 {
		return fmt.Errorf("lichess: seek increment %d is outside 0..180 seconds", p.IncrementSeconds)
	}
	if !SeekCompatible(int(p.TimeMinutes*60), p.IncrementSeconds) {
		return fmt.Errorf("lichess: a real-time seek needs rapid or slower — %.1f+%d is faster",
			p.TimeMinutes, p.IncrementSeconds)
	}
	if err := guard(ctx); err != nil {
		return err
	}
	// The lobby's 5-per-minute limit is per IP, which for us means per PLAYERBASE.
	// Refuse locally with a real explanation rather than spending the shared
	// budget to be told the same thing by a 429.
	if err := TakeSeekSlot(); err != nil {
		return err
	}

	form := url.Values{
		"time":      {strconv.FormatFloat(p.TimeMinutes, 'f', -1, 64)},
		"increment": {fmt.Sprint(p.IncrementSeconds)},
		"rated":     {fmt.Sprint(p.Rated)},
	}
	if p.RatingRange != "" {
		form.Set("ratingRange", p.RatingRange)
	}
	if p.Color != "" && p.Color != "random" {
		form.Set("color", p.Color)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, apiBase+"/api/board/seek",
		strings.NewReader(form.Encode()))
	if err != nil {
		return fmt.Errorf("lichess: build seek request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Accept", "application/x-ndjson")

	// streamClient, not client: a client timeout would cancel the seek out from
	// under a player still waiting for an opponent.
	resp, err := streamClient.Do(req)
	if err != nil {
		return fmt.Errorf("lichess: seek request: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(io.LimitReader(resp.Body, maxBody))
		return &APIError{Status: resp.StatusCode, Body: strings.TrimSpace(string(raw))}
	}

	// Drain until cancelled or closed. There is nothing to parse — holding the
	// connection is the entire purpose.
	_, err = io.Copy(io.Discard, resp.Body)
	if ctxErr := ctx.Err(); ctxErr != nil {
		return ctxErr
	}
	if err != nil {
		return fmt.Errorf("lichess: seek stream: %w", err)
	}
	return nil // lichess closed it: matched, or expired
}

// SeekCorrespondence posts a correspondence seek. days ∈ {1,2,3,5,7,10,14}.
//
// This is the one matchmaking call that costs the relay nothing: it returns a
// buffered {id} and holds no stream. The real-time seek does the opposite (see
// below), which is why this is the only seek shape gamchess offers.
func SeekCorrespondence(ctx context.Context, token string, days int, rated bool) error {
	switch days {
	case 1, 2, 3, 5, 7, 10, 14:
	default:
		return fmt.Errorf("lichess: correspondence seek days must be one of 1,2,3,5,7,10,14; got %d", days)
	}
	return post(ctx, token, "/api/board/seek", url.Values{
		"days":  {fmt.Sprint(days)},
		"rated": {fmt.Sprint(rated)},
	})
}

// ── HTTP plumbing ──

func post(ctx context.Context, token, path string, form url.Values) error {
	_, err := postBody(ctx, token, path, form)
	return err
}

// postBody runs a buffered authed POST and returns the body. Fails closed on
// anything that isn't 2xx, and includes lichess's own error text — their 400s
// say useful things ("This game cannot be aborted", "Not your turn") that are
// worth having in a log line.
func postBody(ctx context.Context, token, path string, form url.Values) ([]byte, error) {
	// lichess asks us to wait a full minute after a 429 before resuming API usage.
	// Honour it here rather than at each call site, so no path can skip it.
	if err := guard(ctx); err != nil {
		return nil, err
	}

	var body io.Reader
	if form != nil {
		body = strings.NewReader(form.Encode())
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, apiBase+path, body)
	if err != nil {
		return nil, fmt.Errorf("lichess: build request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/json")
	if form != nil {
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("lichess: request %s: %w", path, err)
	}
	defer resp.Body.Close()

	raw, _ := io.ReadAll(io.LimitReader(resp.Body, maxBody))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, &APIError{Status: resp.StatusCode, Body: strings.TrimSpace(string(raw))}
	}
	return raw, nil
}

// APIError carries a non-2xx from lichess, status included so callers can tell a
// dead token (401) from a refused move (400) from a rate limit (429).
type APIError struct {
	Status int
	Body   string
}

func (e *APIError) Error() string {
	if e.Body == "" {
		return fmt.Sprintf("lichess: status %d", e.Status)
	}
	return fmt.Sprintf("lichess: status %d: %s", e.Status, truncate(e.Body, 200))
}

// Unauthorized reports a dead/revoked token — the player revoked our grant on
// lichess's Security page, or it expired. The link row is now useless.
func (e *APIError) Unauthorized() bool { return e.Status == http.StatusUnauthorized }

// RateLimited reports a 429. lichess's guidance is one request at a time and a
// full 60s wait — never a tight retry.
func (e *APIError) RateLimited() bool { return e.Status == http.StatusTooManyRequests }

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "…"
}
