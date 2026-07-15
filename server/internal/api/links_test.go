package api

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// Nil db pool again: everything asserted here must be rejected before the first
// query. The DB-backed paths (upsert, the username-taken 409, delete) need
// Postgres — see `make testinst` in issue #7 §9.

func linkReq(method, body, token string) *http.Request {
	r := httptest.NewRequest(method, "/api/v1/links/lichess", strings.NewReader(body))
	r.Header.Set(steamIDHeader, testSteamID)
	r.Header.Set("Authorization", "Bearer "+token)
	return r
}

func TestPutLichessLinkValidatesUsername(t *testing.T) {
	okVerifier(t)
	bad := []string{
		"",
		"a",                          // under the 2-char floor
		strings.Repeat("a", 31),      // over the 30-char ceiling
		"_leading_underscore",        // lichess names start alphanumeric
		"-leading-dash",
		"has spaces",
		"has/slash",
		"has@at",
		`<script>alert(1)</script>`,
	}
	for _, u := range bad {
		w := httptest.NewRecorder()
		archiveHandler().putLichessLink(w, linkReq(http.MethodPut, `{"lichess_username":"`+u+`"}`, "good"))
		if w.Code != http.StatusBadRequest {
			t.Errorf("username %q: want 400, got %d", u, w.Code)
		}
	}
}

func TestPutLichessLinkAcceptsRealNames(t *testing.T) {
	okVerifier(t)
	// Reaching the nil DB panics, which proves validation let it through.
	for _, u := range []string{"thibault", "German11", "a_b-c", "ab", strings.Repeat("a", 30)} {
		func() {
			defer func() { recover() }()
			w := httptest.NewRecorder()
			archiveHandler().putLichessLink(w, linkReq(http.MethodPut, `{"lichess_username":"`+u+`"}`, "good"))
			if w.Code == http.StatusBadRequest {
				t.Errorf("username %q: valid lichess name rejected", u)
			}
		}()
	}
}

func TestPutLichessLinkMalformedBody(t *testing.T) {
	okVerifier(t)
	w := httptest.NewRecorder()
	archiveHandler().putLichessLink(w, linkReq(http.MethodPut, `{not json`, "good"))
	if w.Code != http.StatusBadRequest {
		t.Fatalf("want 400, got %d", w.Code)
	}
}

// The link is identity data — an unauthed caller must never write or remove one.
func TestLinkEndpointsRequireAuth(t *testing.T) {
	stubVerifier(t, func(context.Context, string, string) (bool, error) { return false, nil })

	w := httptest.NewRecorder()
	archiveHandler().putLichessLink(w, linkReq(http.MethodPut, `{"lichess_username":"thibault"}`, "bad"))
	if w.Code != http.StatusUnauthorized {
		t.Errorf("PUT: want 401, got %d", w.Code)
	}

	w = httptest.NewRecorder()
	archiveHandler().deleteLichessLink(w, linkReq(http.MethodDelete, "", "bad"))
	if w.Code != http.StatusUnauthorized {
		t.Errorf("DELETE: want 401, got %d", w.Code)
	}
}
