package store

import (
	"testing"
	"time"
)

func TestLiveClocks(t *testing.T) {
	base := time.Date(2026, 7, 24, 12, 0, 0, 0, time.UTC)

	tests := []struct {
		name              string
		g                 RelayGame
		afterMs           int64 // ms elapsed since last_move_at
		wantW, wantB      int64
		wantFlag          string
	}{
		{
			name:    "white to move, 3s elapsed off a 60s bank",
			g:       RelayGame{Status: "live", InitialMs: 60000, Turn: "w", WhiteMs: 60000, BlackMs: 60000, LastMoveAt: base},
			afterMs: 3000,
			wantW:   57000, wantB: 60000, wantFlag: "",
		},
		{
			name:    "black to move ticks black only",
			g:       RelayGame{Status: "live", InitialMs: 60000, Turn: "b", WhiteMs: 40000, BlackMs: 10000, LastMoveAt: base},
			afterMs: 2500,
			wantW:   40000, wantB: 7500, wantFlag: "",
		},
		{
			name:    "white flags when the bank runs out",
			g:       RelayGame{Status: "live", InitialMs: 60000, Turn: "w", WhiteMs: 2000, BlackMs: 30000, LastMoveAt: base},
			afterMs: 5000,
			wantW:   0, wantB: 30000, wantFlag: "w",
		},
		{
			name:    "exactly zero flags",
			g:       RelayGame{Status: "live", InitialMs: 60000, Turn: "b", WhiteMs: 30000, BlackMs: 1000, LastMoveAt: base},
			afterMs: 1000,
			wantW:   30000, wantB: 0, wantFlag: "b",
		},
		{
			name:    "untimed never ticks or flags",
			g:       RelayGame{Status: "live", InitialMs: -1, Turn: "w", WhiteMs: 0, BlackMs: 0, LastMoveAt: base},
			afterMs: 99999,
			wantW:   0, wantB: 0, wantFlag: "",
		},
		{
			name:    "finished game reports its stored clocks untouched",
			g:       RelayGame{Status: "white_won", InitialMs: 60000, Turn: "b", WhiteMs: 12000, BlackMs: 0, LastMoveAt: base},
			afterMs: 5000,
			wantW:   12000, wantB: 0, wantFlag: "",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			now := base.Add(time.Duration(tc.afterMs) * time.Millisecond)
			w, b, flag := tc.g.LiveClocks(now)
			if w != tc.wantW || b != tc.wantB || flag != tc.wantFlag {
				t.Fatalf("LiveClocks = (%d, %d, %q), want (%d, %d, %q)",
					w, b, flag, tc.wantW, tc.wantB, tc.wantFlag)
			}
		})
	}
}

// The house rule: a live clock must never read HIGHER than the time actually
// left. Any elapsed time only ever subtracts from the ticking side.
func TestLiveClocksNeverReadsHigh(t *testing.T) {
	base := time.Date(2026, 7, 24, 12, 0, 0, 0, time.UTC)
	g := RelayGame{Status: "live", InitialMs: 180000, Turn: "w", WhiteMs: 180000, BlackMs: 180000, LastMoveAt: base}
	for _, ms := range []int64{0, 1, 500, 1000, 60000, 179999} {
		w, b, _ := g.LiveClocks(base.Add(time.Duration(ms) * time.Millisecond))
		if w > g.WhiteMs {
			t.Fatalf("white read high: %d > %d after %dms", w, g.WhiteMs, ms)
		}
		if b != g.BlackMs {
			t.Fatalf("idle black moved: %d != %d", b, g.BlackMs)
		}
	}
}

func TestPlyAndUntimed(t *testing.T) {
	cases := []struct {
		moves   string
		wantPly int
	}{
		{"", 0},
		{"e2e4", 1},
		{"e2e4 e7e5", 2},
		{"e2e4 e7e5 g1f3", 3},
	}
	for _, c := range cases {
		g := RelayGame{Moves: c.moves}
		if g.Ply() != c.wantPly {
			t.Errorf("Ply(%q) = %d, want %d", c.moves, g.Ply(), c.wantPly)
		}
	}
	if !(RelayGame{InitialMs: -1}).Untimed() {
		t.Error("InitialMs -1 should be untimed")
	}
	if (RelayGame{InitialMs: 180000}).Untimed() {
		t.Error("InitialMs 180000 should be timed")
	}
	if !(RelayGame{Status: "draw"}).Over() {
		t.Error("draw should be over")
	}
	if (RelayGame{Status: "live"}).Over() {
		t.Error("live should not be over")
	}
}

func TestOpposite(t *testing.T) {
	if opposite("w") != "b" || opposite("b") != "w" {
		t.Fatal("opposite is wrong")
	}
}
