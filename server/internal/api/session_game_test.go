package api

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"go.uber.org/zap"
)

// sessionHandler builds a handler with a nil db pool: nothing on the session path
// may touch the database, so a nil pool is the assertion.
func sessionHandler() *handler {
	return &handler{log: zap.NewNop(), sessions: newSessions("test-secret")}
}

// countFacepunchCalls stubs the Facepunch boundary and returns a counter plus a
// restore func. The count is the point of most of these tests: the session exists
// to make it zero.
func countFacepunchCalls(t *testing.T, allow bool) *int {
	t.Helper()
	n := 0
	prev := validateToken
	validateToken = func(ctx context.Context, steamID, token string) (bool, error) {
		n++
		return allow, nil
	}
	t.Cleanup(func() { validateToken = prev })
	return &n
}

// Game sessions (M9): the bearer the s&box client carries instead of paying a
// Facepunch round-trip per request.
//
// The forgery and confusion cases matter far more than the happy path here. A
// game session authorises everything that SteamID can do — including playing
// lichess games as them — and sessions are stateless, so there is NO revoking one
// short of rotating SESSION_SECRET and signing out every player and browser at
// once.

const gameSteamID = int64(76561197960287930)

func TestGameSessionRoundTrips(t *testing.T) {
	s := newSessions("test-secret")
	tok, exp := s.issueGame(gameSteamID)

	if !strings.HasPrefix(tok, gamePrefix) {
		t.Fatalf("token %q lacks the %q prefix — requireSteam won't recognise it", tok, gamePrefix)
	}
	id, ok := s.readGame(tok)
	if !ok || id != gameSteamID {
		t.Fatalf("readGame = (%d, %v), want (%d, true)", id, ok, gameSteamID)
	}

	// An hour, not the web's 30 days. The short window is the entire justification
	// for handing the client a bearer at all.
	if d := time.Until(exp); d > sessionGameTTL+time.Minute || d < sessionGameTTL-time.Minute {
		t.Errorf("TTL is %v, want ~%v", d, sessionGameTTL)
	}
}

// THE case this design exists for.
//
// Sign `steamID|expiry` alone and a web cookie and a game bearer are the same
// bytes under the same key. A leaked 30-day cookie, replayed as `gcs_<value>`,
// would then authorise the game API for its full month and the 1-hour game TTL
// would be pure decoration. The audience is inside the MAC precisely so this
// fails.
func TestWebCookieIsNotAGameBearer(t *testing.T) {
	s := newSessions("test-secret")

	webValue := s.issue(gameSteamID) // a 30-day browser cookie
	if id, ok := s.readGame(gamePrefix + webValue); ok {
		t.Fatalf("a web cookie replayed as a game bearer authorised SteamID %d "+
			"— the 1-hour game TTL is meaningless if this passes", id)
	}
	// ...and the reverse: a game bearer must not open the archive as a cookie.
	gameTok, _ := s.issueGame(gameSteamID)
	raw := strings.TrimPrefix(gameTok, gamePrefix)
	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: raw})
	if id, ok := s.read(r); ok {
		t.Fatalf("a game bearer worked as a web cookie for SteamID %d", id)
	}
}

func TestGameSessionRejectsForgeries(t *testing.T) {
	s := newSessions("test-secret")
	other := newSessions("a-different-secret")

	valid, _ := s.issueGame(gameSteamID)

	cases := map[string]string{
		"no prefix":                 strings.TrimPrefix(valid, gamePrefix),
		"empty":                     "",
		"prefix only":               gamePrefix,
		"not base64":                gamePrefix + "!!!!not base64!!!!",
		"signed by a different key": func() string { v, _ := other.issueGame(gameSteamID); return v }(),
		"unsigned payload":          gamePrefix + encodeCookie("game|76561197960287930|99999999999"),
		"web audience":              gamePrefix + s.issue(gameSteamID),
		"too few fields":            gamePrefix + encodeCookie("76561197960287930|99999999999|deadbeef"),
	}
	for name, tok := range cases {
		t.Run(name, func(t *testing.T) {
			if id, ok := s.readGame(tok); ok {
				t.Fatalf("accepted a forged token as SteamID %d", id)
			}
		})
	}
}

// A tampered SteamID must not survive the MAC. This is the whole point of signing.
func TestGameSessionRejectsTamperedSteamID(t *testing.T) {
	s := newSessions("test-secret")
	valid, _ := s.issueGame(gameSteamID)
	raw := decodeCookie(t, strings.TrimPrefix(valid, gamePrefix))

	parts := strings.Split(raw, "|")
	if len(parts) != 4 {
		t.Fatalf("unexpected payload shape %q", raw)
	}
	parts[1] = "76561197960287931" // somebody else
	if id, ok := s.readGame(gamePrefix + encodeCookie(strings.Join(parts, "|"))); ok {
		t.Fatalf("a tampered SteamID verified as %d", id)
	}
}

func TestGameSessionRejectsExpired(t *testing.T) {
	s := newSessions("test-secret")
	// Hand-build an expired-but-correctly-signed token: the MAC is fine, only the
	// clock says no.
	payload := "game|76561197960287930|" + itoa(time.Now().Add(-time.Minute).Unix())
	tok := gamePrefix + encodeCookie(payload+"|"+s.mac(payload))
	if id, ok := s.readGame(tok); ok {
		t.Fatalf("an expired session verified as %d", id)
	}
}

// ── The gate ──

// requireSteam must take a valid session without ever calling Facepunch. That is
// the entire point: a polling client would otherwise cost one Facepunch
// round-trip per player per ~5s, forever.
func TestRequireSteamAcceptsSessionWithoutFacepunch(t *testing.T) {
	h := sessionHandler()
	calls := countFacepunchCalls(t, true)

	tok, _ := h.sessions.issueGame(gameSteamID)
	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.Header.Set("Authorization", "Bearer "+tok)
	w := httptest.NewRecorder()

	id, ok := h.requireSteam(w, r)
	if !ok || id != gameSteamID {
		t.Fatalf("requireSteam = (%d, %v), want (%d, true)", id, ok, gameSteamID)
	}
	if *calls != 0 {
		t.Fatalf("made %d Facepunch calls for a session-authed request — must be 0", *calls)
	}
}

// An expired session must 401 rather than fall through to the Facepunch path:
// falling through spends a live round-trip to prove what we already know, and the
// 401 is exactly what makes the client re-mint.
func TestExpiredSessionDoesNotFallThroughToFacepunch(t *testing.T) {
	h := sessionHandler()
	calls := countFacepunchCalls(t, true)

	payload := "game|76561197960287930|" + itoa(time.Now().Add(-time.Minute).Unix())
	tok := gamePrefix + encodeCookie(payload+"|"+h.sessions.mac(payload))

	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.Header.Set("Authorization", "Bearer "+tok)
	r.Header.Set(steamIDHeader, "76561197960287930")
	w := httptest.NewRecorder()

	if _, ok := h.requireSteam(w, r); ok {
		t.Fatal("an expired session authorised a request")
	}
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("status %d, want 401", w.Code)
	}
	if *calls != 0 {
		t.Fatalf("made %d Facepunch calls for an expired session — should refuse locally", *calls)
	}
}

// ── Minting ──

// A session must not be able to mint a session. If it could, a client would renew
// itself forever and the 1-hour TTL — the whole reason a stateless, unrevokable
// bearer is acceptable — would be a fiction.
func TestSessionCannotMintASession(t *testing.T) {
	h := sessionHandler()
	countFacepunchCalls(t, true) // even a Facepunch that says yes must not help here

	tok, _ := h.sessions.issueGame(gameSteamID)
	r := httptest.NewRequest(http.MethodPost, "/api/v1/session", nil)
	r.Header.Set("Authorization", "Bearer "+tok)
	r.Header.Set(steamIDHeader, "76561197960287930")
	w := httptest.NewRecorder()

	h.postSession(w, r)
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("status %d, want 401 — a session that renews itself never expires", w.Code)
	}
}

func TestPostSessionMintsFromAFacepunchToken(t *testing.T) {
	h := sessionHandler()
	countFacepunchCalls(t, true)

	r := httptest.NewRequest(http.MethodPost, "/api/v1/session", nil)
	r.Header.Set("Authorization", "Bearer a-real-fp-token")
	r.Header.Set(steamIDHeader, "76561197960287930")
	w := httptest.NewRecorder()

	h.postSession(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("status %d: %s", w.Code, w.Body.String())
	}
	var out SessionResponse
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	id, ok := h.sessions.readGame(out.Token)
	if !ok || id != gameSteamID {
		t.Fatalf("minted token verifies as (%d, %v)", id, ok)
	}
	if out.ExpiresAt <= time.Now().Unix() {
		t.Error("expires_at is in the past")
	}
}

// Fail closed: a Facepunch outage must never mint a session.
func TestPostSessionFailsClosed(t *testing.T) {
	h := sessionHandler()

	prev := validateToken
	validateToken = func(ctx context.Context, steamID, token string) (bool, error) {
		return false, errors.New("facepunch is down")
	}
	defer func() { validateToken = prev }()

	r := httptest.NewRequest(http.MethodPost, "/api/v1/session", nil)
	r.Header.Set("Authorization", "Bearer a-token")
	r.Header.Set(steamIDHeader, "76561197960287930")
	w := httptest.NewRecorder()

	h.postSession(w, r)
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("status %d, want 401 — a Facepunch outage must not mint sessions", w.Code)
	}
}
