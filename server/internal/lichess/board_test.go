package lichess

import (
	"context"
	"io"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"
)

// ── Clock / speed rules ──

func TestValidClockLimit(t *testing.T) {
	// lichess documents the domain as 0, 15, 30, 45, 60, 90, or any multiple of
	// 60 up to 10800.
	for _, ok := range []int{0, 15, 30, 45, 60, 90, 120, 180, 600, 1800, 10800} {
		if !ValidClockLimit(ok) {
			t.Errorf("clock.limit %d should be valid", ok)
		}
	}
	for _, bad := range []int{-60, 1, 14, 31, 100, 150, 10860, 99999} {
		if ValidClockLimit(bad) {
			t.Errorf("clock.limit %d should be invalid", bad)
		}
	}
}

// The Board API refuses anything faster than blitz. This is the rule that
// decides which Gambit tables can offer lichess play at all, so it is pinned
// against the real presets from TimeControl.All.
func TestChallengeCompatibleMatchesGambitsPresets(t *testing.T) {
	cases := []struct {
		name           string
		limit, inc     int
		unlimited      bool
		wantCompatible bool
	}{
		// Estimated total = limit + 40*inc; lichess's Blitz band starts at 180.
		{"Bullet 1+0", 60, 0, false, false},      // 60 → Bullet. NEVER mirrors.
		{"Blitz 3+2", 180, 2, false, true},       // 260 → Blitz
		{"Rapid 10+0", 600, 0, false, true},      // 600 → Rapid
		{"Classical 30+0", 1800, 0, false, true}, // 1800 → Classical
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := ChallengeCompatible(c.limit, c.inc); got != c.wantCompatible {
				t.Fatalf("ChallengeCompatible(%d,%d) = %v, want %v", c.limit, c.inc, got, c.wantCompatible)
			}
		})
	}
}

func TestEstimateTotalSeconds(t *testing.T) {
	// scalachess: limit + 40*increment.
	if got := EstimateTotalSeconds(180, 2); got != 260 {
		t.Fatalf("3+2 estimates %d, want 260", got)
	}
	if got := EstimateTotalSeconds(60, 0); got != 60 {
		t.Fatalf("1+0 estimates %d, want 60", got)
	}
}

// The exact boundary: 180 is Blitz (in), 179 is Bullet (out).
func TestChallengeCompatibleBoundary(t *testing.T) {
	if ChallengeCompatible(179, 0) {
		t.Fatal("an estimated total of 179 is Bullet — must be refused")
	}
	if !ChallengeCompatible(180, 0) {
		t.Fatal("an estimated total of 180 is Blitz — must be allowed")
	}
}

func TestChallengeParamsValidate(t *testing.T) {
	t.Run("unlimited is always allowed", func(t *testing.T) {
		// No clock → Correspondence speed → comfortably past the blitz floor.
		if err := (ChallengeParams{Unlimited: true}).validate(); err != nil {
			t.Fatalf("unlimited should validate, got %v", err)
		}
	})
	t.Run("bullet is refused before we spend a request", func(t *testing.T) {
		if err := (ChallengeParams{LimitSeconds: 60}).validate(); err == nil {
			t.Fatal("bullet must be refused locally")
		}
	})
	t.Run("off-domain clock limit", func(t *testing.T) {
		if err := (ChallengeParams{LimitSeconds: 200, IncrementSeconds: 0}).validate(); err == nil {
			t.Fatal("200 is not a legal clock.limit")
		}
	})
	t.Run("increment out of range", func(t *testing.T) {
		if err := (ChallengeParams{LimitSeconds: 600, IncrementSeconds: 61}).validate(); err == nil {
			t.Fatal("increment 61 is outside 0..60")
		}
	})
}

// ── Challenge ──

func TestChallengeUserByNameForm(t *testing.T) {
	var got url.Values
	var path string
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		path = r.URL.Path
		body, _ := io.ReadAll(r.Body)
		got, _ = url.ParseQuery(string(body))
		io.WriteString(w, `{"id":"abcd1234","status":"created","url":"https://lichess.org/abcd1234"}`)
	})

	res, err := ChallengeUserByName(context.Background(), "tok", "MaryChess", ChallengeParams{
		LimitSeconds: 180, IncrementSeconds: 2, Rated: false, Color: "white",
	})
	if err != nil {
		t.Fatal(err)
	}
	if res.ID != "abcd1234" {
		t.Fatalf("challenge id: got %q", res.ID)
	}
	if path != "/api/challenge/MaryChess" {
		t.Fatalf("path: got %q", path)
	}
	for key, want := range map[string]string{
		"clock.limit":     "180",
		"clock.increment": "2",
		"rated":           "false",
		"color":           "white",
	} {
		if g := got.Get(key); g != want {
			t.Errorf("form %s: got %q want %q", key, g, want)
		}
	}
}

// Omitting the clock fields is what asks lichess for an unlimited game.
// Sending 0/0 would ask for a 0+0 clock, which is a different (rejected) thing.
func TestChallengeUnlimitedOmitsTheClock(t *testing.T) {
	var got url.Values
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		got, _ = url.ParseQuery(string(body))
		io.WriteString(w, `{"id":"unl12345","status":"created"}`)
	})

	if _, err := ChallengeUserByName(context.Background(), "tok", "Mary",
		ChallengeParams{Unlimited: true}); err != nil {
		t.Fatal(err)
	}
	if got.Has("clock.limit") || got.Has("clock.increment") {
		t.Fatalf("unlimited must omit both clock fields, sent %v", got)
	}
}

func TestChallengeBulletNeverReachesLichess(t *testing.T) {
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		t.Fatal("a bullet challenge must be refused locally, not sent")
	})
	if _, err := ChallengeUserByName(context.Background(), "tok", "Mary",
		ChallengeParams{LimitSeconds: 60, IncrementSeconds: 0}); err == nil {
		t.Fatal("expected a bullet challenge to be refused")
	}
}

func TestChallenge200WithNoIDFailsClosed(t *testing.T) {
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"status":"created"}`)
	})
	if _, err := ChallengeUserByName(context.Background(), "tok", "Mary",
		ChallengeParams{LimitSeconds: 180, IncrementSeconds: 2}); err == nil {
		t.Fatal("expected an error when the challenge has no id")
	}
}

// ── APIError ──

func TestAPIErrorClassification(t *testing.T) {
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusUnauthorized)
		io.WriteString(w, "No such token")
	})

	err := Resign(context.Background(), "dead", "gameid")
	if err == nil {
		t.Fatal("expected an error")
	}
	apiErr, ok := err.(*APIError)
	if !ok {
		t.Fatalf("want *APIError, got %T", err)
	}
	// A dead token has to be distinguishable from a refused move: one means the
	// link row is garbage, the other means the player mis-clicked.
	if !apiErr.Unauthorized() || apiErr.RateLimited() {
		t.Fatalf("401 misclassified: %+v", apiErr)
	}
	if !strings.Contains(apiErr.Error(), "No such token") {
		t.Fatalf("lichess's own error text is worth keeping: %q", apiErr.Error())
	}
}

// ── Writers ──

func TestWriterPaths(t *testing.T) {
	cases := []struct {
		name     string
		call     func(context.Context, string) error
		wantPath string
		wantForm map[string]string
	}{
		{"move", func(ctx context.Context, tok string) error {
			return Move(ctx, tok, "g4me", "e2e4", false)
		}, "/api/board/game/g4me/move/e2e4", nil},
		{"resign", func(ctx context.Context, tok string) error {
			return Resign(ctx, tok, "g4me")
		}, "/api/board/game/g4me/resign", nil},
		{"draw yes", func(ctx context.Context, tok string) error {
			return Draw(ctx, tok, "g4me", true)
		}, "/api/board/game/g4me/draw/yes", nil},
		{"draw no", func(ctx context.Context, tok string) error {
			return Draw(ctx, tok, "g4me", false)
		}, "/api/board/game/g4me/draw/no", nil},
		{"abort", func(ctx context.Context, tok string) error {
			return Abort(ctx, tok, "g4me")
		}, "/api/board/game/g4me/abort", nil},
		{"accept challenge", func(ctx context.Context, tok string) error {
			return AcceptChallenge(ctx, tok, "ch4l")
		}, "/api/challenge/ch4l/accept", nil},
		{"chat", func(ctx context.Context, tok string) error {
			return Chat(ctx, tok, "g4me", "player", "gg")
		}, "/api/board/game/g4me/chat", map[string]string{"room": "player", "text": "gg"}},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			var gotPath string
			var gotForm url.Values
			stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
				gotPath = r.URL.Path
				body, _ := io.ReadAll(r.Body)
				gotForm, _ = url.ParseQuery(string(body))
				if got := r.Header.Get("Authorization"); got != "Bearer tok" {
					t.Errorf("Authorization: got %q", got)
				}
				io.WriteString(w, `{"ok":true}`)
			})
			if err := c.call(context.Background(), "tok"); err != nil {
				t.Fatal(err)
			}
			if gotPath != c.wantPath {
				t.Fatalf("path: got %q want %q", gotPath, c.wantPath)
			}
			for k, v := range c.wantForm {
				if g := gotForm.Get(k); g != v {
					t.Errorf("form %s: got %q want %q", k, g, v)
				}
			}
		})
	}
}

func TestMoveOfferingDraw(t *testing.T) {
	var gotQuery string
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		gotQuery = r.URL.RawQuery
		io.WriteString(w, `{"ok":true}`)
	})
	if err := Move(context.Background(), "tok", "g4me", "e2e4", true); err != nil {
		t.Fatal(err)
	}
	if gotQuery != "offeringDraw=true" {
		t.Fatalf("query: got %q want offeringDraw=true", gotQuery)
	}
}

// THE trap this file exists to pin down: lila has TWO functions called
// isBoardCompatible, with different thresholds, governing different endpoints.
// A challenge accepts blitz; a seek does not. Collapsing them would silently
// make every seek fail (or, worse, make us offer one that can't work).
func TestChallengeAndSeekFloorsDiffer(t *testing.T) {
	// Blitz 3+2 → estimate 260. Challengeable, NOT seekable.
	if !ChallengeCompatible(180, 2) {
		t.Error("blitz 3+2 must be challengeable")
	}
	if SeekCompatible(180, 2) {
		t.Error("blitz 3+2 must NOT be seekable — a real-time seek needs rapid or slower")
	}

	// Rapid 10+0 → 600. Both.
	if !ChallengeCompatible(600, 0) || !SeekCompatible(600, 0) {
		t.Error("rapid 10+0 must be both challengeable and seekable")
	}

	// The exact Rapid boundary: 480 in, 479 out.
	if SeekCompatible(479, 0) {
		t.Error("an estimated total of 479 is Blitz — not seekable")
	}
	if !SeekCompatible(480, 0) {
		t.Error("an estimated total of 480 is Rapid — seekable")
	}

	if ChallengeFloorSeconds != 180 || SeekFloorSeconds != 480 {
		t.Fatalf("floors moved: challenge=%d seek=%d", ChallengeFloorSeconds, SeekFloorSeconds)
	}
}

func TestSeekRealtime(t *testing.T) {
	// The seek budget is process-wide (it mirrors a per-IP limit), so tests must
	// not inherit each other's spend.
	ResetGovernor()
	t.Cleanup(ResetGovernor)

	t.Run("posts minutes, not seconds", func(t *testing.T) {
		ResetGovernor()
		var got url.Values
		var path string
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			path = r.URL.Path
			body, _ := io.ReadAll(r.Body)
			got, _ = url.ParseQuery(string(body))
			// A real seek holds open; closing immediately stands for "matched".
		})

		if err := SeekRealtime(context.Background(), "tok", SeekParams{TimeMinutes: 10}); err != nil {
			t.Fatal(err)
		}
		if path != "/api/board/seek" {
			t.Fatalf("path: got %q", path)
		}
		// MINUTES. ChallengeParams.LimitSeconds is seconds — mixing them up asks
		// for a 10-second game while meaning ten minutes.
		if got.Get("time") != "10" {
			t.Fatalf("time: got %q, want 10 (minutes)", got.Get("time"))
		}
		if got.Get("increment") != "0" {
			t.Fatalf("increment: got %q", got.Get("increment"))
		}
	})

	t.Run("blitz is refused locally", func(t *testing.T) {
		ResetGovernor()
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			t.Fatal("a blitz seek must not be sent — lichess refuses it")
		})
		// 3+2 = 260 estimated: challengeable, not seekable.
		if err := SeekRealtime(context.Background(), "tok", SeekParams{TimeMinutes: 3, IncrementSeconds: 2}); err == nil {
			t.Fatal("expected a blitz seek to be refused")
		}
	})

	t.Run("out-of-range time", func(t *testing.T) {
		ResetGovernor()
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			t.Fatal("must not send an out-of-range seek")
		})
		if err := SeekRealtime(context.Background(), "tok", SeekParams{TimeMinutes: 181}); err == nil {
			t.Fatal("181 minutes is outside 0..180")
		}
		if err := SeekRealtime(context.Background(), "tok", SeekParams{TimeMinutes: 10, IncrementSeconds: 181}); err == nil {
			t.Fatal("181s increment is outside 0..180")
		}
	})

	// The 5/min-per-IP cap is shared by every Gambit player, so a 429 must be
	// legible rather than a generic failure.
	t.Run("429 surfaces as a rate limit", func(t *testing.T) {
		ResetGovernor()
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusTooManyRequests)
		})
		err := SeekRealtime(context.Background(), "tok", SeekParams{TimeMinutes: 10})
		apiErr, ok := err.(*APIError)
		if !ok || !apiErr.RateLimited() {
			t.Fatalf("want a rate-limited APIError, got %#v", err)
		}
	})

	// The connection IS the seek: we must hold it until told to stop.
	t.Run("holds open until cancelled", func(t *testing.T) {
		ResetGovernor()
		stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusOK)
			w.(http.Flusher).Flush()
			<-r.Context().Done()
		})

		ctx, cancel := context.WithCancel(context.Background())
		done := make(chan error, 1)
		go func() { done <- SeekRealtime(ctx, "tok", SeekParams{TimeMinutes: 10}) }()

		select {
		case err := <-done:
			t.Fatalf("returned before cancellation (%v) — the seek would be dropped instantly", err)
		case <-time.After(150 * time.Millisecond):
		}

		cancel()
		select {
		case err := <-done:
			if err != context.Canceled {
				t.Fatalf("want context.Canceled, got %v", err)
			}
		case <-time.After(5 * time.Second):
			t.Fatal("seek did not stop on cancellation")
		}
	})
}

func TestSeekCorrespondenceRejectsBadDays(t *testing.T) {
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		t.Fatal("an illegal days value must not be sent")
	})
	for _, bad := range []int{0, 4, 6, 8, 15, -1} {
		if err := SeekCorrespondence(context.Background(), "tok", bad, false); err == nil {
			t.Errorf("days=%d should be refused", bad)
		}
	}
}

// ── Streams ──

// ndjsonServer serves canned ndjson lines and then closes.
func ndjsonServer(t *testing.T, lines string) {
	t.Helper()
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer tok" {
			t.Errorf("Authorization: got %q", got)
		}
		w.Header().Set("Content-Type", "application/x-ndjson")
		io.WriteString(w, lines)
	})
}

func TestStreamGameStateMachine(t *testing.T) {
	// A real game's opening lines: gameFull, then gameState per move, then a
	// terminal gameState. Blank lines are lichess's ~7s keepalive.
	ndjsonServer(t, `{"type":"gameFull","id":"g4me","speed":"blitz","rated":false,`+
		`"white":{"id":"terry","name":"Terry"},"black":{"id":"mary","name":"Mary"},`+
		`"initialFen":"startpos","clock":{"initial":180000,"increment":2000},`+
		`"state":{"type":"gameState","moves":"","wtime":180000,"btime":180000,"winc":2000,"binc":2000,"status":"started"}}
{"type":"gameState","moves":"e2e4","wtime":179000,"btime":180000,"winc":2000,"binc":2000,"status":"started"}

{"type":"chatLine","username":"Mary","text":"hi","room":"player"}
{"type":"gameState","moves":"e2e4 e7e5","wtime":179000,"btime":178000,"winc":2000,"binc":2000,"status":"started"}
{"type":"gameState","moves":"e2e4 e7e5 d1h5 b8c6 f1c4 g8f6 h5f7","wtime":170000,"btime":171000,"winc":2000,"binc":2000,"status":"mate","winner":"white"}
`)

	var events []GameEvent
	err := StreamGame(context.Background(), "tok", "g4me", func(e GameEvent) {
		events = append(events, e)
	})
	if err != nil {
		t.Fatal(err)
	}

	// 5 events: gameFull, 2 gameStates, chatLine, terminal gameState. The blank
	// keepalive line must NOT produce one.
	if len(events) != 5 {
		t.Fatalf("got %d events, want 5: %+v", len(events), events)
	}

	full := events[0].Full
	if full == nil || full.ID != "g4me" {
		t.Fatalf("first event must be gameFull: %+v", events[0])
	}
	if full.White.ID != "terry" || full.Black.ID != "mary" {
		t.Fatalf("gameFull players: %+v", full)
	}
	if full.Clock == nil || full.Clock.Initial != 180000 || full.Clock.Increment != 2000 {
		t.Fatalf("gameFull clock: %+v", full.Clock)
	}

	// chatLine is delivered by type with no payload — it must not be mistaken
	// for a state. It is at index 2, not 3: the blank keepalive between it and
	// the previous line produces no event at all.
	if events[2].Type != "chatLine" || events[2].State != nil || events[2].Full != nil {
		t.Fatalf("chatLine mis-parsed: %+v", events[2])
	}

	// Each gameState carries the WHOLE move list, not a delta — that's what lets
	// a dropped line cost nothing.
	last := events[4].State
	if last == nil {
		t.Fatalf("last event should be a gameState: %+v", events[4])
	}
	if last.Moves != "e2e4 e7e5 d1h5 b8c6 f1c4 g8f6 h5f7" {
		t.Fatalf("terminal moves: %q", last.Moves)
	}
	if last.Status != "mate" || last.Winner != "white" {
		t.Fatalf("terminal state: %+v", last)
	}
	if !Finished(last.Status) {
		t.Fatal("mate must read as finished")
	}
}

func TestFinished(t *testing.T) {
	for _, live := range []string{"", "created", "started"} {
		if Finished(live) {
			t.Errorf("status %q should be live", live)
		}
	}
	// Every other value in lichess's status enum is terminal.
	for _, over := range []string{"mate", "resign", "stalemate", "timeout", "draw",
		"outoftime", "aborted", "cheat", "noStart", "unknownFinish",
		"insufficientMaterialClaim", "variantEnd"} {
		if !Finished(over) {
			t.Errorf("status %q should be finished", over)
		}
	}
}

func TestStreamEvents(t *testing.T) {
	ndjsonServer(t, `{"type":"challenge","challenge":{"id":"ch4l","status":"created",`+
		`"challenger":{"id":"terry","name":"Terry"},"destUser":{"id":"mary","name":"Mary"},`+
		`"speed":"blitz","direction":"in"}}

{"type":"gameStart","game":{"gameId":"g4me","fullId":"g4mefull","color":"white","speed":"blitz"}}
{"type":"gameFinish","game":{"gameId":"g4me"}}
`)

	var got []Event
	if err := StreamEvents(context.Background(), "tok", func(e Event) { got = append(got, e) }); err != nil {
		t.Fatal(err)
	}
	if len(got) != 3 {
		t.Fatalf("got %d events, want 3", len(got))
	}
	if got[0].Type != "challenge" || got[0].Challenge == nil || got[0].Challenge.ID != "ch4l" {
		t.Fatalf("challenge event: %+v", got[0])
	}
	// direction "in" is how we tell a challenge TO us from one we issued — the
	// auto-accept hangs off it.
	if got[0].Challenge.Direction != "in" || got[0].Challenge.Challenger.ID != "terry" {
		t.Fatalf("challenge detail: %+v", got[0].Challenge)
	}
	if got[1].Type != "gameStart" || got[1].Game == nil || got[1].Game.GameID != "g4me" {
		t.Fatalf("gameStart event: %+v", got[1])
	}
	if got[2].Type != "gameFinish" {
		t.Fatalf("gameFinish event: %+v", got[2])
	}
}

// One malformed line must not take a live game's stream down with it — the next
// gameState carries the whole position anyway.
func TestStreamSkipsMalformedLines(t *testing.T) {
	ndjsonServer(t, `{"type":"gameStart","game":{"gameId":"g1"}}
not json at all
{"type":"gameFinish","game":{"gameId":"g1"}}
`)
	var got []Event
	if err := StreamEvents(context.Background(), "tok", func(e Event) { got = append(got, e) }); err != nil {
		t.Fatalf("a bad line must not fail the stream: %v", err)
	}
	if len(got) != 2 {
		t.Fatalf("got %d events, want 2 (the garbage line skipped)", len(got))
	}
}

func TestStreamNon200FailsClosed(t *testing.T) {
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusUnauthorized)
	})
	err := StreamEvents(context.Background(), "dead", func(Event) {
		t.Fatal("no events should be delivered on a 401")
	})
	if err == nil {
		t.Fatal("expected an error on 401")
	}
}

// Cancellation is how the relay stops a stream (unseat, game over, shutdown), so
// it must report ctx.Err() rather than a generic read failure.
func TestStreamCancellation(t *testing.T) {
	stubAPI(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/x-ndjson")
		io.WriteString(w, "{\"type\":\"gameStart\",\"game\":{\"gameId\":\"g1\"}}\n")
		w.(http.Flusher).Flush()
		<-r.Context().Done() // hold it open like a real stream
	})

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		done <- StreamEvents(ctx, "tok", func(e Event) { cancel() })
	}()

	select {
	case err := <-done:
		if err != context.Canceled {
			t.Fatalf("want context.Canceled, got %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("stream did not stop on cancellation")
	}
}
