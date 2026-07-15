package api

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gamah/gambit/server/internal/store"
	"github.com/google/uuid"
	"go.uber.org/zap"
)

// The lichess link flow and the play relay's HTTP surface.
//
// Two halves, two identities, one rule:
//   - LINKING happens in a browser, gated on a Steam OpenID session, because the
//     OAuth consent has to happen somewhere with a URL bar the player can read.
//   - PLAYING happens in-game, gated on a Facepunch token.
//
// Both prove the same SteamID, and neither ever takes a SteamID from a request.

// pendingTTL bounds an in-flight OAuth link. Ten minutes is long enough to read
// the consent screen and short enough that an abandoned flow is forgotten.
const pendingTTL = 10 * time.Minute

// pendingLink is one in-flight OAuth link: the PKCE verifier, and the SteamID
// the flow was started for.
//
// The SteamID is bound HERE, server-side, at the moment we redirect — never
// taken from the callback. That is what stops a stranger completing someone
// else's link: the state string is the only thing the browser carries back, and
// it maps to a SteamID we chose.
type pendingLink struct {
	steamID  int64
	verifier string
	created  time.Time
}

// pendingLinks is the state store: mint-on-redirect, burn-on-callback.
//
// Modelled on nonceStore (web_auth.go) — same mutex, same lazy sweep on the
// write path, same check-and-burn in one method. In-memory is right: one
// container, and a restart mid-link just means "click the link again".
type pendingLinks struct {
	mu  sync.Mutex
	m   map[string]pendingLink
	ttl time.Duration
}

func newPendingLinks(ttl time.Duration) *pendingLinks {
	return &pendingLinks{m: map[string]pendingLink{}, ttl: ttl}
}

// put mints a state string bound to steamID and verifier.
func (p *pendingLinks) put(steamID int64, verifier string) (string, error) {
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		// A guessable state is a CSRF hole in the link flow. Refuse to start one.
		return "", err
	}
	state := base64.RawURLEncoding.EncodeToString(raw)

	p.mu.Lock()
	defer p.mu.Unlock()

	now := time.Now()
	for k, v := range p.m { // sweep on the write path, as nonceStore does
		if now.Sub(v.created) > p.ttl {
			delete(p.m, k)
		}
	}
	p.m[state] = pendingLink{steamID: steamID, verifier: verifier, created: now}
	return state, nil
}

// use consumes a state exactly once. False means unknown, expired, or replayed —
// all of which are "refuse", and none of which get a distinguishing message.
func (p *pendingLinks) use(state string) (pendingLink, bool) {
	p.mu.Lock()
	defer p.mu.Unlock()

	v, ok := p.m[state]
	if !ok {
		return pendingLink{}, false
	}
	delete(p.m, state) // burn it, whether or not it turns out to be fresh
	if time.Since(v.created) > p.ttl {
		return pendingLink{}, false
	}
	return v, true
}

// lichessRedirectURL is THE redirect URI, derived once from PUBLIC_BASE_URL
// exactly as steamReturnURL() is.
//
// Deriving it once is not tidiness: lichess compares the authorize and token
// values byte for byte, so two hand-built copies that differ by a slash is a
// link flow that fails at the last step. It also means the test instance points
// at itself and never at prod.
func (h *handler) lichessRedirectURL() string {
	return strings.TrimSuffix(h.baseURL, "/") + "/lichess/callback"
}

// lichessReady reports whether linking can run: a base URL to come back to, and
// a key to encrypt the token with.
func (h *handler) lichessReady() bool {
	return h.baseURL != "" && h.tokens != nil
}

// GET /lichess/link — the page the player lands on, and the URL the in-game
// board copies to the clipboard.
//
// Steam-session gated, which is what makes the copy-a-URL flow safe: the URL is
// a CONSTANT with no secret in it, so whoever opens it links THEIR OWN accounts.
// Handing it to a friend just links the friend. There is nothing to leak.
//
// This renders the disclosure page rather than bouncing straight to lichess: the
// player should read what they're about to grant before a consent screen asks
// them to approve it. /lichess/start is the bounce.
func (h *handler) lichessLink(w http.ResponseWriter, r *http.Request) {
	if !h.lichessReady() {
		h.renderLichessPage(w, http.StatusNotImplemented, lichessPage{
			Title: "Lichess linking is switched off",
			Body:  "This gamchess instance has no lichess configuration, so accounts can't be linked here.",
		})
		return
	}
	if _, ok := h.sessions.read(r); !ok {
		// Sign in with Steam first. The OpenID return lands on "/", so the player
		// clicks the link once more — a redirect chain that survives that round
		// trip isn't worth the state it would need.
		http.Redirect(w, r, "/auth/steam/login", http.StatusFound)
		return
	}
	h.renderLichessConsent(w)
}

// GET /lichess/start — mint the PKCE pair and bounce to lichess's consent screen.
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

	verifier, challenge, err := lichess.NewVerifier()
	if err != nil {
		h.log.Error("could not mint a PKCE verifier", zap.Error(err))
		h.renderLichessPage(w, http.StatusInternalServerError, lichessPage{
			Title: "Something went wrong",
			Body:  "Couldn't start the link. Try again.",
		})
		return
	}

	state, err := h.pending.put(steamID, verifier)
	if err != nil {
		h.log.Error("could not mint a link state", zap.Error(err))
		h.renderLichessPage(w, http.StatusInternalServerError, lichessPage{
			Title: "Something went wrong",
			Body:  "Couldn't start the link. Try again.",
		})
		return
	}

	http.Redirect(w, r, lichess.AuthorizeURL(lichess.ClientID, h.lichessRedirectURL(), state, challenge),
		http.StatusFound)
}

// GET /lichess/callback — lichess sends the browser back here.
//
// NOTE the Caddy rule this route inherits: the OAuth code arrives in the QUERY
// STRING, so these vhosts must never gain a `log` directive — Caddy would write
// the code to disk. Caddy logs nothing unless configured; the job is not to
// start. Same rule as /auth/steam/return.
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

	// The state carries the identity — burn it first, and fail closed. An
	// unknown, expired or replayed state gets no detail: it is either a bug or
	// an attack, and neither deserves one.
	pend, ok := h.pending.use(q.Get("state"))
	if !ok {
		h.log.Warn("lichess callback with an unknown or replayed state")
		h.renderLichessPage(w, http.StatusBadRequest, lichessPage{
			Title: "That link expired",
			Body:  "Start again from the lichess board in-game.",
		})
		return
	}

	code := q.Get("code")
	if code == "" {
		h.renderLichessPage(w, http.StatusBadRequest, lichessPage{
			Title: "That link expired",
			Body:  "Start again from the lichess board in-game.",
		})
		return
	}

	ctx := r.Context()

	// redirect_uri must be byte-identical to the one we authorized with.
	tok, err := lichess.Exchange(ctx, lichess.ClientID, h.lichessRedirectURL(), code, pend.verifier)
	if err != nil {
		h.log.Warn("lichess token exchange failed", zap.Error(err))
		h.renderLichessPage(w, http.StatusBadGateway, lichessPage{
			Title: "Couldn't finish the link",
			Body:  "Lichess didn't complete the exchange. Try again.",
		})
		return
	}

	// Identity is what lichess echoes back for this token — never anything the
	// browser told us. Same rule as trusting only Facepunch's SteamId.
	lichessID, username, err := lichess.Account(ctx, tok.AccessToken)
	if err != nil {
		h.log.Warn("lichess account lookup failed", zap.Error(err))
		h.renderLichessPage(w, http.StatusBadGateway, lichessPage{
			Title: "Couldn't finish the link",
			Body:  "Lichess wouldn't say who that token belongs to. Try again.",
		})
		return
	}

	// Encrypt BEFORE the row exists. There is no plaintext token column and no
	// code path that writes one.
	ct, nonce, err := h.tokens.Seal(tok.AccessToken)
	if err != nil {
		h.log.Error("could not encrypt the lichess token", zap.Error(err))
		h.renderLichessPage(w, http.StatusInternalServerError, lichessPage{
			Title: "Couldn't finish the link",
			Body:  "Try again.",
		})
		return
	}

	// The player is Steam-verified, so the FK target is honest.
	if err := store.EnsurePlayer(ctx, h.db, pend.steamID, true); err != nil {
		h.log.Error("ensure player failed on lichess link", zap.Error(err))
		h.renderLichessPage(w, http.StatusInternalServerError, lichessPage{
			Title: "Couldn't finish the link", Body: "Try again.",
		})
		return
	}

	_, err = store.UpsertLichessLink(ctx, h.db, store.LichessLink{
		SteamID:    pend.steamID,
		LichessID:  lichessID,
		Username:   username,
		TokenEnc:   ct,
		TokenNonce: nonce,
		Scopes:     lichess.Scope,
	})
	if errors.Is(err, store.ErrLichessIDTaken) {
		// Someone else already holds this lichess account. Never a silent steal.
		// The token we just minted is useless to us now — kill it rather than
		// leave a live grant lying around for an account we didn't link.
		h.revokeQuietly(tok.AccessToken)
		h.renderLichessPage(w, http.StatusConflict, lichessPage{
			Title: "That lichess account is already linked",
			Body: "The lichess account " + username + " is linked to a different Steam account. " +
				"Unlink it there first, then try again.",
		})
		return
	}
	if err != nil {
		h.log.Error("could not store the lichess link", zap.Error(err))
		h.revokeQuietly(tok.AccessToken)
		h.renderLichessPage(w, http.StatusInternalServerError, lichessPage{
			Title: "Couldn't finish the link", Body: "Try again.",
		})
		return
	}

	h.log.Info("lichess account linked", zap.Int64("steam_id", pend.steamID),
		zap.String("lichess_id", lichessID))
	h.renderLichessLinked(w, username)
}

// revokeQuietly kills a token we minted but couldn't use. Best-effort and
// detached: the browser is waiting, and a token we fail to revoke is a nuisance
// rather than a hole (it is ours, and unlinking revokes it later).
func (h *handler) revokeQuietly(token string) {
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if err := lichess.Revoke(ctx, token); err != nil {
			h.log.Warn("could not revoke an unused lichess token", zap.Error(err))
		}
	}()
}

// POST /lichess/unlink — the web unlink button on the linked page.
//
// POST, never GET: a link that unlinks would fire on any prefetch or crawl.
// Session-gated, and SameSite=Lax keeps the cookie off a cross-site POST.
//
// Shares revokeAndForget with the in-game DELETE, so there is exactly one
// implementation of "unlink" and the two surfaces cannot drift.
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

	switch err := h.revokeAndForget(r.Context(), steamID); {
	case errors.Is(err, store.ErrNotFound):
		h.renderLichessPage(w, http.StatusOK, lichessPage{
			Title: "Not linked",
			Body:  "There was no lichess account linked to your Steam account.",
		})
	case err != nil:
		h.log.Error("web unlink failed", zap.Error(err))
		h.renderLichessPage(w, http.StatusInternalServerError, lichessPage{
			Title: "Couldn't unlink", Body: "Try again.",
		})
	default:
		h.renderLichessPage(w, http.StatusOK, lichessPage{
			Title: "Unlinked",
			Body: "Your lichess account is no longer linked, and we've asked lichess to " +
				"revoke the token. You can link again any time from the lichess board in-game.",
		})
	}
}

// revokeAndForget is the one implementation of "unlink": best-effort revoke at
// lichess, then delete the row.
//
// The order matters and so does the "best-effort". We revoke first because a
// token we've already forgotten can never be revoked (the revoke must be signed
// BY that token — lichess has no admin form). But we delete the row whether or
// not the revoke worked, because a player who pressed unlink must end up
// unlinked; a failed revoke leaves a token that dies on its own in ~a year, and
// that is why the copy tells people lichess's Security page is the real off
// switch.
//
// Returns store.ErrNotFound when there was nothing linked.
func (h *handler) revokeAndForget(ctx context.Context, steamID int64) error {
	link, err := store.LichessLinkBySteamID(ctx, h.db, steamID)
	if err != nil {
		return err
	}

	if h.tokens != nil {
		if token, derr := h.tokens.Open(link.TokenEnc, link.TokenNonce); derr == nil {
			if rerr := lichess.Revoke(ctx, token); rerr != nil {
				h.log.Warn("could not revoke the lichess token on unlink",
					zap.Int64("steam_id", steamID), zap.Error(rerr))
			}
		} else {
			h.log.Warn("could not decrypt a token to revoke it", zap.Error(derr))
		}
	}

	if _, err := store.DeleteLichessLink(ctx, h.db, steamID); err != nil {
		return err
	}
	h.log.Info("lichess account unlinked", zap.Int64("steam_id", steamID))
	return nil
}

// lichessLinkJSON is the wire shape of a link, for the client's status poll.
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
	// Note what is NOT here: the token. It never crosses this seam in either
	// direction — the client authenticates to gamchess, and gamchess acts on
	// lichess.
	writeJSON(w, http.StatusOK, lichessLinkJSON{
		Linked:    true,
		LichessID: link.LichessID,
		Username:  link.Username,
	})
}

// DELETE /api/v1/lichess — unlink, from in-game.
//
// The same revoke-then-forget as the web button; see revokeAndForget for why the
// revoke is best-effort and the delete is not.
func (h *handler) lichessUnlink(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	if h.db == nil {
		writeError(w, http.StatusNotImplemented, "lichess is not configured")
		return
	}

	err := h.revokeAndForget(r.Context(), steamID)
	// Nothing linked is the state the caller asked for, so it's a 200, not a 404.
	if err != nil && !errors.Is(err, store.ErrNotFound) {
		h.log.Error("unlink failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, lichessLinkJSON{Linked: false})
}

// ── Play relay ──

// playPost is one seat's intent to play this table's game on lichess.
type playPost struct {
	ClientGameID string `json:"client_game_id"`
	WhiteSteamID string `json:"white_steam_id"`
	BlackSteamID string `json:"black_steam_id"`
	LimitSeconds int    `json:"limit_seconds"`
	IncrementSec int    `json:"increment_seconds"`
	Unlimited    bool   `json:"unlimited"`
}

// POST /api/v1/lichess/play — "I want this table's game played on lichess".
//
// BOTH seats must post this, each with their own Facepunch token, before a
// challenge is issued. See the relay's doc comment for why that is the whole
// authorisation story and not a formality.
func (h *handler) lichessPlay(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	if !h.relay.Enabled() {
		writeError(w, http.StatusNotImplemented, "lichess play is not configured on this server")
		return
	}

	var in playPost
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

	// Same rule as archiving: the seats are CLAIMS, so you may only ask for a
	// game you are sitting in.
	if !seatMatches(steamID, white) && !seatMatches(steamID, black) {
		writeError(w, http.StatusForbidden, "you may only start a game you are seated in")
		return
	}

	// Bullet can never reach lichess from any path — the Board API refuses
	// anything faster than blitz. Reject it here with a readable reason rather
	// than spending a lichess request to be told the same thing.
	if !in.Unlimited && !lichess.ChallengeCompatible(in.LimitSeconds, in.IncrementSec) {
		writeError(w, http.StatusBadRequest,
			"lichess's Board API won't play anything faster than blitz — bullet tables can't mirror")
		return
	}

	p, err := h.relay.Join(r.Context(), steamID, PlayRequest{
		ClientGameID: in.ClientGameID,
		WhiteSteamID: *white,
		BlackSteamID: *black,
		LimitSeconds: in.LimitSeconds,
		IncrementSec: in.IncrementSec,
		Unlimited:    in.Unlimited,
	})
	if err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}

	writeJSON(w, http.StatusOK, h.stamped(p, steamID))
}

// seekPost asks lichess's lobby to find a random opponent.
type seekPost struct {
	ClientGameID string `json:"client_game_id"`
	// Minutes, matching lichess's own unit for a seek (their challenge endpoint
	// uses seconds — the asymmetry is theirs).
	TimeMinutes  float64 `json:"time_minutes"`
	IncrementSec int     `json:"increment_seconds"`
	Rated        bool    `json:"rated"`
	RatingRange  string  `json:"rating_range"`
	Color        string  `json:"color"`
}

// POST /api/v1/lichess/seek — play a random lichess opponent.
//
// Unlike /play, this needs ONE caller, not two: you are spending your own token
// to play a stranger who opts in on lichess's side by their own choice. Nobody
// is dragged into anything, so there is nobody to get consent from.
//
// The opponent is not in this lobby, so the table's other seat is irrelevant —
// this works from a table you're sitting at alone.
func (h *handler) lichessSeek(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	if !h.relay.Enabled() {
		writeError(w, http.StatusNotImplemented, "lichess play is not configured on this server")
		return
	}

	var in seekPost
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	if _, err := uuid.Parse(in.ClientGameID); err != nil {
		writeError(w, http.StatusBadRequest, "client_game_id must be a UUID")
		return
	}

	limitSeconds := int(in.TimeMinutes * 60)

	// A real-time seek needs RAPID or slower — a stricter floor than a challenge's
	// blitz. Two different lila functions, both called isBoardCompatible; see the
	// lichess package. Refuse here with a reason rather than spend one of the five
	// seeks-per-minute the whole playerbase shares.
	if !lichess.SeekCompatible(limitSeconds, in.IncrementSec) {
		writeError(w, http.StatusBadRequest,
			"lichess's lobby only takes rapid or slower seeks — blitz and faster can't be seeked (a direct challenge at a table can)")
		return
	}
	if in.RatingRange != "" && !validRatingRange(in.RatingRange) {
		writeError(w, http.StatusBadRequest, `rating_range must look like "1500-1800"`)
		return
	}
	switch in.Color {
	case "", "random", "white", "black":
	default:
		writeError(w, http.StatusBadRequest, `color must be white, black or random`)
		return
	}

	p, err := h.relay.Join(r.Context(), steamID, PlayRequest{
		ClientGameID:  in.ClientGameID,
		Seek:          true,
		SeekerSteamID: steamID,
		LimitSeconds:  limitSeconds,
		IncrementSec:  in.IncrementSec,
		Rated:         in.Rated,
		RatingRange:   in.RatingRange,
		Color:         in.Color,
	})
	if err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, h.stamped(p, steamID))
}

// ratingRangeRe matches lichess's "1500-1800" form.
var ratingRangeRe = regexp.MustCompile(`^[0-9]{3,4}-[0-9]{3,4}$`)

func validRatingRange(s string) bool { return ratingRangeRe.MatchString(s) }

// DELETE /api/v1/lichess/play/{id} — withdraw a seek, or drop a pending pairing.
//
// For a seek this is what actually removes it from lichess's lobby (the held
// connection IS the seek), so it is not optional politeness: a player who walks
// away must stop being pairable.
func (h *handler) lichessPlayCancel(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	p, found := h.relay.Lookup(r.PathValue("id"))
	if !found {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}
	if _, seated := p.seatOf(steamID); !seated {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}
	if err := h.relay.Cancel(p, steamID); err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, h.stamped(p, steamID))
}

// stamped returns the play's state with YourColor filled in for this caller.
//
// The snapshot is shared by everyone watching, but "which side am I?" isn't —
// and for a seek it is the ONLY way a client can know, because the opponent is a
// stranger with no SteamID to match against.
func (h *handler) stamped(p *play, steamID int64) PlayState {
	state, _ := p.snapshot()
	if color, ok := p.seatOf(steamID); ok {
		state.YourColor = color
	}
	return state
}

// GET /api/v1/lichess/play/{id}?since=N — the game-state transport.
//
// A long poll: hangs up to pollHold seconds waiting for the state to pass
// version N, then answers with whatever it has. The client loops. See play.Wait
// for why this is a poll and not a WebSocket.
func (h *handler) lichessPlayState(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	if !h.relay.Enabled() {
		writeError(w, http.StatusNotImplemented, "lichess play is not configured on this server")
		return
	}

	p, found := h.relay.Lookup(r.PathValue("id"))
	if !found {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}
	// 404, not 403 — the same rule as the archive: a game you aren't in must be
	// indistinguishable from one that doesn't exist.
	if _, seated := p.seatOf(steamID); !seated {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}

	since, _ := strconv.ParseUint(r.URL.Query().Get("since"), 10, 64)
	state := p.Wait(r.Context(), since)
	if color, seated := p.seatOf(steamID); seated {
		state.YourColor = color
	}
	writeJSON(w, http.StatusOK, state)
}

// POST /api/v1/lichess/play/{id}/{action} — move / resign / draw / abort.
//
// gamchess acts with the CALLER's own token, and only for the seat they hold.
func (h *handler) lichessPlayAct(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	if !h.relay.Enabled() {
		writeError(w, http.StatusNotImplemented, "lichess play is not configured on this server")
		return
	}

	p, found := h.relay.Lookup(r.PathValue("id"))
	if !found {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}
	if _, seated := p.seatOf(steamID); !seated {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}

	var in struct {
		Uci string `json:"uci"`
	}
	// A body is optional (resign has none) — a decode failure is not fatal here.
	json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<10)).Decode(&in)

	if err := h.relay.Act(r.Context(), p, steamID, r.PathValue("action"), in.Uci); err != nil {
		var apiErr *lichess.APIError
		if errors.As(err, &apiErr) && apiErr.Unauthorized() {
			// The player revoked our grant on lichess (or it expired) — the link
			// row is now useless, and they need to re-link.
			writeError(w, http.StatusUnauthorized, "lichess rejected the token — re-link your account")
			return
		}
		writeError(w, http.StatusBadRequest, err.Error())
		return
	}
	// The confirmed state comes back down the stream, not from here: lichess is
	// the authority on what actually happened, and the poll will carry it.
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// POST /api/v1/lichess/audit — sweep our token store against lichess.
//
// THIS IS THE ONLY FAST INCIDENT LEVER WE OWN. If the store is ever suspected,
// this says which of our tokens are still live, in seconds. It cannot revoke
// them — lichess has no bulk revoke, and DELETE /api/token kills exactly one and
// must be signed by it. Auditing is the capability; mass revocation is not.
//
// Operator-gated: it needs the audit key, not a player session. Any linked
// player could otherwise learn how many accounts are linked.
func (h *handler) lichessAudit(w http.ResponseWriter, r *http.Request) {
	if h.auditKey == "" {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	given := strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "))
	// Constant-time: this is a shared secret on an otherwise unauthenticated
	// route, and a timing oracle would let it be guessed byte by byte. 404 (not
	// 401) on a miss, so the route isn't discoverable by probing.
	if subtle.ConstantTimeCompare([]byte(given), []byte(h.auditKey)) != 1 {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	if !h.relay.Enabled() {
		writeError(w, http.StatusNotImplemented, "lichess is not configured")
		return
	}

	links, err := store.AllLichessLinks(r.Context(), h.db)
	if err != nil {
		h.log.Error("audit: could not list links", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	type row struct {
		SteamID   string `json:"steam_id"`
		LichessID string `json:"lichess_id"`
		Live      bool   `json:"live"`
		Scopes    string `json:"scopes,omitempty"`
		Note      string `json:"note,omitempty"`
	}

	// Batch at lichess's documented limit of 1000 tokens per call.
	const batch = 1000
	out := make([]row, 0, len(links))
	byToken := map[string]store.LichessLink{}
	var tokens []string

	flush := func() error {
		if len(tokens) == 0 {
			return nil
		}
		res, err := lichess.TokenTest(r.Context(), tokens)
		if err != nil {
			return err
		}
		for _, tok := range tokens {
			link := byToken[tok]
			st := res[tok]
			out = append(out, row{
				SteamID:   strconv.FormatInt(link.SteamID, 10),
				LichessID: link.LichessID,
				Live:      st.Live,
				Scopes:    st.Scopes,
			})
		}
		tokens = tokens[:0]
		return nil
	}

	for _, link := range links {
		token, err := h.tokens.Open(link.TokenEnc, link.TokenNonce)
		if err != nil {
			out = append(out, row{
				SteamID:   strconv.FormatInt(link.SteamID, 10),
				LichessID: link.LichessID,
				Note:      "stored token will not decrypt",
			})
			continue
		}
		byToken[token] = link
		tokens = append(tokens, token)
		if len(tokens) == batch {
			if err := flush(); err != nil {
				h.log.Error("audit: token test failed", zap.Error(err))
				writeError(w, http.StatusBadGateway, "lichess token test failed")
				return
			}
		}
	}
	if err := flush(); err != nil {
		h.log.Error("audit: token test failed", zap.Error(err))
		writeError(w, http.StatusBadGateway, "lichess token test failed")
		return
	}

	live := 0
	for _, rr := range out {
		if rr.Live {
			live++
		}
	}
	h.log.Info("lichess token audit", zap.Int("links", len(links)), zap.Int("live", live))
	writeJSON(w, http.StatusOK, map[string]any{
		"links": len(links),
		"live":  live,
		"rows":  out,
	})
}
