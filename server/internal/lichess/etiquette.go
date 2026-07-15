package lichess

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"sync"
	"time"
)

// Being a good lichess API citizen.
//
// This file exists because Gambit's whole relay runs from ONE IP, so every
// Gambit player shares one set of lichess's per-IP limits. Misbehaving wouldn't
// just get one user throttled — it would break the feature for everyone and burn
// the goodwill needed to ever ask for more headroom.
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
// their limits are per-IP and we are one IP, so a 429 anywhere means we are
// collectively going too fast.
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

	// seeks records recent real-time seek attempts, because lichess's lobby limit
	// (5 per minute per IP — lila Limiters.setupPost) is the one Gambit is most
	// likely to hit: it is shared across every player, and a seek is a thing a
	// bored player will click repeatedly. Better to refuse locally with a real
	// explanation than to spend the shared budget on a 429.
	seeks []time.Time
}

var gov = &governor{}

// SeeksPerMinute mirrors lila's Limiters.setupPost — RateLimit[IpAddress](5,
// 1.minute). Per IP, so this is 5 per minute for every Gambit player COMBINED.
//
// [SOURCE] lila master, 2026-07-15. Re-check before relying on it.
const SeeksPerMinute = 5

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

// takeSeek reserves one of the shared per-minute seek slots, or reports how long
// to wait. Best-effort: it tracks what WE sent, and other clients on this IP
// (there are none today) would not be counted.
func (g *governor) takeSeek() (wait time.Duration, ok bool) {
	g.mu.Lock()
	defer g.mu.Unlock()

	now := time.Now()
	cutoff := now.Add(-time.Minute)
	kept := g.seeks[:0]
	for _, t := range g.seeks {
		if t.After(cutoff) {
			kept = append(kept, t)
		}
	}
	g.seeks = kept

	if len(g.seeks) >= SeeksPerMinute {
		// Oldest one leaves the window at oldest+1m.
		return time.Until(g.seeks[0].Add(time.Minute)), false
	}
	g.seeks = append(g.seeks, now)
	return 0, true
}

// Backoff reports how long until we'll talk to lichess again, or 0.
func Backoff() time.Duration {
	gov.mu.Lock()
	defer gov.mu.Unlock()
	return time.Until(gov.until)
}

// ResetGovernor clears the backoff and the seek window. Tests only.
func ResetGovernor() {
	gov.mu.Lock()
	gov.until = time.Time{}
	gov.seeks = nil
	gov.mu.Unlock()
}

// agentTransport stamps the User-Agent on every outbound request and watches for
// 429s.
//
// A RoundTripper rather than a header set at each call site, deliberately: it is
// the only way to be sure EVERY path is covered — buffered calls, both streams,
// and anything added later. A call site that forgets is a call site lichess can't
// attribute.
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

// TakeSeekSlot reserves one of the shared 5-per-minute lobby seek slots.
//
// Callers MUST take a slot before seeking, and must report the wait to the
// player rather than retrying. This is the limit Gambit is most likely to hit,
// and it is shared by the entire playerbase.
func TakeSeekSlot() error {
	if wait, ok := gov.takeSeek(); !ok {
		return fmt.Errorf("%w: lichess allows about %d lobby seeks a minute across all of Terry's Gambit; about %ds to wait",
			ErrSeekBudget, SeeksPerMinute, int(wait.Seconds())+1)
	}
	return nil
}

// ErrSeekBudget means the shared per-minute seek budget is spent.
var ErrSeekBudget = errors.New("lichess: the shared seek budget is spent")
