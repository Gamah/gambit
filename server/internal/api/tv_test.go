package api

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"regexp"
	"strconv"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
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

// An unknown channel must 404 without opening anything upstream.
func TestTvStateUnknownChannel404s(t *testing.T) {
	h := tvHandler(t)
	var opened int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		return ctx.Err()
	}

	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/notachannel", nil))
	r.SetPathValue("channel", "notachannel")
	w := httptest.NewRecorder()
	h.tvState(w, r)

	if w.Code != http.StatusNotFound {
		t.Fatalf("status %d, want 404", w.Code)
	}
	if n := atomic.LoadInt32(&opened); n != 0 {
		t.Fatalf("opened %d upstream streams for a junk channel — must be 0", n)
	}
}

// ── The gate ──

// TV is anonymous UPSTREAM, which is exactly why our proxy of it must not be
// open: an unauthed relay is a free CDN for lichess's content pointable by any
// script, and every byte of it is attributed to our IP and our User-Agent.
func TestTvRequiresASession(t *testing.T) {
	h := tvHandler(t)
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
			h.tvState(w, r)
			if w.Code != http.StatusUnauthorized {
				t.Fatalf("status %d, want 401 — TV must never be an open relay", w.Code)
			}
		})
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

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)

	st := waitForVersion(t, ch, 1)
	if st.GameID != "BQ7M0K1i" {
		t.Errorf("game id %q", st.GameID)
	}
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

	before := st.Version
	frames <- decodeTvFrame(t, tvFenFrame)
	st = waitForVersion(t, ch, before)

	if st.LastMoveUci != "d7f6" {
		t.Errorf("last move %q", st.LastMoveUci)
	}
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
	ch := h.tv.watch(lichess.ChannelBlitz)

	frames <- decodeTvFrame(t, tvFeaturedFrame)
	st := waitForVersion(t, ch, 1)
	frames <- decodeTvFrame(t, tvFenFrame)
	st = waitForVersion(t, ch, st.Version)
	if st.LastMoveUci == "" {
		t.Fatal("setup: expected a last move")
	}

	next := `{"t":"featured","d":{"id":"NEWGAME1","orientation":"white","players":[` +
		`{"color":"white","user":{"name":"alice"},"rating":2100,"seconds":180},` +
		`{"color":"black","user":{"name":"bob"},"rating":2050,"seconds":180}],` +
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`
	frames <- decodeTvFrame(t, next)
	st = waitForVersion(t, ch, st.Version)

	if st.GameID != "NEWGAME1" {
		t.Errorf("game id %q, want NEWGAME1", st.GameID)
	}
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

// N clients must cost lichess ONE stream. This is the deal with lichess, and it
// is why per-client channel choice is affordable at all.
func TestTvSecondWatcherReusesTheUpstream(t *testing.T) {
	h := tvHandler(t)
	var opened int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		return ctx.Err()
	}

	a := h.tv.watch(lichess.ChannelBlitz)
	for i := 0; i < 20; i++ {
		if got := h.tv.watch(lichess.ChannelBlitz); got != a {
			t.Fatal("watch returned a different channel object — that is a second upstream")
		}
	}
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 1 })
	if n := atomic.LoadInt32(&opened); n != 1 {
		t.Fatalf("opened %d upstreams for 21 watchers, want exactly 1", n)
	}

	// A different channel is a different upstream — that's the bound: one per
	// channel, not one per player.
	h.tv.watch(lichess.ChannelRapid)
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 2 })
}

// Concurrent first-watchers must not race into two upstreams.
func TestTvConcurrentWatchersOpenOneUpstream(t *testing.T) {
	h := tvHandler(t)
	var opened int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		return ctx.Err()
	}

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
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 1 })
	if n := atomic.LoadInt32(&opened); n != 1 {
		t.Fatalf("opened %d upstreams under concurrency, want 1", n)
	}
}

// The last watcher leaving drops the upstream — after a TTL, not immediately.
func TestTvIdleUpstreamIsDroppedAfterTTL(t *testing.T) {
	h := tvHandler(t)
	h.tv.idleTTL = 50 * time.Millisecond

	var opened, cancelled int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		atomic.AddInt32(&cancelled, 1)
		return ctx.Err()
	}

	h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 1 })

	// Still inside the TTL: a sweep must NOT drop it. A watcher between two polls
	// is not a watcher who left.
	h.tv.sweep()
	if len(h.tv.channels) != 1 {
		t.Fatal("swept a channel that was polled moments ago")
	}
	if atomic.LoadInt32(&cancelled) != 0 {
		t.Fatal("cancelled an upstream that still had a watcher")
	}

	time.Sleep(60 * time.Millisecond)
	h.tv.sweep()

	if len(h.tv.channels) != 0 {
		t.Fatal("idle channel survived the sweep — this leaks a stream to lichess forever")
	}
	waitFor(t, func() bool { return atomic.LoadInt32(&cancelled) == 1 })
}

// A watcher returning DURING the TTL keeps the existing stream rather than
// causing a close+reopen against lichess.
func TestTvWatcherDuringTTLReusesTheUpstream(t *testing.T) {
	h := tvHandler(t)
	h.tv.idleTTL = 200 * time.Millisecond

	var opened int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		return ctx.Err()
	}

	first := h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 1 })

	// Come back before the TTL expires, repeatedly. Each touch pushes the deadline
	// out, so a steadily-polled channel is never swept.
	for i := 0; i < 5; i++ {
		time.Sleep(50 * time.Millisecond)
		if got := h.tv.watch(lichess.ChannelBlitz); got != first {
			t.Fatal("a returning watcher got a new channel — the stream was reopened")
		}
		h.tv.sweep()
	}

	if n := atomic.LoadInt32(&opened); n != 1 {
		t.Fatalf("opened %d upstreams, want 1 — a returning watcher must reuse", n)
	}
	if len(h.tv.channels) != 1 {
		t.Fatal("a steadily-polled channel was swept")
	}
}

// After a drop, a fresh watcher opens a NEW upstream rather than getting nothing.
func TestTvWatchAfterDropReopens(t *testing.T) {
	h := tvHandler(t)
	h.tv.idleTTL = 10 * time.Millisecond
	var opened int32
	h.tv.streamTv = func(ctx context.Context, c lichess.Channel, fn func(lichess.TvEvent)) error {
		atomic.AddInt32(&opened, 1)
		<-ctx.Done()
		return ctx.Err()
	}

	h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 1 })
	time.Sleep(20 * time.Millisecond)
	h.tv.sweep()
	if len(h.tv.channels) != 0 {
		t.Fatal("setup: expected the channel to be swept")
	}

	h.tv.watch(lichess.ChannelBlitz)
	waitFor(t, func() bool { return atomic.LoadInt32(&opened) == 2 })
}

// ── The long poll ──

// since=version must hold rather than spin. The client polls in a tight loop;
// answering instantly with an unchanged state is how you get hundreds of
// requests a second.
func TestTvLongPollHoldsWhenNothingChanged(t *testing.T) {
	h := tvHandler(t)
	ch := h.tv.watch(lichess.ChannelBlitz)
	st, _ := ch.snapshot()

	since := strconv.FormatUint(st.Version, 10)
	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/blitz?since="+since, nil))
	r.SetPathValue("channel", "blitz")
	ctx, cancel := context.WithTimeout(r.Context(), 80*time.Millisecond)
	defer cancel()
	r = r.WithContext(ctx)

	start := time.Now()
	w := httptest.NewRecorder()
	h.tvState(w, r)

	// The request context died first, so the handler should have returned on it —
	// the point is that it did NOT return immediately with the same version.
	if elapsed := time.Since(start); elapsed < 50*time.Millisecond {
		t.Fatalf("returned after %v without holding — the client would spin", elapsed)
	}
}

// A stale `since` must answer immediately: that is the catch-up path.
func TestTvLongPollAnswersImmediatelyWhenBehind(t *testing.T) {
	h := tvHandler(t)
	h.tv.watch(lichess.ChannelBlitz)

	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/blitz?since=0", nil))
	r.SetPathValue("channel", "blitz")
	w := httptest.NewRecorder()

	done := make(chan struct{})
	go func() { h.tvState(w, r); close(done) }()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("held a poll that was already behind — catch-up must be immediate")
	}

	if w.Code != http.StatusOK {
		t.Fatalf("status %d", w.Code)
	}
	var st TvState
	if err := json.Unmarshal(w.Body.Bytes(), &st); err != nil {
		t.Fatal(err)
	}
	if st.Channel != "blitz" || st.Label != "Blitz" {
		t.Errorf("channel/label = %q/%q", st.Channel, st.Label)
	}
	if st.Version == 0 {
		t.Error("version 0 — the client would never advance")
	}
}

// Polling is what marks a channel as wanted; the handler must touch it or a
// watched channel gets swept out from under its viewers.
func TestTvPollTouchesTheChannel(t *testing.T) {
	h := tvHandler(t)
	h.tv.idleTTL = 100 * time.Millisecond
	h.tv.watch(lichess.ChannelBlitz)

	time.Sleep(70 * time.Millisecond)

	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/blitz?since=0", nil))
	r.SetPathValue("channel", "blitz")
	h.tvState(httptest.NewRecorder(), r)

	time.Sleep(50 * time.Millisecond) // past the original deadline, not the touched one
	h.tv.sweep()
	if len(h.tv.channels) != 1 {
		t.Fatal("a channel being actively polled was swept")
	}
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

func waitForVersion(t *testing.T, ch *tvChannel, after uint64) TvState {
	t.Helper()
	deadline := time.After(2 * time.Second)
	for {
		st, changed := ch.snapshot()
		if st.Version > after {
			return st
		}
		select {
		case <-changed:
		case <-deadline:
			t.Fatalf("no state past version %d within 2s", after)
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
	st := waitForVersion(t, ch, 1)
	if st.LastGameID != "" || st.LastStatus != "" {
		t.Errorf("claimed a previous game on the FIRST featured: %q/%q", st.LastGameID, st.LastStatus)
	}
	mu.Lock()
	n := len(asked)
	mu.Unlock()
	if n != 0 {
		t.Fatalf("asked lichess %d times before any game had ended — must be 0", n)
	}

	// A second featured: the first game just ended.
	next := `{"t":"featured","d":{"id":"NEWGAME1","orientation":"white","players":[` +
		`{"color":"white","user":{"name":"alice"},"rating":2100,"seconds":180},` +
		`{"color":"black","user":{"name":"bob"},"rating":2050,"seconds":180}],` +
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`
	frames <- decodeTvFrame(t, next)
	st = waitForVersion(t, ch, st.Version)

	mu.Lock()
	got := append([]string(nil), asked...)
	mu.Unlock()
	if len(got) != 1 || got[0] != "BQ7M0K1i" {
		t.Fatalf("asked for %v, want exactly [BQ7M0K1i] — the game that ended, not the new one", got)
	}

	// The ending and its replacement must arrive TOGETHER, or the client would show the
	// new game before it ever learned the old one finished.
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
	st := waitForVersion(t, ch, 1)

	frames <- decodeTvFrame(t, `{"t":"featured","d":{"id":"G2","orientation":"white","players":[`+
		`{"color":"white","user":{"name":"a"}},{"color":"black","user":{"name":"b"}}],`+
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`)
	st = waitForVersion(t, ch, st.Version)

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
	st := waitForVersion(t, ch, 1)

	frames <- decodeTvFrame(t, `{"t":"featured","d":{"id":"G2","orientation":"white","players":[`+
		`{"color":"white","user":{"name":"a"}},{"color":"black","user":{"name":"b"}}],`+
		`"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"}}`)
	st = waitForVersion(t, ch, st.Version)

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
	st := waitForVersion(t, ch, 1)
	for i := 0; i < 5; i++ {
		frames <- decodeTvFrame(t, tvFenFrame)
		st, _ = ch.snapshot()
	}
	_ = st

	if n := atomic.LoadInt32(&calls); n != 0 {
		t.Fatalf("%d result fetches for plain moves — that is one lichess request per MOVE", n)
	}
}

// ── The clock's age and hold (M11) ──
//
// The wall's clock read HIGH — above the time actually left, which is the one
// direction the house rule forbids. These two fields are what let the client
// subtract the staleness, so they are worth pinning hard: silently zero, and the
// clock is wrong again with nothing failing.

// ClockAgeMs must measure from when LICHESS's value reached us, not from when the
// request did. Getting this backwards yields ~0 forever and looks like it works.
func TestTvClockAgeMeasuresFromTheFrameNotTheRequest(t *testing.T) {
	h := tvHandler(t)
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

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForVersion(t, ch, 1) // past the seed state watch() publishes, to the featured frame

	// Let the value go stale on our side before anyone asks for it.
	time.Sleep(60 * time.Millisecond)

	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/blitz?since=0", nil))
	r.SetPathValue("channel", "blitz")
	w := httptest.NewRecorder()
	h.tvState(w, r)

	var st TvState
	if err := json.Unmarshal(w.Body.Bytes(), &st); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if st.ClockAgeMs < 50 {
		t.Errorf("clock_age_ms = %d, want >= 50 — it is measuring the request, not the frame", st.ClockAgeMs)
	}
	if st.ClockAgeMs > 5000 {
		t.Errorf("clock_age_ms = %d, implausible", st.ClockAgeMs)
	}
	// This request did not wait: it had a newer version to hand.
	if st.HoldMs > 50 {
		t.Errorf("hold_ms = %d, want ~0 — nothing was held", st.HoldMs)
	}
}

// A poll that finds nothing new sits for pollHold and then answers. That wait is
// OURS and must be reported as hold, not left for the client to read as network
// latency — it would subtract up to 5s from the clock.
func TestTvHoldIsReportedSoTheClientCantReadItAsLatency(t *testing.T) {
	h := tvHandler(t)
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

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	st0 := waitForVersion(t, ch, 1) // past the seed state watch() publishes, to the featured frame

	// Ask for the version we already have, so the handler has nothing to say and
	// holds — then land a frame to release it, well short of pollHold.
	//
	// `polling` is closed immediately before the handler runs. Without it this test
	// races its own goroutine: if the frame lands first the handler takes the
	// early-return path, reports hold=0 honestly, and the test reads that as the bug
	// it is looking for. It did exactly that.
	polling := make(chan struct{})
	done := make(chan TvState, 1)
	go func() {
		r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/blitz?since="+strconv.FormatUint(st0.Version, 10), nil))
		r.SetPathValue("channel", "blitz")
		w := httptest.NewRecorder()
		close(polling)
		h.tvState(w, r)
		var st TvState
		if err := json.Unmarshal(w.Body.Bytes(), &st); err != nil {
			t.Errorf("decode: %v (status %d)", err, w.Code)
		}
		done <- st
	}()

	<-polling
	time.Sleep(150 * time.Millisecond)
	frames <- decodeTvFrame(t, tvFenFrame)

	select {
	case st := <-done:
		if st.Version <= st0.Version {
			t.Fatalf("poll returned version %d, want past %d — it never waited", st.Version, st0.Version)
		}
		if st.HoldMs < 100 {
			t.Errorf("hold_ms = %d, want >= 100 — the client will read our wait as latency", st.HoldMs)
		}
		// The frame that woke us is fresh, even though we held a while. Age and hold
		// are different quantities and this is the case that proves it.
		if st.ClockAgeMs > 60 {
			t.Errorf("clock_age_ms = %d, want ~0 — the frame just landed", st.ClockAgeMs)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("poll never returned")
	}
}

// ageAt writes to the response copy only. If it ever touched the shared state,
// two clients polling one channel would overwrite each other's timings — and the
// stored value would age cumulatively, so the clock would drift further wrong the
// longer the channel stayed up.
func TestTvAgeAtDoesNotMutateSharedState(t *testing.T) {
	h := tvHandler(t)
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

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	waitForVersion(t, ch, 1) // past the seed state watch() publishes, to the featured frame

	for i := 0; i < 3; i++ {
		r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/blitz?since=0", nil))
		r.SetPathValue("channel", "blitz")
		h.tvState(httptest.NewRecorder(), r)
	}

	stored, _ := ch.snapshot()
	if stored.ClockAgeMs != 0 || stored.HoldMs != 0 {
		t.Errorf("serving a poll wrote timings back into the channel: age=%d hold=%d — these are per-response",
			stored.ClockAgeMs, stored.HoldMs)
	}
}

// The clock stamp must not ride on the FEN alone: a fen frame carries new clocks
// and must re-stamp, or every move after the first would report the FIRST frame's
// age and the correction would grow without bound.
func TestTvFenFrameRestampsTheClock(t *testing.T) {
	h := tvHandler(t)
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

	ch := h.tv.watch(lichess.ChannelBlitz)
	frames <- decodeTvFrame(t, tvFeaturedFrame)
	st0 := waitForVersion(t, ch, 1) // past the seed state watch() publishes, to the featured frame

	time.Sleep(60 * time.Millisecond)
	frames <- decodeTvFrame(t, tvFenFrame)
	waitForVersion(t, ch, st0.Version)

	r := authed(t, h, httptest.NewRequest("GET", "/api/v1/tv/blitz?since=0", nil))
	r.SetPathValue("channel", "blitz")
	w := httptest.NewRecorder()
	h.tvState(w, r)

	var st TvState
	json.Unmarshal(w.Body.Bytes(), &st)
	if st.ClockAgeMs > 50 {
		t.Errorf("clock_age_ms = %d after a fresh fen frame — the stamp is stuck on the featured frame", st.ClockAgeMs)
	}
}
