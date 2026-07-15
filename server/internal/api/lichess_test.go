package api

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gamah/gambit/server/internal/relay"
	"go.uber.org/zap"
)

const goodState = "Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MEFCQ0RFRg" // 38 chars of base64url

// relayHandler builds a handler with the relay enabled and no DB — the relay
// path must never touch Postgres, and a nil pool here proves it.
func relayHandler() *handler {
	return &handler{
		log:     zap.NewNop(),
		version: "test",
		baseURL: "https://chess.gamah.net",
		relay:   relay.New(5*time.Minute, 2*time.Minute),
	}
}

func okVerifier(t *testing.T) {
	stubVerifier(t, func(_ context.Context, id, tok string) (bool, error) {
		return tok == "good", nil
	})
}

func beginReq(state string) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "/api/v1/auth/lichess/begin",
		strings.NewReader(`{"state":"`+state+`"}`))
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")
	return r
}

func TestLichessBegin(t *testing.T) {
	t.Run("registers state and returns the redirect_uri", func(t *testing.T) {
		okVerifier(t)
		h := relayHandler()
		w := httptest.NewRecorder()
		h.lichessBegin(w, beginReq(goodState))

		if w.Code != http.StatusOK {
			t.Fatalf("want 200, got %d (%s)", w.Code, w.Body)
		}
		var body map[string]string
		json.NewDecoder(w.Body).Decode(&body)
		if body["redirect_uri"] != "https://chess.gamah.net/callback" {
			t.Fatalf("bad redirect_uri: %q", body["redirect_uri"])
		}
		if id, ok := h.relay.ResolveState(goodState); !ok || id != 76561197960287930 {
			t.Fatalf("state not registered to the caller: (%d,%v)", id, ok)
		}
	})

	t.Run("unauthed callers cannot register a state", func(t *testing.T) {
		stubVerifier(t, func(context.Context, string, string) (bool, error) { return false, nil })
		h := relayHandler()
		w := httptest.NewRecorder()
		h.lichessBegin(w, beginReq(goodState))

		if w.Code != http.StatusUnauthorized {
			t.Fatalf("want 401, got %d", w.Code)
		}
		if _, ok := h.relay.ResolveState(goodState); ok {
			t.Fatal("a rejected caller must not register a state")
		}
	})

	// The entropy floor is a security control, not validation politeness: a
	// guessable state lets an attacker deliver their code to a victim's client.
	t.Run("low-entropy or malformed states are rejected", func(t *testing.T) {
		okVerifier(t)
		bad := []string{
			"",
			"short",
			"76561197960287930",                  // the SteamID itself — public, guessable
			strings.Repeat("a", 31),              // just under the floor
			strings.Repeat("a", 129),             // over the ceiling
			"has spaces in it aaaaaaaaaaaaaaaaa", // charset
			"has/slashes/aaaaaaaaaaaaaaaaaaaaaa",
		}
		for _, s := range bad {
			h := relayHandler()
			w := httptest.NewRecorder()
			h.lichessBegin(w, beginReq(s))
			if w.Code != http.StatusBadRequest {
				t.Errorf("state %q: want 400, got %d", s, w.Code)
			}
		}
	})

	t.Run("32 chars is accepted (the floor)", func(t *testing.T) {
		okVerifier(t)
		h := relayHandler()
		w := httptest.NewRecorder()
		h.lichessBegin(w, beginReq(strings.Repeat("a", 32)))
		if w.Code != http.StatusOK {
			t.Fatalf("want 200, got %d", w.Code)
		}
	})

	t.Run("malformed body is a 400", func(t *testing.T) {
		okVerifier(t)
		h := relayHandler()
		r := httptest.NewRequest(http.MethodPost, "/api/v1/auth/lichess/begin",
			strings.NewReader(`not json`))
		r.Header.Set(steamIDHeader, testSteamID)
		r.Header.Set("Authorization", "Bearer good")
		w := httptest.NewRecorder()
		h.lichessBegin(w, r)
		if w.Code != http.StatusBadRequest {
			t.Fatalf("want 400, got %d", w.Code)
		}
	})

	t.Run("disabled relay is a 501", func(t *testing.T) {
		okVerifier(t)
		h := relayHandler()
		h.baseURL = ""
		w := httptest.NewRecorder()
		h.lichessBegin(w, beginReq(goodState))
		if w.Code != http.StatusNotImplemented {
			t.Fatalf("want 501, got %d", w.Code)
		}
	})
}

func callbackReq(query string) *http.Request {
	return httptest.NewRequest(http.MethodGet, "/callback?"+query, nil)
}

func TestLichessCallback(t *testing.T) {
	t.Run("parks the code against the state's steamid", func(t *testing.T) {
		h := relayHandler()
		h.relay.Begin(goodState, 76561197960287930)

		w := httptest.NewRecorder()
		h.lichessCallback(w, callbackReq("code=oauth-code-1&state="+goodState))
		if w.Code != http.StatusOK {
			t.Fatalf("want 200, got %d", w.Code)
		}
		if got, ok := h.relay.TakeCode(76561197960287930); !ok || got != "oauth-code-1" {
			t.Fatalf("code not parked: (%q,%v)", got, ok)
		}
	})

	// THE acceptance criterion: the page must never carry the code.
	t.Run("never renders the code", func(t *testing.T) {
		h := relayHandler()
		h.relay.Begin(goodState, 76561197960287930)

		w := httptest.NewRecorder()
		h.lichessCallback(w, callbackReq("code=SUPERSECRETCODE&state="+goodState))

		if strings.Contains(w.Body.String(), "SUPERSECRETCODE") {
			t.Fatal("callback page leaked the OAuth code into the response body")
		}
		if strings.Contains(strings.Join(w.Header().Values("Location"), " "), "SUPERSECRETCODE") {
			t.Fatal("callback leaked the OAuth code into a redirect")
		}
	})

	t.Run("unknown state parks nothing", func(t *testing.T) {
		h := relayHandler()
		w := httptest.NewRecorder()
		h.lichessCallback(w, callbackReq("code=oauth-code-1&state="+goodState))
		if _, ok := h.relay.TakeCode(76561197960287930); ok {
			t.Fatal("a code arrived for a state nobody registered")
		}
	})

	// lichess reports declined consent this way; it must not park anything.
	t.Run("error param parks nothing", func(t *testing.T) {
		h := relayHandler()
		h.relay.Begin(goodState, 76561197960287930)
		w := httptest.NewRecorder()
		h.lichessCallback(w, callbackReq("error=access_denied&state="+goodState))
		if _, ok := h.relay.TakeCode(76561197960287930); ok {
			t.Fatal("parked a code despite an error response")
		}
	})

	t.Run("missing halves park nothing", func(t *testing.T) {
		for _, q := range []string{"code=x", "state=" + goodState, ""} {
			h := relayHandler()
			h.relay.Begin(goodState, 76561197960287930)
			w := httptest.NewRecorder()
			h.lichessCallback(w, callbackReq(q))
			if _, ok := h.relay.TakeCode(76561197960287930); ok {
				t.Fatalf("query %q parked a code", q)
			}
		}
	})

	// A replayed callback must not re-park: the state is consumed on first use.
	t.Run("replayed callback parks nothing the second time", func(t *testing.T) {
		h := relayHandler()
		h.relay.Begin(goodState, 76561197960287930)

		h.lichessCallback(httptest.NewRecorder(), callbackReq("code=first&state="+goodState))
		h.relay.TakeCode(76561197960287930) // client claims it

		h.lichessCallback(httptest.NewRecorder(), callbackReq("code=second&state="+goodState))
		if got, ok := h.relay.TakeCode(76561197960287930); ok {
			t.Fatalf("replay parked a second code: %q", got)
		}
	})
}

func codeReq(steamID, token string) *http.Request {
	r := httptest.NewRequest(http.MethodGet, "/api/v1/auth/lichess/code", nil)
	r.Header.Set(steamIDHeader, steamID)
	r.Header.Set("Authorization", "Bearer "+token)
	return r
}

func TestLichessCode(t *testing.T) {
	t.Run("returns the code once, then 404s", func(t *testing.T) {
		okVerifier(t)
		h := relayHandler()
		h.relay.StashCode(76561197960287930, "oauth-code-1")

		w := httptest.NewRecorder()
		h.lichessCode(w, codeReq(testSteamID, "good"))
		if w.Code != http.StatusOK {
			t.Fatalf("want 200, got %d", w.Code)
		}
		var body map[string]string
		json.NewDecoder(w.Body).Decode(&body)
		if body["code"] != "oauth-code-1" {
			t.Fatalf("want the code back, got %q", body["code"])
		}
		if w.Header().Get("Cache-Control") != "no-store" {
			t.Errorf("code response must be no-store, got %q", w.Header().Get("Cache-Control"))
		}

		w2 := httptest.NewRecorder()
		h.lichessCode(w2, codeReq(testSteamID, "good"))
		if w2.Code != http.StatusNotFound {
			t.Fatalf("second claim should 404, got %d", w2.Code)
		}
	})

	t.Run("404 while nothing is pending", func(t *testing.T) {
		okVerifier(t)
		h := relayHandler()
		w := httptest.NewRecorder()
		h.lichessCode(w, codeReq(testSteamID, "good"))
		if w.Code != http.StatusNotFound {
			t.Fatalf("want 404, got %d", w.Code)
		}
	})

	// The whole point of gating /code on the FP token: nobody else can claim it.
	t.Run("another steamid cannot claim your code", func(t *testing.T) {
		stubVerifier(t, func(_ context.Context, id, tok string) (bool, error) {
			return tok == "good", nil // both accounts hold valid tokens
		})
		h := relayHandler()
		h.relay.StashCode(76561197960287930, "victims-code")

		w := httptest.NewRecorder()
		h.lichessCode(w, codeReq("76561197960287931", "good"))
		if w.Code != http.StatusNotFound {
			t.Fatalf("attacker should see 404, got %d", w.Code)
		}
		if strings.Contains(w.Body.String(), "victims-code") {
			t.Fatal("leaked another player's code")
		}
		// And the victim's code must survive the attempt.
		if got, ok := h.relay.TakeCode(76561197960287930); !ok || got != "victims-code" {
			t.Fatal("attacker's poll consumed the victim's code")
		}
	})

	t.Run("unauthed caller gets 401, not 404", func(t *testing.T) {
		stubVerifier(t, func(context.Context, string, string) (bool, error) { return false, nil })
		h := relayHandler()
		h.relay.StashCode(76561197960287930, "oauth-code-1")
		w := httptest.NewRecorder()
		h.lichessCode(w, codeReq(testSteamID, "bad"))
		if w.Code != http.StatusUnauthorized {
			t.Fatalf("want 401, got %d", w.Code)
		}
		if _, ok := h.relay.TakeCode(76561197960287930); !ok {
			t.Fatal("a rejected poll consumed the pending code")
		}
	})
}

// End-to-end through the real mux: begin -> callback -> code.
func TestRelayEndToEnd(t *testing.T) {
	okVerifier(t)
	h := relayHandler()
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/v1/auth/lichess/begin", h.lichessBegin)
	mux.HandleFunc("GET /api/v1/auth/lichess/code", h.lichessCode)
	mux.HandleFunc("GET /callback", h.lichessCallback)

	w := httptest.NewRecorder()
	mux.ServeHTTP(w, beginReq(goodState))
	if w.Code != http.StatusOK {
		t.Fatalf("begin: want 200, got %d", w.Code)
	}

	w = httptest.NewRecorder()
	mux.ServeHTTP(w, callbackReq("code=e2e-code&state="+goodState))
	if w.Code != http.StatusOK {
		t.Fatalf("callback: want 200, got %d", w.Code)
	}

	w = httptest.NewRecorder()
	mux.ServeHTTP(w, codeReq(testSteamID, "good"))
	if w.Code != http.StatusOK {
		t.Fatalf("code: want 200, got %d", w.Code)
	}
	var body map[string]string
	json.NewDecoder(w.Body).Decode(&body)
	if body["code"] != "e2e-code" {
		t.Fatalf("want e2e-code, got %q", body["code"])
	}
}
