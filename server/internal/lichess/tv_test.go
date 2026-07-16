package lichess

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// ── The leak check ──

// /api/tv/{channel}/feed is `security: []` upstream — anonymous. Attaching a
// player's board:play token to it would hand their credential to an endpoint that
// never asked for it, on a stream we hold open for hours. TV must send NO
// Authorization header, ever.
//
// This is the one test standing between the shared stream reader and a real
// credential leak: `stream` sends the header for every other caller, and TV is
// the first one that must not.
func TestStreamTvSendsNoAuthorization(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	var gotAuth string
	var gotUA string
	var gotPath string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotAuth = r.Header.Get("Authorization")
		gotUA = r.Header.Get("User-Agent")
		gotPath = r.URL.Path
		w.Header().Set("Content-Type", "application/x-ndjson")
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(tvFeaturedLine + "\n"))
		if f, ok := w.(http.Flusher); ok {
			f.Flush()
		}
	}))
	defer srv.Close()

	prev := apiBase
	apiBase = srv.URL
	defer func() { apiBase = prev }()

	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	StreamTv(ctx, ChannelBlitz, func(TvEvent) {})

	if gotAuth != "" {
		t.Fatalf("TV request carried an Authorization header (%q) — that is a token "+
			"leak to an endpoint that never asked for one", gotAuth)
	}
	// The User-Agent is how lichess attributes our traffic; it must survive even on
	// the anonymous path. It comes from the RoundTripper, so no call site can drop it.
	if gotUA != UserAgent {
		t.Errorf("User-Agent = %q, want %q — lichess must be able to attribute this", gotUA, UserAgent)
	}
	if gotPath != "/api/tv/blitz/feed" {
		t.Errorf("path = %q", gotPath)
	}
}

// A channel that didn't come from ValidChannel must never become a request.
func TestStreamTvRefusesAnInvalidChannel(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	var hits int
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits++
	}))
	defer srv.Close()

	prev := apiBase
	apiBase = srv.URL
	defer func() { apiBase = prev }()

	for _, bad := range []Channel{"notachannel", "../account", "", "BLITZ", "ultrabullet"} {
		err := StreamTv(context.Background(), bad, func(TvEvent) {})
		if err != ErrBadChannel {
			t.Errorf("StreamTv(%q) = %v, want ErrBadChannel", bad, err)
		}
	}
	if hits != 0 {
		t.Fatalf("made %d requests for invalid channels — must be 0", hits)
	}
}

// ── Etiquette ──

// lichess's limits are per-IP and Gambit's whole relay is ONE IP, so a 429
// anywhere means we are collectively too fast. TV is no exception: it must refuse
// to open during the post-429 minute rather than spend the shared budget.
func TestStreamTvBacksOffAfter429(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	var hits int
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits++
		w.WriteHeader(http.StatusTooManyRequests)
	}))
	defer srv.Close()

	prev := apiBase
	apiBase = srv.URL
	defer func() { apiBase = prev }()

	// The first attempt reaches lichess and earns the 429; the RoundTripper notes
	// it process-wide.
	if err := StreamTv(context.Background(), ChannelBlitz, func(TvEvent) {}); err == nil {
		t.Fatal("a 429 response should have surfaced as an error")
	}
	if hits != 1 {
		t.Fatalf("made %d requests, want 1", hits)
	}

	// Every subsequent attempt — on ANY channel — must be refused locally without
	// touching the network.
	for _, c := range []Channel{ChannelBlitz, ChannelRapid, ChannelBest} {
		err := StreamTv(context.Background(), c, func(TvEvent) {})
		if err != ErrBackingOff {
			t.Errorf("StreamTv(%q) during backoff = %v, want ErrBackingOff", c, err)
		}
	}
	if hits != 1 {
		t.Fatalf("made %d requests during the backoff minute — must stay at 1", hits)
	}
}

// A 429 earned by TV must stop the BOARD API too, and vice versa. Per-IP means
// the budget is shared across every feature, so the backoff is process-wide.
func TestTv429StopsEverythingElse(t *testing.T) {
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusTooManyRequests)
	}))
	defer srv.Close()

	prev := apiBase
	apiBase = srv.URL
	defer func() { apiBase = prev }()

	StreamTv(context.Background(), ChannelBlitz, func(TvEvent) {})

	if err := StreamGame(context.Background(), "a-token", "gameid", func(GameEvent) {}); err != ErrBackingOff {
		t.Errorf("a TV 429 left the board API running: %v — the limit is per-IP and we are one IP", err)
	}
}

// ── The frame decoder ──

// Captured from the live feed on 2026-07-15. The envelope is {"t":…,"d":…} and
// players nest name/title under `user`; both were wrong when written from memory,
// which is why these fixtures are real bytes rather than hand-built structs.
const (
	tvFeaturedLine = `{"t":"featured","d":{"id":"BQ7M0K1i","orientation":"white","players":[` +
		`{"color":"white","user":{"name":"DiazVelandia","title":"FM","id":"diazvelandia"},"rating":2954,"seconds":60},` +
		`{"color":"black","user":{"name":"Yoozhik","id":"yoozhik"},"rating":2931,"seconds":60}],` +
		`"fen":"r3r1n1/1b1nqpbk/1p1p2pp/p1pPp3/2P1P3/3BBNNP/PP1QRPP1/5RK1 b - - 2 18"}}`

	tvFenLine = `{"t":"fen","d":{"fen":"r3r1n1/1b2qpbk/1p1p1npp/p1pPp3/2P1P3/3BBNNP/PP1QRPP1/5RK1 w - - 3 19",` +
		`"lm":"d7f6","wc":56,"bc":51}}`
)

func TestDecodeTvFeatured(t *testing.T) {
	ev, ok := DecodeTvFrame([]byte(tvFeaturedLine))
	if !ok || ev.Type != "featured" || ev.Featured == nil {
		t.Fatalf("decode = (%+v, %v)", ev, ok)
	}
	f := ev.Featured
	if f.ID != "BQ7M0K1i" {
		t.Errorf("id %q", f.ID)
	}
	if f.White().Name() != "DiazVelandia" || f.White().Title() != "FM" || f.White().Rating != 2954 {
		t.Errorf("white = %+v", f.White())
	}
	if f.Black().Name() != "Yoozhik" || f.Black().Title() != "" {
		t.Errorf("black = %+v", f.Black())
	}
	// The starting clock, per side.
	if f.White().Seconds != 60 || f.Black().Seconds != 60 {
		t.Errorf("seconds = %d/%d", f.White().Seconds, f.Black().Seconds)
	}
	if !strings.HasPrefix(f.Fen, "r3r1n1/") {
		t.Errorf("fen %q", f.Fen)
	}
}

func TestDecodeTvFen(t *testing.T) {
	ev, ok := DecodeTvFrame([]byte(tvFenLine))
	if !ok || ev.Type != "fen" || ev.Fen == nil {
		t.Fatalf("decode = (%+v, %v)", ev, ok)
	}
	if ev.Fen.LM != "d7f6" {
		t.Errorf("lm %q", ev.Fen.LM)
	}
	// SECONDS on this endpoint — the Board API sends the same idea in ms.
	if ev.Fen.WC != 56 || ev.Fen.BC != 51 {
		t.Errorf("clocks = %d/%d, want 56/51", ev.Fen.WC, ev.Fen.BC)
	}
}

// A malformed line must be skippable, not fatal: the next fen frame carries the
// whole position, so there is nothing to recover and no reason to drop the feed.
func TestDecodeTvFrameTolerance(t *testing.T) {
	for _, bad := range []string{"", "{", "not json", `{"t":"featured","d":"a string"}`, `{"t":"fen","d":[]}`} {
		if _, ok := DecodeTvFrame([]byte(bad)); ok {
			t.Errorf("DecodeTvFrame(%q) claimed success", bad)
		}
	}
	// An unknown type is NOT an error — lichess may add frames, and we ignore them.
	ev, ok := DecodeTvFrame([]byte(`{"t":"somethingNew","d":{}}`))
	if !ok || ev.Type != "somethingNew" {
		t.Errorf("unknown frame = (%+v, %v), want it passed through", ev, ok)
	}
	if ev.Featured != nil || ev.Fen != nil {
		t.Error("an unknown frame produced a payload")
	}
}
