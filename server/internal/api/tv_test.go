package api

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"regexp"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gorilla/websocket"
	"go.uber.org/zap"
)

// TV needs no DB at all, so like the lichess tests these run with a nil pool: if
// a query ever creeps onto this path, these panic rather than quietly passing.
func tvHandler(t *testing.T) *handler {
	t.Helper()
	h := &handler{
		log:      zap.NewNop(),
		version:  "test",
		sessions: newSessions("test-secret"),
	}
	h.tv = newTv(h.log)
	// Never reach the real lichess from a test. Overridden per-test where the
	// frames matter.
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		<-ctx.Done()
		return ctx.Err()
	}
	h.tv.gameResult = func(context.Context, string) (lichess.TvResult, error) {
		return lichess.TvResult{}, errors.New("no lichess in tests")
	}
	return h
}

// framePump wires a channel of frames into the tv relay and returns the send side.
func framePump(h *handler) chan lichess.TvEvent {
	frames := make(chan lichess.TvEvent)
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		for {
			select {
			case ev := <-frames:
				fn(ev)
			case <-ctx.Done():
				return ctx.Err()
			}
		}
	}
	return frames
}

// countingStream makes streamTv record how many upstreams were opened and block
// until cancelled. Returned pointer is read atomically.
func countingStream(h *handler) *int32 {
	var opened int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		return ctx.Err()
	}
	return &opened
}

// connsFor reads a channel's live connection count under the tv lock. Test-only,
// but declared on the production type so it sees the real field.
func (t *tv) connsFor(c lichess.Channel) int {
	t.mu.Lock()
	defer t.mu.Unlock()
	if ch, ok := t.channels[c]; ok {
		return ch.conns
	}
	return -1
}

// authed stamps a valid game session on a request.
func authed(t *testing.T, h *handler, r *http.Request) *http.Request {
	t.Helper()
	tok, _ := h.sessions.issueGame(76561197960287930)
	r.Header.Set("Authorization", "Bearer "+tok)
	return r
}

// ── The channel allowlist is a security boundary ──

// The channel key arrives from the wire and is concatenated into a lichess URL.
// An arbitrary string reaching that URL is a request forgery against lichess
// carrying our IP and our User-Agent — so the allowlist is the whole defence, and
// it must reject before anything opens a stream.
func TestTvChannelAllowlistRefusesJunk(t *testing.T) {
	bad := []string{
		"", " ",
		// Case matters: lichess spells it ultraBullet (lcfirst of lila's Tv.Channel).
		"BLITZ", "Blitz", "ultrabullet", "UltraBullet", "threecheck", "kingofthehill",
		// Whitespace is not trimmed anywhere, and must not be.
		"blitz ", " blitz",
		// Real lichess things that are NOT TV channels.
		"tv", "feed", "channels", "swiss", "team",
		// Traversal / injection shapes. These must never become a URL.
		"../account", "blitz/../../account", "blitz?foo=1", "blitz#x",
		"http://evil.example/", "//evil.example", "%2e%2e%2faccount",
	}
	for _, s := range bad {
		if c, ok := lichess.ValidChannel(s); ok {
			t.Errorf("ValidChannel(%q) accepted it as %q — this reaches a lichess URL", s, c)
		}
	}
}

// All 16 of lichess's channels, variants included.
//
// The variants were excluded at first on the reasoning that the client's vendored
// rules are standard-only and so "can't draw them". That rule governs PLAYING (where
// ChessGame parses the FEN and validates moves) and not the wall, which reads the
// piece-placement field and walks its characters — chess960's X-FEN castling is never
// read, crazyhouse's pockets fall off the `file < 8` guard, threeCheck's counters ride
// at the end of the FEN, and the rest are plain standard placement.
//
// This list is the whole of GET /api/tv/channels, read 2026-07-15.
func TestTvChannelAllowlistAcceptsEveryLichessChannel(t *testing.T) {
	want := []string{
		"best", "bullet", "blitz", "rapid", "classical", "ultraBullet",
		"chess960", "crazyhouse", "kingOfTheHill", "threeCheck",
		"antichess", "atomic", "horde", "racingKings",
		"bot", "computer",
	}
	for _, s := range want {
		c, ok := lichess.ValidChannel(s)
		if !ok {
			t.Fatalf("ValidChannel(%q) rejected a channel we offer", s)
		}
		if lichess.ChannelLabel(c) == "" {
			t.Errorf("channel %q has no label", s)
		}
	}
	if len(lichess.ChannelOrder) != len(want) {
		t.Errorf("ChannelOrder has %d entries, want %d — they must agree",
			len(lichess.ChannelOrder), len(want))
	}
	// Every ordered channel must be valid: the order list is what the client
	// cycles through, so an entry that ValidChannel refuses is a dead menu item.
	for _, c := range lichess.ChannelOrder {
		if _, ok := lichess.ValidChannel(string(c)); !ok {
			t.Errorf("ChannelOrder has %q, which ValidChannel rejects", c)
		}
	}
	// ...and the order must have no duplicates, or the client's cycle stutters.
	seen := map[lichess.Channel]bool{}
	for _, c := range lichess.ChannelOrder {
		if seen[c] {
			t.Errorf("ChannelOrder lists %q twice", c)
		}
		seen[c] = true
	}
	// Every allowlisted channel must be REACHABLE from the order list, or it's a
	// channel the server serves and no client can ever ask for.
	for _, c := range want {
		if !seen[lichess.Channel(c)] {
			t.Errorf("%q is allowlisted but missing from ChannelOrder", c)
		}
	}
	// Top Rated: the best game in progress, whatever the speed — what a wall wants.
	if lichess.ChannelDefault != lichess.ChannelBest {
		t.Errorf("default channel is %q, want best", lichess.ChannelDefault)
	}
}

// The client hand-mirrors this list in Code/Game/LichessTv.cs — no codegen, so the
// two can drift, and a drift means gamchess 404s a channel the settings board offers.
// This reads the client's file and holds it to ours.
//
// The dotnet harness checks the same agreement from the other side; this one exists so
// a change made ONLY on the server still fails, on the machine where the Go tests run.
func TestClientChannelListMatchesTheAllowlist(t *testing.T) {
	src, err := os.ReadFile("../../../client/Code/Game/LichessTv.cs")
	if err != nil {
		t.Skipf("client source not present: %v", err)
	}

	// Each entry looks like: new( "kingOfTheHill", "King of the Hill", Group.Variant ),
	re := regexp.MustCompile(`new\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*Group\.\w+\s*\)`)
	found := map[string]string{}
	for _, m := range re.FindAllStringSubmatch(string(src), -1) {
		found[m[1]] = m[2]
	}
	if len(found) == 0 {
		t.Fatal("parsed no channels out of LichessTv.cs — has the shape of LichessTv.All changed?")
	}

	for key, label := range found {
		c, ok := lichess.ValidChannel(key)
		if !ok {
			t.Errorf("client offers %q, which gamchess's allowlist rejects — it would 404", key)
			continue
		}
		if got := lichess.ChannelLabel(c); got != label {
			t.Errorf("channel %q: client labels it %q, server %q", key, label, got)
		}
	}
	for _, c := range lichess.ChannelOrder {
		if _, ok := found[string(c)]; !ok {
			t.Errorf("gamchess serves %q but the client never offers it", c)
		}
	}
}

// An unknown channel must 404 without opening anything upstream — checked before
// the upgrade, so a junk channel never becomes a socket or a stream.
func TestTvSocketUnknownChannel404s(t *testing.T) {
	h := tvHandler(t)
	opened := countingStream(h)

	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/notachannel", nil))
	r.SetPathValue("channel", "notachannel")
	w := httptest.NewRecorder()
	h.tvSocket(w, r)

	if w.Code != http.StatusNotFound {
		t.Fatalf("status %d, want 404", w.Code)
	}
	if n := atomic.LoadInt32(opened); n != 0 {
		t.Fatalf("opened %d upstream streams for a junk channel — must be 0", n)
	}
}

// ── The gate ──

// TV is anonymous UPSTREAM, which is exactly why our proxy of it must not be
// open: an unauthed relay is a free CDN for lichess's content pointable by any
// script, and every byte of it is attributed to our IP and our User-Agent. The
// gate runs BEFORE the upgrade — a bad credential is a plain 401 with no socket
// and no upstream.
func TestTvSocketRequiresASession(t *testing.T) {
	h := tvHandler(t)
	opened := countingStream(h)
	for _, tc := range []struct{ name, auth string }{
		{"no credentials", ""},
		{"garbage bearer", "Bearer nonsense"},
		{"forged session", "Bearer gcs_bm90LWEtcmVhbC1tYWM"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := httptest.NewRequest("GET", "/api/v1/tv/blitz", nil)
			r.SetPathValue("channel", "blitz")
			if tc.auth != "" {
				r.Header.Set("Authorization", tc.auth)
			}
			w := httptest.NewRecorder()
			h.tvSocket(w, r)
			if w.Code != http.StatusUnauthorized {
				t.Fatalf("status %d, want 401 — TV must never be an open relay", w.Code)
			}
		})
	}
	if n := atomic.LoadInt32(opened); n != 0 {
		t.Fatalf("opened %d upstreams for unauthed requests — must be 0", n)
	}
}

// ── The frame state machine ──

// Frames captured from the LIVE feed on 2026-07-15. The envelope is {"t":…,"d":…}
// — not the {"type":…} the Board API stream uses — and the player shape nests
// name/title under `user` with rating/seconds as siblings. Both were got wrong
// from memory first; these fixtures are the record of what lichess actually sends.
const (
	tvFeaturedFrame = `{"t":"featured","d":{"id":"BQ7M0K1i","orientation":"white","players":[` +
		`{"color":"white","user":{"name":"DiazVelandia","title":"FM","id":"diazvelandia"},"rating":2954,"seconds":60},` +
		`{"color":"black","user":{"name":"Yoozhik","id":"yoozhik"},"rating":2931,"seconds":60}],` +
		`"fen":"r3r1n1/1b1nqpbk/1p1p2pp/p1pPp3/2P1P3/3BBNNP/PP1QRPP1/5RK1 b - - 2 18"}}`

	tvFenFrame = `{"t":"fen","d":{"fen":"r3r1n1/1b2qpbk/1p1p1npp/p1pPp3/2P1P3/3BBNNP/PP1QRPP1/5RK1 w - - 3 19",` +
		`"lm":"d7f6","wc":56,"bc":51}}`
)

func TestTvFeaturedThenFen(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)

	st := waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })
	if st.URL != "https://lichess.org/BQ7M0K1i" {
		t.Errorf("url %q", st.URL)
	}
	if st.WhiteName != "DiazVelandia" || st.WhiteTitle != "FM" || st.WhiteRating != 2954 {
		t.Errorf("white = %q/%q/%d", st.WhiteName, st.WhiteTitle, st.WhiteRating)
	}
	if st.BlackName != "Yoozhik" || st.BlackTitle != "" || st.BlackRating != 2931 {
		t.Errorf("black = %q/%q/%d", st.BlackName, st.BlackTitle, st.BlackRating)
	}
	// featured's `seconds` is the starting clock. Without it the wall reads 0:00
	// until the first move lands.
	if st.WhiteClock != 60 || st.BlackClock != 60 {
		t.Errorf("clocks = %d/%d, want 60/60 from featured.seconds", st.WhiteClock, st.BlackClock)
	}
	// The FEN says "b" to move.
	if st.TickingSeat != "black" {
		t.Errorf("ticking seat %q, want black", st.TickingSeat)
	}
	if st.LastMoveUci != "" {
		t.Errorf("a fresh featured game has no last move, got %q", st.LastMoveUci)
	}

	frames <- decodeTvFrame(t, tvFenFrame)
	st = waitForState(t, ch, func(s TvState) bool { return s.LastMoveUci == "d7f6" })

	// Seconds, not milliseconds — lichess sends the TV clock in seconds and the
	// Board API sends the same idea in ms. The client renders these directly.
	if st.WhiteClock != 56 || st.BlackClock != 51 {
		t.Errorf("clocks = %d/%d, want 56/51", st.WhiteClock, st.BlackClock)
	}
	if st.TickingSeat != "white" {
		t.Errorf("ticking seat %q, want white", st.TickingSeat)
	}
	// The game id must survive a fen frame — only a featured frame changes it.
	if st.GameID != "BQ7M0K1i" {
		t.Errorf("fen frame clobbered the game id: %q", st.GameID)
	}
}

// The featured game changes when the old one ends. The stale last-move highlight
// must not carry across into a different game.
func TestTvFeaturedChangeClearsLastMove(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)
	ch := h.tv.watch(lichess.ChannelBlitz)

	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })
	frames <- decodeTvFrame(t, tvFenFrame)
	st := waitForState(t, ch, func(s TvState) bool { return s.LastMoveUci == "d7f6" })
	if st.LastMoveUci == "" {
		t.Fatal("setup: expected a last move")
	}

	next := `{"t":"featured","d":{"id":"NEWGAME1","orientation":"white","players":[` +
		`{"color":"white","user":{"name":"alice"},"rating":2100,"seconds":180},` +
		`{"color":"black","user":{"name":"bob"},"rating":2050,"seconds":180}],` +
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`
	frames <- decodeTvFrame(t, next)
	st = waitForState(t, ch, func(s TvState) bool { return s.GameID == "NEWGAME1" })

	if st.LastMoveUci != "" {
		t.Errorf("last move %q carried across into a different game", st.LastMoveUci)
	}
	if st.WhiteName != "alice" || st.BlackName != "bob" {
		t.Errorf("players = %q/%q", st.WhiteName, st.BlackName)
	}
	if st.TickingSeat != "white" {
		t.Errorf("ticking seat %q, want white", st.TickingSeat)
	}
}

// lichess sends the players array in no documented order. Reading [0] as white
// works until it doesn't.
func TestTvPlayersMatchedByColourNotIndex(t *testing.T) {
	reversed := `{"t":"featured","d":{"id":"X1","orientation":"white","players":[` +
		`{"color":"black","user":{"name":"blackplayer"},"rating":2000,"seconds":60},` +
		`{"color":"white","user":{"name":"whiteplayer"},"rating":2500,"seconds":60}],` +
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`

	ev := decodeTvFrame(t, reversed)
	if got := ev.Featured.White().Name(); got != "whiteplayer" {
		t.Errorf("White() = %q — matched by index, not colour", got)
	}
	if got := ev.Featured.Black().Name(); got != "blackplayer" {
		t.Errorf("Black() = %q — matched by index, not colour", got)
	}
}

// The bot/computer channels send a player with no `user`. We don't offer those
// channels, but lichess can put an AI in a standard one, and a nil deref on the
// wall would take the whole feed down.
func TestTvAnonymousPlayerDoesNotPanic(t *testing.T) {
	anon := `{"t":"featured","d":{"id":"X2","orientation":"white","players":[` +
		`{"color":"white","rating":1500,"seconds":60},` +
		`{"color":"black","user":{"name":"human"},"rating":1600,"seconds":60}],` +
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`

	ev := decodeTvFrame(t, anon)
	if got := ev.Featured.White().Name(); got != "Anonymous" {
		t.Errorf("nameless player = %q, want Anonymous", got)
	}
	if got := ev.Featured.White().Title(); got != "" {
		t.Errorf("nameless player title = %q", got)
	}
}

func TestSideToMove(t *testing.T) {
	cases := map[string]string{
		"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1":             "white",
		"r3r1n1/1b1nqpbk/1p1p2pp/p1pPp3/2P1P3/3BBNNP/PP1QRPP1/5RK1 b - - 2 18": "black",
		// Malformed input must not be reported as white's turn.
		"":       "",
		"rnbq":   "",
		"rnbq ":  "",
		"rnbq x": "",
	}
	for fen, want := range cases {
		if got := sideToMove(fen); got != want {
			t.Errorf("sideToMove(%q) = %q, want %q", fen, got, want)
		}
	}
}

// ── Ref-counting: the invariant that makes this worth proxying ──

// N connections must cost lichess ONE stream. This is the deal with lichess, and it
// is why per-client channel choice is affordable at all.
func TestTvSecondWatcherReusesTheUpstream(t *testing.T) {
	h := tvHandler(t)
	opened := countingStream(h)

	a := h.tv.watch(lichess.ChannelBlitz)
	for i := 0; i < 20; i++ {
		if got := h.tv.watch(lichess.ChannelBlitz); got != a {
			t.Fatal("watch returned a different channel object — that is a second upstream")
		}
	}
	waitFor(t, func() bool { return atomic.LoadInt32(opened) == 1 })
	if n := atomic.LoadInt32(opened); n != 1 {
		t.Fatalf("opened %d upstreams for 21 watchers, want exactly 1", n)
	}
	// 21 watches means 21 live connections on the one channel.
	if got := h.tv.connsFor(lichess.ChannelBlitz); got != 21 {
		t.Fatalf("conns = %d, want 21 — every watch is a connection", got)
	}

	// A different channel is a different upstream — that's the bound: one per
	// channel, not one per player.
	h.tv.watch(lichess.ChannelRapid)
	waitFor(t, func() bool { return atomic.LoadInt32(opened) == 2 })
}

// Concurrent first-watchers must not race into two upstreams.
func TestTvConcurrentWatchersOpenOneUpstream(t *testing.T) {
	h := tvHandler(t)
	opened := countingStream(h)

	var wg sync.WaitGroup
	seen := make([]*tvChannel, 50)
	for i := 0; i < 50; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			seen[i] = h.tv.watch(lichess.ChannelBlitz)
		}(i)
	}
	wg.Wait()

	for i, ch := range seen {
		if ch != seen[0] {
			t.Fatalf("watcher %d got a different channel object — raced into a second upstream", i)
		}
	}
	waitFor(t, func() bool { return atomic.LoadInt32(opened) == 1 })
	if n := atomic.LoadInt32(opened); n != 1 {
		t.Fatalf("opened %d upstreams under concurrency, want 1", n)
	}
	if got := h.tv.connsFor(lichess.ChannelBlitz); got != 50 {
		t.Fatalf("conns = %d after 50 concurrent watches, want 50", got)
	}
}

// The upstream drops after the LAST connection leaves — after a linger, not
// immediately, and never while a connection is still attached.
func TestTvUpstreamDroppedWhenLastConnectionLeaves(t *testing.T) {
	h := tvHandler(t)
	h.tv.lingerTTL = 50 * time.Millisecond

	var opened, cancelled int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		atomic.AddInt32(&cancelled, 1)
		return ctx.Err()
	}

	ch := h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 1 })

	// A connection is still attached: a sweep must NOT drop it, however long ago.
	time.Sleep(60 * time.Millisecond)
	h.tv.sweep()
	if len(h.tv.channels) != 1 {
		t.Fatal("swept a channel that still had a live connection")
	}
	if atomic.LoadInt32(&cancelled) != 0 {
		t.Fatal("cancelled an upstream that still had a connection")
	}

	// The last connection leaves. Within the linger a sweep still keeps it — a
	// channel switch reconnects within a round trip and must not flap the upstream.
	h.tv.leave(lichess.ChannelBlitz, ch)
	h.tv.sweep()
	if len(h.tv.channels) != 1 {
		t.Fatal("dropped the upstream inside the linger window")
	}

	// Past the linger, the sweeper cancels it. This is the ONLY thing that closes an
	// upstream, so it running is what keeps a stream from leaking to lichess.
	time.Sleep(60 * time.Millisecond)
	h.tv.sweep()
	if len(h.tv.channels) != 0 {
		t.Fatal("idle channel survived the sweep — this leaks a stream to lichess forever")
	}
	waitFor(t, func() bool { return atomic.LoadInt32(&cancelled) == 1 })
}

// A leave-then-rejoin inside the linger window must reuse the existing stream
// rather than cause a close+reopen against lichess — the A→B→A channel switch.
func TestTvChannelSwitchDoesNotFlap(t *testing.T) {
	h := tvHandler(t)
	h.tv.lingerTTL = time.Second
	opened := countingStream(h)

	first := h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(opened) == 1 })

	// Leave (conns → 0, linger starts) and immediately rejoin, several times. Each
	// rejoin is inside the linger, so the same upstream is reused.
	for i := 0; i < 5; i++ {
		h.tv.leave(lichess.ChannelBlitz, first)
		h.tv.sweep() // conns==0 but within linger — must NOT drop
		if got := h.tv.watch(lichess.ChannelBlitz); got != first {
			t.Fatal("a rejoin inside the linger got a new channel — the stream was reopened")
		}
	}

	if n := atomic.LoadInt32(opened); n != 1 {
		t.Fatalf("opened %d upstreams, want 1 — a rejoin inside the linger must reuse", n)
	}
	if len(h.tv.channels) != 1 {
		t.Fatal("a channel being rejoined was swept")
	}
}

// After a drop, a fresh watcher opens a NEW upstream rather than getting nothing.
func TestTvWatchAfterDropReopens(t *testing.T) {
	h := tvHandler(t)
	h.tv.lingerTTL = 10 * time.Millisecond
	opened := countingStream(h)

	ch := h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(opened) == 1 })
	h.tv.leave(lichess.ChannelBlitz, ch)
	time.Sleep(20 * time.Millisecond)
	h.tv.sweep()
	if len(h.tv.channels) != 0 {
		t.Fatal("setup: expected the channel to be swept")
	}

	h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(opened) == 2 })
}

// ── The WebSocket push (M18) ──
//
// These exercise the real upgrade/broadcast/decrement paths over an httptest
// server and a gorilla dial, so the handler, the auth-before-upgrade gate, the
// full-snapshot wire and the leak-proof `defer leave` are all run, not reasoned
// about.

// tvServer stands the tvSocket route up on a real HTTP server and returns its ws://
// base. Go 1.22's method-and-path mux gives {channel} to PathValue exactly as
// production's does.
func tvServer(t *testing.T, h *handler) string {
	t.Helper()
	mux := http.NewServeMux()
	mux.HandleFunc("GET /api/v1/tv/{channel}", h.tvSocket)
	srv := httptest.NewServer(mux)
	t.Cleanup(srv.Close)
	return "ws" + strings.TrimPrefix(srv.URL, "http")
}

func dialTv(t *testing.T, h *handler, wsBase, channel string) (*websocket.Conn, *http.Response, error) {
	t.Helper()
	tok, _ := h.sessions.issueGame(76561197960287930)
	hdr := http.Header{"Authorization": {"Bearer " + tok}}
	return websocket.DefaultDialer.Dial(wsBase+"/api/v1/tv/"+channel, hdr)
}

func readTvRaw(t *testing.T, conn *websocket.Conn) []byte {
	t.Helper()
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	_, msg, err := conn.ReadMessage()
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	return msg
}

func readTv(t *testing.T, conn *websocket.Conn) TvState {
	t.Helper()
	var st TvState
	if err := json.Unmarshal(readTvRaw(t, conn), &st); err != nil {
		t.Fatalf("decode: %v", err)
	}
	return st
}

// A dial with no credentials must be refused at the handshake with a 401 — no
// upgrade, no upstream.
func TestTvSocketDialRequiresASession(t *testing.T) {
	h := tvHandler(t)
	opened := countingStream(h)
	wsBase := tvServer(t, h)

	conn, resp, err := websocket.DefaultDialer.Dial(wsBase+"/api/v1/tv/blitz", nil)
	if err == nil {
		conn.Close()
		t.Fatal("handshake succeeded without a session — TV must never be an open relay")
	}
	if resp == nil || resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("status %v, want 401", resp)
	}
	if n := atomic.LoadInt32(opened); n != 0 {
		t.Fatalf("opened %d upstreams for an unauthed dial — must be 0", n)
	}
}

// A change pushes a full snapshot to a connected client, and the wire carries none
// of the retired long-poll fields.
func TestTvSocketPushesOnChange(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)
	wsBase := tvServer(t, h)

	conn, _, err := dialTv(t, h, wsBase, "blitz")
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	// The first push on connect is the seed state (channel/label, no game yet).
	seed := readTv(t, conn)
	if seed.Channel != "blitz" || seed.Label != "Blitz" {
		t.Fatalf("seed = %q/%q, want blitz/Blitz", seed.Channel, seed.Label)
	}

	// A featured frame pushes the game.
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	raw := readTvRaw(t, conn)
	if bytes.Contains(raw, []byte(`"version"`)) {
		t.Errorf("wire still carries a version cursor: %s", raw)
	}
	if bytes.Contains(raw, []byte(`"hold_ms"`)) {
		t.Errorf("wire still carries hold_ms: %s", raw)
	}
	var st TvState
	if err := json.Unmarshal(raw, &st); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if st.GameID != "BQ7M0K1i" || st.WhiteName != "DiazVelandia" {
		t.Fatalf("featured not pushed: %q/%q", st.GameID, st.WhiteName)
	}

	// A move pushes the next full snapshot.
	frames <- decodeTvFrame(t, tvFenFrame)
	st = readTv(t, conn)
	if st.LastMoveUci != "d7f6" || st.WhiteClock != 56 || st.BlackClock != 51 {
		t.Fatalf("fen not pushed: lm=%q %d/%d", st.LastMoveUci, st.WhiteClock, st.BlackClock)
	}
}

// A client that connects mid-think is handed the stored frame immediately, and its
// age_ms reflects how stale that frame is — the one case the whole-second floor
// can't cover, so it must be real, not zero.
func TestTvSocketSendsCurrentSnapshotOnConnect(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)
	wsBase := tvServer(t, h)

	// Open the upstream and land a game BEFORE anyone connects, then let it age.
	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })
	time.Sleep(60 * time.Millisecond)

	conn, _, err := dialTv(t, h, wsBase, "blitz")
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	st := readTv(t, conn)
	if st.GameID != "BQ7M0K1i" {
		t.Fatalf("connect didn't get the current game: %q", st.GameID)
	}
	if st.AgeMs < 50 {
		t.Errorf("age_ms = %d on a stale connect, want >= 50 — the client would read HIGH", st.AgeMs)
	}
	if st.AgeMs > 5000 {
		t.Errorf("age_ms = %d, implausible", st.AgeMs)
	}
}

// N connected clients cost lichess ONE stream — the invariant, over the real
// socket path this time.
func TestTvNViewersOneUpstream(t *testing.T) {
	h := tvHandler(t)
	opened := countingStream(h)
	wsBase := tvServer(t, h)

	var conns []*websocket.Conn
	for i := 0; i < 8; i++ {
		conn, _, err := dialTv(t, h, wsBase, "blitz")
		if err != nil {
			t.Fatalf("dial %d: %v", i, err)
		}
		readTv(t, conn) // drain the seed push so the connection is fully established
		conns = append(conns, conn)
	}
	defer func() {
		for _, c := range conns {
			c.Close()
		}
	}()

	waitFor(t, func() bool { return h.tv.connsFor(lichess.ChannelBlitz) == 8 })
	if n := atomic.LoadInt32(opened); n != 1 {
		t.Fatalf("opened %d upstreams for 8 viewers, want exactly 1", n)
	}
}

// A rude TCP close — no WebSocket close handshake — must still run the `defer leave`
// and drop the connection count. This is the whole reason the ref count is a defer
// decrement and not a code path: a dropped socket gives us no clean exit to hook.
func TestTvSocketDeferDecrementsOnAbruptClose(t *testing.T) {
	h := tvHandler(t)
	countingStream(h)
	wsBase := tvServer(t, h)

	conn, _, err := dialTv(t, h, wsBase, "blitz")
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	readTv(t, conn)
	waitFor(t, func() bool { return h.tv.connsFor(lichess.ChannelBlitz) == 1 })

	// Slam the underlying TCP connection shut without a close frame.
	conn.UnderlyingConn().Close()

	waitFor(t, func() bool { return h.tv.connsFor(lichess.ChannelBlitz) == 0 })
}

// ── The channel list ──

func TestTvChannelsRoute(t *testing.T) {
	h := tvHandler(t)
	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/channels", nil))
	w := httptest.NewRecorder()
	h.tvChannels(w, r)

	if w.Code != http.StatusOK {
		t.Fatalf("status %d", w.Code)
	}
	var out TvChannelsResponse
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if out.Default != "best" {
		t.Errorf("default %q, want best", out.Default)
	}
	if len(out.Channels) != 16 {
		t.Fatalf("%d channels, want all 16 of lichess's", len(out.Channels))
	}
	// Everything advertised must be something we'd actually serve.
	for _, c := range out.Channels {
		if _, ok := lichess.ValidChannel(c.Key); !ok {
			t.Errorf("advertised %q, which the allowlist rejects", c.Key)
		}
		if c.Label == "" {
			t.Errorf("channel %q has no label", c.Key)
		}
	}
}

func TestTvChannelsNeedsASession(t *testing.T) {
	h := tvHandler(t)
	w := httptest.NewRecorder()
	h.tvChannels(w, httptest.NewRequest("GET", "/api/v1/tv/channels", nil))
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("status %d, want 401", w.Code)
	}
}

// ── helpers ──

func decodeTvFrame(t *testing.T, line string) lichess.TvEvent {
	t.Helper()
	ev, ok := lichess.DecodeTvFrame([]byte(line))
	if !ok {
		t.Fatalf("could not decode frame: %s", line)
	}
	return ev
}

// waitForState blocks until the channel's state satisfies pred, using the same
// close-and-replace wake the writers do — the version cursor it replaces is gone.
func waitForState(t *testing.T, ch *tvChannel, pred func(TvState) bool) TvState {
	t.Helper()
	deadline := time.After(2 * time.Second)
	for {
		st, changed := ch.snapshot()
		if pred(st) {
			return st
		}
		select {
		case <-changed:
		case <-deadline:
			t.Fatalf("state predicate not met within 2s (last: game=%q lm=%q lastStatus=%q)",
				st.GameID, st.LastMoveUci, st.LastStatus)
		}
	}
}

func waitFor(t *testing.T, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatal("condition not met within 2s")
}

// ── The end-of-game fanfare ──

// The TV feed NEVER says a game ended — 95s of the ultraBullet channel produces only
// `featured` and `fen`, and a game ending is just a swap to a new `featured`. So the
// relay has to notice the swap and go ask how the old game went; these tests pin that,
// because the alternative (waiting for a gameOver frame) would wait forever.

func TestTvFetchesTheResultOfTheGameThatJustEnded(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)

	var asked []string
	var mu sync.Mutex
	h.tv.gameResult = func(_ context.Context, id string) (lichess.TvResult, error) {
		mu.Lock()
		asked = append(asked, id)
		mu.Unlock()
		return lichess.TvResult{Status: "outoftime", Winner: "white"}, nil
	}

	ch := h.tv.watch(lichess.ChannelBlitz)

	// First featured: nothing has ended yet, so nothing may be asked.
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	st := waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })
	if st.LastGameID != "" || st.LastStatus != "" {
		t.Errorf("claimed a previous game on the FIRST featured: %q/%q", st.LastGameID, st.LastStatus)
	}
	mu.Lock()
	n := len(asked)
	mu.Unlock()
	if n != 0 {
		t.Fatalf("asked lichess %d times before any game had ended — must be 0", n)
	}

	// A second featured: the first game just ended. The swap is published immediately
	// and the reason is fetched off the reader's path (see TestTvPublishesTheSwapBeforeTheResult
	// for that ordering) — so the fully-settled state carries both the new game and the
	// reason, though they arrive in two pushes.
	next := `{"t":"featured","d":{"id":"NEWGAME1","orientation":"white","players":[` +
		`{"color":"white","user":{"name":"alice"},"rating":2100,"seconds":180},` +
		`{"color":"black","user":{"name":"bob"},"rating":2050,"seconds":180}],` +
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`
	frames <- decodeTvFrame(t, next)
	st = waitForState(t, ch, func(s TvState) bool { return s.GameID == "NEWGAME1" && s.LastStatus != "" })

	mu.Lock()
	got := append([]string(nil), asked...)
	mu.Unlock()
	if len(got) != 1 || got[0] != "BQ7M0K1i" {
		t.Fatalf("asked for %v, want exactly [BQ7M0K1i] — the game that ended, not the new one", got)
	}

	if st.LastGameID != "BQ7M0K1i" {
		t.Errorf("last_game_id %q, want BQ7M0K1i", st.LastGameID)
	}
	if st.LastStatus != "outoftime" || st.LastWinner != "white" {
		t.Errorf("last result = %q/%q, want outoftime/white", st.LastStatus, st.LastWinner)
	}
	// The names as they were — the client shouldn't have to have kept them.
	if st.LastWhiteName != "DiazVelandia" || st.LastBlackName != "Yoozhik" {
		t.Errorf("last names = %q/%q, want the OLD game's players", st.LastWhiteName, st.LastBlackName)
	}
	if st.GameID != "NEWGAME1" || st.WhiteName != "alice" {
		t.Errorf("the new game didn't land: %q/%q", st.GameID, st.WhiteName)
	}
}

// The swap must be published WITHOUT waiting for the result fetch. The client starts
// its fanfare from the game id changing alone, so a slow lichess export must never
// freeze the wall — the ending used to sit on the wrong side of the publish and the
// board stayed frozen with no fanfare for the whole fetch. The reason arrives later.
func TestTvPublishesTheSwapBeforeTheResult(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)

	release := make(chan struct{})
	h.tv.gameResult = func(ctx context.Context, id string) (lichess.TvResult, error) {
		select {
		case <-release:
			return lichess.TvResult{Status: "mate", Winner: "black"}, nil
		case <-ctx.Done():
			return lichess.TvResult{}, ctx.Err()
		}
	}

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })

	frames <- decodeTvFrame(t, `{"t":"featured","d":{"id":"G2","orientation":"white","players":[`+
		`{"color":"white","user":{"name":"a"}},{"color":"black","user":{"name":"b"}}],`+
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`)

	// The swap lands while gameResult is still blocked: the new game and the ending id
	// are here, the reason is not.
	st := waitForState(t, ch, func(s TvState) bool { return s.GameID == "G2" })
	if st.LastGameID != "BQ7M0K1i" {
		t.Errorf("ending not announced with the swap: last_game_id %q", st.LastGameID)
	}
	if st.LastStatus != "" {
		t.Errorf("last_status %q — must be empty until the fetch returns", st.LastStatus)
	}

	// Let the fetch finish; the reason arrives in a later push without disturbing the
	// ending id it belongs to.
	close(release)
	st = waitForState(t, ch, func(s TvState) bool { return s.LastStatus == "mate" })
	if st.LastWinner != "black" {
		t.Errorf("late result winner %q, want black", st.LastWinner)
	}
	if st.LastGameID != "BQ7M0K1i" {
		t.Errorf("last_game_id changed under the late result: %q", st.LastGameID)
	}
	if st.GameID != "G2" {
		t.Errorf("the late result clobbered the current game: %q", st.GameID)
	}
}

// A fast channel can swap AGAIN before a result fetch returns. By then the state names
// a newer ending, so the stale answer must be dropped rather than pinned to it — and it
// must not close `changed`, or every connected client re-pushes and re-snaps its locally
// -run clocks off a no-op. Tested directly on setLastResult because "a no-op happened"
// has no wire signal to wait on from the outside.
func TestTvSetLastResultDropsAStaleEnding(t *testing.T) {
	ch := &tvChannel{changed: make(chan struct{})}
	ch.state.LastGameID = "G2"

	// A result for a game the state has already moved past: dropped, `changed` NOT closed.
	stale := ch.changed
	ch.setLastResult("OLDGAME", "outoftime", "white")
	if ch.state.LastStatus != "" || ch.state.LastWinner != "" {
		t.Errorf("stale result applied: %q/%q", ch.state.LastStatus, ch.state.LastWinner)
	}
	select {
	case <-stale:
		t.Error("stale ending closed changed — that re-pushes to every client and re-snaps their clocks")
	default:
	}

	// A result for the current ending: applied, and `changed` closed so waiters wake.
	live := ch.changed
	ch.setLastResult("G2", "mate", "black")
	if ch.state.LastStatus != "mate" || ch.state.LastWinner != "black" {
		t.Errorf("current result not applied: %q/%q", ch.state.LastStatus, ch.state.LastWinner)
	}
	select {
	case <-live:
	default:
		t.Error("waiters were not woken when the reason landed")
	}
}

// A draw has no winner — lichess omits the field rather than sending a third value,
// so an empty winner is an answer and must not read as "we don't know".
func TestTvFanfareHandlesADraw(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)
	h.tv.gameResult = func(context.Context, string) (lichess.TvResult, error) {
		return lichess.TvResult{Status: "stalemate", Winner: ""}, nil
	}

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })

	frames <- decodeTvFrame(t, `{"t":"featured","d":{"id":"G2","orientation":"white","players":[`+
		`{"color":"white","user":{"name":"a"}},{"color":"black","user":{"name":"b"}}],`+
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`)
	// The reason is fetched off the reader's path now — wait for it to settle.
	st := waitForState(t, ch, func(s TvState) bool { return s.LastStatus != "" })

	if st.LastStatus != "stalemate" {
		t.Errorf("last_status %q, want stalemate", st.LastStatus)
	}
	if st.LastWinner != "" {
		t.Errorf("last_winner %q, want empty for a draw", st.LastWinner)
	}
	if st.LastGameID == "" {
		t.Error("a draw is still an ending — last_game_id must be set")
	}
}

// lichess being unable or unwilling to say how a game ended must cost the fanfare its
// detail and NOTHING else. The wall must still move on.
func TestTvFanfareSurvivesAFailedResultFetch(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)
	h.tv.gameResult = func(context.Context, string) (lichess.TvResult, error) {
		return lichess.TvResult{}, errors.New("lichess said no")
	}

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })

	frames <- decodeTvFrame(t, `{"t":"featured","d":{"id":"G2","orientation":"white","players":[`+
		`{"color":"white","user":{"name":"a"}},{"color":"black","user":{"name":"b"}}],`+
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`)
	st := waitForState(t, ch, func(s TvState) bool { return s.GameID == "G2" })

	// The ending is still announced — the client needs to know the game it was watching
	// finished, even without a reason.
	if st.LastGameID != "BQ7M0K1i" {
		t.Errorf("last_game_id %q — a failed fetch must not lose the ending itself", st.LastGameID)
	}
	if st.LastStatus != "" {
		t.Errorf("last_status %q, want empty when the fetch failed", st.LastStatus)
	}
	// And the new game must be live regardless.
	if st.GameID != "G2" || st.Fen == "" {
		t.Errorf("the wall stopped moving after a failed result fetch: %q", st.GameID)
	}
}

// A `fen` frame is not a game ending, and must never trigger a fetch.
func TestTvFenFramesNeverFetchAResult(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)

	var calls int32
	h.tv.gameResult = func(context.Context, string) (lichess.TvResult, error) {
		atomic.AddInt32(&calls, 1)
		return lichess.TvResult{}, nil
	}

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })
	for i := 0; i < 5; i++ {
		frames <- decodeTvFrame(t, tvFenFrame)
	}
	waitForState(t, ch, func(s TvState) bool { return s.LastMoveUci == "d7f6" })

	if n := atomic.LoadInt32(&calls); n != 0 {
		t.Fatalf("%d result fetches for plain moves — that is one lichess request per MOVE", n)
	}
}

// ── The clock's age stamp (M18) ──
//
// The favor-low clock rests on one field, age_ms, stamped at send. Silently zero it
// and a mid-think connect reads HIGH again with nothing failing — so pin the stamp
// and pin that serving a client never writes the age back into the shared state.

// stamp must measure from when LICHESS's value reached us (clockAt), not from now.
func TestTvStampMeasuresAgeFromTheClockStamp(t *testing.T) {
	now := time.Now()
	st := TvState{clockAt: now.Add(-60 * time.Millisecond)}
	st.stamp(now)
	if st.AgeMs < 50 || st.AgeMs > 5000 {
		t.Errorf("age_ms = %d, want ~60 (measured from clockAt)", st.AgeMs)
	}

	// A state that never carried a clock (the seed) has nothing to age.
	seed := TvState{}
	seed.stamp(now)
	if seed.AgeMs != 0 {
		t.Errorf("age_ms = %d on a state with no clock, want 0", seed.AgeMs)
	}
}

// A fen frame carries fresh clocks and must RE-stamp — or every move after the first
// would report the first frame's age and the correction would grow without bound.
func TestTvFenFrameRestampsTheClock(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)
	ch := h.tv.watch(lichess.ChannelBlitz)

	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })

	time.Sleep(60 * time.Millisecond)
	frames <- decodeTvFrame(t, tvFenFrame)
	waitForState(t, ch, func(s TvState) bool { return s.LastMoveUci == "d7f6" })

	st, _ := ch.snapshot()
	st.stamp(time.Now())
	if st.AgeMs > 50 {
		t.Errorf("age_ms = %d after a fresh fen frame — the stamp is stuck on the featured frame", st.AgeMs)
	}
}

// stamp writes to the response copy only. If it ever touched the shared state, two
// clients on one channel would age it cumulatively and every push would read staler
// than the last.
func TestTvStampDoesNotMutateSharedState(t *testing.T) {
	h := tvHandler(t)
	frames := framePump(h)
	ch := h.tv.watch(lichess.ChannelBlitz)

	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForState(t, ch, func(s TvState) bool { return s.GameID == "BQ7M0K1i" })

	for i := 0; i < 3; i++ {
		st, _ := ch.snapshot()
		st.stamp(time.Now())
	}

	stored, _ := ch.snapshot()
	if stored.AgeMs != 0 {
		t.Errorf("stamping a snapshot wrote the age back into the channel: %d — it is per-response", stored.AgeMs)
	}
}
