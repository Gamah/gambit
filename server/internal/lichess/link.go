// Package lichess is gamchess's remaining lichess surface: the two anonymous or
// near-anonymous things a server still does after the token moved to the client.
//
// # What this package is NOT, since HTTPFIX
//
// It is not an OAuth client, not a Board API client, and not a custodian. The
// PKCE exchange, the game streams, the moves, the seeks and the challenges all
// happen in the s&box client now, with a token that lives on the player's own
// machine. gamchess holds no lichess secret of any kind.
//
// What is left:
//
//   - AuthorizeURL — building the consent URL, because redirect_uri must be
//     derived ONCE from PUBLIC_BASE_URL and matched byte-for-byte at the
//     exchange, and deriving it server-side is also what keeps the test instance
//     pointing at itself rather than prod.
//   - Account — called EXACTLY ONCE per link, with a token that transits
//     gamchess and is immediately discarded, to learn who that token belongs to.
//     See the note on Account for why this one transit is the whole identity
//     story.
//   - tv.go — anonymous upstream, no token, no scope, no custody. None of the
//     above applies to it and none of it may creep in.
//
// # Facts re-derived 2026-08-05
//
// Per CLAUDE.md's re-derive rule, the constants here came from the live
// lichess-org/api OpenAPI spec and lichess-org/lila master on that date, not
// from this repo's history. Re-read the spec before trusting any of it.
package lichess

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
)

// Endpoints are package vars, not consts, so tests can point them at an
// httptest server. Production never reassigns them.
var (
	authorizeEndpoint = "https://lichess.org/oauth"
	accountEndpoint   = "https://lichess.org/api/account"
)

// Scopes is what a Gambit link asks lichess for, space-separated in the order
// they are shown to the player.
//
// board:play is the one that plays games — a single all-or-nothing grant
// covering seek, both streams, move, resign, draw, abort and chat, with no
// read-only subset. It also satisfies the challenge endpoints, whose spec lists
// challenge:write/bot:play/board:play as ALTERNATIVES.
//
// The rest were added when HTTPFIX forced a re-link on everyone anyway (owner
// decision, 2026-08-05). That matters because a scope change normally costs a
// full re-link for every linked player — there are no refresh tokens — so this
// branch is the one moment in the project's life when widening is free. It is
// not licence to widen again casually.
//
// TWO THINGS STAY OUT, and not for risk reasons:
//
//	web:mobile / web:polygon — their own descriptions are "Official Lichess
//	mobile app" and "Take Take Take". Taking one is claiming first-party status
//	to bypass a gate lichess put on third-party board clients deliberately. See
//	PLAN No. 13; do not "fix" the blitz seek this way.
//
//	web:mod — moderator tooling, not ours to ask for.
//
// AND THE DISCLOSURE COPY IS PART OF THIS CONSTANT. InfoScreen's Lichess branch
// and lichess_pages.go's consent page enumerate what the grant can do. Adding a
// scope here without rewriting them ships a lie to the one screen a cautious
// player reads before consenting.
//	msg:write — dropped before HTTPFIX shipped. It is SENDING ONLY and
//	permanently so (there is no msg:read scope at all, and reading an inbox is
//	web:mobile), so anything built on it is fire-and-forget by nature — and
//	nothing was. A scope nothing uses is one more line on the consent page.
const Scopes = "board:play puzzle:read puzzle:write follow:read"

// ClientID identifies Gambit to lichess. A CONSTANT, not config, and not a
// credential — worth being precise about, because it looks like both.
//
// lichess has no client registration and no way to reserve a name; their own
// error text is literally "client_id required (choose any)".
//
// It carries no operational force. lichess does NOT record it on the token: an
// AccessToken stores clientOrigin — the scheme://host of our redirect_uri — and
// has no client_id field. Everything that matters keys on that origin instead:
// the player's "revoke this app" button on /account/security, and any
// lichess-side "kill every Gambit token" request.
//
// It is public and IMPERSONABLE BY DESIGN: because lichess cannot bind a
// redirect_uri to an unregistered client_id, anyone may run a flow claiming this
// string and pointing at their own callback. It authenticates nothing. PKCE
// secures the exchange; the redirect URI decides who receives a code.
const ClientID = "net.gamah.gambit"

// AuthorizeURL is where the player's browser goes to consent.
//
// redirectURI must match the token exchange BYTE FOR BYTE. gamchess derives it
// once from PUBLIC_BASE_URL and hands the same value to the client for its
// exchange, rather than letting the client hardcode one — a hardcoded copy would
// silently break the test instance, which must point at itself.
//
// challenge is the client's PKCE S256 challenge. gamchess never sees the
// verifier, so a code parked at our callback is worthless to us by construction.
func AuthorizeURL(clientID, redirectURI, state, challenge string) string {
	q := url.Values{
		"response_type":         {"code"},
		"client_id":             {clientID},
		"redirect_uri":          {redirectURI},
		"scope":                 {Scopes},
		"state":                 {state},
		"code_challenge":        {challenge},
		"code_challenge_method": {"S256"},
	}
	return authorizeEndpoint + "?" + q.Encode()
}

// Account identifies the token's owner.
//
// # This is the ONE place a player's lichess token touches gamchess
//
// The client exchanges the code itself and holds the token; at link time it
// POSTs that token here once, gamchess asks lichess whose it is, and gamchess
// discards it. It is never stored, never logged, never put in an error string.
//
// Why it is needed at all: NOTHING else can establish a verified lichess
// identity. POST /api/token returns no user id; POST /api/token/test is
// anonymous but still requires POSTing the token, so it costs the same transit
// and returns only a userId (no display username, which is a second request);
// and a client-asserted id is a claim, which would let anyone squat a real
// account's row.
//
// GET /api/account is `security: [OAuth2: []]` — a token, but NO specific scope
// — so the token the player just minted reads it and no scope widens for this.
//
// The cost, stated honestly: "gamchess cannot hold your token" is a PROMISE
// here, not a structure. The owner's call (2026-08-05) is that this is
// acceptable — token compromise is not what we optimise against. What it obliges
// is the discipline above: lichess records clientOrigin per token, so abuse of a
// leaked Gambit token is attributable to Gambit and their lever is killing the
// whole app on that origin.
//
// id is the canonical lowercase key and the ONLY thing stored as identity;
// username is display casing and is cosmetic. Same rule as trusting only the
// SteamId Facepunch echoes back: believe the provider, never the caller.
func Account(ctx context.Context, token string) (id, username string, err error) {
	if err := guard(ctx); err != nil {
		return "", "", err
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, accountEndpoint, nil)
	if err != nil {
		return "", "", fmt.Errorf("lichess: build account request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		// Deliberately does not wrap anything derived from the request: an error
		// string is a place a token can end up.
		return "", "", fmt.Errorf("lichess: account request failed")
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", "", fmt.Errorf("lichess: account status %d", resp.StatusCode)
	}

	var acct struct {
		ID       string `json:"id"`
		Username string `json:"username"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, maxBody)).Decode(&acct); err != nil {
		return "", "", fmt.Errorf("lichess: decode account: %w", err)
	}
	if acct.ID == "" {
		return "", "", fmt.Errorf("lichess: account response carried no id")
	}
	if acct.Username == "" {
		acct.Username = acct.ID
	}
	return acct.ID, acct.Username, nil
}

// ValidUsername reports whether s could be a lichess username. lila's own rule:
// 2-30 chars of letters, digits, underscore and hyphen, starting with a letter
// or digit.
//
// Kept server-side because the rendezvous directory hands usernames BETWEEN
// clients, so a value from one client becomes a URL path segment in another's
// challenge. The client validates too; this is the copy that can't be skipped.
func ValidUsername(s string) bool {
	if len(s) < 2 || len(s) > 30 {
		return false
	}
	for i, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9':
		case (r == '_' || r == '-') && i > 0:
		default:
			return false
		}
	}
	return true
}
