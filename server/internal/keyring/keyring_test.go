package keyring

import (
	"bytes"
	"context"
	"encoding/base64"
	"errors"
	"testing"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gamah/gambit/server/internal/store"
)

// kek builds a distinct KEK cipher from a fill byte, so tests can prove one KEK
// can't open another's ciphertext.
func kek(t *testing.T, fill byte) *lichess.Cipher {
	t.Helper()
	c, err := lichess.NewCipher(base64.StdEncoding.EncodeToString(bytes.Repeat([]byte{fill}, 32)))
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func seedLink(ms *memStore, steamID int64, ct, nonce []byte, version int32) {
	ms.links[steamID] = store.LichessLink{
		SteamID: steamID, LichessID: "u", Username: "u",
		TokenEnc: ct, TokenNonce: nonce, KeyVersion: version, Scopes: lichess.Scope,
	}
}

// An empty store bootstraps exactly one data key at version 1, and it seals and
// opens round-trip under that key.
func TestBootstrapAndRoundTrip(t *testing.T) {
	ms := newMemStore()
	k, err := newRing(context.Background(), ms, kek(t, 0x11), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if got := k.CurrentVersion(); got != 1 {
		t.Fatalf("current version = %d, want 1", got)
	}
	if len(ms.versions) != 1 {
		t.Fatalf("bootstrapped %d key versions, want 1", len(ms.versions))
	}

	ct, nonce, ver, err := k.SealToken("lio_secret")
	if err != nil {
		t.Fatal(err)
	}
	if ver != 1 {
		t.Fatalf("sealed under version %d, want 1", ver)
	}
	got, err := k.OpenToken(ct, nonce, ver)
	if err != nil || got != "lio_secret" {
		t.Fatalf("round trip got (%q, %v)", got, err)
	}
	// The DEK row must be ciphertext, not the raw key.
	if bytes.Contains(ms.versions[1].DEKEnc, ct) {
		t.Fatal("data key row appears to contain a token ciphertext")
	}
}

// Version 0 is the legacy sentinel: a token sealed directly under the KEK (as
// every pre-M15 row was) opens with OpenToken(.., 0).
func TestOpenLegacyVersionZero(t *testing.T) {
	theKEK := kek(t, 0x22)
	ms := newMemStore()
	k, err := newRing(context.Background(), ms, theKEK, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	ct, nonce, err := theKEK.Seal("legacy_token")
	if err != nil {
		t.Fatal(err)
	}
	got, err := k.OpenToken(ct, nonce, 0)
	if err != nil || got != "legacy_token" {
		t.Fatalf("legacy open got (%q, %v)", got, err)
	}
}

// Rotation makes a new current version; tokens sealed under the old one still open.
func TestRotateKeepsOldOpenable(t *testing.T) {
	ctx := context.Background()
	ms := newMemStore()
	k, err := newRing(ctx, ms, kek(t, 0x33), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	ct1, n1, v1, _ := k.SealToken("under_v1")

	v2, err := k.Rotate(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if v2 != 2 || k.CurrentVersion() != 2 {
		t.Fatalf("after rotate: new=%d current=%d, want 2/2", v2, k.CurrentVersion())
	}
	// New seals go under v2.
	if _, _, ver, _ := k.SealToken("under_v2"); ver != 2 {
		t.Fatalf("post-rotate seal used version %d, want 2", ver)
	}
	// The v1 token still opens.
	if got, err := k.OpenToken(ct1, n1, v1); err != nil || got != "under_v1" {
		t.Fatalf("old-version open got (%q, %v)", got, err)
	}
	// An unknown version is a clean error, not a wrong-key decrypt.
	if _, err := k.OpenToken(ct1, n1, 99); err == nil {
		t.Fatal("expected an error opening an unknown version")
	}
}

// The re-encrypt sweep lifts legacy and stale rows onto the current key, leaves
// already-current rows alone, retires drained keys, and never corrupts a token.
func TestReEncryptSweep(t *testing.T) {
	ctx := context.Background()
	ms := newMemStore()
	theKEK := kek(t, 0x44)
	k, err := newRing(ctx, ms, theKEK, nil, nil) // v1 current
	if err != nil {
		t.Fatal(err)
	}

	// A legacy (v0) row sealed under the KEK directly.
	lct, lnonce, _ := theKEK.Seal("legacy_tok")
	seedLink(ms, 1, lct, lnonce, 0)
	// An already-current (v1) row — must be left untouched.
	cct, cnonce, cver, _ := k.SealToken("current_tok")
	seedLink(ms, 2, cct, cnonce, cver)

	n, err := k.ReEncrypt(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if n != 1 {
		t.Fatalf("re-encrypted %d rows, want 1 (only the legacy one)", n)
	}
	// The legacy row is now current and still decrypts to the same token.
	if got := ms.links[1]; got.KeyVersion != 1 {
		t.Fatalf("legacy row version = %d, want 1", got.KeyVersion)
	}
	if got, err := k.OpenToken(ms.links[1].TokenEnc, ms.links[1].TokenNonce, ms.links[1].KeyVersion); err != nil || got != "legacy_tok" {
		t.Fatalf("re-encrypted legacy token got (%q, %v)", got, err)
	}

	// Now rotate and sweep again: both rows move to v2.
	if _, err := k.Rotate(ctx); err != nil {
		t.Fatal(err)
	}
	n, err = k.ReEncrypt(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if n != 2 {
		t.Fatalf("second sweep re-encrypted %d rows, want 2", n)
	}
	if got, err := k.OpenToken(ms.links[2].TokenEnc, ms.links[2].TokenNonce, ms.links[2].KeyVersion); err != nil || got != "current_tok" {
		t.Fatalf("row 2 token after two sweeps got (%q, %v)", got, err)
	}

	// v1 now protects nothing and can be retired; v2 (current) cannot.
	if _, err := ms.RetireUnusedKeyVersions(ctx, k.CurrentVersion()); err != nil {
		t.Fatal(err)
	}
	if ms.versions[1].RetiredAt == nil {
		t.Fatal("v1 should be retired once nothing references it")
	}
	if ms.versions[2].RetiredAt != nil {
		t.Fatal("the current key must never be retired")
	}
}

// The optimistic guard: a row whose version already advanced (a concurrent
// re-link) is not clobbered by a stale re-seal.
func TestReEncryptOptimisticGuard(t *testing.T) {
	ms := newMemStore()
	seedLink(ms, 7, []byte("ct"), []byte("nonce"), 5)
	ok, err := ms.ReEncryptLinkToken(context.Background(), 7, []byte("new"), []byte("newn"), 9, 3)
	if err != nil {
		t.Fatal(err)
	}
	if ok {
		t.Fatal("update on a stale fromVersion should no-op")
	}
	if got := ms.links[7]; string(got.TokenEnc) != "ct" || got.KeyVersion != 5 {
		t.Fatal("the row was clobbered despite a stale fromVersion")
	}
}

// A rotated KEK: LICHESS_TOKEN_KEY_OLD unwraps the data key, and it is re-sealed
// under the new KEK and persisted, so the old KEK can be dropped next deploy.
func TestKEKReWrapOnLoad(t *testing.T) {
	ctx := context.Background()
	oldK := kek(t, 0x55)
	newK := kek(t, 0x66)

	// Bootstrap a data key under the OLD KEK.
	ms := newMemStore()
	k0, err := newRing(ctx, ms, oldK, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	tok, tnonce, tver, _ := k0.SealToken("carried_token")

	// The stored DEK is currently sealed under oldK, not newK.
	if _, err := newK.Open(ms.versions[1].DEKEnc, ms.versions[1].DEKNonce); err == nil {
		t.Fatal("precondition: the DEK should not yet open under the new KEK")
	}

	// Load with the new KEK, old KEK supplied for the migration.
	k1, err := newRing(ctx, ms, newK, oldK, nil)
	if err != nil {
		t.Fatalf("re-wrap load failed: %v", err)
	}
	// The row is now sealed under the new KEK — old KEK can be dropped.
	if _, err := newK.Open(ms.versions[1].DEKEnc, ms.versions[1].DEKNonce); err != nil {
		t.Fatalf("DEK was not re-wrapped under the new KEK: %v", err)
	}
	// And the token sealed before the rotation still opens.
	if got, err := k1.OpenToken(tok, tnonce, tver); err != nil || got != "carried_token" {
		t.Fatalf("token across KEK rotation got (%q, %v)", got, err)
	}
}

// Without the old KEK, a data key the current KEK can't open is a hard boot
// error — better than silently running unable to play half the linked accounts.
func TestKEKMismatchWithoutOldIsFatal(t *testing.T) {
	ctx := context.Background()
	ms := newMemStore()
	if _, err := newRing(ctx, ms, kek(t, 0x77), nil, nil); err != nil {
		t.Fatal(err)
	}
	// Reload under a DIFFERENT KEK with no old key configured.
	if _, err := newRing(ctx, ms, kek(t, 0x88), nil, nil); err == nil {
		t.Fatal("expected a fatal error when the KEK cannot open a stored data key")
	}
}

// A blank KEK is ErrNoKey — "lichess is off" — not a crash and not plaintext.
func TestBlankKEKIsErrNoKey(t *testing.T) {
	if _, err := NewEphemeral(""); !errors.Is(err, ErrNoKey) {
		t.Fatalf("want ErrNoKey, got %v", err)
	}
}
