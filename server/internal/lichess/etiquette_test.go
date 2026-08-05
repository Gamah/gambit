package lichess

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// These tests exist because gamchess's lichess traffic all leaves one IP under
// one User-Agent, so misbehaving here doesn't throttle one caller — it stops the
// TV wall for every lobby and burns the goodwill needed to ever ask for more
// headroom. This is the file to point at if lichess ever asks how we behave.
//
// Since HTTPFIX the surface is small: an anonymous TV stream, an anonymous
// game/export, and ONE authed call per link (Account). The etiquette rules that
// used to be exercised through the Board API are exercised through those. The
// seek self-limit moved to the client along with the seeks themselves — its
// tests live in the dotnet harness now, not here.

// stubAPI points apiBase at a test server for the duration of the test.
func stubAPI(t *testing.T, h http.HandlerFunc) {
	t.Helper()
	srv := httptest.NewServer(h)
	prev := apiBase
	prevAccount := accountEndpoint
	apiBase = srv.URL
	accountEndpoint = srv.URL + "/api/account"
	t.Cleanup(func() {
		apiBase = prev
		accountEndpoint = prevAccount
		srv.Close()
	})
}

// lichess records a userAgent per access token (AccessToken.scala), so this
// string is how they can attribute, throttle or contact us. A generic or absent
// one makes Gambit invisible in their logs — exactly the wrong thing if we're
// asking for an allowance.
func TestUserAgentIsSentOnEveryRequest(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	var seen []string
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		seen = append(seen, r.Header.Get("User-Agent"))
		io.WriteString(w, `{"id":"terry","username":"Terry"}`)
	})

	// A buffered authed call, a buffered anonymous one, and a stream. All three
	// must identify us.
	if _, _, err := Account(context.Background(), "tok"); err != nil {
		t.Fatal(err)
	}
	if _, err := GameResult(context.Background(), "g4me"); err != nil {
		t.Fatal(err)
	}
	if err := StreamTv(context.Background(), ChannelBest, func(TvEvent) {}); err != nil {
		t.Fatal(err)
	}

	if len(seen) != 3 {
		t.Fatalf("expected 3 requests, saw %d", len(seen))
	}
	for i, ua := range seen {
		if ua != UserAgent {
			t.Errorf("request %d sent User-Agent %q, want %q", i, ua, UserAgent)
		}
	}
}

// A User-Agent that doesn't say who we are or how to reach us is no better than
// none, for the one purpose it has.
func TestUserAgentIdentifiesUs(t *testing.T) {
	for _, want := range []string{"TerrysGambit", "chess.gamah.net", "contact:"} {
		if !strings.Contains(UserAgent, want) {
			t.Errorf("User-Agent %q should contain %q", UserAgent, want)
		}
	}
}

// lichess: "If you receive an HTTP response with a 429 status, please wait a
// full minute before resuming API usage." A 429 is per-IP, so it must stop
// EVERYTHING, not just the call that earned it.
func TestA429StopsAllOutboundCalls(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	var calls int
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.WriteHeader(http.StatusTooManyRequests)
	})

	// One 429 on any endpoint...
	if _, _, err := Account(context.Background(), "tok"); err == nil {
		t.Fatal("expected an error on 429")
	}
	if calls != 1 {
		t.Fatalf("expected 1 call, got %d", calls)
	}

	// ...and every other endpoint stops sending, rather than retrying into a ban.
	if _, _, err := Account(context.Background(), "tok"); !errors.Is(err, ErrBackingOff) {
		t.Fatalf("Account should back off after a 429, got %v", err)
	}
	if _, err := GameResult(context.Background(), "g4me"); !errors.Is(err, ErrBackingOff) {
		t.Fatalf("GameResult should back off after a 429, got %v", err)
	}
	if err := StreamTv(context.Background(), ChannelBest, func(TvEvent) {}); !errors.Is(err, ErrBackingOff) {
		t.Fatalf("streams should back off after a 429, got %v", err)
	}
	if calls != 1 {
		t.Fatalf("nothing more should have been sent; lichess saw %d calls", calls)
	}

	// And it really is the full minute lichess asks for.
	if got := Backoff(); got < 55*time.Second || got > backoffAfter429 {
		t.Fatalf("backoff is %v, want ~%v", got, backoffAfter429)
	}
}

func TestBackoffClears(t *testing.T) {
	ResetGovernor()
	gov.note429()
	if Backoff() <= 0 {
		t.Fatal("expected a backoff")
	}
	ResetGovernor()
	if Backoff() > 0 {
		t.Fatal("ResetGovernor should clear the backoff")
	}
}

// The token transits gamchess exactly once, for Account, and must never leave a
// trace. This is the promise the custody note in link.go makes; a failure path
// that formats the request into an error is the realistic way it would break.
func TestAccountErrorsNeverCarryTheToken(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	const token = "lio_supersecrettokenvalue"
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusUnauthorized)
		// lichess's own body, echoed back — a plausible place for a leak if it
		// were ever included in the error.
		io.WriteString(w, `{"error":"No such token: `+token+`"}`)
	})

	_, _, err := Account(context.Background(), token)
	if err == nil {
		t.Fatal("expected an error")
	}
	if strings.Contains(err.Error(), token) {
		t.Fatalf("the error carries the token: %q", err)
	}
}
