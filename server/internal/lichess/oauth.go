// Package lichess is gamchess's client for the lichess API: the OAuth
// (Authorization Code + PKCE) link flow, and the Board API that plays a real
// game on a linked account's behalf.
//
// Shaped after internal/steam deliberately — package-var endpoints so tests can
// stub the boundary, io.LimitReader on every body, and fail closed on anything
// unexpected. That package is the proven trust boundary in this service; this
// one is the second, so it copies rather than improvises.
//
// # Scope
//
// board:play, and NOTHING else. It is a single all-or-nothing grant covering
// seek, the event/game streams, move, resign, draw, abort and chat — there is no
// read-only subset. It also satisfies the challenge endpoints (their spec lists
// challenge:write/bot:play/board:play as ALTERNATIVES, any one of which is
// enough), so the direct-challenge play flow needs no second scope. Requesting
// more would be asking for capability we never use, and a scope change forces
// every linked player through a full re-link — there are no refresh tokens.
//
// # Facts re-derived 2026-07-15
//
// Per CLAUDE.md's re-derive rule, every constant here came from the live
// lichess-org/api OpenAPI spec and lichess-org/lila master on that date, not
// from this repo's history. A stale constraint is worse than no constraint —
// re-read the spec before trusting any of it.
package lichess

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Endpoints are package vars, not consts, so handler and unit tests can point
// them at an httptest server — exactly how steam/auth_test.go stubs
// steam.endpoint. Production never reassigns them.
var (
	authorizeEndpoint = "https://lichess.org/oauth"
	tokenEndpoint     = "https://lichess.org/api/token"
	accountEndpoint   = "https://lichess.org/api/account"
	apiBase           = "https://lichess.org"
)

// Scope is the one and only scope gamchess ever requests. See the package docs
// before widening it.
const Scope = "board:play"

// ClientID identifies Gambit to lichess. A CONSTANT, not config, and not a
// credential — this is worth being precise about, because it looks like both.
//
// lichess has no client registration and no way to reserve a name; their own
// error text is literally "client_id required (choose any)", and lila's only
// check is that it is non-empty (AuthorizationRequest.scala → ClientIdRequired).
//
// It carries no operational force. lichess does NOT record it on the token: an
// AccessToken stores clientOrigin — the scheme://host of our redirect_uri — and
// has no client_id field (AccessToken.scala). Everything that matters keys on
// that origin instead: the player's "revoke this app" button on
// /account/security (POST /oauth/revoke-client → revokeByClientOrigin, filtered
// to their own userId), and any lichess-side "kill every Gambit token" request.
// So changing this string invalidates nothing and revokes nothing.
//
// It is therefore NOT an env var. A knob that can differ between prod and test
// while nothing breaks is not configuration, it's noise — and an operator would
// reasonably assume it was a security setting. The thing that genuinely differs
// per instance is the redirect URI, which derives from PUBLIC_BASE_URL.
//
// It is also public and IMPERSONABLE BY DESIGN: every player who links sees it
// in their URL bar, and because lichess cannot bind a redirect_uri to an
// unregistered client_id, anyone may run an OAuth flow claiming this string and
// pointing at their own callback. It authenticates nothing. PKCE is what secures
// our exchange; the redirect URI is what decides who receives a code.
const ClientID = "net.gamah.gambit"

// client bounds every non-streaming call. The streams in board.go deliberately
// do NOT use it — a client timeout would kill a long-lived stream mid-game.
var client = &http.Client{Timeout: 10 * time.Second}

// maxBody caps what we'll read from lichess on a buffered call. Their JSON
// bodies are a few KB; this is slack, not a budget.
const maxBody = 1 << 20

// NewVerifier mints a PKCE verifier and its S256 challenge.
//
// 32 random bytes → 43 base64url chars, which is exactly RFC 7636's minimum
// verifier length. lichess has a CodeVerifierTooShort error whose threshold is
// undocumented; 43 is the floor the RFC mandates, so it is the shortest thing
// that can be correct — recorded as an open spike to confirm against the live
// server on first link.
func NewVerifier() (verifier, challenge string, err error) {
	raw := make([]byte, 32)
	if _, err := io.ReadFull(rand.Reader, raw); err != nil {
		return "", "", fmt.Errorf("lichess: read verifier: %w", err)
	}
	verifier = base64.RawURLEncoding.EncodeToString(raw)

	// S256 is the only method lichess accepts; "plain" is not an option.
	sum := sha256.Sum256([]byte(verifier))
	challenge = base64.RawURLEncoding.EncodeToString(sum[:])
	return verifier, challenge, nil
}

// AuthorizeURL is where we send the player's browser to consent.
//
// There is no client registration at lichess and no way to reserve a client_id —
// their own error text is literally "client_id required (choose any)". Ours is a
// reverse-domain string by convention only; it authenticates nothing, which is
// exactly why PKCE carries the exchange.
//
// redirectURI must match the token exchange BYTE FOR BYTE. Callers derive it
// once (from PUBLIC_BASE_URL) and pass the same value to both — never build it
// twice.
func AuthorizeURL(clientID, redirectURI, state, challenge string) string {
	q := url.Values{
		"response_type":         {"code"},
		"client_id":             {clientID},
		"redirect_uri":          {redirectURI},
		"scope":                 {Scope},
		"state":                 {state},
		"code_challenge":        {challenge},
		"code_challenge_method": {"S256"},
	}
	return authorizeEndpoint + "?" + q.Encode()
}

// Token is what the exchange yields. There is NO refresh token: lichess tokens
// are long-lived (expires_in ≈ 31536000, about a year) and re-linking is the
// only renewal path. Nothing here should be built to refresh.
type Token struct {
	AccessToken string `json:"access_token"`
	TokenType   string `json:"token_type"`
	ExpiresIn   int64  `json:"expires_in"`
}

// Exchange trades an authorization code for a token, proving with the PKCE
// verifier that we are the same party that started the flow.
//
// Fails closed: any transport error, non-200, undecodable body or empty token is
// an error and no token, never a partial success.
func Exchange(ctx context.Context, clientID, redirectURI, code, verifier string) (Token, error) {
	form := url.Values{
		"grant_type":    {"authorization_code"},
		"code":          {code},
		"code_verifier": {verifier},
		"redirect_uri":  {redirectURI},
		"client_id":     {clientID},
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, tokenEndpoint,
		strings.NewReader(form.Encode()))
	if err != nil {
		return Token{}, fmt.Errorf("lichess: build token request: %w", err)
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Accept", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return Token{}, fmt.Errorf("lichess: token request: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return Token{}, fmt.Errorf("lichess: token status %d", resp.StatusCode)
	}

	var t Token
	if err := json.NewDecoder(io.LimitReader(resp.Body, maxBody)).Decode(&t); err != nil {
		return Token{}, fmt.Errorf("lichess: decode token: %w", err)
	}
	if t.AccessToken == "" {
		return Token{}, fmt.Errorf("lichess: token response carried no access_token")
	}
	return t, nil
}

// Account identifies the token's owner.
//
// id is the canonical lowercase key and the ONLY thing we store as identity;
// username is display casing and is cosmetic. This is the lichess-side mirror of
// the Facepunch rule: trust what the provider echoes back, never what the client
// claimed.
func Account(ctx context.Context, token string) (id, username string, err error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, accountEndpoint, nil)
	if err != nil {
		return "", "", fmt.Errorf("lichess: build account request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return "", "", fmt.Errorf("lichess: account request: %w", err)
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

// Revoke kills a token at lichess. DELETE /api/token is signed BY the token
// being revoked — there is no admin form and no bulk endpoint, which is why
// unlink revokes one token at a time and why mass-revoke is not a lever we own.
//
// Best-effort by contract: unlink must delete our row whether or not this
// succeeds. A token we've forgotten but failed to revoke is bad; a row we can't
// delete because lichess is down is worse.
func Revoke(ctx context.Context, token string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodDelete, apiBase+"/api/token", nil)
	if err != nil {
		return fmt.Errorf("lichess: build revoke request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+token)

	resp, err := client.Do(req)
	if err != nil {
		return fmt.Errorf("lichess: revoke request: %w", err)
	}
	defer resp.Body.Close()
	// 204 is the documented success. 401 means it's already dead, which is the
	// outcome we wanted — treat it as success so a double-unlink isn't an error.
	if resp.StatusCode == http.StatusNoContent || resp.StatusCode == http.StatusUnauthorized {
		return nil
	}
	return fmt.Errorf("lichess: revoke status %d", resp.StatusCode)
}

// TokenStatus is one entry from an audit sweep. Live is false for a token
// lichess reports as null — revoked, expired, or never ours.
type TokenStatus struct {
	Live   bool
	UserID string
	Scopes string
}

// TokenTest is the audit sweep, and it matters more than it looks: it is the
// ONLY fast incident lever gamchess owns. If this store is ever suspected, we
// can learn which of our tokens are still live in seconds (1000 per call, and
// lichess's limit is ~10k credits per 10 min per IP) — but we cannot mass-revoke
// them, because DELETE /api/token kills exactly one and must be signed by it.
// Auditing is the capability; revoking is N serial calls.
//
// The endpoint needs no auth: it tells you about tokens you already hold.
func TokenTest(ctx context.Context, tokens []string) (map[string]TokenStatus, error) {
	out := make(map[string]TokenStatus, len(tokens))
	if len(tokens) == 0 {
		return out, nil
	}
	if len(tokens) > 1000 {
		return nil, fmt.Errorf("lichess: token test takes at most 1000 tokens, got %d", len(tokens))
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, apiBase+"/api/token/test",
		strings.NewReader(strings.Join(tokens, ",")))
	if err != nil {
		return nil, fmt.Errorf("lichess: build token test request: %w", err)
	}
	req.Header.Set("Content-Type", "text/plain")

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("lichess: token test request: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("lichess: token test status %d", resp.StatusCode)
	}

	// Per token: an object, or a JSON null for one that isn't live.
	var raw map[string]*struct {
		UserID  string `json:"userId"`
		Scopes  string `json:"scopes"`
		Expires *int64 `json:"expires"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, maxBody)).Decode(&raw); err != nil {
		return nil, fmt.Errorf("lichess: decode token test: %w", err)
	}

	for tok, info := range raw {
		if info == nil {
			out[tok] = TokenStatus{Live: false}
			continue
		}
		out[tok] = TokenStatus{Live: true, UserID: info.UserID, Scopes: info.Scopes}
	}
	return out, nil
}
