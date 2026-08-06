package lichess

import (
	"context"
	"errors"
	"net/http"
	"sync"
	"time"
)

// Being a good lichess API citizen.
//
// gamchess still talks to lichess for ONE thing — TV (an anonymous feed per
// channel, plus a game/export per game end) — and that traffic all leaves from
// one IP under one User-Agent. Since HTTPFIX it is no longer every player's game
// traffic as well: each client now spends its own IP on its own games, and the
// client carries its own port of these rules (Code/Api/Lichess).
//
// What did NOT change is the obligation. We are still attributable, still
// bounded by lichess's per-IP limits, and still one 429 away from being asked to
// slow down on behalf of every wall in every lobby.
//
// lichess's published rules (lichess.org/page/api-tips and the API spec intro,
// read 2026-07-15) are short and we follow all of them:
//
//   - "Only make one request at a time."
//   - "If you receive an HTTP response with a 429 status, please wait a full
//     minute before resuming API usage." / "Reduce your request frequency."
//   - Don't poll endpoints meant to be streamed.
//
// There is no published User-Agent rule, but lichess records a userAgent per
// access token (AccessToken.scala), so a descriptive one is how they can see who
// we are — which is exactly what a conversation about limits needs. Ours names
// the project and a contact.

// UserAgent identifies Gambit to lichess on every request, including streams.
//
// Deliberately specific: a real name, a URL, and a contact. If lichess ever needs
// to attribute traffic, throttle us, or reach us, this is the string that lets
// them do it instead of guessing. Do not make it generic, and do not put a
// version-only string here.
const UserAgent = "TerrysGambit/1.0 (+https://chess.gamah.net; chess in s&box; contact: anthropic@gamah.net)"

// Backoff after a 429. lichess says "wait a full minute"; we take that literally
// and apply it to EVERY outbound call, not just the one that got limited —
// their limits are per-IP, so a 429 anywhere means this box is going too fast.
const backoffAfter429 = 60 * time.Second

// ErrBackingOff means we are inside the post-429 minute and refused to send.
// Callers surface it as "lichess is busy, try again shortly" — never as a retry
// loop, which is how a throttle becomes a ban.
var ErrBackingOff = errors.New("lichess: backing off after a rate limit — try again in a minute")

// governor enforces the etiquette above across the whole process.
type governor struct {
	mu sync.Mutex

	// until is when the post-429 backoff expires.
	until time.Time
}

var gov = &governor{}

// check reports whether we may send at all.
func (g *governor) check() error {
	g.mu.Lock()
	defer g.mu.Unlock()
	if time.Now().Before(g.until) {
		return ErrBackingOff
	}
	return nil
}

// note429 starts the backoff.
func (g *governor) note429() {
	g.mu.Lock()
	g.until = time.Now().Add(backoffAfter429)
	g.mu.Unlock()
}

// Backoff reports how long until we'll talk to lichess again, or 0.
func Backoff() time.Duration {
	gov.mu.Lock()
	defer gov.mu.Unlock()
	return time.Until(gov.until)
}

// ResetGovernor clears the backoff. Tests only.
func ResetGovernor() {
	gov.mu.Lock()
	gov.until = time.Time{}
	gov.mu.Unlock()
}

// agentTransport stamps the User-Agent on every outbound request and watches for
// 429s.
//
// A RoundTripper rather than a header set at each call site, deliberately: it is
// the only way to be sure EVERY path is covered — buffered calls, streams, and
// anything added later. A call site that forgets is a call site lichess can't
// attribute. (The s&box client has no RoundTripper; it replaces this guarantee
// with a single seam every request is built through. Same property, hand-held.)
type agentTransport struct{ base http.RoundTripper }

func (t *agentTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	req.Header.Set("User-Agent", UserAgent)

	base := t.base
	if base == nil {
		base = http.DefaultTransport
	}
	resp, err := base.RoundTrip(req)

	// A 429 anywhere means we are collectively over a shared limit; back
	// everything off, not just this caller.
	if err == nil && resp != nil && resp.StatusCode == http.StatusTooManyRequests {
		gov.note429()
	}
	return resp, err
}

func init() {
	// Both clients get the same treatment. streamClient has no timeout (a stream
	// must outlive one), but it still identifies itself and still honours 429s.
	client.Transport = &agentTransport{}
	streamClient.Transport = &agentTransport{}
}

// guard is the pre-flight every outbound call makes.
func guard(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	return gov.check()
}
