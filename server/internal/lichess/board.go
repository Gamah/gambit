package lichess

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
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
	// Standing takeback proposals. lichess OMITS these when false rather than
	// sending false, so absent and "not proposing" are the same thing — which is
	// what a bool zero-value already means.
	Wtakeback bool `json:"wtakeback"`
	Btakeback bool `json:"btakeback"`
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

// stream is the shared ndjson reader. Deliberately tiny and shared: the callers
// differ only in URL and line shape, and a streaming bug is the kind that only
// shows up mid-game.
//
// A BLANK token means an anonymous stream and sends no Authorization header at
// all. That is not a convenience: /api/tv/{channel}/feed is `security: []`
// upstream, and attaching a player's board:play token to a request that does not
// need it would hand their credential to an endpoint that never asked for it, on
// a stream we hold open for hours. TV must stay anonymous.
func stream(ctx context.Context, token, u string, onLine func([]byte) error) error {
	return streamReq(ctx, token, http.MethodGet, u, nil, onLine)
}

// streamReq is stream() with a method and an optional form body, for the one
// ndjson stream lichess opens in answer to a POST (a keep-alive challenge).
//
// A non-2xx here returns an *APIError carrying lichess's own body, not a bare
// status: the challenge endpoint says useful things ("No such user: bob", "You
// cannot challenge yourself") that a player needs to read, and a status alone
// would throw them away.
func streamReq(ctx context.Context, token, method, u string, form url.Values, onLine func([]byte) error) error {
	if err := guard(ctx); err != nil {
		return err
	}

	var body io.Reader
	if form != nil {
		body = strings.NewReader(form.Encode())
	}

	req, err := http.NewRequestWithContext(ctx, method, u, body)
	if err != nil {
		return fmt.Errorf("lichess: build stream request: %w", err)
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	req.Header.Set("Accept", "application/x-ndjson")
	if form != nil {
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}

	resp, err := streamClient.Do(req)
	if err != nil {
		return fmt.Errorf("lichess: stream request: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		raw, _ := io.ReadAll(io.LimitReader(resp.Body, maxBody))
		return &APIError{Status: resp.StatusCode, Body: strings.TrimSpace(string(raw))}
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
//
// A 200 HERE MEANS NOTHING. lila's setDraw returns Unit and the controller wraps
// it in fuccess, so every call answers 200 {"ok":true} — including one it dropped
// on the floor. lichess silently refuses an offer before ply 2, a second offer
// within 20 ply of your last one, and one against an AI. The documented 400 does
// not fire. The ONLY truth about whether an offer landed is Wdraw/Bdraw on the
// next gameState, which is why nothing upstream of here reports success.
func Draw(ctx context.Context, token, gameID string, accept bool) error {
	verb := "no"
	if accept {
		verb = "yes"
	}
	return post(ctx, token, "/api/board/game/"+url.PathEscape(gameID)+"/draw/"+verb, nil)
}

// Takeback proposes a takeback (accept=true), or accepts one the opponent has
// already proposed — lichess folds both into takeback/yes exactly as it does for
// draw/yes. accept=false declines.
//
// lichess refuses a takeback before both players have moved, and in tournament,
// simul and swiss games. It says so by IGNORING the call, not by failing it (see
// the note on Draw): the truth is Wtakeback/Btakeback on the next gameState.
func Takeback(ctx context.Context, token, gameID string, accept bool) error {
	verb := "no"
	if accept {
		verb = "yes"
	}
	return post(ctx, token, "/api/board/game/"+url.PathEscape(gameID)+"/takeback/"+verb, nil)
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

// OpenChallengeResult is the subset of lichess's ChallengeOpenJson we surface.
//
// URL is the neutral link — whoever opens it first takes an open seat, colour
// random. URLWhite/URLBlack are the SAME game with a forced colour (they are
// literally URL + "?color=white"/"?color=black"), so handing a specific one out
// is how a creator says "you play this side". id is what cancels it.
type OpenChallengeResult struct {
	ID       string `json:"id"`
	URL      string `json:"url"`
	URLWhite string `json:"urlWhite"`
	URLBlack string `json:"urlBlack"`
}

// OpenChallenge creates a lichess OPEN challenge — a game any two people can join
// by opening its link. It is a different animal from every other flow here, and
// the differences are the whole point:
//
//   - It is NOT relayed and NOT rendered on the Gambit board. Both sides are
//     anonymous to us (lichess returns challenger:null, destUser:null); the game
//     is played in whatever browsers open the links, on lichess.org. gamchess
//     cannot stream it as a player because NEITHER player is our token's account —
//     there is no API to make an authenticated account a participant in an open
//     challenge. So this returns a link and steps back; it never holds a stream.
//   - It has NO board-API speed floor. The floors on ChallengeUserByName and
//     SeekRealtime gate games our TOKEN plays through the Board API; an open
//     challenge is played on the web, so BULLET is fine here where it can reach
//     lichess by no other path. Only lichess's clock DOMAIN still applies.
//   - It is created ANONYMOUSLY — no token. `security: []` says it needs none, and
//     that is not the whole story: if you DO present a token, lichess scope-checks it
//     and requires `challenge:write`, which Gambit never holds (board:play is our only
//     scope). Sending the player's board:play token here 403s "Missing scope:
//     challenge:write" (seen live). So we send no token; our User-Agent still names us
//     via the RoundTripper. The one thing lost is cancellation — lichess ties the
//     cancel right to the creating token, and an anonymous challenge has none — but see
//     below: that costs nothing.
//
// Because our player is never a participant, an abandoned open challenge is
// harmless in a way a named challenge is not — nobody can start a game on our
// player's account by accepting it. It simply expires (24h) if unused, which is why
// creating it anonymously (and so being unable to cancel it) is fine: cancelling was
// only ever tidiness, not safety.
func OpenChallenge(ctx context.Context, p ChallengeParams) (OpenChallengeResult, error) {
	// Only the clock DOMAIN matters here — no board-compatibility floor (see the
	// doc comment: this game is web-played, not played by our token).
	if !p.Unlimited {
		if !ValidClockLimit(p.LimitSeconds) {
			return OpenChallengeResult{}, fmt.Errorf("lichess: clock.limit %d is not a legal value", p.LimitSeconds)
		}
		if p.IncrementSeconds < 0 || p.IncrementSeconds > 60 {
			return OpenChallengeResult{}, fmt.Errorf("lichess: clock.increment %d is outside 0..60", p.IncrementSeconds)
		}
	}

	form := url.Values{}
	// Omitting both clock fields asks for a correspondence (unlimited) game, the
	// same rule as a challenge. Colour is NOT a request field on an open challenge —
	// it is carried by which of URLWhite/URLBlack the creator hands out.
	if !p.Unlimited {
		form.Set("clock.limit", fmt.Sprint(p.LimitSeconds))
		form.Set("clock.increment", fmt.Sprint(p.IncrementSeconds))
	}
	form.Set("rated", fmt.Sprint(p.Rated))

	// "" token → anonymous (no Authorization header). See the doc comment: a board:play
	// token here is refused for want of challenge:write.
	body, err := postBody(ctx, "", "/api/challenge/open", form)
	if err != nil {
		return OpenChallengeResult{}, err
	}
	var out OpenChallengeResult
	if err := json.Unmarshal(body, &out); err != nil {
		return OpenChallengeResult{}, fmt.Errorf("lichess: decode open challenge: %w", err)
	}
	if out.ID == "" || out.URL == "" {
		return OpenChallengeResult{}, fmt.Errorf("lichess: open challenge response carried no link")
	}
	return out, nil
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
// default table is Blitz 3+0. It also dodges the seek's 5/min-per-IP cap, which
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

// errChallengeDone stops the keep-alive reader once lichess has answered. A
// sentinel rather than a real failure: reaching it is the SUCCESS path.
var errChallengeDone = errors.New("challenge resolved")

// Challenge outcomes, verbatim from lila's ChallengeKeepAliveStream.
const (
	ChallengeAccepted = "accepted"
	ChallengeDeclined = "declined"
	ChallengeCanceled = "canceled"
)

// ChallengeKeepAlive challenges a username and HOLDS THE CHALLENGE ALIVE until
// the opponent answers, calling onOpen with the challenge as soon as lichess
// mints it. Returns the outcome: "accepted", "declined" or "canceled".
//
// # Why this exists next to ChallengeUserByName
//
// The buffered call is right for the PAIRED flow, where gamchess holds both
// tokens and accepts in well under a second. It is useless for challenging a
// REAL PERSON: a real-time challenge is swept 20 seconds after it was last seen,
// and no human reads a notification and clicks Accept that fast.
//
// keepAliveStream is lichess's answer. Read from lila (not the docs) on
// 2026-07-16, because the two disagree and the difference is load-bearing:
//
//   - The stream does NOT hold anything open by magic. lila schedules
//     `api.ping(challenge.id)` every 15s for as long as we read it, and ping is
//     `repo.setSeen` — it just keeps bumping seenAt ahead of the sweeper.
//   - lila emits {"done": "accepted"|"declined"|"canceled"} and closes. Those
//     three strings are the whole vocabulary.
//   - CLOSING THE STREAM DOES NOT CANCEL THE CHALLENGE. The OpenAPI description
//     ("Challenge is kept alive until the connection is closed") reads like it
//     does. It doesn't: ChallengeKeepAliveStream's completion handler only
//     cancels the ping timer and unsubscribes. The challenge is then swept to
//     Status.Offline — which lingers for THREE HOURS and is still acceptable
//     (a later ping calls setSeenAgain and revives it outright).
//
// That last point is why relay.Cancel POSTs an explicit /cancel and does not
// merely hang up. Hanging up alone would leave a challenge a stranger could
// accept hours after the player stood up and walked away, starting a real game
// on their account at a board nobody is sitting at — which is exactly the harm
// the two-intent rule exists to prevent, self-inflicted.
func ChallengeKeepAlive(ctx context.Context, token, username string, p ChallengeParams, onOpen func(ChallengeResult)) (string, error) {
	if err := p.validate(); err != nil {
		return "", err
	}

	form := url.Values{}
	// Omitting BOTH clock fields is what asks for an unlimited game — see
	// ChallengeUserByName.
	if !p.Unlimited {
		form.Set("clock.limit", fmt.Sprint(p.LimitSeconds))
		form.Set("clock.increment", fmt.Sprint(p.IncrementSeconds))
	}
	form.Set("rated", fmt.Sprint(p.Rated))
	if p.Color != "" {
		form.Set("color", p.Color)
	}
	form.Set("keepAliveStream", "true")

	u := apiBase + "/api/challenge/" + url.PathEscape(username)

	var done string
	err := streamReq(ctx, token, http.MethodPost, u, form, func(line []byte) error {
		// Two shapes on one stream: the challenge itself, then the verdict.
		var msg struct {
			Done   string `json:"done"`
			ID     string `json:"id"`
			Status string `json:"status"`
			URL    string `json:"url"`
		}
		if err := json.Unmarshal(line, &msg); err != nil {
			return nil
		}
		if msg.Done != "" {
			done = msg.Done
			return errChallengeDone
		}
		if msg.ID != "" && onOpen != nil {
			onOpen(ChallengeResult{ID: msg.ID, Status: msg.Status, URL: msg.URL})
		}
		return nil
	})

	if errors.Is(err, errChallengeDone) {
		return done, nil
	}
	if err != nil {
		return "", err
	}
	// lichess closed the stream without a verdict. Not an error we can name, and
	// guessing "accepted" would start streaming a game that may not exist.
	if done == "" {
		return "", fmt.Errorf("lichess closed the challenge without answering")
	}
	return done, nil
}

// ValidUsername reports whether s could be a lichess username, so a typo costs
// no request. lila's own rule: 2-30 chars of letters, digits, underscore and
// hyphen, starting with a letter or digit.
//
// A GATE, not decoration: the value is typed by a player and becomes a URL path
// segment. url.PathEscape already stops it forging a path, so this is about
// spending our shared per-IP challenge budget on something that cannot work.
func ValidUsername(s string) bool {
	if len(s) < 2 || len(s) > 30 {
		return false
	}
	for i, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9':
		case (r == '_' || r == '-') && i > 0:
		default:
			return false
		}
	}
	return true
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
// direct challenge is Gambit's primary path: the default table is Blitz 3+0,
// which estimates at 180 — fine to challenge with, refused as a seek.
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

	// RatingRange narrows the opponent pool, e.g. "1500-1800" — ABSOLUTE ratings,
	// never a delta, both ends within 400-2900, min < max strictly. An invalid
	// string is a 400, not a silent default.
	//
	// Leaving it empty is not "pair me with anyone" and not laziness: for a
	// real-time hook lila discards a default range and centres a Gaussian band on
	// the seeker's REAL rating (Hook.ratingRangeOrDefault -> RatingRange.defaultFor).
	// It knows their rating; we don't. So empty is the STRONGEST value we can send,
	// and Gambit always sends it. See CLAUDE.md's ratingRange trap before setting
	// this to anything.
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
	// A blank token means an ANONYMOUS request — no Authorization header at all, the same
	// contract the stream client documents. It matters for /api/challenge/open, which is
	// security:[] (needs no auth) but scope-checks any token you DO present and requires
	// challenge:write — a scope Gambit never holds. Sending an empty bearer would 401; not
	// sending one is what lets that endpoint through. Our User-Agent still identifies us
	// via the RoundTripper.
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
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
