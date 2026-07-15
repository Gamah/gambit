package lichess

import (
	"context"
	"encoding/json"
	"errors"
)

// ErrBadChannel means a channel key was not on the allowlist. It never reached
// lichess.
var ErrBadChannel = errors.New("lichess: unknown TV channel")

// lichess TV: the featured game on a channel, streamed.
//
// # This is the one lichess feature with no security surface upstream
//
// GET /api/tv/{channel}/feed is `security: []` — anonymous. No token, no scope,
// no custody question, nothing to encrypt, nothing to revoke, nothing to audit.
// None of the hard part of the Board API applies here, and none of the token
// machinery may creep in: TV must keep working for a player who has never linked
// a lichess account and never will.
//
// That is a fact about LICHESS's side. Our own proxy of it is still session-gated
// — anonymous upstream is exactly why an open endpoint here would be attractive
// to abuse, and every byte of that abuse would carry our IP and our User-Agent.
// See api.tv.
//
// # One upstream per channel
//
// The whole reason TV goes through gamchess rather than letting clients hit
// lichess directly: lichess advocates one stream held centrally and fanned out to
// N viewers. 100 players watching blitz cost lichess exactly one stream. The
// ref-counting that enforces that lives in api.tv; this file is only the reader.

// Channel is a lichess TV channel key.
type Channel string

// Every channel lichess publishes, and we offer all of them.
//
// # Why the variants ARE here
//
// M9 first shipped the six standard speeds, on the reasoning that the client's
// vendored chess rules are standard-only so a variant FEN could not be drawn.
// That reasoning was wrong, and it is worth recording why: the rule is real for
// PLAYING (the Board API path, where ChessGame parses the FEN and validates
// moves) and does not apply to the wall, which parses nothing. The client's
// SpectatorBoard3D takes the piece-placement field alone and walks its
// characters, so:
//
//   - chess960's X-FEN castling field (HDhd) is never read;
//   - crazyhouse's pockets ride on the end of the placement field
//     (…/RNBQKBNR[Pp]) and fall off the board walker's `file < 8` guard;
//   - threeCheck's counters ride at the end of the whole FEN;
//   - kingOfTheHill/antichess/atomic/horde/racingKings are plain standard
//     placement — only the RULES differ, and the wall doesn't know the rules.
//
// Verified against each variant's real starting FEN. Two of them hide state the
// wall cannot show (crazyhouse pockets, threeCheck counts); the client says so
// rather than letting a viewer think the board is broken.
//
// Keys are the lcfirst of lila's Tv.Channel and are CASE-SENSITIVE
// ("ultraBullet", not "ultrabullet"). Read off GET /api/tv/channels 2026-07-15 —
// all 16 of them, which is the whole set.
const (
	ChannelBest        Channel = "best" // "Top Rated"
	ChannelBullet      Channel = "bullet"
	ChannelBlitz       Channel = "blitz"
	ChannelRapid       Channel = "rapid"
	ChannelClassical   Channel = "classical"
	ChannelUltraBullet Channel = "ultraBullet"

	ChannelChess960      Channel = "chess960"
	ChannelCrazyhouse    Channel = "crazyhouse"
	ChannelKingOfTheHill Channel = "kingOfTheHill"
	ChannelThreeCheck    Channel = "threeCheck"
	ChannelAntichess     Channel = "antichess"
	ChannelAtomic        Channel = "atomic"
	ChannelHorde         Channel = "horde"
	ChannelRacingKings   Channel = "racingKings"

	ChannelBot      Channel = "bot"
	ChannelComputer Channel = "computer"
)

// ChannelDefault is what a lobby suggests when nobody has chosen.
const ChannelDefault = ChannelBlitz

// channels is the allowlist, and it is a SECURITY boundary, not a menu. The
// channel key arrives from the wire and is concatenated into a lichess URL — an
// arbitrary string reaching that URL is a request forgery against lichess with
// our IP and our User-Agent on it. Nothing may build a TV URL from a key that did
// not come out of ValidChannel.
//
// That it currently holds EVERY channel lichess offers does not make it
// decoration: the point is that the set is closed and ours, not that it is small.
// A map rather than url.PathEscape — escaping makes a hostile key safe, but it
// still sends it.
var channels = map[Channel]string{
	ChannelBest:        "Top Rated",
	ChannelBullet:      "Bullet",
	ChannelBlitz:       "Blitz",
	ChannelRapid:       "Rapid",
	ChannelClassical:   "Classical",
	ChannelUltraBullet: "UltraBullet",

	ChannelChess960:      "Chess960",
	ChannelCrazyhouse:    "Crazyhouse",
	ChannelKingOfTheHill: "King of the Hill",
	ChannelThreeCheck:    "Three-check",
	ChannelAntichess:     "Antichess",
	ChannelAtomic:        "Atomic",
	ChannelHorde:         "Horde",
	ChannelRacingKings:   "Racing Kings",

	ChannelBot:      "Bot",
	ChannelComputer: "Computer",
}

// ChannelOrder is the display order — speeds, then variants, then bots. Kept
// separate from the map because a Go map has no order and the wall wants a
// stable cycle. Must stay in step with the client's LichessTv.All.
var ChannelOrder = []Channel{
	ChannelBest, ChannelBullet, ChannelBlitz, ChannelRapid, ChannelClassical, ChannelUltraBullet,
	ChannelChess960, ChannelCrazyhouse, ChannelKingOfTheHill, ChannelThreeCheck,
	ChannelAntichess, ChannelAtomic, ChannelHorde, ChannelRacingKings,
	ChannelBot, ChannelComputer,
}

// ValidChannel maps a wire string to a channel we're willing to ask lichess for.
// The ONLY way to obtain a Channel from untrusted input.
func ValidChannel(s string) (Channel, bool) {
	c := Channel(s)
	if _, ok := channels[c]; !ok {
		return "", false
	}
	return c, true
}

// ChannelLabel is the human name for a valid channel, or "" for anything else.
func ChannelLabel(c Channel) string { return channels[c] }

// TvPlayer is one side of the featured game.
//
// The shape is lichess's, read off the live feed on 2026-07-15 — NOT flat:
//
//	{"color":"white","user":{"name":"DiazVelandia","title":"FM","id":"diazvelandia"},
//	 "rating":2954,"seconds":60}
//
// so name/title nest under `user` while rating/seconds are siblings of it. `user`
// is absent for an anonymous or AI player (the bot/computer channels), which is
// why Name() tolerates a nil one rather than assuming.
//
// Rating/Title are display-only. A stranger on lichess TV has no Gambit identity
// and no SteamID — nothing here may ever be treated as a caller.
type TvPlayer struct {
	Color string `json:"color"`
	User  *struct {
		Name  string `json:"name"`
		Title string `json:"title,omitempty"`
		ID    string `json:"id,omitempty"`
	} `json:"user,omitempty"`
	Rating int `json:"rating,omitempty"`
	// Seconds is the STARTING clock for this side, present on featured only. It is
	// what lets the wall show clocks before the first fen frame arrives.
	Seconds int `json:"seconds,omitempty"`
}

// Name is the display name, or "Anonymous" when lichess sent no user (AI/anon).
func (p TvPlayer) Name() string {
	if p.User == nil || p.User.Name == "" {
		return "Anonymous"
	}
	return p.User.Name
}

// Title is the player's title ("FM", "GM", …) or "".
func (p TvPlayer) Title() string {
	if p.User == nil {
		return ""
	}
	return p.User.Title
}

// TvFeatured is the `featured` message: a new game took over the channel.
// lichess sends one immediately on connect, and again each time the featured
// game changes (typically because the last one ended).
type TvFeatured struct {
	ID      string     `json:"id"`
	Orient  string     `json:"orientation"`
	Players []TvPlayer `json:"players"`
	Fen     string     `json:"fen"`
}

// side returns the player of the given colour, or a zero TvPlayer.
func (f TvFeatured) side(color string) TvPlayer {
	for _, p := range f.Players {
		if p.Color == color {
			return p
		}
	}
	return TvPlayer{}
}

// White and Black pick a side by colour rather than by index — lichess sends the
// array in no documented order, and assuming [0] is white is the kind of thing
// that works until it doesn't.
func (f TvFeatured) White() TvPlayer { return f.side("white") }
func (f TvFeatured) Black() TvPlayer { return f.side("black") }

// TvFen is the `fen` message: a move happened in the featured game.
//
// WC/BC are CLOCKS IN SECONDS — not the milliseconds the Board API uses for the
// same idea. Two lichess endpoints, two units; seconds is what TimeControl.Format
// takes on the client, so this one happens to need no conversion. Don't
// generalise from it.
type TvFen struct {
	Fen string `json:"fen"`
	LM  string `json:"lm"` // last move, UCI
	WC  int    `json:"wc"`
	BC  int    `json:"bc"`
}

// TvEvent is one decoded frame. Exactly one pointer is non-nil.
type TvEvent struct {
	Type     string
	Featured *TvFeatured
	Fen      *TvFen
}

// StreamTv holds /api/tv/{channel}/feed open, calling fn per frame, until the
// context is cancelled or lichess closes it. ANONYMOUS — no token, by design.
//
// c must have come from ValidChannel; passing a Channel built any other way is
// how an arbitrary string reaches a lichess URL.
func StreamTv(ctx context.Context, c Channel, fn func(TvEvent)) error {
	if _, ok := channels[c]; !ok {
		// Belt and braces. The caller should have validated, and this is not a
		// substitute for that, but a bad key must never become an outbound request.
		return ErrBadChannel
	}
	u := apiBase + "/api/tv/" + string(c) + "/feed"
	return stream(ctx, "", u, func(line []byte) error {
		if ev, ok := DecodeTvFrame(line); ok {
			fn(ev)
		}
		// A malformed line must not kill a live feed — skip it. The next fen frame
		// carries the whole position anyway, so there is nothing to recover.
		return nil
	})
}

// DecodeTvFrame decodes one ndjson line from the TV feed. ok=false means the line
// was unreadable and should be skipped.
//
// Exported so the relay's tests can drive the state machine from REAL captured
// frames rather than from hand-built structs — the two things this function gets
// right (the envelope and the player shape) are both things that were wrong when
// written from memory, so a test that bypasses it would prove nothing.
func DecodeTvFrame(line []byte) (TvEvent, bool) {
	// The TV feed's envelope is {"t":<type>,"d":{…}} — NOT the {"type":…} with
	// fields inline that the Board API stream uses. Two lichess streams, two
	// envelopes; verified against the live feed 2026-07-15.
	var head struct {
		T string          `json:"t"`
		D json.RawMessage `json:"d"`
	}
	if err := json.Unmarshal(line, &head); err != nil {
		return TvEvent{}, false
	}

	switch head.T {
	case "featured":
		var f TvFeatured
		if err := json.Unmarshal(head.D, &f); err != nil {
			return TvEvent{}, false
		}
		return TvEvent{Type: head.T, Featured: &f}, true
	case "fen":
		var f TvFen
		if err := json.Unmarshal(head.D, &f); err != nil {
			return TvEvent{}, false
		}
		return TvEvent{Type: head.T, Fen: &f}, true
	default:
		// Anything lichess adds later.
		return TvEvent{Type: head.T}, true
	}
}
