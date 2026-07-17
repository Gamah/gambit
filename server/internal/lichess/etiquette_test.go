package lichess

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

// These tests exist because Gambit's entire relay runs from ONE IP, so every
// player shares one set of lichess's per-IP limits. Misbehaving here doesn't
// throttle one user — it breaks the feature for everyone and burns the goodwill
// needed to ever ask for more headroom. This is the file to point at if lichess
// ever asks how we behave.

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
		io.WriteString(w, `{"id":"terry","username":"Terry","ok":true}`)
	})

	// A buffered call, and a stream. Both must identify us.
	if _, _, err := Account(context.Background(), "tok"); err != nil {
		t.Fatal(err)
	}
	if err := Resign(context.Background(), "tok", "g4me"); err != nil {
		t.Fatal(err)
	}
	if err := StreamEvents(context.Background(), "tok", func(Event) {}); err != nil {
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
// full minute before resuming API usage." A 429 is per-IP, and we are one IP —
// so it must stop EVERYTHING, not just the call that earned it.
func TestA429StopsAllOutboundCalls(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	var calls int
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.WriteHeader(http.StatusTooManyRequests)
	})

	// One 429 on any endpoint...
	if err := Resign(context.Background(), "tok", "g4me"); err == nil {
		t.Fatal("expected an error on 429")
	}
	if calls != 1 {
		t.Fatalf("expected 1 call, got %d", calls)
	}

	// ...and every other endpoint stops sending, rather than retrying into a ban.
	if _, _, err := Account(context.Background(), "tok"); !errors.Is(err, ErrBackingOff) {
		t.Fatalf("Account should back off after a 429, got %v", err)
	}
	if err := Move(context.Background(), "tok", "g4me", "e2e4", false); !errors.Is(err, ErrBackingOff) {
		t.Fatalf("Move should back off after a 429, got %v", err)
	}
	if err := StreamEvents(context.Background(), "tok", func(Event) {}); !errors.Is(err, ErrBackingOff) {
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

// The lobby's 5-per-minute cap is per IP, i.e. per PLAYERBASE. We self-limit so
// a player clicking "find a game" repeatedly can't spend everyone's budget and
// earn a 429 for the whole server.
func TestSeekBudgetIsSelfLimited(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	for i := 0; i < SeeksPerMinute; i++ {
		if err := TakeSeekSlot(); err != nil {
			t.Fatalf("seek %d of %d should be allowed, got %v", i+1, SeeksPerMinute, err)
		}
	}

	err := TakeSeekSlot()
	if !errors.Is(err, ErrSeekBudget) {
		t.Fatalf("seek %d must be refused locally, got %v", SeeksPerMinute+1, err)
	}
	// The refusal has to be legible — a player deserves to know it's a shared
	// limit and roughly how long to wait, not just "failed".
	if !strings.Contains(err.Error(), "a minute") && !strings.Contains(err.Error(), "to wait") {
		t.Fatalf("the refusal should say how long to wait: %q", err)
	}
}

func TestSeekBudgetRefusesBeforeSending(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	var sent int
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) { sent++ })

	// Spend the budget without touching the network.
	for i := 0; i < SeeksPerMinute; i++ {
		if err := TakeSeekSlot(); err != nil {
			t.Fatal(err)
		}
	}

	// The next real seek must not reach lichess at all.
	err := SeekRealtime(context.Background(), "tok", SeekParams{TimeMinutes: 10}, nil)
	if !errors.Is(err, ErrSeekBudget) {
		t.Fatalf("want ErrSeekBudget, got %v", err)
	}
	if sent != 0 {
		t.Fatalf("an over-budget seek must not be sent; lichess saw %d", sent)
	}
}

// The window slides: spending the budget must not lock seeking out forever.
func TestSeekBudgetWindowSlides(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	for i := 0; i < SeeksPerMinute; i++ {
		if err := TakeSeekSlot(); err != nil {
			t.Fatal(err)
		}
	}
	if err := TakeSeekSlot(); err == nil {
		t.Fatal("expected the budget to be spent")
	}

	// Age every recorded seek out of the window.
	gov.mu.Lock()
	for i := range gov.seeks {
		gov.seeks[i] = gov.seeks[i].Add(-2 * time.Minute)
	}
	gov.mu.Unlock()

	if err := TakeSeekSlot(); err != nil {
		t.Fatalf("the window should have slid open, got %v", err)
	}
}

// Our self-limit must mirror lila's, or it is either useless or needlessly
// stingy. [SOURCE] Limiters.setupPost = RateLimit[IpAddress](5, 1.minute).
func TestSeekBudgetMatchesLila(t *testing.T) {
	if SeeksPerMinute != 5 {
		t.Fatalf("SeeksPerMinute is %d; lila's setupPost allows 5/min/IP", SeeksPerMinute)
	}
}
