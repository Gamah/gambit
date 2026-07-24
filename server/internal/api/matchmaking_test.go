package api

import (
	"testing"
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

func TestColorWords(t *testing.T) {
	// Role-based, so it holds in self-play where a SteamID compare would be ambiguous.
	if colorWord("w") != "white" || colorWord("b") != "black" || colorWord("") != "" {
		t.Fatalf("colorWord wrong: %q %q %q", colorWord("w"), colorWord("b"), colorWord(""))
	}
	if oppositeColorWord("w") != "black" || oppositeColorWord("b") != "white" || oppositeColorWord("") != "" {
		t.Fatalf("oppositeColorWord wrong")
	}
	// The invariant self-play relies on: opener and joiner always get opposite sides.
	for _, c := range []string{"w", "b"} {
		if colorWord(c) == oppositeColorWord(c) {
			t.Fatalf("opener and joiner got the same side for %q", c)
		}
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
