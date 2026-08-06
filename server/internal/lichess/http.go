package lichess

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// The shared HTTP plumbing every lichess call in this package rides on.
//
// # Why this file exists on its own
//
// It was carved out of oauth.go and board.go when the lichess TOKEN moved to the
// client (HTTPFIX). Both of those files were deleted; tv.go was not, and tv.go
// depended on helpers that lived in both of them. Relocating them here first, as
// a pure move, is what let the teardown be a deletion rather than a rewrite.
//
// # THE TEMPTING FIX THAT MUST NOT BE MADE
//
// If something here ever fails to compile, the fix is NOT to drop a fresh
// `&http.Client{}` into the calling file. That compiles, TV appears to work, and
// it silently:
//
//   - strips the User-Agent off every TV request, breaking the one obligation
//     lichess actually asks of us (they record a userAgent per token and it is
//     how they can attribute or reach us), and
//   - stops TV's 429s from arming the process-wide governor, so a rate limit on
//     the TV path no longer backs the rest of us off.
//
// Both clients below get their Transport from etiquette.go's init(). That is the
// only place a User-Agent is guaranteed, and going around it is going around the
// etiquette.

// apiBase is a VAR, not a const: tv_test.go repoints it at an httptest server.
// Production never reassigns it.
var apiBase = "https://lichess.org"

// client bounds every buffered call. The streams deliberately do NOT use it — a
// client timeout applies to the whole request including the body read, so it
// would kill a healthy stream mid-feed.
var client = &http.Client{Timeout: 10 * time.Second}

// streamClient has NO Timeout, for the reason above. Cancellation is the caller's
// context (channel dropped, shutdown).
var streamClient = &http.Client{}

// maxBody caps what we'll read from lichess on a buffered call. Their JSON bodies
// are a few KB; this is slack, not a budget.
const maxBody = 1 << 20

// maxStreamLine bounds one ndjson line. This is slack too.
const maxStreamLine = 1 << 20

// stream is the shared ndjson reader.
//
// A BLANK token means an anonymous stream and sends no Authorization header at
// all. That is not a convenience: /api/tv/{channel}/feed is `security: []`
// upstream, and attaching a token to a request that does not need it would hand
// a credential to an endpoint that never asked for it, on a stream held open for
// hours. TV must stay anonymous — tv_test.go's TestStreamTvSendsNoAuthorization
// is the standing proof, and it must keep passing.
func stream(ctx context.Context, token, u string, onLine func([]byte) error) error {
	return streamReq(ctx, token, http.MethodGet, u, nil, onLine)
}

// streamReq is stream() with a method and an optional form body.
//
// A non-2xx returns an *APIError carrying lichess's own body, not a bare status:
// their error text says useful things that a status alone would throw away.
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
	if ctxErr := ctx.Err(); ctxErr != nil {
		return ctxErr
	}
	return nil
}

// APIError carries a non-2xx from lichess, status included so callers can tell a
// dead token (401) from a rate limit (429).
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

// Unauthorized reports a dead/revoked token.
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
