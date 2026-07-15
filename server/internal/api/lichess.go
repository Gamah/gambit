package api

import (
	"encoding/json"
	"net/http"
	"regexp"
	"strings"
)

// The sign-in dance, and why it is shaped this way:
//
//  1. Client (FP-authed) generates a high-entropy `state` AND a PKCE verifier.
//     It POSTs only the state here. The verifier never leaves the client, and the
//     code_challenge never comes to us either — it rides straight to lichess in
//     the authorize URL the client builds itself.
//  2. Client sends the user to lichess with redirect_uri = <base>/callback.
//  3. lichess redirects the browser to /callback?code=&state=. We resolve
//     state -> steamID and park the code in memory. We never render it.
//  4. Client (FP-authed) polls /code, gets the code once, and exchanges it for a
//     bearer AT LICHESS, using the verifier only it holds.
//
// gamchess's entire job is routing a code back to the SteamID that started the
// flow. It never holds a lichess bearer, which is why there is no token column
// in the schema and no exchange path in this file.
//
// The accepted cost: the game must be running to sign in, since the client holds
// the verifier. Sign-in is initiated from the game anyway, so this is not a real
// constraint — it just means the website can't sign you into lichess on its own.

// stateRe bounds the client-generated state. The length floor is the point: state
// is the only thing tying a lichess redirect back to a SteamID, so a guessable
// one would let an attacker deliver their code to a victim's client and sign the
// victim into the attacker's account. 32+ chars of base64url is ~192 bits.
var stateRe = regexp.MustCompile(`^[A-Za-z0-9_-]{32,128}$`)

// relayEnabled reports whether PUBLIC_BASE_URL was configured. Blank disables the
// relay entirely (there is no redirect_uri to hand lichess).
func (h *handler) relayEnabled() bool { return h.baseURL != "" }

// POST /api/v1/auth/lichess/begin — FP token. Body {state}.
func (h *handler) lichessBegin(w http.ResponseWriter, r *http.Request) {
	if !h.relayEnabled() {
		writeError(w, http.StatusNotImplemented, "lichess relay disabled")
		return
	}
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}

	var body struct {
		State string `json:"state"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&body); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	state := strings.TrimSpace(body.State)
	if !stateRe.MatchString(state) {
		writeError(w, http.StatusBadRequest, "state must be 32-128 chars of [A-Za-z0-9_-]")
		return
	}

	// Deliberately no DB touch here. The relay is pure memory, so sign-in does
	// not depend on Postgres being up; the players row is created when we first
	// persist something for this SteamID (a link or an archived game).
	h.relay.Begin(state, steamID)

	// Hand back the exact redirect_uri so the client doesn't have to reconstruct
	// it — lichess requires a byte-identical value at authorize and at exchange.
	writeJSON(w, http.StatusOK, map[string]string{"redirect_uri": h.callbackURL()})
}

// GET /callback?code&state — no auth; this is a browser landing from lichess.
//
// NOTHING in this handler may log the query string: it carries the code. The
// requirement extends past the app — confirm no Caddy or system access log
// captures query strings for this vhost. Caddy writes no access log unless one
// is configured, so the default is already safe; the job is not to add one.
func (h *handler) lichessCallback(w http.ResponseWriter, r *http.Request) {
	if !h.relayEnabled() {
		h.renderCallback(w, http.StatusNotImplemented, false)
		return
	}
	q := r.URL.Query()
	code, state := q.Get("code"), q.Get("state")

	// lichess reports user-declined consent as ?error=access_denied. Treat any
	// error, or a missing half, as a plain failure — same neutral page either way.
	if q.Get("error") != "" || code == "" || state == "" {
		h.renderCallback(w, http.StatusOK, false)
		return
	}

	steamID, ok := h.relay.ResolveState(state)
	if !ok {
		// Unknown, expired, or already-used state. Deliberately indistinguishable
		// from any other failure in the response.
		h.renderCallback(w, http.StatusOK, false)
		return
	}
	h.relay.StashCode(steamID, code)
	h.renderCallback(w, http.StatusOK, true)
}

// GET /api/v1/auth/lichess/code — FP token. Returns this SteamID's pending code
// exactly once; 404 until one is present.
func (h *handler) lichessCode(w http.ResponseWriter, r *http.Request) {
	if !h.relayEnabled() {
		writeError(w, http.StatusNotImplemented, "lichess relay disabled")
		return
	}
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	code, ok := h.relay.TakeCode(steamID)
	if !ok {
		// The client polls this, so 404 is the normal "not yet" answer.
		w.Header().Set("Cache-Control", "no-store")
		writeError(w, http.StatusNotFound, "no pending code")
		return
	}
	w.Header().Set("Cache-Control", "no-store")
	writeJSON(w, http.StatusOK, map[string]string{"code": code})
}

func (h *handler) callbackURL() string {
	return strings.TrimSuffix(h.baseURL, "/") + "/callback"
}

// renderCallback writes the neutral browser page. It takes no code parameter and
// has no way to render one — the page must never show the code, because the
// whole design assumes the code goes back to the game over an authenticated
// channel, not through the user's clipboard.
func (h *handler) renderCallback(w http.ResponseWriter, status int, success bool) {
	msg := "Sign-in failed or expired. Head back to Terry's Gambit and try again."
	if success {
		msg = "You're signed in. Head back to Terry's Gambit — it's picking this up now."
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.WriteHeader(status)
	w.Write([]byte(`<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>Terry's Gambit</title>
<style>
  :root { color-scheme: light dark; }
  body { margin: 0; min-height: 100vh; display: flex; align-items: center;
         justify-content: center; font: 16px/1.5 system-ui, sans-serif;
         background: #111; color: #eee; }
  main { max-width: 32rem; padding: 2rem; text-align: center; }
  h1 { font-size: 1.25rem; margin: 0 0 .5rem; }
  p { margin: 0; opacity: .75; }
</style>
</head>
<body><main>
<h1>Terry's Gambit</h1>
<p>` + msg + `</p>
</main></body>
</html>`))
}
