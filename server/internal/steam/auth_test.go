package steam

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

// withStub points the package at a test server returning the given response and
// restores the real endpoint afterwards.
func withStub(t *testing.T, handler http.HandlerFunc) {
	t.Helper()
	srv := httptest.NewServer(handler)
	prev := endpoint
	endpoint = srv.URL
	t.Cleanup(func() { endpoint = prev; srv.Close() })
}

func TestValidateToken(t *testing.T) {
	const steamID = "76561197960287930"

	t.Run("ok and matching steamid", func(t *testing.T) {
		withStub(t, func(w http.ResponseWriter, r *http.Request) {
			var in struct {
				SteamID int64  `json:"steamid"`
				Token   string `json:"token"`
			}
			json.NewDecoder(r.Body).Decode(&in)
			json.NewEncoder(w).Encode(map[string]any{"SteamId": in.SteamID, "Status": "ok"})
		})
		ok, err := ValidateToken(context.Background(), steamID, "good")
		if err != nil || !ok {
			t.Fatalf("want (true,nil), got (%v,%v)", ok, err)
		}
	})

	t.Run("status not ok", func(t *testing.T) {
		withStub(t, func(w http.ResponseWriter, r *http.Request) {
			json.NewEncoder(w).Encode(map[string]any{"SteamId": 76561197960287930, "Status": "error"})
		})
		if ok, _ := ValidateToken(context.Background(), steamID, "bad"); ok {
			t.Fatal("expected deny on non-ok status")
		}
	})

	// THE test. A valid, unexpired, Facepunch-issued token for account Y must
	// never authorise as account X. If only this case survives, keep this one.
	t.Run("steamid mismatch is denied", func(t *testing.T) {
		withStub(t, func(w http.ResponseWriter, r *http.Request) {
			// Valid token, but for a different account.
			json.NewEncoder(w).Encode(map[string]any{"SteamId": 1, "Status": "ok"})
		})
		if ok, _ := ValidateToken(context.Background(), steamID, "good"); ok {
			t.Fatal("expected deny when SteamId does not match")
		}
	})

	t.Run("non-200 fails closed", func(t *testing.T) {
		withStub(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusInternalServerError)
		})
		ok, err := ValidateToken(context.Background(), steamID, "good")
		if ok || err == nil {
			t.Fatalf("expected (false, err) on 500, got (%v,%v)", ok, err)
		}
	})

	t.Run("bad steamid", func(t *testing.T) {
		if ok, err := ValidateToken(context.Background(), "notanumber", "good"); ok || err == nil {
			t.Fatalf("expected (false, err), got (%v,%v)", ok, err)
		}
	})

	// A Facepunch outage must deny, never grant. The stub server is closed before
	// the call, so client.Do fails at the transport layer.
	t.Run("transport error fails closed", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
		prev := endpoint
		endpoint = srv.URL
		srv.Close()
		t.Cleanup(func() { endpoint = prev })

		ok, err := ValidateToken(context.Background(), steamID, "good")
		if ok || err == nil {
			t.Fatalf("expected (false, err) when Facepunch is unreachable, got (%v,%v)", ok, err)
		}
	})

	// Garbage body must not decode into a zero-value struct that somehow passes.
	t.Run("undecodable body fails closed", func(t *testing.T) {
		withStub(t, func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte("<html>not json</html>"))
		})
		ok, err := ValidateToken(context.Background(), steamID, "good")
		if ok || err == nil {
			t.Fatalf("expected (false, err) on undecodable body, got (%v,%v)", ok, err)
		}
	})
}
