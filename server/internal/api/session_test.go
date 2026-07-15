package api

import (
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"
)

// A session cookie is the only thing standing between a browser and someone's
// archive, so the forgery cases matter more than the happy path.

func decodeCookie(t *testing.T, v string) string {
	t.Helper()
	b, err := base64.RawURLEncoding.DecodeString(v)
	if err != nil {
		t.Fatalf("decode cookie: %v", err)
	}
	return string(b)
}

func encodeCookie(s string) string { return base64.RawURLEncoding.EncodeToString([]byte(s)) }

func itoa(n int64) string { return strconv.FormatInt(n, 10) }

func withSession(s *sessions, steamID int64) *http.Request {
	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: s.issue(steamID)})
	return r
}

func TestSessionRoundTrip(t *testing.T) {
	s := newSessions("test-secret")
	got, ok := s.read(withSession(s, 76561197960287930))
	if !ok || got != 76561197960287930 {
		t.Fatalf("want (76561197960287930,true), got (%d,%v)", got, ok)
	}
}

func TestSessionNoCookie(t *testing.T) {
	s := newSessions("test-secret")
	if _, ok := s.read(httptest.NewRequest(http.MethodGet, "/", nil)); ok {
		t.Fatal("no cookie must not yield a session")
	}
}

// The whole point of the MAC: a cookie minted by a different key is worthless.
func TestSessionForeignKeyRejected(t *testing.T) {
	attacker := newSessions("attackers-secret")
	real := newSessions("real-secret")

	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: attacker.issue(76561197960287930)})

	if _, ok := real.read(r); ok {
		t.Fatal("a cookie signed with another key must be rejected")
	}
}

// Tamper with the SteamID and the MAC no longer matches — you can't promote
// yourself to someone else's archive by editing the cookie.
func TestSessionTamperedSteamIDRejected(t *testing.T) {
	s := newSessions("test-secret")
	raw := s.issue(76561197960287930)

	decoded := decodeCookie(t, raw)
	parts := strings.Split(decoded, "|")
	forged := encodeCookie("76561197960287999" + "|" + parts[1] + "|" + parts[2])

	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: forged})
	if _, ok := s.read(r); ok {
		t.Fatal("a tampered steam id must be rejected")
	}
}

func TestSessionExpiryRejected(t *testing.T) {
	s := newSessions("test-secret")
	// Mint a cookie that expired an hour ago, correctly signed.
	payload := "76561197960287930|" + itoa(time.Now().Add(-time.Hour).Unix())
	value := encodeCookie(payload + "|" + s.mac(payload))

	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: value})
	if _, ok := s.read(r); ok {
		t.Fatal("an expired session must be rejected even when correctly signed")
	}
}

func TestSessionGarbageRejected(t *testing.T) {
	s := newSessions("test-secret")
	for _, v := range []string{"", "!!!not-base64!!!", encodeCookie("only|two"), encodeCookie("a|b|c|d"), encodeCookie("notanum|123|abc")} {
		r := httptest.NewRequest(http.MethodGet, "/", nil)
		r.AddCookie(&http.Cookie{Name: sessionCookie, Value: v})
		if _, ok := s.read(r); ok {
			t.Errorf("garbage cookie %q must be rejected", v)
		}
	}
}

// A random per-process key must still produce working sessions — that's the
// no-config default.
func TestSessionRandomKeyWorks(t *testing.T) {
	s := newSessions("")
	if got, ok := s.read(withSession(s, 42)); !ok || got != 42 {
		t.Fatalf("random-key sessions should work in-process, got (%d,%v)", got, ok)
	}
	// ...but must not be interchangeable with another process's.
	other := newSessions("")
	if _, ok := other.read(withSession(s, 42)); ok {
		t.Fatal("two random keys must not validate each other's cookies")
	}
}

func TestSessionClear(t *testing.T) {
	s := newSessions("test-secret")
	w := httptest.NewRecorder()
	s.clear(w)
	c := w.Result().Cookies()[0]
	if c.Value != "" || c.MaxAge >= 0 {
		t.Fatalf("clear must expire the cookie, got value=%q maxage=%d", c.Value, c.MaxAge)
	}
}

func TestSessionCookieFlags(t *testing.T) {
	s := newSessions("test-secret")
	w := httptest.NewRecorder()
	s.set(w, 76561197960287930)
	c := w.Result().Cookies()[0]

	if !c.HttpOnly {
		t.Error("session cookie must be HttpOnly — no script needs it")
	}
	if !c.Secure {
		t.Error("session cookie must be Secure — Caddy fronts us with TLS")
	}
	// Lax, not Strict: the Steam OpenID return is a top-level cross-site GET, and
	// Strict would drop the cookie on exactly that navigation.
	if c.SameSite != http.SameSiteLaxMode {
		t.Errorf("session cookie must be SameSite=Lax, got %v", c.SameSite)
	}
}

// ── nonce replay ──

func TestNonceIsSingleUse(t *testing.T) {
	n := newNonceStore(30 * time.Minute)
	if !n.use("abc") {
		t.Fatal("first use should be accepted")
	}
	// steam.Verify only shape-checks the nonce; this is what actually stops a
	// captured return URL being replayed into a fresh session.
	if n.use("abc") {
		t.Fatal("a replayed nonce must be rejected")
	}
}

func TestNonceDistinctValuesIndependent(t *testing.T) {
	n := newNonceStore(30 * time.Minute)
	if !n.use("abc") || !n.use("def") {
		t.Fatal("distinct nonces must both be accepted")
	}
}

func TestNonceSweepsExpired(t *testing.T) {
	n := newNonceStore(time.Nanosecond)
	n.use("old")
	time.Sleep(2 * time.Millisecond)
	// The sweep runs on the write path; after it, "old" is forgotten and would be
	// accepted again — fine, because Steam's signature on it is long dead.
	n.use("trigger-sweep")
	n.mu.Lock()
	_, stillThere := n.seen["old"]
	n.mu.Unlock()
	if stillThere {
		t.Fatal("expired nonce should have been swept")
	}
}
