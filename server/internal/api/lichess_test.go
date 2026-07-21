package api

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gamah/gambit/server/internal/keyring"
	"github.com/gamah/gambit/server/internal/lichess"
	"go.uber.org/zap"
)

// Like archiveHandler, these run with a NIL db pool on purpose: every case here
// must be refused BEFORE any DB touch, so a nil pool is the assertion. If a
// check ever moves after the first query, these panic instead of quietly passing.
func lichessHandler(t *testing.T) *handler {
	t.Helper()
	key := base64.StdEncoding.EncodeToString(make([]byte, 32))
	k, err := keyring.NewEphemeral(key)
	if err != nil {
		t.Fatal(err)
	}
	h := &handler{
		log:      zap.NewNop(),
		version:  "test",
		baseURL:  "https://testchess.gamah.net",
		sessions: newSessions("test-secret"),
		nonces:   newNonceStore(time.Minute),
		keys:     k,
		pending:  newPendingLinks(pendingTTL),
	}
	h.relay = newRelay(h.log, nil, k)
	return h
}

// ── The redirect URI ──

// lichess compares the authorize and token redirect_uri byte for byte, so this
// has to be derived once and be exactly right. Deriving it from PUBLIC_BASE_URL
// is also what stops the TEST instance sending players back to PROD.
func TestLichessRedirectURL(t *testing.T) {
	h := lichessHandler(t)
	if got := h.lichessRedirectURL(); got != "https://testchess.gamah.net/lichess/callback" {
		t.Fatalf("got %q", got)
	}

	// A trailing slash on the base must not produce a double slash — that would
	// be a different URI to lichess, and the exchange would fail at the last step.
	h.baseURL = "https://testchess.gamah.net/"
	if got := h.lichessRedirectURL(); got != "https://testchess.gamah.net/lichess/callback" {
		t.Fatalf("trailing slash not normalised: %q", got)
	}
}

// ── The pending-link (state) store ──

func TestPendingLinksBurnOnUse(t *testing.T) {
	p := newPendingLinks(time.Minute)
	state, err := p.put(76561197960287930, "verifier-1")
	if err != nil {
		t.Fatal(err)
	}

	got, ok := p.use(state)
	if !ok || got.steamID != 76561197960287930 || got.verifier != "verifier-1" {
		t.Fatalf("first use: got (%+v,%v)", got, ok)
	}

	// Replay must fail. Without this, a captured callback URL could be replayed
	// to re-link for as long as the code stayed valid.
	if _, ok := p.use(state); ok {
		t.Fatal("a state must be single-use")
	}
}

func TestPendingLinksUnknownState(t *testing.T) {
	p := newPendingLinks(time.Minute)
	if _, ok := p.use("never-issued"); ok {
		t.Fatal("an unknown state must not resolve")
	}
	if _, ok := p.use(""); ok {
		t.Fatal("an empty state must not resolve")
	}
}

func TestPendingLinksExpiry(t *testing.T) {
	p := newPendingLinks(-time.Second) // everything is already expired
	state, err := p.put(1, "v")
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := p.use(state); ok {
		t.Fatal("an expired state must not resolve")
	}
}

func TestPendingLinksStatesAreUnique(t *testing.T) {
	p := newPendingLinks(time.Minute)
	seen := map[string]bool{}
	for i := 0; i < 50; i++ {
		s, err := p.put(int64(i+1), "v")
		if err != nil {
			t.Fatal(err)
		}
		if seen[s] {
			t.Fatal("a state was reused — it is the CSRF guard for the link flow")
		}
		seen[s] = true
	}
}

// The SteamID is bound server-side when we redirect, NOT read from the callback.
// This is what stops one player completing another's link.
func TestPendingLinkCarriesTheSteamIDWeChose(t *testing.T) {
	p := newPendingLinks(time.Minute)
	state, _ := p.put(76561197960287930, "v")
	got, ok := p.use(state)
	if !ok || got.steamID != 76561197960287930 {
		t.Fatalf("got %+v", got)
	}
}

// ── /lichess/link ──

func TestLichessLinkNeedsSteamSignIn(t *testing.T) {
	w := httptest.NewRecorder()
	lichessHandler(t).lichessLink(w, httptest.NewRequest(http.MethodGet, "/lichess/link", nil))

	if w.Code != http.StatusFound {
		t.Fatalf("want a 302 to Steam sign-in, got %d", w.Code)
	}
	if loc := w.Header().Get("Location"); loc != "/auth/steam/login" {
		t.Fatalf("want /auth/steam/login, got %q", loc)
	}
}

func TestLichessLinkShowsDisclosureWhenSignedIn(t *testing.T) {
	h := lichessHandler(t)
	r := httptest.NewRequest(http.MethodGet, "/lichess/link", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: h.sessions.issue(76561197960287930)})

	w := httptest.NewRecorder()
	h.lichessLink(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("want 200, got %d", w.Code)
	}

	body := w.Body.String()
	// The disclosure is the point of this page, and these are the claims that
	// must survive any edit to it.
	for _, want := range []string{
		"board:play",        // exactly what we ask for
		"never sees either", // Gambit sees neither password
		"encrypted",         // the token is stored, and stored sealed
		"/lichess/start",    // and the bounce is a separate click
	} {
		if !strings.Contains(body, want) {
			t.Errorf("the consent page no longer mentions %q", want)
		}
	}
	// Must not bounce to lichess before the player has read anything.
	if strings.Contains(body, "lichess.org/oauth") {
		t.Error("the consent page should not itself be the OAuth redirect")
	}
}

func TestLichessLinkOffWithoutAKey(t *testing.T) {
	h := lichessHandler(t)
	h.keys = nil // no LICHESS_TOKEN_KEY ⇒ feature off

	w := httptest.NewRecorder()
	h.lichessLink(w, httptest.NewRequest(http.MethodGet, "/lichess/link", nil))
	if w.Code != http.StatusNotImplemented {
		t.Fatalf("want 501 when lichess is unconfigured, got %d", w.Code)
	}
}

// ── /lichess/start ──

func TestLichessStartRedirectsToLichess(t *testing.T) {
	h := lichessHandler(t)
	r := httptest.NewRequest(http.MethodGet, "/lichess/start", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: h.sessions.issue(76561197960287930)})

	w := httptest.NewRecorder()
	h.lichessStart(w, r)
	if w.Code != http.StatusFound {
		t.Fatalf("want 302, got %d (%s)", w.Code, w.Body)
	}

	loc := w.Header().Get("Location")
	if !strings.HasPrefix(loc, "https://lichess.org/oauth?") {
		t.Fatalf("want a bounce to lichess, got %q", loc)
	}
	// THE scope assertion, at the HTTP layer this time: board:play and nothing
	// else ever leaves this server.
	if !strings.Contains(loc, "scope=board%3Aplay") {
		t.Fatalf("scope is not board:play: %q", loc)
	}
	if !strings.Contains(loc, "code_challenge_method=S256") {
		t.Fatalf("PKCE S256 is missing: %q", loc)
	}
	if !strings.Contains(loc, "redirect_uri=https%3A%2F%2Ftestchess.gamah.net%2Flichess%2Fcallback") {
		t.Fatalf("redirect_uri is not derived from the base URL: %q", loc)
	}
}

func TestLichessStartNeedsSteamSignIn(t *testing.T) {
	w := httptest.NewRecorder()
	lichessHandler(t).lichessStart(w, httptest.NewRequest(http.MethodGet, "/lichess/start", nil))
	if w.Code != http.StatusFound || w.Header().Get("Location") != "/auth/steam/login" {
		t.Fatalf("want a 302 to Steam sign-in, got %d → %q", w.Code, w.Header().Get("Location"))
	}
}

// ── /lichess/callback ──

// Every one of these must be refused before any lichess call or DB touch (the
// pool is nil, so a DB touch panics rather than passes).
func TestLichessCallbackFailsClosed(t *testing.T) {
	h := lichessHandler(t)

	t.Run("unknown state", func(t *testing.T) {
		w := httptest.NewRecorder()
		h.lichessCallback(w, httptest.NewRequest(http.MethodGet,
			"/lichess/callback?code=abc&state=never-issued", nil))
		if w.Code != http.StatusBadRequest {
			t.Fatalf("want 400, got %d", w.Code)
		}
	})

	t.Run("no state at all", func(t *testing.T) {
		w := httptest.NewRecorder()
		h.lichessCallback(w, httptest.NewRequest(http.MethodGet, "/lichess/callback?code=abc", nil))
		if w.Code != http.StatusBadRequest {
			t.Fatalf("want 400, got %d", w.Code)
		}
	})

	t.Run("replayed state", func(t *testing.T) {
		state, _ := h.pending.put(76561197960287930, "v")
		h.pending.use(state) // burn it, as a first callback would

		w := httptest.NewRecorder()
		h.lichessCallback(w, httptest.NewRequest(http.MethodGet,
			"/lichess/callback?code=abc&state="+state, nil))
		if w.Code != http.StatusBadRequest {
			t.Fatalf("a replayed state must be refused, got %d", w.Code)
		}
	})

	t.Run("state present but no code", func(t *testing.T) {
		state, _ := h.pending.put(76561197960287930, "v")
		w := httptest.NewRecorder()
		h.lichessCallback(w, httptest.NewRequest(http.MethodGet, "/lichess/callback?state="+state, nil))
		if w.Code != http.StatusBadRequest {
			t.Fatalf("want 400, got %d", w.Code)
		}
	})

	// A refused consent comes back as ?error=, and must burn nothing and say so
	// plainly rather than looking like a bug.
	t.Run("user declined on lichess", func(t *testing.T) {
		w := httptest.NewRecorder()
		h.lichessCallback(w, httptest.NewRequest(http.MethodGet,
			"/lichess/callback?error=access_denied", nil))
		if w.Code != http.StatusOK {
			t.Fatalf("want 200, got %d", w.Code)
		}
		// html/template escapes the apostrophe, so match on the unambiguous part.
		if !strings.Contains(w.Body.String(), "approve the link on lichess") {
			t.Fatalf("want a plain explanation, got %s", w.Body)
		}
	})
}

// A used state must be consumed even when the flow later fails, or a captured
// callback could be retried.
func TestLichessCallbackBurnsTheStateEvenOnFailure(t *testing.T) {
	h := lichessHandler(t)
	state, _ := h.pending.put(76561197960287930, "v")

	// No code ⇒ fails after the state is burned.
	w := httptest.NewRecorder()
	h.lichessCallback(w, httptest.NewRequest(http.MethodGet, "/lichess/callback?state="+state, nil))

	if _, ok := h.pending.use(state); ok {
		t.Fatal("the state survived a failed callback — it must be single-use regardless")
	}
}

// ── /api/v1/lichess ──

func TestLichessStatusNeedsACaller(t *testing.T) {
	w := httptest.NewRecorder()
	lichessHandler(t).lichessStatus(w, httptest.NewRequest(http.MethodGet, "/api/v1/lichess", nil))
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("want 401 with no credentials, got %d", w.Code)
	}
}

// There is no ?steam_id= and there must never be one: it would make every
// player's lichess identity enumerable by anyone who could sign in. This test
// exists to fail if someone adds one.
func TestLichessStatusIgnoresAClaimedSteamID(t *testing.T) {
	h := lichessHandler(t)
	h.db = nil // with no DB the handler answers {linked:false} for whoever asks

	okVerifier(t)
	r := httptest.NewRequest(http.MethodGet,
		"/api/v1/lichess?steam_id=76561197960287999", nil)
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")

	w := httptest.NewRecorder()
	h.lichessStatus(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("want 200, got %d (%s)", w.Code, w.Body)
	}

	var got lichessLinkJSON
	if err := json.Unmarshal(w.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	if got.Linked {
		t.Fatal("a query-string steam_id must never produce someone else's link")
	}
}

// ── The play relay ──

func playBody(clientGameID, white, black string, limit, inc int) string {
	b, _ := json.Marshal(playPost{
		ClientGameID: clientGameID,
		WhiteSteamID: white,
		BlackSteamID: black,
		LimitSeconds: limit,
		IncrementSec: inc,
	})
	return string(b)
}

func playReq(body string) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "/api/v1/lichess/play", strings.NewReader(body))
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")
	return r
}

// The seats in the body are CLAIMS. You may only ask for a game you are sitting
// in — the same rule that guards the archive, and for the same reason.
func TestLichessPlayRejectsNonParticipant(t *testing.T) {
	okVerifier(t)
	w := httptest.NewRecorder()
	lichessHandler(t).lichessPlay(w, playReq(
		playBody(validUUID, "76561197960287931", "76561197960287932", 180, 2)))

	if w.Code != http.StatusForbidden {
		t.Fatalf("want 403 for a game the caller isn't in, got %d (%s)", w.Code, w.Body)
	}
}

// Bullet can never reach lichess from any path — the Board API refuses anything
// faster than blitz. Refuse it here rather than spend a lichess request learning it.
func TestLichessPlayRejectsBullet(t *testing.T) {
	okVerifier(t)
	w := httptest.NewRecorder()
	lichessHandler(t).lichessPlay(w, playReq(
		playBody(validUUID, testSteamID, "76561197960287931", 60, 0)))

	if w.Code != http.StatusBadRequest {
		t.Fatalf("want 400 for a bullet table, got %d (%s)", w.Code, w.Body)
	}
	if !strings.Contains(w.Body.String(), "blitz") {
		t.Fatalf("the reason should name the blitz floor: %s", w.Body)
	}
}

func TestLichessPlayValidation(t *testing.T) {
	cases := map[string]string{
		"not a uuid":        playBody("nope", testSteamID, "76561197960287931", 180, 2),
		"malformed seat":    playBody(validUUID, testSteamID, "not-a-steamid", 180, 2),
		"same player twice": playBody(validUUID, testSteamID, testSteamID, 180, 2),
		"empty black seat":  playBody(validUUID, testSteamID, "0", 180, 2),
	}
	for name, body := range cases {
		t.Run(name, func(t *testing.T) {
			okVerifier(t)
			w := httptest.NewRecorder()
			lichessHandler(t).lichessPlay(w, playReq(body))
			if w.Code != http.StatusBadRequest {
				t.Fatalf("want 400, got %d (%s)", w.Code, w.Body)
			}
		})
	}
}

func TestLichessPlayNeedsSteam(t *testing.T) {
	w := httptest.NewRecorder()
	r := httptest.NewRequest(http.MethodPost, "/api/v1/lichess/play",
		strings.NewReader(playBody(validUUID, testSteamID, "76561197960287931", 180, 2)))
	lichessHandler(t).lichessPlay(w, r)
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("want 401 with no credentials, got %d", w.Code)
	}
}

// A game you aren't seated in must 404 — not 403 — so ids aren't probeable.
func TestLichessPlayStateHidesOtherPeoplesGames(t *testing.T) {
	h := lichessHandler(t)
	okVerifier(t)

	// A play between two other people.
	h.relay.plays["someone-elses"] = newPlay(PlayRequest{
		ClientGameID: "someone-elses",
		WhiteSteamID: 76561197960287931,
		BlackSteamID: 76561197960287932,
	})

	r := httptest.NewRequest(http.MethodGet, "/api/v1/lichess/play/someone-elses", nil)
	r.SetPathValue("id", "someone-elses")
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")

	w := httptest.NewRecorder()
	h.lichessPlayState(w, r)
	if w.Code != http.StatusNotFound {
		t.Fatalf("want 404 (indistinguishable from absent), got %d (%s)", w.Code, w.Body)
	}
}

func TestLichessPlayActRejectsNonSeat(t *testing.T) {
	h := lichessHandler(t)
	okVerifier(t)

	h.relay.plays["theirs"] = newPlay(PlayRequest{
		ClientGameID: "theirs",
		WhiteSteamID: 76561197960287931,
		BlackSteamID: 76561197960287932,
	})

	r := httptest.NewRequest(http.MethodPost, "/api/v1/lichess/play/theirs/resign", nil)
	r.SetPathValue("id", "theirs")
	r.SetPathValue("action", "resign")
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")

	w := httptest.NewRecorder()
	h.lichessPlayAct(w, r)
	if w.Code != http.StatusNotFound {
		t.Fatalf("a stranger must not be able to resign someone's game: got %d", w.Code)
	}
}

// ── Relay internals ──

// One seat asking is not enough. This is the whole authorisation story for the
// relay: without it, any linked player could drag any other linked player into a
// lichess game at will, because gamchess holds both their tokens.
func TestRelayNeedsBothSeatsToAgree(t *testing.T) {
	r := newRelay(zap.NewNop(), nil, nil)
	req := PlayRequest{
		ClientGameID: validUUID,
		WhiteSteamID: 1001,
		BlackSteamID: 1002,
		LimitSeconds: 180,
		IncrementSec: 2,
	}

	p, err := r.Join(context.Background(), 1001, req)
	if err != nil {
		t.Fatal(err)
	}
	state, _ := p.snapshot()
	if state.Status != playWaiting {
		t.Fatalf("one intent should still be waiting, got %q", state.Status)
	}

	// The same seat asking twice is still one seat.
	if _, err := r.Join(context.Background(), 1001, req); err != nil {
		t.Fatal(err)
	}
	state, _ = p.snapshot()
	if state.Status != playWaiting {
		t.Fatalf("the same seat asking twice must not start a game, got %q", state.Status)
	}
}

func TestRelayRejectsAStranger(t *testing.T) {
	r := newRelay(zap.NewNop(), nil, nil)
	req := PlayRequest{ClientGameID: validUUID, WhiteSteamID: 1001, BlackSteamID: 1002}

	if _, err := r.Join(context.Background(), 1001, req); err != nil {
		t.Fatal(err)
	}
	// Someone who isn't at the table cannot join it, even naming the right seats.
	if _, err := r.Join(context.Background(), 9999, req); err == nil {
		t.Fatal("a non-seat must not be able to join a play")
	}
}

// Both seats must describe the SAME game, or one client's terms would silently
// win over the other's.
func TestRelayRejectsMismatchedTerms(t *testing.T) {
	r := newRelay(zap.NewNop(), nil, nil)
	base := PlayRequest{ClientGameID: validUUID, WhiteSteamID: 1001, BlackSteamID: 1002,
		LimitSeconds: 180, IncrementSec: 2}

	if _, err := r.Join(context.Background(), 1001, base); err != nil {
		t.Fatal(err)
	}

	other := base
	other.LimitSeconds = 600 // a different clock
	if _, err := r.Join(context.Background(), 1002, other); err == nil {
		t.Fatal("mismatched clocks must be refused")
	}

	swapped := base
	swapped.WhiteSteamID, swapped.BlackSteamID = base.BlackSteamID, base.WhiteSteamID
	if _, err := r.Join(context.Background(), 1002, swapped); err == nil {
		t.Fatal("swapped seats must be refused")
	}
}

func TestPlaySeatOf(t *testing.T) {
	p := newPlay(PlayRequest{WhiteSteamID: 1001, BlackSteamID: 1002})

	if c, ok := p.seatOf(1001); !ok || c != "white" {
		t.Fatalf("white: got (%q,%v)", c, ok)
	}
	if c, ok := p.seatOf(1002); !ok || c != "black" {
		t.Fatalf("black: got (%q,%v)", c, ok)
	}
	if _, ok := p.seatOf(9999); ok {
		t.Fatal("a stranger holds no seat")
	}
	// 0 means "empty seat" everywhere in this codebase — it must never match one.
	if _, ok := p.seatOf(0); ok {
		t.Fatal("0 must not resolve to a seat")
	}
}

func TestPlayVersionAdvancesOnEveryUpdate(t *testing.T) {
	p := newPlay(PlayRequest{WhiteSteamID: 1, BlackSteamID: 2})
	first, _ := p.snapshot()

	p.update(func(s *PlayState) { s.Moves = "e2e4" })
	second, _ := p.snapshot()

	// The long poll is keyed on this: if the version didn't move, both clients
	// would sleep through the update.
	if second.Version <= first.Version {
		t.Fatalf("version did not advance: %d → %d", first.Version, second.Version)
	}
	if second.Moves != "e2e4" {
		t.Fatalf("state did not update: %+v", second)
	}
}

// A waiter parked on the old version must wake the moment the state changes.
func TestPlayWaitWakesOnUpdate(t *testing.T) {
	p := newPlay(PlayRequest{WhiteSteamID: 1, BlackSteamID: 2})
	start, _ := p.snapshot()

	done := make(chan PlayState, 1)
	go func() { done <- p.Wait(context.Background(), start.Version) }()

	// Give the waiter a moment to park, then move the state.
	time.Sleep(50 * time.Millisecond)
	p.update(func(s *PlayState) { s.Moves = "e2e4 e7e5" })

	select {
	case got := <-done:
		if got.Moves != "e2e4 e7e5" {
			t.Fatalf("woke with a stale state: %+v", got)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("Wait did not wake on an update")
	}
}

// Already-behind means answer immediately — never park. Otherwise a client that
// missed an update would wait a full poll interval for news it could have had.
func TestPlayWaitReturnsImmediatelyWhenBehind(t *testing.T) {
	p := newPlay(PlayRequest{WhiteSteamID: 1, BlackSteamID: 2})
	p.update(func(s *PlayState) { s.Moves = "e2e4" })

	start := time.Now()
	got := p.Wait(context.Background(), 0) // since=0, way behind
	if time.Since(start) > time.Second {
		t.Fatal("Wait parked even though the caller was already behind")
	}
	if got.Moves != "e2e4" {
		t.Fatalf("got %+v", got)
	}
}

func TestPlayWaitHonoursCancellation(t *testing.T) {
	p := newPlay(PlayRequest{WhiteSteamID: 1, BlackSteamID: 2})
	current, _ := p.snapshot()

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { p.Wait(ctx, current.Version); close(done) }()

	time.Sleep(50 * time.Millisecond)
	cancel() // the client hung up

	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("Wait ignored a cancelled request context")
	}
}

func TestApplyStateMarksFinished(t *testing.T) {
	s := &PlayState{Status: playLive}
	applyState(s, &lichess.GameState{
		Type: "gameState", Moves: "e2e4 e7e5", Wtime: 1000, Btime: 2000,
		Status: "mate", Winner: "white",
	}, time.Now())

	if !s.Finished || s.Status != playOver {
		t.Fatalf("a mate must close the game: %+v", s)
	}
	if s.Winner != "white" || s.WhiteTimeMs != 1000 || s.BlackTimeMs != 2000 {
		t.Fatalf("state not folded through: %+v", s)
	}

	live := &PlayState{Status: playLive}
	applyState(live, &lichess.GameState{Status: "started", Moves: "e2e4"}, time.Now())
	if live.Finished || live.Status != playLive {
		t.Fatalf("a started game must stay live: %+v", live)
	}
}

// clockAt tracks when the clocks last CHANGED, so the client can age a frozen
// clock. A move refreshes it; a draw/takeback gameState (same clocks) must not, or
// the age reads ~0 for a value that is really a whole think old.
func TestApplyStateStampsClockOnlyOnChange(t *testing.T) {
	s := &PlayState{Status: playLive}

	t0 := time.Now()
	applyState(s, &lichess.GameState{Moves: "e2e4", Wtime: 180000, Btime: 180000}, t0)
	if !s.clockAt.Equal(t0) {
		t.Fatalf("first clock frame must stamp clockAt: got %v want %v", s.clockAt, t0)
	}

	// A draw offer: identical clocks, no move. clockAt must not move forward.
	t1 := t0.Add(4 * time.Second)
	applyState(s, &lichess.GameState{Moves: "e2e4", Wtime: 180000, Btime: 180000, Wdraw: true}, t1)
	if !s.clockAt.Equal(t0) {
		t.Fatalf("a no-move gameState must leave clockAt alone: got %v want %v", s.clockAt, t0)
	}

	// A real move: at least one clock changes, so the stamp advances.
	t2 := t0.Add(9 * time.Second)
	applyState(s, &lichess.GameState{Moves: "e2e4 e7e5", Wtime: 180000, Btime: 176000}, t2)
	if !s.clockAt.Equal(t2) {
		t.Fatalf("a move must refresh clockAt: got %v want %v", s.clockAt, t2)
	}
}

// ageAt derives the two send-time staleness fields as DURATIONS (skew-proof), from
// clockAt and the request-start baseline — the same contract as TvState.ageAt.
func TestPlayStateAgeAt(t *testing.T) {
	now := time.Now()
	s := PlayState{clockAt: now.Add(-2 * time.Second)}
	s.ageAt(now, now.Add(-5*time.Second))

	if s.ClockAgeMs < 1900 || s.ClockAgeMs > 2100 {
		t.Errorf("clock_age_ms = %d, want ~2000", s.ClockAgeMs)
	}
	if s.HoldMs < 4900 || s.HoldMs > 5100 {
		t.Errorf("hold_ms = %d, want ~5000", s.HoldMs)
	}

	// No clock yet ⇒ no age (a zero clockAt must not read as an enormous age).
	var fresh PlayState
	fresh.ageAt(now, now)
	if fresh.ClockAgeMs != 0 {
		t.Errorf("clock_age_ms = %d with no clock, want 0", fresh.ClockAgeMs)
	}
}

// No key ⇒ no tokens ⇒ no lichess. Feature-off must be a real gate, not a
// warning that leaves the routes half-working.
func TestRelayDisabledWithoutACipher(t *testing.T) {
	if newRelay(zap.NewNop(), nil, nil).Enabled() {
		t.Fatal("a relay with no cipher must not report enabled")
	}
}

func TestSweepDropsAbandonedIntents(t *testing.T) {
	r := newRelay(zap.NewNop(), nil, nil)
	p := newPlay(PlayRequest{ClientGameID: "old", WhiteSteamID: 1, BlackSteamID: 2})
	// One seat asked, the other never came.
	p.created = time.Now().Add(-2 * playIntentTTL)
	r.plays["old"] = p

	r.sweep()
	if _, ok := r.Lookup("old"); ok {
		t.Fatal("an abandoned intent should have been swept")
	}
}

func TestSweepKeepsRecentPlays(t *testing.T) {
	r := newRelay(zap.NewNop(), nil, nil)
	r.plays["fresh"] = newPlay(PlayRequest{ClientGameID: "fresh", WhiteSteamID: 1, BlackSteamID: 2})

	r.sweep()
	if _, ok := r.Lookup("fresh"); !ok {
		t.Fatal("a fresh play must survive a sweep")
	}
}

// ── The audit sweep ──

func TestAuditHiddenWithoutAKey(t *testing.T) {
	h := lichessHandler(t)
	h.auditKey = ""

	w := httptest.NewRecorder()
	h.lichessAudit(w, httptest.NewRequest(http.MethodPost, "/api/v1/lichess/audit", nil))
	// 404, not 401: an unconfigured route shouldn't be discoverable by probing.
	if w.Code != http.StatusNotFound {
		t.Fatalf("want 404 when unconfigured, got %d", w.Code)
	}
}

func TestAuditRejectsAWrongKey(t *testing.T) {
	h := lichessHandler(t)
	h.auditKey = "the-real-key"

	for name, given := range map[string]string{
		"no header": "",
		"wrong key": "Bearer nope",
		"prefix":    "Bearer the-real",
		"too long":  "Bearer the-real-key-plus",
	} {
		t.Run(name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodPost, "/api/v1/lichess/audit", nil)
			if given != "" {
				r.Header.Set("Authorization", given)
			}
			w := httptest.NewRecorder()
			h.lichessAudit(w, r)
			if w.Code != http.StatusNotFound {
				t.Fatalf("want 404, got %d", w.Code)
			}
		})
	}
}

// ── Direct challenge to a named lichess user ──

func challengeBody(clientGameID, opponent string, limit, inc int) string {
	b, _ := json.Marshal(challengePost{
		ClientGameID: clientGameID,
		Opponent:     opponent,
		LimitSeconds: limit,
		IncrementSec: inc,
	})
	return string(b)
}

func challengeReq(body string) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "/api/v1/lichess/challenge", strings.NewReader(body))
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")
	return r
}

func TestLichessChallengeValidation(t *testing.T) {
	cases := map[string]string{
		"not a uuid":       challengeBody("nope", "Mary", 180, 2),
		"empty opponent":   challengeBody(validUUID, "", 180, 2),
		"bad opponent":     challengeBody(validUUID, "has space", 180, 2),
		"bullet is capped": challengeBody(validUUID, "Mary", 60, 0),
	}
	for name, body := range cases {
		t.Run(name, func(t *testing.T) {
			okVerifier(t)
			w := httptest.NewRecorder()
			lichessHandler(t).lichessChallenge(w, challengeReq(body))
			if w.Code != http.StatusBadRequest {
				t.Fatalf("want 400, got %d (%s)", w.Code, w.Body)
			}
		})
	}
}

func TestLichessChallengeNeedsSteam(t *testing.T) {
	w := httptest.NewRecorder()
	r := httptest.NewRequest(http.MethodPost, "/api/v1/lichess/challenge",
		strings.NewReader(challengeBody(validUUID, "Mary", 180, 2)))
	lichessHandler(t).lichessChallenge(w, r)
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("want 401 with no credentials, got %d", w.Code)
	}
}

// A challenge is a SOLO flow: one intent starts it, the same as a seek and
// unlike the paired /play. Nobody else is being committed to anything — the
// named opponent consents on lichess's side, in their own client.
func TestChallengeStartsOnOneIntent(t *testing.T) {
	req := PlayRequest{
		ClientGameID: validUUID,
		Challenge:    true,
		Opponent:     "Mary",
		SoloSteamID:  1001,
		LimitSeconds: 180,
		IncrementSec: 2,
	}
	if n := req.intentsNeeded(); n != 1 {
		t.Fatalf("a challenge needs 1 intent, got %d", n)
	}
	if !req.solo() {
		t.Fatal("a challenge is a solo flow")
	}
}

// A solo flow reports its one player through your_color once lichess confirms a
// side — before that the seat is unknown, exactly as for a seek. Until gameFull
// lands, seatOf returns ("", true): the player is IN the game but their colour
// is not yet known.
func TestChallengeSeatUnknownUntilGameFull(t *testing.T) {
	p := newPlay(PlayRequest{
		ClientGameID: validUUID, Challenge: true, Opponent: "Mary", SoloSteamID: 1001,
	})

	color, ok := p.seatOf(1001)
	if !ok {
		t.Fatal("the challenger is in the game even before a colour is known")
	}
	if color != "" {
		t.Fatalf("colour should be unknown until gameFull, got %q", color)
	}
	// A stranger still holds no seat.
	if _, ok := p.seatOf(9999); ok {
		t.Fatal("only the challenger is in a solo game")
	}

	// gameFull arrives; our player is Black.
	p.soloLichessID = "mary_lichess" // pretend we linked as this id
	p.resolveSoloColor(&lichess.GameFull{
		White: lichess.GamePlayer{ID: "stranger"},
		Black: lichess.GamePlayer{ID: "mary_lichess"},
	})
	if color, ok := p.seatOf(1001); !ok || color != "black" {
		t.Fatalf("after gameFull the challenger is Black: got (%q,%v)", color, ok)
	}
	st, _ := p.snapshot()
	if st.BlackSteamID != "1001" || st.WhiteSteamID != "" {
		t.Fatalf("the stranger's seat must stay empty: %+v", st)
	}
}

// A challenge shares the one-solo-request-per-player slot with a seek, so a
// player can't leave two invitations open across two boards.
func TestChallengeSharesTheSoloSlot(t *testing.T) {
	r := newRelay(zap.NewNop(), nil, nil)
	first := PlayRequest{ClientGameID: validUUID, Challenge: true, Opponent: "Mary", SoloSteamID: 1001}
	if err := r.claimPending(1001, first.ClientGameID); err != nil {
		t.Fatal(err)
	}
	// A second, different request for the same player is refused while the first
	// is live.
	r.plays[first.ClientGameID] = newPlay(first)
	if err := r.claimPending(1001, "another-game-id"); err == nil {
		t.Fatal("a player may not hold two outstanding solo requests")
	}
	// Re-posting the SAME one is fine (the client just retried).
	if err := r.claimPending(1001, first.ClientGameID); err != nil {
		t.Fatalf("re-claiming the same request must be allowed: %v", err)
	}
}

// A challenge marks the game as stranger-opposite (Seek=true on the wire) and
// records the opponent's name, which is what the client uses to tell the two
// apart.
func TestChallengeStateShape(t *testing.T) {
	p := newPlay(PlayRequest{
		ClientGameID: validUUID, Challenge: true, Opponent: "Mary", SoloSteamID: 1001,
	})
	st, _ := p.snapshot()
	if !st.Seek {
		t.Fatal("a challenge has a stranger opposite, so Seek is set for the client")
	}
	if st.Opponent != "Mary" {
		t.Fatalf("the opponent's name should be carried: %+v", st)
	}
	// No seats are filled until lichess confirms a colour.
	if st.WhiteSteamID != "" || st.BlackSteamID != "" {
		t.Fatalf("a solo flow fills no seat up front: %+v", st)
	}
}

// ── Abandonment (a client that stops polling a live game) ──

// A paired game where one seat's client falls silent past abandonTTL is the seat the
// relay resigns on their behalf — the other seat, still polling, is left alone.
func TestAbandonedSeatDetectsAStaleClient(t *testing.T) {
	p := newPlay(PlayRequest{ClientGameID: validUUID, WhiteSteamID: 1001, BlackSteamID: 1002,
		LimitSeconds: 180, IncrementSec: 2})
	now := time.Now()

	// Both seats just polled → nobody abandoned.
	p.markPolled(1001)
	p.markPolled(1002)
	if got := p.abandonedSeat(now); got != 0 {
		t.Fatalf("both seats fresh, want 0, got %d", got)
	}

	// White still polling, Black gone quiet past the TTL → Black is the abandoned seat.
	p.mu.Lock()
	p.lastPoll[1002] = now.Add(-abandonTTL - time.Second)
	p.mu.Unlock()
	if got := p.abandonedSeat(now); got != 1002 {
		t.Fatalf("black is stale, want 1002, got %d", got)
	}
}

// A seat that never polled is measured from the game's creation, so it still gets the
// full grace before it counts as gone; and once resigned, the sweep reports nothing
// more (the resign fires at most once).
func TestAbandonedSeatGraceAndOnce(t *testing.T) {
	p := newPlay(PlayRequest{ClientGameID: validUUID, WhiteSteamID: 1001, BlackSteamID: 1002})
	now := time.Now()

	// Freshly created, nobody has polled yet → still within grace, nobody abandoned.
	if got := p.abandonedSeat(now); got != 0 {
		t.Fatalf("fresh game within grace, want 0, got %d", got)
	}

	// Past the TTL from creation with no poll → the first unpolled seat is abandoned.
	future := now.Add(2 * abandonTTL)
	if got := p.abandonedSeat(future); got != 1001 {
		t.Fatalf("want 1001 abandoned, got %d", got)
	}

	// Once resigned, no seat is reported again — the guard that makes the resign fire once.
	p.mu.Lock()
	p.abandonResigned = true
	p.mu.Unlock()
	if got := p.abandonedSeat(future); got != 0 {
		t.Fatalf("after resign, want 0, got %d", got)
	}
}

// A solo flow (seek / open link / challenge) has one Gambit seat; when that one
// client stops polling, it is the seat to resign.
func TestAbandonedSeatSolo(t *testing.T) {
	p := newPlay(PlayRequest{ClientGameID: validUUID, Open: true, SoloSteamID: 1001})
	now := time.Now()

	p.markPolled(1001)
	if got := p.abandonedSeat(now); got != 0 {
		t.Fatalf("fresh solo player, want 0, got %d", got)
	}

	p.mu.Lock()
	p.lastPoll[1001] = now.Add(-abandonTTL - time.Second)
	p.mu.Unlock()
	if got := p.abandonedSeat(now); got != 1001 {
		t.Fatalf("stale solo player, want 1001, got %d", got)
	}
}

// One relayed game per player: lichess does not document permission to play concurrent
// games through the Board API, so a player already in a live relayed game may not start
// a second — that table falls back to a local game instead.
func TestOneRelayedGamePerPlayer(t *testing.T) {
	r := newRelay(zap.NewNop(), nil, nil)

	// A live game White (1001) is already in.
	const otherUUID = "b7e1c2d3-4f5a-4b6c-8d9e-0f1a2b3c4d5e"
	live := newPlay(PlayRequest{ClientGameID: validUUID, WhiteSteamID: 1001, BlackSteamID: 1002})
	live.started = true
	r.plays[validUUID] = live

	// White trying to start a DIFFERENT game elsewhere is refused.
	if _, err := r.Join(context.Background(), 1001, PlayRequest{
		ClientGameID: otherUUID, WhiteSteamID: 1001, BlackSteamID: 1003}); err == nil {
		t.Fatal("a player already in a live game must not start a second relayed game")
	}

	// A player NOT in that game is unaffected.
	if _, err := r.Join(context.Background(), 1003, PlayRequest{
		ClientGameID: otherUUID, WhiteSteamID: 1003, BlackSteamID: 1004}); err != nil {
		t.Fatalf("an uninvolved player must still start a game: %v", err)
	}

	// Re-posting the SAME game (the paired flow's second seat) is never blocked by the gate.
	if _, err := r.Join(context.Background(), 1002, PlayRequest{
		ClientGameID: validUUID, WhiteSteamID: 1001, BlackSteamID: 1002}); err != nil {
		t.Fatalf("the same game's other seat must still join: %v", err)
	}
}
