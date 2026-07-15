package relay

import (
	"sync"
	"testing"
	"time"
)

const steamA, steamB int64 = 76561197960287930, 76561197960287931

// atTime pins the store's clock so expiry is tested without sleeping.
func atTime(s *Store, t time.Time) { s.now = func() time.Time { return t } }

func TestStateRoundTrip(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	s.Begin("state-abc", steamA)

	got, ok := s.ResolveState("state-abc")
	if !ok || got != steamA {
		t.Fatalf("want (%d,true), got (%d,%v)", steamA, got, ok)
	}
}

func TestStateIsSingleUse(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	s.Begin("state-abc", steamA)

	if _, ok := s.ResolveState("state-abc"); !ok {
		t.Fatal("first resolve should succeed")
	}
	// A replayed state must not resolve — otherwise a leaked callback URL could
	// be fired twice.
	if _, ok := s.ResolveState("state-abc"); ok {
		t.Fatal("state must not resolve twice")
	}
}

func TestUnknownStateDoesNotResolve(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	if _, ok := s.ResolveState("never-registered"); ok {
		t.Fatal("unknown state must not resolve")
	}
}

func TestExpiredStateDoesNotResolve(t *testing.T) {
	base := time.Now()
	s := New(5*time.Minute, 2*time.Minute)
	atTime(s, base)
	s.Begin("state-abc", steamA)

	atTime(s, base.Add(5*time.Minute+time.Second))
	if _, ok := s.ResolveState("state-abc"); ok {
		t.Fatal("expired state must not resolve")
	}
}

// An expired state must still be consumed, so it can't resolve later even if the
// clock is somehow read differently on a subsequent call.
func TestExpiredStateIsStillConsumed(t *testing.T) {
	base := time.Now()
	s := New(5*time.Minute, 2*time.Minute)
	atTime(s, base)
	s.Begin("state-abc", steamA)

	atTime(s, base.Add(time.Hour))
	s.ResolveState("state-abc")

	atTime(s, base)
	if _, ok := s.ResolveState("state-abc"); ok {
		t.Fatal("a consumed state must stay gone")
	}
}

func TestCodeRoundTripIsSingleUse(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	s.StashCode(steamA, "oauth-code-1")

	got, ok := s.TakeCode(steamA)
	if !ok || got != "oauth-code-1" {
		t.Fatalf("want (oauth-code-1,true), got (%q,%v)", got, ok)
	}
	if _, ok := s.TakeCode(steamA); ok {
		t.Fatal("code must be claimable only once")
	}
}

func TestNoCodeYields404Path(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	if _, ok := s.TakeCode(steamA); ok {
		t.Fatal("no code stashed — must not return one")
	}
}

func TestExpiredCodeIsNotReturned(t *testing.T) {
	base := time.Now()
	s := New(5*time.Minute, 2*time.Minute)
	atTime(s, base)
	s.StashCode(steamA, "oauth-code-1")

	atTime(s, base.Add(2*time.Minute+time.Second))
	if _, ok := s.TakeCode(steamA); ok {
		t.Fatal("expired code must not be returned")
	}
}

// The core isolation property: one player's code is never visible to another.
func TestCodesAreIsolatedPerSteamID(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	s.StashCode(steamA, "code-for-A")

	if _, ok := s.TakeCode(steamB); ok {
		t.Fatal("steamB must not receive steamA's code")
	}
	got, ok := s.TakeCode(steamA)
	if !ok || got != "code-for-A" {
		t.Fatalf("steamA's code went missing: (%q,%v)", got, ok)
	}
}

func TestLastCodeWins(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	s.StashCode(steamA, "stale")
	s.StashCode(steamA, "fresh")

	if got, _ := s.TakeCode(steamA); got != "fresh" {
		t.Fatalf("want fresh, got %q", got)
	}
}

// Begin sweeps, so an abandoned sign-in can't pile up forever.
func TestBeginSweepsExpiredEntries(t *testing.T) {
	base := time.Now()
	s := New(5*time.Minute, 2*time.Minute)
	atTime(s, base)
	s.Begin("abandoned", steamA)
	s.StashCode(steamB, "abandoned-code")

	atTime(s, base.Add(time.Hour))
	s.Begin("fresh", steamA)

	s.mu.Lock()
	nStates, nCodes := len(s.states), len(s.codes)
	s.mu.Unlock()
	if nStates != 1 {
		t.Fatalf("want only the fresh state left, got %d", nStates)
	}
	if nCodes != 0 {
		t.Fatalf("want expired code swept, got %d", nCodes)
	}
}

// -race guard: the callback (browser) and the poll (game client) hit the store
// concurrently in real use.
func TestConcurrentAccessIsRaceFree(t *testing.T) {
	s := New(5*time.Minute, 2*time.Minute)
	var wg sync.WaitGroup
	for i := 0; i < 50; i++ {
		wg.Add(3)
		go func() { defer wg.Done(); s.Begin("state-x", steamA) }()
		go func() { defer wg.Done(); s.ResolveState("state-x") }()
		go func() { defer wg.Done(); s.StashCode(steamA, "c"); s.TakeCode(steamA) }()
	}
	wg.Wait()
}
