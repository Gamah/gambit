package steam

import (
	"context"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
)

func withOpenIDStub(t *testing.T, body string) {
	t.Helper()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte(body))
	}))
	prev := openidEndpoint
	openidEndpoint = srv.URL
	t.Cleanup(func() { openidEndpoint = prev; srv.Close() })
}

func TestLoginURL(t *testing.T) {
	u := LoginURL("https://chess.gamah.net", "https://chess.gamah.net/auth/steam/return?nonce=abc")
	parsed, err := url.Parse(u)
	if err != nil {
		t.Fatal(err)
	}
	q := parsed.Query()
	if q.Get("openid.mode") != "checkid_setup" {
		t.Errorf("mode = %q", q.Get("openid.mode"))
	}
	if q.Get("openid.realm") != "https://chess.gamah.net" {
		t.Errorf("realm = %q", q.Get("openid.realm"))
	}
	if !strings.Contains(q.Get("openid.return_to"), "nonce=abc") {
		t.Errorf("return_to = %q", q.Get("openid.return_to"))
	}
}

func TestVerify(t *testing.T) {
	const returnTo = "https://chess.gamah.net/auth/steam/return"
	// validParams builds a well-formed assertion. op_endpoint must match the
	// (stubbed) openidEndpoint and return_to must match what the caller registered.
	validParams := func() url.Values {
		return url.Values{
			"openid.claimed_id":     {"https://steamcommunity.com/openid/id/76561197960287930"},
			"openid.sig":            {"whatever"},
			"openid.op_endpoint":    {openidEndpoint},
			"openid.return_to":      {returnTo + "?nonce=abc"},
			"openid.response_nonce": {"2026-06-14T00:00:00Zabcdef"},
		}
	}

	t.Run("valid", func(t *testing.T) {
		withOpenIDStub(t, "ns:http://specs.openid.net/auth/2.0\nis_valid:true\n")
		id, ok, err := Verify(context.Background(), validParams(), returnTo)
		if err != nil || !ok || id != "76561197960287930" {
			t.Fatalf("got id=%q ok=%v err=%v", id, ok, err)
		}
	})

	t.Run("steam says invalid", func(t *testing.T) {
		withOpenIDStub(t, "ns:http://specs.openid.net/auth/2.0\nis_valid:false\n")
		if _, ok, _ := Verify(context.Background(), validParams(), returnTo); ok {
			t.Fatal("expected ok=false when Steam reports is_valid:false")
		}
	})

	t.Run("forged claimed_id rejected before round-trip", func(t *testing.T) {
		withOpenIDStub(t, "is_valid:true\n")
		bad := url.Values{"openid.claimed_id": {"https://evil.example/openid/id/123"}}
		if _, ok, err := Verify(context.Background(), bad, returnTo); ok || err == nil {
			t.Fatal("expected rejection of non-steam claimed_id")
		}
	})

	t.Run("op_endpoint mismatch rejected", func(t *testing.T) {
		withOpenIDStub(t, "is_valid:true\n")
		p := validParams()
		p.Set("openid.op_endpoint", "https://evil.example/openid/login")
		if _, ok, err := Verify(context.Background(), p, returnTo); ok || err == nil {
			t.Fatal("expected rejection of foreign op_endpoint")
		}
	})

	t.Run("return_to mismatch rejected", func(t *testing.T) {
		withOpenIDStub(t, "is_valid:true\n")
		p := validParams()
		p.Set("openid.return_to", "https://evil.example/auth/steam/return")
		if _, ok, err := Verify(context.Background(), p, returnTo); ok || err == nil {
			t.Fatal("expected rejection of mismatched return_to")
		}
	})

	t.Run("missing response_nonce rejected", func(t *testing.T) {
		withOpenIDStub(t, "is_valid:true\n")
		p := validParams()
		p.Del("openid.response_nonce")
		if _, ok, err := Verify(context.Background(), p, returnTo); ok || err == nil {
			t.Fatal("expected rejection of missing response_nonce")
		}
	})
}
