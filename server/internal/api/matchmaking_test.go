package api

import (
	"testing"

	"github.com/gamah/gambit/server/internal/store"
)

func TestParseTimeControl(t *testing.T) {
	tests := []struct {
		in           string
		wantInit     int64
		wantInc      int64
		wantOK       bool
	}{
		{"180+2", 180000, 2000, true},
		{"600+0", 600000, 0, true},
		{"60+1", 60000, 1000, true},
		{"-", -1, 0, true},
		{"", -1, 0, true},
		{"180", 0, 0, false},   // no increment field
		{"0+0", 0, 0, false},   // non-positive base
		{"abc+2", 0, 0, false}, // garbage
		{"180+-1", 0, 0, false},
		{"999999+0", 0, 0, false}, // absurd base rejected
	}
	for _, tc := range tests {
		init, inc, ok := parseTimeControl(tc.in)
		if ok != tc.wantOK || (ok && (init != tc.wantInit || inc != tc.wantInc)) {
			t.Errorf("parseTimeControl(%q) = (%d, %d, %v), want (%d, %d, %v)",
				tc.in, init, inc, ok, tc.wantInit, tc.wantInc, tc.wantOK)
		}
	}
}

func TestColorFor(t *testing.T) {
	white := int64(11111111111111111)
	black := int64(22222222222222222)
	other := int64(33333333333333333)
	m := store.Match{WhiteSteamID: &white, BlackSteamID: &black}

	if got := colorFor(m, white); got != "white" {
		t.Errorf("white seat = %q, want white", got)
	}
	if got := colorFor(m, black); got != "black" {
		t.Errorf("black seat = %q, want black", got)
	}
	if got := colorFor(m, other); got != "" {
		t.Errorf("non-participant = %q, want empty", got)
	}
	if got := colorFor(store.Match{}, white); got != "" {
		t.Errorf("unmatched = %q, want empty", got)
	}
}

// coinHeads must actually vary — a stuck coin would make the opener always White,
// the exact thing the feature forbids. Over many flips both outcomes appear.
func TestCoinHeadsVaries(t *testing.T) {
	heads, tails := 0, 0
	for i := 0; i < 200; i++ {
		if coinHeads() {
			heads++
		} else {
			tails++
		}
	}
	if heads == 0 || tails == 0 {
		t.Fatalf("coin is stuck: heads=%d tails=%d", heads, tails)
	}
}
