package steam

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strings"
)

// openidEndpoint is Steam's OpenID 2.0 provider. Package var so tests can stub it.
var openidEndpoint = "https://steamcommunity.com/openid/login"

// claimedIDRe pulls the SteamID64 out of the claimed_id Steam returns, e.g.
// https://steamcommunity.com/openid/id/76561197960287930
var claimedIDRe = regexp.MustCompile(`^https://steamcommunity\.com/openid/id/(\d+)$`)

// LoginURL builds the Steam OpenID redirect URL. realm is the site root Steam
// shows the user and scopes the login to (e.g. https://chess.gamah.net); returnTo
// is the absolute URL Steam sends the browser back to after authenticating and
// must live under realm. identifier_select lets Steam pick the identity (the
// signed-in user) — we learn the SteamID only on the verified return.
func LoginURL(realm, returnTo string) string {
	const idSelect = "http://specs.openid.net/auth/2.0/identifier_select"
	q := url.Values{
		"openid.ns":         {"http://specs.openid.net/auth/2.0"},
		"openid.mode":       {"checkid_setup"},
		"openid.return_to":  {returnTo},
		"openid.realm":      {realm},
		"openid.identity":   {idSelect},
		"openid.claimed_id": {idSelect},
	}
	return openidEndpoint + "?" + q.Encode()
}

// Verify confirms an OpenID assertion actually came from Steam and returns the
// asserted SteamID64. params are the openid.* query values Steam appended to the
// return_to URL. expectedReturnTo is the absolute return URL we registered (sans
// the per-login nonce query): we assert the asserted openid.return_to matches it
// so a signed assertion can't be replayed against a different endpoint, pin
// openid.op_endpoint to Steam's provider (so a valid signature can only come from
// Steam, not an attacker-chosen op we'd happily query), and reject a missing
// openid.response_nonce. We then echo the params back to Steam with
// mode=check_authentication — without that step anyone could forge a return URL
// claiming any SteamID. Fails closed: any error returns ok=false.
//
// Replay note (S3): the response_nonce is shape-checked here; single-use
// enforcement of its value is the caller's job (it owns the TTL store).
func Verify(ctx context.Context, params url.Values, expectedReturnTo string) (steamID64 string, ok bool, err error) {
	// Reject obviously-forged identities before spending a round-trip.
	m := claimedIDRe.FindStringSubmatch(params.Get("openid.claimed_id"))
	if m == nil {
		return "", false, fmt.Errorf("steam: claimed_id not a steam identity: %q", params.Get("openid.claimed_id"))
	}
	steamID64 = m[1]

	// Pin the OpenID provider: a valid signature only means something if it was
	// issued by Steam's endpoint and not some attacker-chosen op.
	if ep := params.Get("openid.op_endpoint"); ep != openidEndpoint {
		return steamID64, false, fmt.Errorf("steam: unexpected op_endpoint %q", ep)
	}

	// Assert the assertion was minted for our return URL. Steam appends its own
	// query params (incl. the nonce) to return_to, so compare scheme+host+path.
	if !returnToMatches(params.Get("openid.return_to"), expectedReturnTo) {
		return steamID64, false, fmt.Errorf("steam: return_to mismatch: %q", params.Get("openid.return_to"))
	}

	// A well-formed assertion always carries a response_nonce; its absence is a
	// forgery signal. The caller enforces single-use of the value.
	if params.Get("openid.response_nonce") == "" {
		return steamID64, false, fmt.Errorf("steam: missing response_nonce")
	}

	// Copy the params and flip the mode so Steam re-signs/validates them.
	check := url.Values{}
	for k, v := range params {
		check[k] = v
	}
	check.Set("openid.mode", "check_authentication")

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, openidEndpoint, strings.NewReader(check.Encode()))
	if err != nil {
		return "", false, fmt.Errorf("steam: build check_authentication: %w", err)
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")

	resp, err := client.Do(req)
	if err != nil {
		return "", false, fmt.Errorf("steam: check_authentication request: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", false, fmt.Errorf("steam: check_authentication status %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 4096))
	if err != nil {
		return "", false, fmt.Errorf("steam: read check_authentication: %w", err)
	}
	// Response is a small key-value document; we require an explicit is_valid:true.
	for _, line := range strings.Split(string(body), "\n") {
		if strings.TrimSpace(line) == "is_valid:true" {
			return steamID64, true, nil
		}
	}
	return steamID64, false, nil
}

// returnToMatches reports whether the asserted openid.return_to refers to the
// same endpoint we registered. Steam echoes our return_to back with its own
// extra query params (mode, nonce, signed, ...), so we compare on
// scheme+host+path and ignore the query string. Empty/unparseable → no match.
func returnToMatches(asserted, expected string) bool {
	if asserted == "" {
		return false
	}
	a, err1 := url.Parse(asserted)
	e, err2 := url.Parse(expected)
	if err1 != nil || err2 != nil {
		return false
	}
	return strings.EqualFold(a.Scheme, e.Scheme) &&
		strings.EqualFold(a.Host, e.Host) &&
		a.Path == e.Path
}
