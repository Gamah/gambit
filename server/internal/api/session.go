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

// Web sessions for the archive viewer.
//
// A session says exactly one thing: "Steam's OpenID provider told us this browser
// is SteamID64 N". It is minted only at the end of a verified OpenID return and
// carries no other authority.
//
// Stateless and HMAC-signed rather than a server-side map, so a deploy doesn't
// sign everyone out. The cookie is `steamID|expiry|MAC` — it holds no secret, and
// the MAC is what makes it unforgeable. There is no session table: gamchess's
// schema has no place for one and doesn't need it.
const (
	sessionCookie = "gamchess_session"
	sessionTTL    = 30 * 24 * time.Hour
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

// issue mints a signed cookie value for an OpenID-verified SteamID.
func (s *sessions) issue(steamID int64) string {
	payload := fmt.Sprintf("%d|%d", steamID, time.Now().Add(sessionTTL).Unix())
	return base64.RawURLEncoding.EncodeToString([]byte(payload + "|" + s.mac(payload)))
}

// read returns the SteamID a cookie proves, or (0, false). Fails closed on any
// malformed, expired, or badly-signed value.
func (s *sessions) read(r *http.Request) (int64, bool) {
	c, err := r.Cookie(sessionCookie)
	if err != nil {
		return 0, false
	}
	raw, err := base64.RawURLEncoding.DecodeString(c.Value)
	if err != nil {
		return 0, false
	}
	parts := strings.Split(string(raw), "|")
	if len(parts) != 3 {
		return 0, false
	}
	payload, gotMAC := parts[0]+"|"+parts[1], parts[2]

	// Constant-time: a timing oracle on the MAC would let it be forged byte by byte.
	if subtle.ConstantTimeCompare([]byte(gotMAC), []byte(s.mac(payload))) != 1 {
		return 0, false
	}

	steamID, err := strconv.ParseInt(parts[0], 10, 64)
	if err != nil || steamID <= 0 {
		return 0, false
	}
	exp, err := strconv.ParseInt(parts[1], 10, 64)
	if err != nil || time.Now().Unix() > exp {
		return 0, false
	}
	return steamID, true
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
