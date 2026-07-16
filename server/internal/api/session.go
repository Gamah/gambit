package api

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Sessions: stateless proof of "Steam/Facepunch told us this caller is SteamID64 N".
//
// Two audiences, one mechanism. A session is minted only at the end of a verified
// Steam OpenID return (the web) or a verified Facepunch token check (the game),
// and carries no authority beyond naming that SteamID.
//
// Stateless and HMAC-signed rather than a server-side map, so a deploy doesn't
// sign everyone out. The payload is `aud|steamID|expiry|MAC` — it holds no secret,
// and the MAC is what makes it unforgeable. There is no session table: gamchess's
// schema has no place for one and doesn't need it.
//
// # Why the audience is INSIDE the MAC
//
// The two audiences have deliberately different lifetimes — 30 days for a browser
// cookie, 1 hour for a game bearer (see sessionGameTTL). That difference is only
// real if the two tokens are not interchangeable. Sign `steamID|expiry` alone and
// both audiences produce the same bytes under the same key: a leaked 30-day cookie
// value, replayed as `gcs_<value>`, would authorise the game API — including
// playing lichess games as that account — for its full month, and the short game
// TTL would be decoration. Domain-separating the MAC is what makes each token
// verify only in its own lane.
const (
	sessionCookie = "gamchess_session"
	sessionTTL    = 30 * 24 * time.Hour

	// audWeb is the archive viewer's cookie. Long-lived: it authorises reading
	// your own game list and little else, and signing in again is a Steam redirect.
	audWeb = "web"

	// audGame is the s&box client's bearer, presented as `Authorization: Bearer
	// gcs_<value>`.
	audGame = "game"

	// gamePrefix marks a game session bearer so requireSteam can tell it from a
	// Facepunch token without a second header. Not a secret and not a namespace —
	// just a discriminator on a field that already carries two kinds of thing.
	gamePrefix = "gcs_"

	// sessionGameTTL is short ON PURPOSE, and it is the one real tradeoff in the
	// session design. A game session authorises everything that SteamID can do,
	// including playing lichess games as them, and sessions are stateless — so
	// there is NO way to revoke one short of rotating SESSION_SECRET, which signs
	// every player and every browser out at once. An hour still removes ~700
	// Facepunch round-trips from a polling client's hour, which is the entire
	// point; a longer window buys nothing and costs a much bigger thing to leak.
	sessionGameTTL = time.Hour
)

// sessionKey signs session cookies. Random per process unless SESSION_SECRET is
// set — which is a deliberate default: with no config you get working sessions
// that simply don't survive a restart (one click to sign back in), and nobody has
// to invent a secret to get started. Set SESSION_SECRET to keep sessions across
// deploys.
type sessions struct {
	key []byte
}

func newSessions(secret string) *sessions {
	if secret != "" {
		sum := sha256.Sum256([]byte(secret))
		return &sessions{key: sum[:]}
	}
	key := make([]byte, 32)
	if _, err := rand.Read(key); err != nil {
		// crypto/rand failing is not survivable — a predictable key means forgeable
		// sessions, so refuse to run rather than degrade.
		panic("gamchess: cannot read crypto/rand for the session key: " + err.Error())
	}
	return &sessions{key: key}
}

func (s *sessions) mac(payload string) string {
	m := hmac.New(sha256.New, s.key)
	m.Write([]byte(payload))
	return hex.EncodeToString(m.Sum(nil))
}

// issueFor mints a signed value naming steamID, scoped to one audience.
func (s *sessions) issueFor(aud string, steamID int64, ttl time.Duration) string {
	payload := fmt.Sprintf("%s|%d|%d", aud, steamID, time.Now().Add(ttl).Unix())
	return base64.RawURLEncoding.EncodeToString([]byte(payload + "|" + s.mac(payload)))
}

// issue mints a signed cookie value for an OpenID-verified SteamID.
func (s *sessions) issue(steamID int64) string {
	return s.issueFor(audWeb, steamID, sessionTTL)
}

// issueGame mints the s&box client's bearer. The caller must already have
// FP-verified steamID — this function attests nothing on its own.
func (s *sessions) issueGame(steamID int64) (string, time.Time) {
	exp := time.Now().Add(sessionGameTTL)
	return gamePrefix + s.issueFor(audGame, steamID, sessionGameTTL), exp
}

// verify returns the SteamID a value proves FOR THE GIVEN AUDIENCE, or (0, false).
// Fails closed on any malformed, expired, wrong-audience, or badly-signed value.
//
// A value minted for a different audience fails here at the MAC compare, not at a
// string check — the audience is signed, so there is nothing to strip or spoof.
func (s *sessions) verify(aud, value string) (int64, bool) {
	raw, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil {
		return 0, false
	}
	parts := strings.Split(string(raw), "|")
	if len(parts) != 4 {
		return 0, false
	}
	payload, gotMAC := parts[0]+"|"+parts[1]+"|"+parts[2], parts[3]

	// Constant-time: a timing oracle on the MAC would let it be forged byte by byte.
	if subtle.ConstantTimeCompare([]byte(gotMAC), []byte(s.mac(payload))) != 1 {
		return 0, false
	}
	// Checked after the MAC, so this only ever rejects a token we really signed —
	// an attacker learns nothing from reaching it.
	if subtle.ConstantTimeCompare([]byte(parts[0]), []byte(aud)) != 1 {
		return 0, false
	}

	steamID, err := strconv.ParseInt(parts[1], 10, 64)
	if err != nil || steamID <= 0 {
		return 0, false
	}
	exp, err := strconv.ParseInt(parts[2], 10, 64)
	if err != nil || time.Now().Unix() > exp {
		return 0, false
	}
	return steamID, true
}

// readGame resolves a game bearer. Reports ok=false for anything without our
// prefix, so requireSteam can fall through to the Facepunch path.
func (s *sessions) readGame(bearer string) (int64, bool) {
	if !strings.HasPrefix(bearer, gamePrefix) {
		return 0, false
	}
	return s.verify(audGame, strings.TrimPrefix(bearer, gamePrefix))
}

// read returns the SteamID a cookie proves, or (0, false).
func (s *sessions) read(r *http.Request) (int64, bool) {
	c, err := r.Cookie(sessionCookie)
	if err != nil {
		return 0, false
	}
	return s.verify(audWeb, c.Value)
}

func (s *sessions) set(w http.ResponseWriter, steamID int64) {
	http.SetCookie(w, &http.Cookie{
		Name:  sessionCookie,
		Value: s.issue(steamID),
		Path:  "/",
		// HttpOnly: no script needs this, and the viewer's JS never reads it.
		HttpOnly: true,
		// Caddy terminates TLS in front of us, so the cookie is only ever seen
		// over HTTPS even though we speak plain HTTP on loopback.
		Secure: true,
		// Lax, not Strict: the OpenID return is a top-level GET navigation from
		// steamcommunity.com, and Strict would drop the cookie on exactly that
		// hop — you'd sign in and land logged out.
		SameSite: http.SameSiteLaxMode,
		Expires:  time.Now().Add(sessionTTL),
	})
}

func (s *sessions) clear(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookie,
		Value:    "",
		Path:     "/",
		HttpOnly: true,
		Secure:   true,
		SameSite: http.SameSiteLaxMode,
		MaxAge:   -1,
	})
}
