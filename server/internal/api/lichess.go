package api

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gamah/gambit/server/internal/store"
	"github.com/google/uuid"
	"go.uber.org/zap"
)

// The lichess link flow, and the directory that lets two seats find each other.
//
// # What gamchess does here, and what it deliberately does not
//
// Since HTTPFIX the s&box client holds its own lichess token and talks to
// lichess itself. gamchess is left with three jobs, each of which exists because
// the client genuinely cannot do it:
//
//  1. HOLD THE REDIRECT URI. lichess compares redirect_uri byte-for-byte between
//     authorize and token, and the client cannot listen on a socket, so there is
//     no loopback escape. The browser has to come back to a server.
//  2. SHOW THE DISCLOSURE. The consent has to happen somewhere with a URL bar the
//     player can read, and what we are asking for should be readable before
//     lichess's own screen asks them to approve it.
//  3. BE THE DIRECTORY. Two seats at a table need each other's lichess usernames
//     to challenge by name, and neither client may simply be told the other's.
//
// What it does NOT do: exchange the code (the client holds the PKCE verifier, so
// a code parked here is worthless to us by construction), hold a token, play a
// game, or revoke anything. There are no lichess secrets in this process.
//
// Two identities, one rule, unchanged: linking is Steam-session gated in a
// browser, everything in-game is Facepunch/session gated, and neither ever takes
// a SteamID from a request.

// linkTTL bounds an in-flight OAuth link. Ten minutes is long enough to read the
// consent screen and short enough that an abandoned flow is forgotten.
const linkTTL = 10 * time.Minute

// linkSlot is one in-flight link: who started it, the PKCE challenge they will
// prove, and the code lichess parks on the way back.
//
// NOTE WHAT IS NOT HERE: the code_verifier. The client mints the verifier/
// challenge pair and keeps the verifier. We hold only the challenge (to build
// the authorize URL) and, briefly, the code. A code without its verifier cannot
// be exchanged, so gamchess never holds anything that could become a token —
// not in transit, not at rest, not in a log.
//
// The SteamID is bound HERE, server-side, when the CLIENT registers the flow —
// never taken from the callback. That is what stops a stranger completing
// someone else's link.
type linkSlot struct {
	steamID   int64
	challenge string
	code      string
	created   time.Time
}

// linkSlots is the state store: the inverse of the pre-HTTPFIX pendingLinks.
//
// It used to be mint-on-redirect / burn-on-callback, because the callback was
// where the exchange happened. Now the callback only PARKS a code and the client
// collects it, so burn-on-use moved to collect — burning at the callback would
// make collection impossible.
//
// Same shape otherwise, and deliberately so: one mutex, a lazy sweep on the
// write path, check-and-burn in one method. Modelled on nonceStore (web_auth.go).
// In-memory is right: one container, and a restart mid-link just means "click
// the link again".
type linkSlots struct {
	mu sync.Mutex
	// byState is the browser's key — the credential on the browser hop.
	byState map[string]*linkSlot
	// bySteam is the client's key. The client never sends a state back to us:
	// collect is answered from the CALLER'S AUTHENTICATED SteamID and nothing
	// else, so a state guessed or stolen from a URL bar cannot collect a code.
	bySteam map[int64]string
	ttl     time.Duration
}

func newLinkSlots(ttl time.Duration) *linkSlots {
	return &linkSlots{byState: map[string]*linkSlot{}, bySteam: map[int64]string{}, ttl: ttl}
}

// start registers a flow for steamID and returns its state.
//
// NEWEST WINS: a player who starts a second link (they lost the tab, they
// changed their mind) evicts the first. Two live slots for one SteamID would
// make collect ambiguous, and the ambiguity would resolve differently depending
// on which browser tab they finished.
func (p *linkSlots) start(steamID int64, challenge string) (string, error) {
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		// A guessable state is a CSRF hole in the link flow. Refuse to start one.
		return "", err
	}
	state := base64.RawURLEncoding.EncodeToString(raw)

	p.mu.Lock()
	defer p.mu.Unlock()
	p.sweepLocked()

	if old, ok := p.bySteam[steamID]; ok {
		delete(p.byState, old)
	}
	p.byState[state] = &linkSlot{steamID: steamID, challenge: challenge, created: time.Now()}
	p.bySteam[steamID] = state
	return state, nil
}

func (p *linkSlots) sweepLocked() {
	now := time.Now()
	for state, slot := range p.byState {
		if now.Sub(slot.created) > p.ttl {
			delete(p.byState, state)
			if p.bySteam[slot.steamID] == state {
				delete(p.bySteam, slot.steamID)
			}
		}
	}
}

// forSteam returns the live slot a player started, or nil.
func (p *linkSlots) forSteam(steamID int64) *linkSlot {
	p.mu.Lock()
	defer p.mu.Unlock()
	state, ok := p.bySteam[steamID]
	if !ok {
		return nil
	}
	slot := p.byState[state]
	if slot == nil || time.Since(slot.created) > p.ttl {
		return nil
	}
	copy := *slot
	return &copy
}

// park stores the code lichess sent back, against its state.
//
// It does NOT burn the state — collect does. It DOES refuse a slot that already
// holds a code: a browser refresh replays a spent authorization code, and
// quietly overwriting a good code with a dead one would turn a stray F5 into a
// failed link with no explanation.
func (p *linkSlots) park(state, code string) bool {
	p.mu.Lock()
	defer p.mu.Unlock()

	slot, ok := p.byState[state]
	if !ok || time.Since(slot.created) > p.ttl || slot.code != "" {
		return false
	}
	slot.code = code
	return true
}

// collect burns the whole slot and returns the parked code, if any.
// (code == "", ok == true) means "the flow is live but the browser hasn't come
// back yet" — the slot survives that case, since there is nothing to burn.
func (p *linkSlots) collect(steamID int64) (code string, live bool) {
	p.mu.Lock()
	defer p.mu.Unlock()

	state, ok := p.bySteam[steamID]
	if !ok {
		return "", false
	}
	slot := p.byState[state]
	if slot == nil || time.Since(slot.created) > p.ttl {
		delete(p.byState, state)
		delete(p.bySteam, steamID)
		return "", false
	}
	if slot.code == "" {
		return "", true // still waiting on the browser
	}
	// Ready: burn the whole slot atomically. The code is single-use at lichess
	// too, so handing it out twice could only ever produce one working exchange
	// and one confusing failure.
	delete(p.byState, state)
	delete(p.bySteam, steamID)
	return slot.code, true
}

// lichessRedirectURL is THE redirect URI, derived once from PUBLIC_BASE_URL
// exactly as steamReturnURL() is.
//
// Deriving it once is not tidiness: lichess compares the authorize and token
// values byte for byte, so two hand-built copies that differ by a slash is a
// link flow that fails at the last step. It is also what keeps the test instance
// pointing at itself rather than prod — which is why it is RETURNED to the
// client for its exchange rather than hardcoded there.
func (h *handler) lichessRedirectURL() string {
	return strings.TrimSuffix(h.baseURL, "/") + "/lichess/callback"
}

// lichessReady reports whether linking can run. Since HTTPFIX that is one
// condition, not two: a base URL to come back to. There is no token key any
// more, because there is no token to encrypt.
func (h *handler) lichessReady() bool { return h.baseURL != "" }

// ── The five steps ──

// linkStartPost is step ①: the client registers a flow before showing the link.
type linkStartPost struct {
	// CodeChallenge is the client's PKCE S256 challenge. We never see the
	// verifier behind it.
	CodeChallenge string `json:"code_challenge"`
}

type linkStartJSON struct {
	State        string `json:"state"`
	AuthorizeURL string `json:"authorize_url"`
	RedirectURI  string `json:"redirect_uri"`
	LinkURL      string `json:"link_url"`
}

// POST /api/v1/lichess/link/start — "I am about to link; here is my challenge."
//
// FP/session gated, so the flow is bound to a verified SteamID before any
// browser is involved.
//
// It returns the authorize URL for completeness, but the client must NOT open it
// directly — see lichessLink below for why that would be strictly worse than
// today. The client shows LinkURL, which is a constant.
func (h *handler) lichessLinkStart(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	if !h.lichessReady() {
		writeError(w, http.StatusNotImplemented, "lichess linking is not configured on this server")
		return
	}

	var in linkStartPost
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	// RFC 7636: a base64url S256 challenge is 43 chars. Shape-check it rather
	// than pass anything at all into an authorize URL.
	if !validCodeChallenge(in.CodeChallenge) {
		writeError(w, http.StatusBadRequest, "code_challenge must be a base64url-encoded S256 challenge")
		return
	}

	state, err := h.links.start(steamID, in.CodeChallenge)
	if err != nil {
		h.log.Error("could not mint a link state", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	writeJSON(w, http.StatusOK, linkStartJSON{
		State:        state,
		AuthorizeURL: lichess.AuthorizeURL(lichess.ClientID, h.lichessRedirectURL(), state, in.CodeChallenge),
		RedirectURI:  h.lichessRedirectURL(),
		LinkURL:      h.baseURL + "/lichess/link",
	})
}

// validCodeChallenge shape-checks an S256 PKCE challenge: 43 base64url chars,
// unpadded.
func validCodeChallenge(s string) bool {
	if len(s) != 43 {
		return false
	}
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
		default:
			return false
		}
	}
	return true
}

// GET /lichess/link — step ②: the page the player lands on, and the URL the
// in-game board copies to the clipboard.
//
// # This stays a CONSTANT, and the raw authorize URL must NOT replace it
//
// Showing the lichess URL directly looks simpler and is the call most likely to
// be got wrong. This URL is safe PRECISELY because it carries no secret: it is
// Steam-session gated, so whoever opens it links THEIR OWN accounts, and handing
// it to a friend just links the friend. A raw authorize URL is bound to YOUR
// state and YOUR SteamID — a friend who opened it would consent on THEIR lichess
// account and YOU would end up holding a grant on it. That is strictly worse
// than anything the old custody design could do.
//
// Keeping a page in the middle is also what preserves the disclosure copy and
// the byte-exact redirect_uri.
func (h *handler) lichessLink(w http.ResponseWriter, r *http.Request) {
	if !h.lichessReady() {
		h.renderLichessPage(w, http.StatusNotImplemented, lichessPage{
			Title: "Lichess linking is switched off",
			Body:  "This gamchess instance has no lichess configuration, so accounts can't be linked here.",
		})
		return
	}
	steamID, ok := h.sessions.read(r)
	if !ok {
		// Sign in with Steam first. The OpenID return lands on "/", so the player
		// clicks the link once more — a redirect chain that survives that round
		// trip isn't worth the state it would need.
		http.Redirect(w, r, "/auth/steam/login", http.StatusFound)
		return
	}

	// The flow is registered by the GAME, not by this page: the client holds the
	// verifier, so it has to start. Someone who opens this URL without the game
	// running gets told that rather than a broken consent screen.
	if h.links.forSteam(steamID) == nil {
		h.renderLichessPage(w, http.StatusOK, lichessPage{
			Title: "Start from the game",
			Body: "Open the lichess board in Terry's Gambit and press Link, then come back to this page. " +
				"The game holds a secret that proves this link is yours, so it has to start there.",
		})
		return
	}
	h.renderLichessConsent(w)
}

// GET /lichess/start — the Continue button on the disclosure page. Bounces to
// lichess's own consent screen with the challenge the client registered.
func (h *handler) lichessStart(w http.ResponseWriter, r *http.Request) {
	if !h.lichessReady() {
		h.renderLichessPage(w, http.StatusNotImplemented, lichessPage{
			Title: "Lichess linking is switched off",
			Body:  "This gamchess instance has no lichess configuration.",
		})
		return
	}
	steamID, ok := h.sessions.read(r)
	if !ok {
		http.Redirect(w, r, "/auth/steam/login", http.StatusFound)
		return
	}

	slot := h.links.forSteam(steamID)
	if slot == nil {
		h.renderLichessPage(w, http.StatusBadRequest, lichessPage{
			Title: "That link expired",
			Body:  "Start again from the lichess board in-game.",
		})
		return
	}

	// The state we hand lichess is the one bound to this SteamID, and the
	// challenge is the client's. Neither came from this request.
	http.Redirect(w, r,
		lichess.AuthorizeURL(lichess.ClientID, h.lichessRedirectURL(), h.stateFor(steamID), slot.challenge),
		http.StatusFound)
}

// stateFor reads back the state bound to a SteamID. Separate from forSteam only
// because the slot copy deliberately doesn't carry it — the state is the
// browser's credential and has no business being handed around in-process more
// than it must.
func (h *handler) stateFor(steamID int64) string {
	h.links.mu.Lock()
	defer h.links.mu.Unlock()
	return h.links.bySteam[steamID]
}

// GET /lichess/callback — step ③: lichess sends the browser back here.
//
// It PARKS the code and does not exchange it: we hold no verifier, so the code
// is inert in our hands. The state is still the credential on the browser hop
// and an unknown/expired/replayed one is still refused with no detail, because
// it is either a bug or an attack and neither deserves one.
//
// NOTE the Caddy rule this route inherits: the OAuth code arrives in the QUERY
// STRING, so these vhosts must never gain a `log` directive — Caddy would write
// it to disk. Caddy logs nothing unless configured; the job is not to start.
//
// It also can no longer name the account it just linked, because gamchess has no
// token at this point and identity costs one. The page says so.
func (h *handler) lichessCallback(w http.ResponseWriter, r *http.Request) {
	if !h.lichessReady() {
		h.renderLichessPage(w, http.StatusNotImplemented, lichessPage{
			Title: "Lichess linking is switched off",
			Body:  "This gamchess instance has no lichess configuration.",
		})
		return
	}

	q := r.URL.Query()

	// lichess reports a refused consent here rather than by not calling back.
	if e := q.Get("error"); e != "" {
		h.renderLichessPage(w, http.StatusOK, lichessPage{
			Title: "Not linked",
			Body:  "You didn't approve the link on lichess, so nothing changed. You can close this tab.",
		})
		return
	}

	code := q.Get("code")
	if code == "" || !h.links.park(q.Get("state"), code) {
		h.log.Warn("lichess callback with an unknown, replayed or already-used state")
		h.renderLichessPage(w, http.StatusBadRequest, lichessPage{
			Title: "That link expired",
			Body:  "Start again from the lichess board in-game.",
		})
		return
	}

	h.renderLichessLinked(w)
}

// linkCollectJSON is step ④'s answer.
type linkCollectJSON struct {
	// Status is "none" (no flow running), "waiting" (the browser hasn't come
	// back) or "ready".
	Status string `json:"status"`
	Code   string `json:"code,omitempty"`
	// RedirectURI must be sent to lichess's token endpoint byte-identically to
	// the authorize call. The client uses THIS, never a hardcoded one — a
	// hardcoded copy silently breaks the test instance.
	RedirectURI string `json:"redirect_uri,omitempty"`
	ClientID    string `json:"client_id,omitempty"`
}

// POST /api/v1/lichess/link/collect — step ④: "has my browser come back yet?"
//
// Keyed on the CALLER'S AUTHENTICATED SteamID and never on a state from the
// body. That is the whole access control: a state seen in a URL bar, a browser
// history or a shoulder-surf collects nothing.
func (h *handler) lichessLinkCollect(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	code, live := h.links.collect(steamID)
	switch {
	case !live:
		writeJSON(w, http.StatusOK, linkCollectJSON{Status: "none"})
	case code == "":
		writeJSON(w, http.StatusOK, linkCollectJSON{Status: "waiting"})
	default:
		writeJSON(w, http.StatusOK, linkCollectJSON{
			Status:      "ready",
			Code:        code,
			RedirectURI: h.lichessRedirectURL(),
			ClientID:    lichess.ClientID,
		})
	}
}

// lichessClaimPost is step ⑤: the client has a token and wants an identity
// recorded against it.
type lichessClaimPost struct {
	// Token is the player's fresh lichess token. IT TRANSITS THIS PROCESS ONCE
	// AND IS DISCARDED — never stored, never logged, never put in an error
	// string. See lichess.Account for the full reasoning and the honest
	// statement of what that costs.
	Token string `json:"token"`
}

// POST /api/v1/lichess/claim — record who this player is on lichess.
//
// The client could tell us its own username (it can read /api/account itself),
// and we do not let it: an asserted identity is a claim, and a claim would let
// anyone squat a real account's row and lock its owner out of ever linking. So
// the token comes here, gamchess asks lichess whose it is, and lichess's answer
// is what gets stored. Same rule as trusting only the SteamId Facepunch echoes
// back.
func (h *handler) lichessClaim(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	if h.db == nil {
		writeError(w, http.StatusNotImplemented, "lichess is not configured")
		return
	}

	var in lichessClaimPost
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	in.Token = strings.TrimSpace(in.Token)
	if in.Token == "" {
		writeError(w, http.StatusBadRequest, "token is required")
		return
	}

	ctx := r.Context()
	lichessID, username, err := lichess.Account(ctx, in.Token)
	if err != nil {
		// Deliberately does not echo err: it is the one error in this file built
		// from a request that carried a token.
		h.log.Warn("lichess would not identify a claimed token")
		writeError(w, http.StatusBadGateway, "lichess wouldn't say who that token belongs to")
		return
	}

	// The player is Steam-verified, so the FK target is honest.
	if err := store.EnsurePlayer(ctx, h.db, steamID, true); err != nil {
		h.log.Error("ensure player failed on lichess claim", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	_, err = store.UpsertLichessLink(ctx, h.db, store.LichessLink{
		SteamID:   steamID,
		LichessID: lichessID,
		Username:  username,
	})
	if errors.Is(err, store.ErrLichessIDTaken) {
		// Someone else already holds this lichess account. Never a silent steal.
		//
		// Unlike the custody version, there is no token for us to revoke on the
		// way out: it is the player's, on their machine, and only they can kill
		// it. The copy tells them how.
		writeError(w, http.StatusConflict,
			"the lichess account "+username+" is linked to a different Steam account — unlink it there first")
		return
	}
	if err != nil {
		h.log.Error("could not store the lichess link", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	h.log.Info("lichess account linked", zap.Int64("steam_id", steamID),
		zap.String("lichess_id", lichessID))
	writeJSON(w, http.StatusOK, lichessLinkJSON{
		Linked:    true,
		LichessID: lichessID,
		Username:  username,
	})
}

// ── Status and unlink ──

// lichessLinkJSON is the wire shape of a link.
//
// `linked` and `username` KEEP THEIR NAMES across HTTPFIX on purpose: gamchess
// deploys before the s&box package updates, so for a window an OLD client polls
// this route, and a renamed field would null out its whole link state rather
// than degrade.
type lichessLinkJSON struct {
	Linked    bool   `json:"linked"`
	LichessID string `json:"lichess_id,omitempty"`
	Username  string `json:"username,omitempty"`
	// LinkURL saves the client hard-coding the path. It carries no secret.
	LinkURL string `json:"link_url,omitempty"`
}

// GET /api/v1/lichess — am I linked?
//
// Only ever answers about the CALLER. There is no ?steam_id=, for the same
// reason the archive has none: it would make every player's lichess identity
// enumerable by anyone who could sign in.
//
// The client no longer NEEDS this to know whether it is linked — it holds the
// token, so that is answerable locally, instantly and offline. What this answers
// is whether gamchess agrees, which matters for the rendezvous directory.
func (h *handler) lichessStatus(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	if h.db == nil {
		writeJSON(w, http.StatusOK, lichessLinkJSON{Linked: false})
		return
	}

	link, err := store.LichessLinkBySteamID(r.Context(), h.db, steamID)
	if errors.Is(err, store.ErrNotFound) {
		writeJSON(w, http.StatusOK, lichessLinkJSON{
			Linked:  false,
			LinkURL: h.baseURL + "/lichess/link",
		})
		return
	}
	if err != nil {
		h.log.Error("lichess status lookup failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, lichessLinkJSON{
		Linked:    true,
		LichessID: link.LichessID,
		Username:  link.Username,
	})
}

// DELETE /api/v1/lichess — unlink, from in-game.
//
// UNLINK IS FINALLY CORRECT RATHER THAN BEST-EFFORT, and the division of labour
// is the point: the CLIENT revokes (DELETE /api/token must be signed by the
// token, which only it holds) and gamchess forgets the row. The old version
// tried to do both and could only promise one.
func (h *handler) lichessUnlink(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	if h.db == nil {
		writeError(w, http.StatusNotImplemented, "lichess is not configured")
		return
	}
	if _, err := store.DeleteLichessLink(r.Context(), h.db, steamID); err != nil {
		h.log.Error("unlink failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	h.log.Info("lichess account unlinked", zap.Int64("steam_id", steamID))
	writeJSON(w, http.StatusOK, lichessLinkJSON{Linked: false})
}

// POST /lichess/unlink — the web unlink button on the linked page.
//
// POST, never GET: a link that unlinks would fire on any prefetch or crawl.
// Session-gated, and SameSite=Lax keeps the cookie off a cross-site POST.
//
// The web has no token to revoke with — the token is on the player's gaming PC —
// so this forgets the row and the copy points at /account/security, which is the
// only place a browser can actually revoke.
func (h *handler) lichessWebUnlink(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.sessions.read(r)
	if !ok {
		http.Redirect(w, r, "/auth/steam/login", http.StatusFound)
		return
	}
	if h.db == nil {
		h.renderLichessPage(w, http.StatusNotImplemented, lichessPage{
			Title: "Lichess is switched off", Body: "Nothing to unlink here.",
		})
		return
	}

	gone, err := store.DeleteLichessLink(r.Context(), h.db, steamID)
	switch {
	case err != nil:
		h.log.Error("web unlink failed", zap.Error(err))
		h.renderLichessPage(w, http.StatusInternalServerError, lichessPage{
			Title: "Couldn't unlink", Body: "Try again.",
		})
	case !gone:
		h.renderLichessPage(w, http.StatusOK, lichessPage{
			Title: "Not linked",
			Body:  "There was no lichess account linked to your Steam account.",
		})
	default:
		h.renderLichessUnlinked(w)
	}
}

// ── The rendezvous directory ──
//
// Two seats at one table want to play their game on lichess. One challenges the
// other BY NAME, so it needs the other's lichess username — and gamchess must
// not hand a player's lichess username to whoever asks for it.
//
// # The two-intent rule survives, and its justification has changed
//
// Under custody, both seats had to post an intent because gamchess held both
// their tokens: a one-sided start would have let any linked player drag any
// other into a real game from anywhere. That reason is GONE — each client now
// acts with its own token and can only ever commit itself, and a one-sided start
// just leaves a challenge sitting in someone's notifications.
//
// What the rule still does is DIRECTORY DISCLOSURE: seat B's username is
// revealed to seat A only once BOTH seats have posted an intent for the same
// client_game_id, and only to those two. Do not carry over the old
// "two independently-authenticated intents are what make it consent" wording —
// it is false now.

// rendezvousTTL bounds a pairing. Generous: the two clients post within a frame
// or two of each other, and a stale entry costs a map slot.
const rendezvousTTL = 10 * time.Minute

type rendezvousSeat struct {
	steamID  int64
	username string
	posted   time.Time
}

type rendezvous struct {
	mu sync.Mutex
	m  map[string]*[2]*rendezvousSeat // client_game_id → white, black
}

func newRendezvous() *rendezvous { return &rendezvous{m: map[string]*[2]*rendezvousSeat{}} }

// post records one seat's intent and reports whether both are now in.
func (rv *rendezvous) post(gameID string, side int, seat rendezvousSeat) (other *rendezvousSeat) {
	rv.mu.Lock()
	defer rv.mu.Unlock()

	now := time.Now()
	for id, pair := range rv.m {
		fresh := false
		for _, s := range pair {
			if s != nil && now.Sub(s.posted) <= rendezvousTTL {
				fresh = true
			}
		}
		if !fresh {
			delete(rv.m, id)
		}
	}

	pair, ok := rv.m[gameID]
	if !ok {
		pair = &[2]*rendezvousSeat{}
		rv.m[gameID] = pair
	}
	pair[side] = &seat

	o := pair[1-side]
	if o == nil || now.Sub(o.posted) > rendezvousTTL {
		return nil
	}
	return o
}

type rendezvousPost struct {
	ClientGameID string `json:"client_game_id"`
	WhiteSteamID string `json:"white_steam_id"`
	BlackSteamID string `json:"black_steam_id"`
}

type rendezvousJSON struct {
	// Ready is true once both seats have posted; only then is Opponent filled in.
	Ready bool `json:"ready"`
	// YourColor is "white" or "black" — which seat the caller holds.
	YourColor string `json:"your_color"`
	// Opponent is the other seat's lichess identity, once both have posted.
	Opponent string `json:"opponent,omitempty"`
	// OpponentID is the canonical lowercase id. The client checks an incoming
	// challenge's challenger id against this before accepting — cheap, strong,
	// and defence in depth against a bug rather than against a liar.
	OpponentID string `json:"opponent_id,omitempty"`
}

// POST /api/v1/lichess/rendezvous — "I'm playing this table's game on lichess;
// who is opposite me?"
//
// Both seats post, each with their own verified identity. The caller learns the
// other's lichess username only once both have.
func (h *handler) lichessRendezvous(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	if h.db == nil {
		writeError(w, http.StatusNotImplemented, "lichess is not configured")
		return
	}

	var in rendezvousPost
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	if _, err := uuid.Parse(in.ClientGameID); err != nil {
		writeError(w, http.StatusBadRequest, "client_game_id must be a UUID")
		return
	}
	white, okW := parseSeat(in.WhiteSteamID)
	black, okB := parseSeat(in.BlackSteamID)
	if !okW || !okB || white == nil || black == nil {
		writeError(w, http.StatusBadRequest, "both seats must be SteamID64 strings")
		return
	}
	if *white == *black {
		writeError(w, http.StatusBadRequest, "a game needs two different players")
		return
	}

	// Same rule as archiving: the seats are CLAIMS, so you may only ask about a
	// game you are sitting in.
	side := -1
	switch {
	case seatMatches(steamID, white):
		side = 0
	case seatMatches(steamID, black):
		side = 1
	default:
		writeError(w, http.StatusForbidden, "you may only rendezvous for a game you are seated in")
		return
	}

	links, err := store.LichessLinksBySteamIDs(r.Context(), h.db, []int64{*white, *black})
	if err != nil {
		h.log.Error("rendezvous link lookup failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	me, linked := links[steamID]
	if !linked {
		writeError(w, http.StatusBadRequest, "link your lichess account first")
		return
	}

	color := "white"
	if side == 1 {
		color = "black"
	}

	other := h.rendezvous.post(in.ClientGameID, side, rendezvousSeat{
		steamID:  steamID,
		username: me.Username,
		posted:   time.Now(),
	})
	if other == nil {
		writeJSON(w, http.StatusOK, rendezvousJSON{YourColor: color})
		return
	}

	// The other seat posted, so we may disclose. Read their identity from the
	// STORE rather than from what they posted — the rendezvous entry is a
	// liveness record, not an identity source.
	link, ok := links[other.steamID]
	if !ok {
		writeError(w, http.StatusConflict, "the other seat's lichess account isn't linked")
		return
	}
	writeJSON(w, http.StatusOK, rendezvousJSON{
		Ready:      true,
		YourColor:  color,
		Opponent:   link.Username,
		OpponentID: link.LichessID,
	})
}
