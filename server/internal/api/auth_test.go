package api

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"

	"go.uber.org/zap"
)

const testSteamID = "76561197960287930"

// stubVerifier points the api package's Facepunch boundary at fn for one test.
func stubVerifier(t *testing.T, fn func(ctx context.Context, steamID64, token string) (bool, error)) {
	t.Helper()
	prev := validateToken
	validateToken = fn
	t.Cleanup(func() { validateToken = prev })
}

// okVerifier stubs the Facepunch boundary as "testSteamID + the token "good" is
// genuine, nothing else is" — the happy path every handler test needs before it
// can reach the logic it actually cares about.
func okVerifier(t *testing.T) {
	t.Helper()
	stubVerifier(t, func(_ context.Context, id, tok string) (bool, error) {
		return id == testSteamID && tok == "good", nil
	})
}

func testHandler() *handler {
	return &handler{log: zap.NewNop(), version: "test"}
}

// authedReq builds a request carrying the claimed SteamID + FP token headers.
func authedReq(steamID, token string) *http.Request {
	r := httptest.NewRequest(http.MethodGet, "/", nil)
	if steamID != "" {
		r.Header.Set(steamIDHeader, steamID)
	}
	if token != "" {
		r.Header.Set("Authorization", "Bearer "+token)
	}
	return r
}

func TestRequireSteam(t *testing.T) {
	t.Run("verified token yields the steamid", func(t *testing.T) {
		stubVerifier(t, func(_ context.Context, id, tok string) (bool, error) {
			return id == testSteamID && tok == "good", nil
		})
		w := httptest.NewRecorder()
		got, ok := testHandler().requireSteam(w, authedReq(testSteamID, "good"))
		if !ok || got != 76561197960287930 {
			t.Fatalf("want (76561197960287930,true), got (%d,%v)", got, ok)
		}
	})

	t.Run("rejected token 401s", func(t *testing.T) {
		stubVerifier(t, func(context.Context, string, string) (bool, error) { return false, nil })
		w := httptest.NewRecorder()
		if _, ok := testHandler().requireSteam(w, authedReq(testSteamID, "bad")); ok {
			t.Fatal("expected deny")
		}
		if w.Code != http.StatusUnauthorized {
			t.Fatalf("want 401, got %d", w.Code)
		}
	})

	// A Facepunch outage must deny, never grant.
	t.Run("verifier error fails closed", func(t *testing.T) {
		stubVerifier(t, func(context.Context, string, string) (bool, error) {
			return false, errors.New("facepunch unreachable")
		})
		w := httptest.NewRecorder()
		if _, ok := testHandler().requireSteam(w, authedReq(testSteamID, "good")); ok {
			t.Fatal("expected deny when the verifier errors")
		}
		if w.Code != http.StatusUnauthorized {
			t.Fatalf("want 401, got %d", w.Code)
		}
	})

	// An out-of-int64-range SteamID passes the 1–20 digit shape check but must
	// not slip through as a bogus identity.
	t.Run("out-of-range steamid 401s", func(t *testing.T) {
		stubVerifier(t, func(context.Context, string, string) (bool, error) { return true, nil })
		w := httptest.NewRecorder()
		if _, ok := testHandler().requireSteam(w, authedReq("99999999999999999999", "good")); ok {
			t.Fatal("expected deny for a steamid that overflows int64")
		}
		if w.Code != http.StatusUnauthorized {
			t.Fatalf("want 401, got %d", w.Code)
		}
	})

	// The SteamID header alone must authorise nothing: no token, no identity.
	t.Run("missing token 401s without calling the verifier", func(t *testing.T) {
		called := false
		stubVerifier(t, func(context.Context, string, string) (bool, error) {
			called = true
			return true, nil
		})
		w := httptest.NewRecorder()
		if _, ok := testHandler().requireSteam(w, authedReq(testSteamID, "")); ok {
			t.Fatal("expected deny with no token")
		}
		if called {
			t.Fatal("verifier must not be consulted without a token")
		}
		if w.Code != http.StatusUnauthorized {
			t.Fatalf("want 401, got %d", w.Code)
		}
	})

	t.Run("malformed steamid 401s without calling the verifier", func(t *testing.T) {
		called := false
		stubVerifier(t, func(context.Context, string, string) (bool, error) {
			called = true
			return true, nil
		})
		for _, id := range []string{"", "notanumber", "7656119796028793x", "-1", "765611979602879301234567890"} {
			w := httptest.NewRecorder()
			if _, ok := testHandler().requireSteam(w, authedReq(id, "good")); ok {
				t.Fatalf("expected deny for steamid %q", id)
			}
			if w.Code != http.StatusUnauthorized {
				t.Fatalf("steamid %q: want 401, got %d", id, w.Code)
			}
		}
		if called {
			t.Fatal("verifier must not be consulted for a malformed steamid")
		}
	})
}

func TestValidSteamID(t *testing.T) {
	valid := []string{"76561197960287930", "1", "12345678901234567890"}
	invalid := []string{
		"", " ", "abc", "-1", "1.0", "1e5", "123456789012345678901", "765611979 60287930",
		// 0 is the client's "empty seat" sentinel, never a player.
		"0",
		// A leading zero would let one account arrive in two spellings.
		"076561197960287930", "00",
	}

	for _, s := range valid {
		if !validSteamID(s) {
			t.Errorf("validSteamID(%q) = false, want true", s)
		}
	}
	for _, s := range invalid {
		if validSteamID(s) {
			t.Errorf("validSteamID(%q) = true, want false", s)
		}
	}
}
