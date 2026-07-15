package lichess

import (
	"bytes"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"strings"
	"testing"
)

// A key of the right size, in the encodings an operator plausibly produces.
var (
	rawKey    = bytes.Repeat([]byte{0x2a}, 32)
	b64Key    = base64.StdEncoding.EncodeToString(rawKey)
	hexKey    = hex.EncodeToString(rawKey)
	rawURLKey = base64.RawURLEncoding.EncodeToString(rawKey)
)

func TestNewCipherKeyEncodings(t *testing.T) {
	for name, key := range map[string]string{
		"std base64":     b64Key,
		"raw url base64": rawURLKey,
		"hex":            hexKey,
		"whitespace":     "  " + b64Key + "\n",
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := NewCipher(key); err != nil {
				t.Fatalf("expected %s key to be accepted, got %v", name, err)
			}
		})
	}
}

// A blank key is "lichess is off", NOT "store it in the clear". This is the
// distinction the whole file exists for, so it gets its own test.
func TestNewCipherBlankKeyIsFeatureOff(t *testing.T) {
	c, err := NewCipher("")
	if !errors.Is(err, ErrNoKey) {
		t.Fatalf("want ErrNoKey, got %v", err)
	}
	if c != nil {
		t.Fatal("want a nil cipher for a blank key")
	}

	// And a nil cipher must refuse to seal rather than pass the token through.
	if _, _, err := c.Seal("tok"); !errors.Is(err, ErrNoKey) {
		t.Fatalf("a nil cipher must refuse to seal, got %v", err)
	}
}

// A key that is present but wrong is a hard error, never a silent fallback: the
// operator meant to set it.
func TestNewCipherBadKeysAreFatal(t *testing.T) {
	for name, key := range map[string]string{
		"too short":     base64.StdEncoding.EncodeToString(make([]byte, 16)),
		"too long":      base64.StdEncoding.EncodeToString(make([]byte, 64)),
		"not encoded":   "hunter2",
		"hex but short": hex.EncodeToString(make([]byte, 16)),
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := NewCipher(key); err == nil {
				t.Fatalf("expected %q to be rejected", name)
			} else if errors.Is(err, ErrNoKey) {
				t.Fatal("a bad key must not masquerade as an absent one")
			}
		})
	}
}

func TestSealOpenRoundTrip(t *testing.T) {
	c, err := NewCipher(b64Key)
	if err != nil {
		t.Fatal(err)
	}
	const token = "lio_ABCdef0123456789"

	ct, nonce, err := c.Seal(token)
	if err != nil {
		t.Fatal(err)
	}
	got, err := c.Open(ct, nonce)
	if err != nil {
		t.Fatal(err)
	}
	if got != token {
		t.Fatalf("round trip: got %q want %q", got, token)
	}

	// The ciphertext must not contain the plaintext. Cheap, but it is the exact
	// mistake ("encrypted" that isn't) worth a test.
	if bytes.Contains(ct, []byte(token)) {
		t.Fatal("ciphertext contains the plaintext token")
	}
}

// GCM nonces must never repeat under one key. Seal the same token twice: both
// nonce and ciphertext must differ.
func TestSealUsesAFreshNonce(t *testing.T) {
	c, _ := NewCipher(b64Key)

	ct1, n1, err := c.Seal("same-token")
	if err != nil {
		t.Fatal(err)
	}
	ct2, n2, err := c.Seal("same-token")
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Equal(n1, n2) {
		t.Fatal("nonce reused across two Seals — GCM is broken by exactly this")
	}
	if bytes.Equal(ct1, ct2) {
		t.Fatal("identical ciphertext for the same plaintext — the nonce isn't reaching the seal")
	}
}

// GCM authenticates: a tampered row must fail loudly, not decrypt to something
// plausible.
func TestOpenRejectsTampering(t *testing.T) {
	c, _ := NewCipher(b64Key)
	ct, nonce, _ := c.Seal("lio_realtoken")

	t.Run("flipped ciphertext byte", func(t *testing.T) {
		bad := append([]byte(nil), ct...)
		bad[0] ^= 0xff
		if _, err := c.Open(bad, nonce); err == nil {
			t.Fatal("expected a tampered ciphertext to fail authentication")
		}
	})

	t.Run("flipped nonce byte", func(t *testing.T) {
		bad := append([]byte(nil), nonce...)
		bad[0] ^= 0xff
		if _, err := c.Open(ct, bad); err == nil {
			t.Fatal("expected a wrong nonce to fail authentication")
		}
	})

	t.Run("wrong length nonce", func(t *testing.T) {
		if _, err := c.Open(ct, []byte{1, 2, 3}); err == nil {
			t.Fatal("expected a short nonce to be rejected")
		}
	})

	t.Run("another key cannot open it", func(t *testing.T) {
		other, _ := NewCipher(hex.EncodeToString(bytes.Repeat([]byte{0x99}, 32)))
		if _, err := other.Open(ct, nonce); err == nil {
			t.Fatal("a different key must not open this ciphertext")
		}
	})
}

// The empty string is a legitimate (if useless) plaintext — make sure it round
// trips rather than tripping a length assumption.
func TestSealEmpty(t *testing.T) {
	c, _ := NewCipher(b64Key)
	ct, nonce, err := c.Seal("")
	if err != nil {
		t.Fatal(err)
	}
	got, err := c.Open(ct, nonce)
	if err != nil || got != "" {
		t.Fatalf("got (%q,%v) want (\"\",nil)", got, err)
	}
}

func TestErrNoKeyMessageDoesNotLeakTheKey(t *testing.T) {
	_, err := NewCipher("hunter2")
	if err != nil && strings.Contains(err.Error(), "hunter2") {
		t.Fatal("the error message echoes the key back")
	}
}
