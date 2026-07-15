package api

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"go.uber.org/zap"
)

// These tests deliberately run with a NIL db pool. Every case here must be
// rejected before any DB touch, so a nil pool is the assertion: if validation
// ever moves after the first query, these panic instead of quietly passing.
//
// The DB-backed paths (idempotent upsert, listing, links) have no coverage here
// because this host has no Postgres. They are exercised by `make testinst` +
// curl — see issue #7 §9.
func archiveHandler() *handler {
	return &handler{
		log:     zap.NewNop(),
		version: "test",
		// Real instances: callerSteamID reads the session before falling back to
		// the FP token, so a nil sessions would panic the moment a cookie appeared.
		sessions: newSessions("test-secret"),
		nonces:   newNonceStore(time.Minute),
	}
}

const validUUID = "3f2b8c1e-5d4a-4c9b-8e7f-1a2b3c4d5e6f"

func gamePostReq(body string) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "/api/v1/games", strings.NewReader(body))
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")
	return r
}

// The rule that matters: seat SteamIDs are unverified claims from the body, so
// you may only archive a game you actually sat in. Without it, anyone holding a
// valid Facepunch token could inject games into any other player's archive,
// since GET /games is keyed on those claimed seat IDs.
func TestPostGameRejectsNonParticipant(t *testing.T) {
	okVerifier(t)
	w := httptest.NewRecorder()
	archiveHandler().postGame(w, gamePostReq(`{
		"client_game_id":"`+validUUID+`",
		"pgn":"1. e4 e5 *",
		"white_steam_id":"76561197960287931",
		"black_steam_id":"76561197960287932",
		"result":"*"
	}`))
	if w.Code != http.StatusForbidden {
		t.Fatalf("want 403 for a game the caller didn't play in, got %d (%s)", w.Code, w.Body)
	}
}

func TestPostGameAcceptsEitherSeat(t *testing.T) {
	okVerifier(t)
	// Reaching the DB (nil -> panic) proves validation passed. That's the signal
	// we want; recover turns it into a pass.
	for _, seat := range []string{
		`"white_steam_id":"76561197960287930","black_steam_id":"76561197960287931"`,
		`"white_steam_id":"76561197960287931","black_steam_id":"76561197960287930"`,
	} {
		func() {
			defer func() { recover() }()
			w := httptest.NewRecorder()
			archiveHandler().postGame(w, gamePostReq(`{
				"client_game_id":"`+validUUID+`",
				"pgn":"1. e4 e5 *",`+seat+`,
				"result":"*"
			}`))
			if w.Code == http.StatusForbidden {
				t.Errorf("seat %s: a participant was rejected as a non-participant", seat)
			}
		}()
	}
}

func TestPostGameValidation(t *testing.T) {
	okVerifier(t)
	seats := `"white_steam_id":"76561197960287930","black_steam_id":"76561197960287931"`

	cases := []struct {
		name, body string
		want       int
	}{
		{"bad client_game_id", `{"client_game_id":"not-a-uuid","pgn":"1. e4 *",` + seats + `,"result":"*"}`, http.StatusBadRequest},
		{"missing client_game_id", `{"pgn":"1. e4 *",` + seats + `,"result":"*"}`, http.StatusBadRequest},
		{"empty pgn", `{"client_game_id":"` + validUUID + `","pgn":"",` + seats + `,"result":"*"}`, http.StatusBadRequest},
		{"bad result", `{"client_game_id":"` + validUUID + `","pgn":"1. e4 *",` + seats + `,"result":"white wins"}`, http.StatusBadRequest},
		{"missing result", `{"client_game_id":"` + validUUID + `","pgn":"1. e4 *",` + seats + `}`, http.StatusBadRequest},
		{"malformed json", `{not json`, http.StatusBadRequest},
		{"no seats at all", `{"client_game_id":"` + validUUID + `","pgn":"1. e4 *","result":"*"}`, http.StatusForbidden},
		{"both seats empty", `{"client_game_id":"` + validUUID + `","pgn":"1. e4 *","white_steam_id":"0","black_steam_id":"0","result":"*"}`, http.StatusForbidden},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			w := httptest.NewRecorder()
			archiveHandler().postGame(w, gamePostReq(c.body))
			if w.Code != c.want {
				t.Fatalf("want %d, got %d (%s)", c.want, w.Code, w.Body)
			}
		})
	}
}

func TestPostGameRejectsOversizePgn(t *testing.T) {
	okVerifier(t)
	w := httptest.NewRecorder()
	archiveHandler().postGame(w, gamePostReq(`{
		"client_game_id":"`+validUUID+`",
		"pgn":"`+strings.Repeat("a", maxPgnBytes+1)+`",
		"white_steam_id":"76561197960287930",
		"black_steam_id":"76561197960287931",
		"result":"*"
	}`))
	// MaxBytesReader trips first and the decode fails; either way it must not
	// reach the DB (nil pool would panic) and must be a 4xx.
	if w.Code < 400 || w.Code >= 500 {
		t.Fatalf("want a 4xx for an oversize pgn, got %d", w.Code)
	}
}

func TestPostGameRequiresAuth(t *testing.T) {
	stubVerifier(t, func(context.Context, string, string) (bool, error) { return false, nil })
	w := httptest.NewRecorder()
	archiveHandler().postGame(w, gamePostReq(`{
		"client_game_id":"`+validUUID+`","pgn":"1. e4 *",
		"white_steam_id":"76561197960287930","black_steam_id":"76561197960287931","result":"*"
	}`))
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("want 401, got %d", w.Code)
	}
}

// The archive is private: no credentials, no games. Not a 400, not an empty
// list — a 401.
func TestArchiveReadsRequireACaller(t *testing.T) {
	w := httptest.NewRecorder()
	archiveHandler().listGames(w, httptest.NewRequest(http.MethodGet, "/api/v1/games", nil))
	if w.Code != http.StatusUnauthorized {
		t.Errorf("listGames unauthed: want 401, got %d", w.Code)
	}

	r := httptest.NewRequest(http.MethodGet, "/api/v1/games/"+validUUID, nil)
	r.SetPathValue("id", validUUID)
	w = httptest.NewRecorder()
	archiveHandler().getGame(w, r)
	if w.Code != http.StatusUnauthorized {
		t.Errorf("getGame unauthed: want 401, got %d", w.Code)
	}
}

// A ?steam_id= in the query must not get you anyone else's archive. The param
// doesn't exist; identity comes from the verified caller. Reaching the nil DB
// (panic) proves we got past auth on OUR identity and ignored the param.
func TestListGamesIgnoresSteamIdParam(t *testing.T) {
	okVerifier(t)
	defer func() {
		if recover() == nil {
			t.Error("expected to reach the DB with the caller's own id")
		}
	}()
	r := httptest.NewRequest(http.MethodGet, "/api/v1/games?steam_id=76561197960287999", nil)
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer good")
	archiveHandler().listGames(httptest.NewRecorder(), r)
}

// A session cookie is accepted in place of an FP token — this is what keeps the
// web viewer working without shipping it a Facepunch token.
func TestArchiveAcceptsAWebSession(t *testing.T) {
	h := archiveHandler()
	defer func() {
		if recover() == nil {
			t.Error("a valid session should have reached the DB")
		}
	}()
	r := httptest.NewRequest(http.MethodGet, "/api/v1/games", nil)
	r.AddCookie(&http.Cookie{Name: sessionCookie, Value: h.sessions.issue(76561197960287930)})
	h.listGames(httptest.NewRecorder(), r)
}

func TestGetGameValidatesUUID(t *testing.T) {
	okVerifier(t)
	for _, id := range []string{"not-a-uuid", "123", ""} {
		r := httptest.NewRequest(http.MethodGet, "/api/v1/games/"+id, nil)
		r.SetPathValue("id", id)
		r.Header.Set(steamIDHeader, testSteamID)
		r.Header.Set("Authorization", "Bearer good")
		w := httptest.NewRecorder()
		archiveHandler().getGame(w, r)
		if w.Code != http.StatusBadRequest {
			t.Errorf("id %q: want 400, got %d", id, w.Code)
		}
	}
}

func TestParseSeat(t *testing.T) {
	// "" and "0" are both "empty seat": the client uses 0 for that
	// (ChessStation), the DB says NULL, and the FK would reject a literal 0.
	for _, raw := range []string{"", "0", "  "} {
		got, ok := parseSeat(raw)
		if !ok || got != nil {
			t.Errorf("parseSeat(%q) = (%v,%v), want (nil,true)", raw, got, ok)
		}
	}
	// SteamIDs arrive as strings so browsers can't corrupt them past 2^53.
	got, ok := parseSeat("76561197960287930")
	if !ok || got == nil || *got != 76561197960287930 {
		t.Fatalf("parseSeat of a real id failed: (%v,%v)", got, ok)
	}
	for _, raw := range []string{"abc", "-1", "1.5", "076561197960287930", "999999999999999999999"} {
		if _, ok := parseSeat(raw); ok {
			t.Errorf("parseSeat(%q) should have been rejected", raw)
		}
	}
}

func TestSeatMatches(t *testing.T) {
	me, them, zero := int64(76561197960287930), int64(76561197960287931), int64(0)

	if !seatMatches(me, &me) {
		t.Error("own seat should match")
	}
	if seatMatches(me, &them) {
		t.Error("someone else's seat must not match")
	}
	if seatMatches(me, nil) {
		t.Error("an empty seat must not match")
	}
	// Guard the 0-vs-0 trap: an empty seat must never match a caller, and since
	// requireSteam can only ever return a real verified ID, 0 should never be one.
	if seatMatches(zero, &zero) {
		t.Error("seat 0 must not authorise a caller of 0")
	}
}

func TestClampInt(t *testing.T) {
	cases := []struct {
		raw               string
		def, lo, hi, want int
	}{
		{"", 50, 1, 200, 50},
		{"abc", 50, 1, 200, 50},
		{"10", 50, 1, 200, 10},
		{"0", 50, 1, 200, 1},
		{"-5", 50, 1, 200, 1},
		{"9999", 50, 1, 200, 200},
		{"200", 50, 1, 200, 200},
	}
	for _, c := range cases {
		if got := clampInt(c.raw, c.def, c.lo, c.hi); got != c.want {
			t.Errorf("clampInt(%q, %d, %d, %d) = %d, want %d", c.raw, c.def, c.lo, c.hi, got, c.want)
		}
	}
}
