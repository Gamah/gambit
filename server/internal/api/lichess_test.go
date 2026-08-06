package api

import (
	"testing"
	"time"
)

// The link store's job is small and its failure modes are all security ones, so
// they get tests rather than a comment.
//
// The shape INVERTED at HTTPFIX: it used to be mint-on-redirect /
// burn-on-callback, because the callback was where the token exchange happened.
// The client exchanges now, so the callback only PARKS a code and the client
// collects it — burn-on-use moved to collect, and burning at the callback would
// make collection impossible.

const testChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

func TestLinkCollectIsKeyedOnTheCallerNotTheState(t *testing.T) {
	slots := newLinkSlots(linkTTL)

	// Alice starts a flow and completes it in her browser.
	state, err := slots.start(1, testChallenge)
	if err != nil {
		t.Fatal(err)
	}
	if !slots.park(state, "the-code") {
		t.Fatal("park should have accepted a live state")
	}

	// Mallory knows the state — it was in a URL bar, a browser history, a
	// referrer. It buys nothing: collect only ever answers about the caller's own
	// authenticated SteamID, and there is no way to pass a state to it.
	if _, live := slots.collect(2); live {
		t.Fatal("a stranger collected a code they did not start")
	}

	code, live := slots.collect(1)
	if !live || code != "the-code" {
		t.Fatalf("Alice should collect her own code, got (%q, %v)", code, live)
	}
}

// Ready burns the whole slot atomically. The code is single-use at lichess too,
// so handing it out twice could only ever produce one working exchange and one
// confusing failure.
func TestLinkCollectBurnsTheSlot(t *testing.T) {
	slots := newLinkSlots(linkTTL)
	state, _ := slots.start(1, testChallenge)
	slots.park(state, "the-code")

	if _, live := slots.collect(1); !live {
		t.Fatal("first collect should succeed")
	}
	if code, live := slots.collect(1); live || code != "" {
		t.Fatalf("second collect should find nothing, got (%q, %v)", code, live)
	}
	// And the state is dead too, so a replayed callback can't refill it.
	if slots.park(state, "another-code") {
		t.Fatal("a burnt state should not accept a code")
	}
}

// "Live but not back yet" and "no flow at all" are different answers, and the
// client shows them differently — waiting vs. start again.
func TestLinkCollectDistinguishesWaitingFromNone(t *testing.T) {
	slots := newLinkSlots(linkTTL)
	if _, live := slots.collect(1); live {
		t.Fatal("no flow should report none")
	}
	slots.start(1, testChallenge)
	code, live := slots.collect(1)
	if !live || code != "" {
		t.Fatalf("a started flow with no code should be waiting, got (%q, %v)", code, live)
	}
	// Waiting must NOT have burnt the slot — there was nothing to burn.
	if slots.forSteam(1) == nil {
		t.Fatal("a waiting collect destroyed the flow")
	}
}

// A browser refresh replays a spent authorization code. Quietly overwriting a
// good code with a dead one would turn a stray F5 into a failed link with no
// explanation.
func TestParkRefusesASecondCode(t *testing.T) {
	slots := newLinkSlots(linkTTL)
	state, _ := slots.start(1, testChallenge)

	if !slots.park(state, "first") {
		t.Fatal("the first code should park")
	}
	if slots.park(state, "second") {
		t.Fatal("a second code parked over a good one")
	}
	code, _ := slots.collect(1)
	if code != "first" {
		t.Fatalf("collected %q, want the first code", code)
	}
}

// An unknown or expired state is refused, and gets no distinguishing answer:
// it is either a bug or an attack, and neither deserves one.
func TestParkRefusesUnknownAndExpiredStates(t *testing.T) {
	slots := newLinkSlots(time.Millisecond)
	if slots.park("never-minted", "code") {
		t.Fatal("an unknown state parked a code")
	}

	state, _ := slots.start(1, testChallenge)
	time.Sleep(5 * time.Millisecond)
	if slots.park(state, "code") {
		t.Fatal("an expired state parked a code")
	}
	if _, live := slots.collect(1); live {
		t.Fatal("an expired flow should report none")
	}
}

// Newest wins. Two live slots for one SteamID would make collect ambiguous, and
// the ambiguity would resolve differently depending on which browser tab the
// player happened to finish.
func TestStartingAgainEvictsTheOlderFlow(t *testing.T) {
	slots := newLinkSlots(linkTTL)
	first, _ := slots.start(1, testChallenge)
	second, _ := slots.start(1, testChallenge)

	if first == second {
		t.Fatal("two flows must not share a state")
	}
	if slots.park(first, "stale") {
		t.Fatal("the abandoned flow still accepted a code")
	}
	if !slots.park(second, "fresh") {
		t.Fatal("the current flow should accept a code")
	}
	if code, _ := slots.collect(1); code != "fresh" {
		t.Fatalf("collected %q, want the newest flow's code", code)
	}
}

// The PKCE challenge becomes a query parameter on an authorize URL, so it is
// shape-checked rather than passed through.
func TestValidCodeChallenge(t *testing.T) {
	if !validCodeChallenge(testChallenge) {
		t.Fatal("a real RFC 7636 S256 challenge should be accepted")
	}
	for _, bad := range []string{
		"",
		"short",
		testChallenge + "x",              // 44 chars
		testChallenge[:42] + "+",         // base64 standard alphabet, not url
		testChallenge[:42] + "=",         // padded
		testChallenge[:42] + " ",         // whitespace
		"../../etc/passwd&scope=web:mod", // the reason this is checked at all
	} {
		if validCodeChallenge(bad) {
			t.Errorf("validCodeChallenge(%q) should be false", bad)
		}
	}
}

// ── The directory ──

// The two-intent rule survives HTTPFIX with a DIFFERENT justification: it can no
// longer stop anyone being dragged into a game (each client acts with its own
// token), but gamchess still must not hand out a player's lichess username to
// whoever asks. One seat posting reveals nothing.
func TestRendezvousDisclosesOnlyWhenBothSeatsHavePosted(t *testing.T) {
	rv := newRendezvous()
	const game = "b0a5c1e2-0000-4000-8000-000000000001"

	if other := rv.post(game, 0, rendezvousSeat{steamID: 1, username: "White", posted: time.Now()}); other != nil {
		t.Fatal("one seat posting should reveal nothing")
	}
	// The same seat posting again is not the other seat.
	if other := rv.post(game, 0, rendezvousSeat{steamID: 1, username: "White", posted: time.Now()}); other != nil {
		t.Fatal("a seat posting twice should not look like a pair")
	}

	other := rv.post(game, 1, rendezvousSeat{steamID: 2, username: "Black", posted: time.Now()})
	if other == nil || other.steamID != 1 {
		t.Fatalf("the second seat should learn the first, got %+v", other)
	}
	// And symmetrically, so the first seat learns the second on its next poll.
	if back := rv.post(game, 0, rendezvousSeat{steamID: 1, username: "White", posted: time.Now()}); back == nil || back.steamID != 2 {
		t.Fatalf("the first seat should learn the second, got %+v", back)
	}
}

// A rendezvous is per game. Sitting down at one table must not disclose anything
// about a different table's pairing.
func TestRendezvousIsScopedToOneGame(t *testing.T) {
	rv := newRendezvous()
	rv.post("b0a5c1e2-0000-4000-8000-000000000001", 0,
		rendezvousSeat{steamID: 1, username: "White", posted: time.Now()})

	if other := rv.post("b0a5c1e2-0000-4000-8000-000000000002", 1,
		rendezvousSeat{steamID: 2, username: "Black", posted: time.Now()}); other != nil {
		t.Fatalf("a different game's seat was disclosed: %+v", other)
	}
}

// A seat that posted an hour ago is not sitting there now.
func TestRendezvousForgetsAStaleSeat(t *testing.T) {
	rv := newRendezvous()
	const game = "b0a5c1e2-0000-4000-8000-000000000003"

	rv.post(game, 0, rendezvousSeat{steamID: 1, username: "White",
		posted: time.Now().Add(-2 * rendezvousTTL)})

	if other := rv.post(game, 1, rendezvousSeat{steamID: 2, username: "Black", posted: time.Now()}); other != nil {
		t.Fatalf("a stale seat was disclosed: %+v", other)
	}
}
