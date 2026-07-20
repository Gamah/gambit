package lichess

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"strings"
)

// Token encryption at rest.
//
// gamchess holds a lichess board:play token for every linked player, which is
// the whole tier-B risk of the custody decision (CLAUDE.md): a DB dump would
// otherwise hand over the ability to play as every linked user for up to a year,
// and lichess has NO bulk revoke — killing N tokens is N serial signed calls.
// Encrypting at rest is the minimum that makes a dump-without-RCE survivable.
//
// AES-256-GCM from the Go stdlib, no new dependency. A fresh 12-byte nonce per
// seal, stored beside the ciphertext (a nonce is not a secret; reusing one under
// the same key is what breaks GCM, so it is never derived or recycled).
//
// This is only the AEAD primitive. It is used at TWO levels by internal/keyring
// (M15): the KEK (LICHESS_TOKEN_KEY) is a Cipher that seals/opens the rotating
// data keys, and each rotating data key is itself a Cipher that seals/opens the
// player tokens. Key rotation, versioning and the re-encrypt sweep all live in
// keyring; nothing about rotation belongs here, because rotation is orchestration
// over many Ciphers, not a property of one.

// ErrNoKey means LICHESS_TOKEN_KEY was blank or unusable. Callers treat it as
// "lichess is switched off", never as "store the token in the clear".
var ErrNoKey = errors.New("lichess: no token encryption key configured")

// Cipher seals and opens lichess tokens. The zero value is unusable on purpose:
// use NewCipher, and honour ErrNoKey.
type Cipher struct {
	aead cipher.AEAD
}

// NewCipher builds the token cipher from a 32-byte key given as base64 (std or
// raw, padded or not) or hex. A blank key returns ErrNoKey — the caller is
// expected to log a warning and run with lichess off, exactly as a blank
// SESSION_SECRET degrades rather than kills the process.
//
// Anything present but unusable (wrong length, undecodable) is a hard error
// rather than a silent fallback: an operator who set the key meant to set it,
// and quietly running unencrypted would be the one outcome this whole file
// exists to prevent.
func NewCipher(key string) (*Cipher, error) {
	key = strings.TrimSpace(key)
	if key == "" {
		return nil, ErrNoKey
	}

	raw, err := decodeKey(key)
	if err != nil {
		return nil, err
	}
	return NewCipherFromBytes(raw)
}

// NewCipherFromBytes builds a Cipher from a raw 32-byte key. NewCipher is this
// plus a decoding step; keyring calls this one directly, because a rotating data
// key is generated as raw random bytes rather than typed by an operator.
func NewCipherFromBytes(raw []byte) (*Cipher, error) {
	if len(raw) != 32 {
		return nil, fmt.Errorf("lichess: key must be 32 bytes (AES-256), got %d", len(raw))
	}

	block, err := aes.NewCipher(raw)
	if err != nil {
		return nil, fmt.Errorf("lichess: build cipher: %w", err)
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("lichess: build GCM: %w", err)
	}
	return &Cipher{aead: aead}, nil
}

// decodeKey accepts the encodings an operator plausibly generates:
// `openssl rand -base64 32`, `openssl rand -hex 32`, or the raw-url base64 a
// password manager might emit.
func decodeKey(key string) ([]byte, error) {
	if raw, err := hex.DecodeString(key); err == nil && len(raw) == 32 {
		return raw, nil
	}
	for _, enc := range []*base64.Encoding{
		base64.StdEncoding, base64.RawStdEncoding,
		base64.URLEncoding, base64.RawURLEncoding,
	} {
		if raw, err := enc.DecodeString(key); err == nil && len(raw) == 32 {
			return raw, nil
		}
	}
	return nil, errors.New("lichess: token key is neither 32-byte hex nor 32-byte base64")
}

// Seal encrypts a token, returning (ciphertext, nonce) for the two BYTEA columns.
func (c *Cipher) Seal(token string) (ct, nonce []byte, err error) {
	if c == nil || c.aead == nil {
		return nil, nil, ErrNoKey
	}

	nonce = make([]byte, c.aead.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		// A nonce we can't randomise is a nonce we might repeat, and a repeated
		// GCM nonce under one key leaks plaintext. Refuse rather than degrade.
		return nil, nil, fmt.Errorf("lichess: read nonce: %w", err)
	}
	return c.aead.Seal(nil, nonce, []byte(token), nil), nonce, nil
}

// Open decrypts a token. GCM authenticates, so a tampered row fails here rather
// than returning a plausible-looking wrong token.
func (c *Cipher) Open(ct, nonce []byte) (string, error) {
	if c == nil || c.aead == nil {
		return "", ErrNoKey
	}
	if len(nonce) != c.aead.NonceSize() {
		return "", errors.New("lichess: stored nonce has the wrong length")
	}
	pt, err := c.aead.Open(nil, nonce, ct, nil)
	if err != nil {
		return "", fmt.Errorf("lichess: decrypt token: %w", err)
	}
	return string(pt), nil
}
