package lichess

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
)

// stubAPI points every lichess endpoint at one test server and restores them
// afterwards — the same trick steam/auth_test.go plays on steam.endpoint, for
// the same reason: the network boundary is where the interesting bugs are.
func stubAPI(t *testing.T, handler http.HandlerFunc) *httptest.Server {
	t.Helper()
	srv := httptest.NewServer(handler)

	pa, pt, pac, pb := authorizeEndpoint, tokenEndpoint, accountEndpoint, apiBase
	authorizeEndpoint = srv.URL + "/oauth"
	tokenEndpoint = srv.URL + "/api/token"
	accountEndpoint = srv.URL + "/api/account"
	apiBase = srv.URL

	t.Cleanup(func() {
		authorizeEndpoint, tokenEndpoint, accountEndpoint, apiBase = pa, pt, pac, pb
		srv.Close()
	})
	return srv
}

// ── PKCE ──

// The RFC 7636 Appendix B test vector. If our S256 challenge doesn't match the
// spec's, lichess rejects every exchange — so this pins the transform itself
// rather than our own round trip.
func TestS256MatchesRFC7636Vector(t *testing.T) {
	const (
		verifier  = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
		wantChall = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
	)
	sum := sha256.Sum256([]byte(verifier))
	got := base64.RawURLEncoding.EncodeToString(sum[:])
	if got != wantChall {
		t.Fatalf("S256 transform is wrong: got %q want %q", got, wantChall)
	}
}

func TestNewVerifier(t *testing.T) {
	verifier, challenge, err := NewVerifier()
	if err != nil {
		t.Fatal(err)
	}

	// RFC 7636 mandates 43..128 chars. 32 random bytes → 43 base64url chars, so
	// we sit exactly on the floor — if this ever drops below 43, lichess answers
	// CodeVerifierTooShort and linking breaks for everyone.
	if len(verifier) < 43 || len(verifier) > 128 {
		t.Fatalf("verifier length %d is outside RFC 7636's 43..128", len(verifier))
	}

	// base64url, unpadded: any other alphabet would be re-encoded by lichess and
	// stop matching.
	if strings.ContainsAny(verifier, "+/=") {
		t.Fatalf("verifier %q is not unpadded base64url", verifier)
	}

	// The challenge must be S256 of the verifier we hand back, or the exchange
	// can never succeed.
	sum := sha256.Sum256([]byte(verifier))
	if want := base64.RawURLEncoding.EncodeToString(sum[:]); challenge != want {
		t.Fatalf("challenge is not S256(verifier): got %q want %q", challenge, want)
	}
}

func TestNewVerifierIsUnpredictable(t *testing.T) {
	seen := map[string]bool{}
	for i := 0; i < 100; i++ {
		v, _, err := NewVerifier()
		if err != nil {
			t.Fatal(err)
		}
		if seen[v] {
			t.Fatal("NewVerifier repeated a verifier — PKCE depends on it being fresh")
		}
		seen[v] = true
	}
}

// ── AuthorizeURL ──

func TestAuthorizeURL(t *testing.T) {
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {})

	raw := AuthorizeURL("net.gamah.gambit", "https://chess.gamah.net/lichess/callback", "st4te", "ch4llenge")
	u, err := url.Parse(raw)
	if err != nil {
		t.Fatal(err)
	}
	q := u.Query()

	for key, want := range map[string]string{
		"response_type":         "code",
		"client_id":             "net.gamah.gambit",
		"redirect_uri":          "https://chess.gamah.net/lichess/callback",
		"state":                 "st4te",
		"code_challenge":        "ch4llenge",
		"code_challenge_method": "S256",
	} {
		if got := q.Get(key); got != want {
			t.Errorf("%s: got %q want %q", key, got, want)
		}
	}

	// THE scope test. board:play and nothing else — a wider grant asks for
	// capability we never use, and a scope change forces every linked player
	// through a full re-link because lichess has no refresh tokens.
	if got := q.Get("scope"); got != "board:play" {
		t.Fatalf("scope: got %q, want exactly \"board:play\"", got)
	}
}

// ── Exchange ──

func TestExchangePostsTheRightForm(t *testing.T) {
	var got url.Values
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		got, _ = url.ParseQuery(string(body))
		if ct := r.Header.Get("Content-Type"); !strings.HasPrefix(ct, "application/x-www-form-urlencoded") {
			t.Errorf("Content-Type: got %q, want form encoding", ct)
		}
		json.NewEncoder(w).Encode(map[string]any{
			"access_token": "lio_tok", "token_type": "Bearer", "expires_in": 31536000,
		})
	})

	const redirect = "https://chess.gamah.net/lichess/callback"
	tok, err := Exchange(context.Background(), "net.gamah.gambit", redirect, "the-code", "the-verifier")
	if err != nil {
		t.Fatal(err)
	}
	if tok.AccessToken != "lio_tok" {
		t.Fatalf("access token: got %q", tok.AccessToken)
	}

	for key, want := range map[string]string{
		"grant_type":    "authorization_code",
		"code":          "the-code",
		"code_verifier": "the-verifier",
		"client_id":     "net.gamah.gambit",
		// Must match the authorize call byte for byte or lichess refuses.
		"redirect_uri": redirect,
	} {
		if g := got.Get(key); g != want {
			t.Errorf("form %s: got %q want %q", key, g, want)
		}
	}
}

func TestExchangeFailsClosed(t *testing.T) {
	t.Run("non-200", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusBadRequest)
			io.WriteString(w, `{"error":"invalid_grant"}`)
		})
		if _, err := Exchange(context.Background(), "c", "r", "code", "v"); err == nil {
			t.Fatal("expected an error on 400")
		}
	})

	t.Run("undecodable body", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			io.WriteString(w, "<html>not json</html>")
		})
		if _, err := Exchange(context.Background(), "c", "r", "code", "v"); err == nil {
			t.Fatal("expected an error on a non-JSON body")
		}
	})

	// A 200 with no token must not read as success — that would store an empty
	// token and link an account we cannot act for.
	t.Run("200 with no access_token", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			io.WriteString(w, `{"token_type":"Bearer"}`)
		})
		if _, err := Exchange(context.Background(), "c", "r", "code", "v"); err == nil {
			t.Fatal("expected an error when the response carries no access_token")
		}
	})

	t.Run("lichess unreachable", func(t *testing.T) {
		srv := stubAPI(t, func(w http.ResponseWriter, r *http.Request) {})
		srv.Close()
		if _, err := Exchange(context.Background(), "c", "r", "code", "v"); err == nil {
			t.Fatal("expected an error when lichess is unreachable")
		}
	})
}

// ── Account ──

func TestAccount(t *testing.T) {
	t.Run("parses id and username", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			if got := r.Header.Get("Authorization"); got != "Bearer lio_tok" {
				t.Errorf("Authorization: got %q", got)
			}
			io.WriteString(w, `{"id":"terrygambit","username":"TerryGambit","perfs":{}}`)
		})
		id, name, err := Account(context.Background(), "lio_tok")
		if err != nil {
			t.Fatal(err)
		}
		// id is the canonical lowercase key and the only thing we key identity
		// on; username is display casing.
		if id != "terrygambit" || name != "TerryGambit" {
			t.Fatalf("got (%q,%q)", id, name)
		}
	})

	t.Run("username falls back to id", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			io.WriteString(w, `{"id":"terrygambit"}`)
		})
		id, name, err := Account(context.Background(), "t")
		if err != nil || id != "terrygambit" || name != "terrygambit" {
			t.Fatalf("got (%q,%q,%v)", id, name, err)
		}
	})

	// A 401 here means the token is dead. It must never yield an empty identity
	// that a caller could store.
	t.Run("401 fails closed", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusUnauthorized)
		})
		if _, _, err := Account(context.Background(), "dead"); err == nil {
			t.Fatal("expected an error on 401")
		}
	})

	t.Run("200 with no id fails closed", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			io.WriteString(w, `{"username":"Ghost"}`)
		})
		if _, _, err := Account(context.Background(), "t"); err == nil {
			t.Fatal("expected an error when the account has no id")
		}
	})
}

// ── Revoke ──

func TestRevoke(t *testing.T) {
	t.Run("204 is success", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			if r.Method != http.MethodDelete {
				t.Errorf("method: got %s want DELETE", r.Method)
			}
			if r.URL.Path != "/api/token" {
				t.Errorf("path: got %s", r.URL.Path)
			}
			// The revoke is signed BY the token being killed — there is no admin form.
			if got := r.Header.Get("Authorization"); got != "Bearer doomed" {
				t.Errorf("Authorization: got %q", got)
			}
			w.WriteHeader(http.StatusNoContent)
		})
		if err := Revoke(context.Background(), "doomed"); err != nil {
			t.Fatal(err)
		}
	})

	// Already-dead is the outcome we wanted, so a double unlink isn't an error.
	t.Run("401 counts as already revoked", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusUnauthorized)
		})
		if err := Revoke(context.Background(), "already-dead"); err != nil {
			t.Fatalf("401 should read as success, got %v", err)
		}
	})

	t.Run("500 is an error", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusInternalServerError)
		})
		if err := Revoke(context.Background(), "t"); err == nil {
			t.Fatal("expected an error on 500")
		}
	})
}

// ── TokenTest (the audit sweep) ──

func TestTokenTest(t *testing.T) {
	t.Run("live and dead tokens", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			body, _ := io.ReadAll(r.Body)
			// Comma-separated, one call, up to 1000 tokens.
			if string(body) != "live1,dead1" {
				t.Errorf("body: got %q want %q", body, "live1,dead1")
			}
			io.WriteString(w, `{"live1":{"userId":"terry","scopes":"board:play","expires":123},"dead1":null}`)
		})

		got, err := TokenTest(context.Background(), []string{"live1", "dead1"})
		if err != nil {
			t.Fatal(err)
		}
		if !got["live1"].Live || got["live1"].UserID != "terry" || got["live1"].Scopes != "board:play" {
			t.Fatalf("live1: %+v", got["live1"])
		}
		// A null entry means revoked/expired/not ours — the sweep's whole point.
		if got["dead1"].Live {
			t.Fatalf("dead1 should not be live: %+v", got["dead1"])
		}
	})

	t.Run("empty input makes no request", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			t.Fatal("TokenTest must not call lichess with no tokens")
		})
		got, err := TokenTest(context.Background(), nil)
		if err != nil || len(got) != 0 {
			t.Fatalf("got (%v,%v)", got, err)
		}
	})

	// lichess caps a call at 1000; batching is the caller's job, so refuse rather
	// than silently truncate the sweep (a truncated audit reads as "all clear").
	t.Run("over 1000 is refused, not truncated", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			t.Fatal("must not send an over-limit batch")
		})
		if _, err := TokenTest(context.Background(), make([]string, 1001)); err == nil {
			t.Fatal("expected a refusal over 1000 tokens")
		}
	})

	t.Run("non-200 fails closed", func(t *testing.T) {
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusTooManyRequests)
		})
		if _, err := TokenTest(context.Background(), []string{"a"}); err == nil {
			t.Fatal("expected an error on 429")
		}
	})
}
