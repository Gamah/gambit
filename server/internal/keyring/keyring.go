// Package keyring is gamchess's envelope encryption for lichess tokens (M15).
//
// # Why this exists
//
// Before M15 every stored lichess token was sealed directly under one static env
// key, LICHESS_TOKEN_KEY, and there was no way to change that key without turning
// every token into an undecryptable row. That was the "no rotation path" open
// spike: a leaked key, or any reason to re-key, forced every linked player to
// re-link. keyring removes that landmine.
//
// # The envelope
//
// One KEK, many DEKs:
//
//   - The KEK (key-encryption key) IS LICHESS_TOKEN_KEY. It stays a static env
//     secret and is still the one thing to back up, but it no longer seals a
//     token directly — it only seals the data keys below.
//   - A DEK (data-encryption key) is 32 random bytes generated here, stored ONLY
//     as ciphertext sealed under the KEK (store.lichess_key_versions). The
//     plaintext DEK lives only in this process's memory. Tokens are sealed under
//     the CURRENT DEK; every token row carries the DEK version that sealed it.
//
// So the chain is KEK (env) → unwraps → DEK (DB, sealed) → unwraps → token (DB,
// sealed). Rotation generates a new DEK and a background sweep re-seals token
// rows onto it; re-keying the KEK re-wraps the handful of DEK rows and never
// touches a token. Version 0 is the legacy sentinel: a token stamped 0 was sealed
// directly under the KEK before M15, and OpenToken opens it with the KEK.
//
// # What the envelope does and does not buy
//
// On this deployment the KEK sits in .env on the same box as the database, so a
// full-box compromise reads both and the envelope adds no secrecy over the old
// single key. Its real value is operational: rotate and re-key WITHOUT orphaning
// links. The confidentiality-vs-a-DB-only-dump story only holds where the KEK is
// genuinely elsewhere, which it isn't here. Recorded so nobody over-claims it.
package keyring

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"io"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gamah/gambit/server/internal/store"
	"github.com/jackc/pgx/v5/pgxpool"
	"go.uber.org/zap"
)

// ErrNoKey re-exports lichess.ErrNoKey so callers switch on one sentinel: a blank
// KEK means "lichess is off", exactly as it did before M15, and NewRouter's
// existing errors.Is check keeps working unchanged.
var ErrNoKey = lichess.ErrNoKey

// reEncryptPage bounds one page of the sweep. Small: the sweep is background work
// and there is no reason to pull thousands of rows into memory at once.
const reEncryptPage = 256

// KeyRing holds the KEK and every unwrapped DEK, and owns rotation and the
// re-encrypt sweep. Safe for concurrent use: SealToken/OpenToken take a read
// lock, Rotate takes the write lock.
type KeyRing struct {
	log   *zap.Logger
	store keyStore

	kek    *lichess.Cipher // seals/opens DEKs, and opens legacy version-0 tokens
	oldKEK *lichess.Cipher // optional previous KEK, for the re-wrap-on-load path; nil when unset

	mu         sync.RWMutex
	deks       map[int32]*lichess.Cipher // every version ever loaded, retired ones included
	current    int32                     // the version SealToken uses; never a retired one
	currentAge time.Time                 // when the current DEK was created — drives rotation cadence
	maxVersion int32                     // highest version number ever assigned, so a new one is always fresh
}

// keyStore is the persistence keyring needs, as an interface so the crypto and
// rotation logic can be unit-tested with an in-memory fake — there is no live
// Postgres on the dev host, and none of this should require one to test.
type keyStore interface {
	AllKeyVersions(ctx context.Context) ([]store.KeyVersion, error)
	InsertKeyVersion(ctx context.Context, version int32, dekEnc, dekNonce []byte) error
	ReWrapKeyVersion(ctx context.Context, version int32, dekEnc, dekNonce []byte) error
	RetireUnusedKeyVersions(ctx context.Context, current int32) (int64, error)
	LinksNeedingReEncrypt(ctx context.Context, current int32, afterSteamID int64, limit int) ([]store.LichessLink, error)
	ReEncryptLinkToken(ctx context.Context, steamID int64, tokenEnc, tokenNonce []byte, toVersion, fromVersion int32) (bool, error)
}

// New builds the keyring from the KEK env values and loads (or bootstraps) the
// data keys from the database.
//
// A blank kekKey returns ErrNoKey — lichess is off, exactly as a blank
// LICHESS_TOKEN_KEY meant before. A present-but-broken key is a hard error, same
// discipline as the old NewCipher. oldKEKKey is optional: set it during a KEK
// rotation and any DEK row the new KEK can't open is unwrapped with the old one
// and re-sealed under the new — see loadLocked.
func New(ctx context.Context, db *pgxpool.Pool, kekKey, oldKEKKey string, log *zap.Logger) (*KeyRing, error) {
	kek, err := lichess.NewCipher(kekKey)
	if err != nil {
		return nil, err // ErrNoKey for blank, hard error for malformed
	}
	var oldKEK *lichess.Cipher
	if oldKEKKey != "" {
		oldKEK, err = lichess.NewCipher(oldKEKKey)
		if err != nil {
			return nil, fmt.Errorf("keyring: LICHESS_TOKEN_KEY_OLD is set but unusable: %w", err)
		}
	}
	return newRing(ctx, pgStore{db}, kek, oldKEK, log)
}

// newRing is the shared constructor over any keyStore. Production passes pgStore;
// tests pass an in-memory fake.
func newRing(ctx context.Context, ks keyStore, kek, oldKEK *lichess.Cipher, log *zap.Logger) (*KeyRing, error) {
	if log == nil {
		log = zap.NewNop()
	}
	k := &KeyRing{
		log:    log,
		store:  ks,
		kek:    kek,
		oldKEK: oldKEK,
		deks:   map[int32]*lichess.Cipher{},
	}
	k.mu.Lock()
	defer k.mu.Unlock()
	if err := k.loadLocked(ctx); err != nil {
		return nil, err
	}
	return k, nil
}

// loadLocked reads every DEK row, unwraps it under the KEK (or the old KEK, then
// re-wraps), and picks the current version. If the table is empty it bootstraps
// version 1. Caller holds the write lock.
func (k *KeyRing) loadLocked(ctx context.Context) error {
	versions, err := k.store.AllKeyVersions(ctx)
	if err != nil {
		return fmt.Errorf("keyring: load key versions: %w", err)
	}

	for _, v := range versions {
		dek, rewrapped, err := k.unwrapDEK(ctx, v)
		if err != nil {
			// A DEK we cannot open means every token under it is unreadable and the
			// KEK is wrong (or LICHESS_TOKEN_KEY_OLD is missing). Fail loudly at
			// boot rather than start a server that silently can't play half its
			// linked accounts.
			return err
		}
		if rewrapped {
			k.log.Info("keyring: re-wrapped a data key under the new KEK",
				zap.Int32("version", v.Version))
		}
		cipher, err := lichess.NewCipherFromBytes(dek)
		if err != nil {
			return fmt.Errorf("keyring: build cipher for version %d: %w", v.Version, err)
		}
		k.deks[v.Version] = cipher
		if v.Version > k.maxVersion {
			k.maxVersion = v.Version
		}
		// Current is the newest version that has not been retired. Retired keys stay
		// loaded (so a stray token under one still opens) but are never chosen.
		if v.RetiredAt == nil && v.Version > k.current {
			k.current = v.Version
			k.currentAge = v.CreatedAt
		}
	}

	if k.current == 0 {
		// Empty table, or (defensively) every row retired: mint the first/next DEK.
		if _, err := k.rotateLocked(ctx); err != nil {
			return fmt.Errorf("keyring: bootstrap first data key: %w", err)
		}
		k.log.Info("keyring: generated the first lichess data key", zap.Int32("version", k.current))
	}
	return nil
}

// unwrapDEK returns the plaintext DEK for a row. It tries the current KEK first;
// on failure, if an old KEK is configured, it tries that and — on success —
// re-seals the row under the current KEK (the KEK-rotation path). rewrapped is
// true when that happened.
func (k *KeyRing) unwrapDEK(ctx context.Context, v store.KeyVersion) (dek []byte, rewrapped bool, err error) {
	if pt, e := k.kek.Open(v.DEKEnc, v.DEKNonce); e == nil {
		return []byte(pt), false, nil
	}
	if k.oldKEK == nil {
		return nil, false, fmt.Errorf("keyring: cannot decrypt data key version %d under the KEK "+
			"(set LICHESS_TOKEN_KEY_OLD if you are rotating the KEK)", v.Version)
	}
	pt, e := k.oldKEK.Open(v.DEKEnc, v.DEKNonce)
	if e != nil {
		return nil, false, fmt.Errorf("keyring: data key version %d opens under neither the current "+
			"nor the old KEK", v.Version)
	}
	// Re-seal under the new KEK and persist, so the old KEK can be dropped next deploy.
	enc, nonce, e := k.kek.Seal(pt)
	if e != nil {
		return nil, false, fmt.Errorf("keyring: re-wrap version %d: %w", v.Version, e)
	}
	if e := k.store.ReWrapKeyVersion(ctx, v.Version, enc, nonce); e != nil {
		return nil, false, fmt.Errorf("keyring: persist re-wrap of version %d: %w", v.Version, e)
	}
	return []byte(pt), true, nil
}

// rotateLocked generates a new DEK, persists it sealed under the KEK, and makes
// it current. Caller holds the write lock. Returns the new version.
func (k *KeyRing) rotateLocked(ctx context.Context) (int32, error) {
	raw := make([]byte, 32)
	if _, err := io.ReadFull(rand.Reader, raw); err != nil {
		return 0, fmt.Errorf("keyring: generate data key: %w", err)
	}
	cipher, err := lichess.NewCipherFromBytes(raw)
	if err != nil {
		return 0, err
	}
	enc, nonce, err := k.kek.Seal(string(raw))
	if err != nil {
		return 0, fmt.Errorf("keyring: seal data key: %w", err)
	}

	version := k.maxVersion + 1
	if err := k.store.InsertKeyVersion(ctx, version, enc, nonce); err != nil {
		return 0, err
	}
	k.deks[version] = cipher
	k.current = version
	k.maxVersion = version
	k.currentAge = time.Now()
	return version, nil
}

// SealToken seals a token under the current DEK, returning the ciphertext, its
// nonce, and the version to stamp on the row.
func (k *KeyRing) SealToken(token string) (ct, nonce []byte, version int32, err error) {
	k.mu.RLock()
	defer k.mu.RUnlock()
	dek := k.deks[k.current]
	if dek == nil {
		return nil, nil, 0, errors.New("keyring: no current data key")
	}
	ct, nonce, err = dek.Seal(token)
	return ct, nonce, k.current, err
}

// OpenToken opens a token sealed under the given version. Version 0 is the legacy
// sentinel — opened with the KEK directly. An unknown version is an error rather
// than a wrong-key decrypt (which GCM would reject anyway).
func (k *KeyRing) OpenToken(ct, nonce []byte, version int32) (string, error) {
	k.mu.RLock()
	defer k.mu.RUnlock()
	if version == 0 {
		return k.kek.Open(ct, nonce)
	}
	dek := k.deks[version]
	if dek == nil {
		return "", fmt.Errorf("keyring: no data key for version %d", version)
	}
	return dek.Open(ct, nonce)
}

// Rotate forces a new current DEK. The background loop calls this on the cadence;
// it is also the explicit lever an operator or a test can pull.
func (k *KeyRing) Rotate(ctx context.Context) (int32, error) {
	k.mu.Lock()
	defer k.mu.Unlock()
	return k.rotateLocked(ctx)
}

// ReEncrypt re-seals every token not already under the current DEK, in cursor
// pages. Returns how many rows it re-sealed. Safe against concurrent re-links:
// the store update no-ops on a row whose version already advanced.
func (k *KeyRing) ReEncrypt(ctx context.Context) (int, error) {
	migrated := 0
	var after int64
	for {
		k.mu.RLock()
		current := k.current
		k.mu.RUnlock()

		links, err := k.store.LinksNeedingReEncrypt(ctx, current, after, reEncryptPage)
		if err != nil {
			return migrated, err
		}
		if len(links) == 0 {
			return migrated, nil
		}
		for _, l := range links {
			after = l.SteamID // advance the cursor past every row, even one we can't re-seal

			token, err := k.OpenToken(l.TokenEnc, l.TokenNonce, l.KeyVersion)
			if err != nil {
				k.log.Error("keyring: a stored token will not decrypt during re-encrypt",
					zap.Int64("steam_id", l.SteamID), zap.Int32("version", l.KeyVersion), zap.Error(err))
				continue
			}
			ct, nonce, ver, err := k.SealToken(token)
			if err != nil {
				k.log.Error("keyring: could not re-seal a token", zap.Int64("steam_id", l.SteamID), zap.Error(err))
				continue
			}
			ok, err := k.store.ReEncryptLinkToken(ctx, l.SteamID, ct, nonce, ver, l.KeyVersion)
			if err != nil {
				return migrated, err
			}
			if ok {
				migrated++
			}
		}
	}
}

// maintain is one pass of the background loop: rotate if the current DEK is older
// than rotateEvery, then drain any lagging rows onto the current DEK, then retire
// keys nothing references. Errors are logged, not fatal — a transient DB error
// must not kill the daemon; the next tick retries.
func (k *KeyRing) maintain(ctx context.Context, rotateEvery time.Duration) {
	if rotateEvery > 0 {
		k.mu.RLock()
		due := time.Since(k.currentAge) >= rotateEvery
		k.mu.RUnlock()
		if due {
			if v, err := k.Rotate(ctx); err != nil {
				k.log.Error("keyring: rotation failed", zap.Error(err))
			} else {
				k.log.Info("keyring: rotated to a new lichess data key", zap.Int32("version", v))
			}
		}
	}

	if n, err := k.ReEncrypt(ctx); err != nil {
		k.log.Error("keyring: re-encrypt sweep failed", zap.Error(err))
	} else if n > 0 {
		k.log.Info("keyring: re-encrypted lichess tokens onto the current key", zap.Int("rows", n))
	}

	k.mu.RLock()
	current := k.current
	k.mu.RUnlock()
	if n, err := k.store.RetireUnusedKeyVersions(ctx, current); err != nil {
		k.log.Error("keyring: retiring unused keys failed", zap.Error(err))
	} else if n > 0 {
		k.log.Info("keyring: retired data keys nothing references anymore", zap.Int64("count", n))
	}
}

// Run is the background daemon: one maintain pass immediately (so legacy version-0
// rows migrate onto a real DEK promptly after deploy), then a pass per checkEvery.
// rotateEvery <= 0 disables the timed rotation but still runs the boot sweep, so
// versioning and legacy migration work even with rotation switched off.
//
// It uses the passed context's lifetime; NewRouter passes a process-lived one, so
// the daemon dies with the process. Blocks — start it in a goroutine.
func (k *KeyRing) Run(ctx context.Context, rotateEvery, checkEvery time.Duration) {
	k.maintain(ctx, rotateEvery)
	if rotateEvery <= 0 {
		return // nothing more to time; the boot sweep already migrated anything legacy
	}
	if checkEvery <= 0 {
		checkEvery = 24 * time.Hour
	}
	t := time.NewTicker(checkEvery)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			k.maintain(ctx, rotateEvery)
		}
	}
}

// CurrentVersion is the version SealToken stamps. For diagnostics/logging.
func (k *KeyRing) CurrentVersion() int32 {
	k.mu.RLock()
	defer k.mu.RUnlock()
	return k.current
}

// pgStore is the production keyStore: a thin pass-through to the SQL in
// internal/store, which is where every statement lives.
type pgStore struct{ db *pgxpool.Pool }

func (s pgStore) AllKeyVersions(ctx context.Context) ([]store.KeyVersion, error) {
	return store.AllKeyVersions(ctx, s.db)
}
func (s pgStore) InsertKeyVersion(ctx context.Context, version int32, dekEnc, dekNonce []byte) error {
	return store.InsertKeyVersion(ctx, s.db, version, dekEnc, dekNonce)
}
func (s pgStore) ReWrapKeyVersion(ctx context.Context, version int32, dekEnc, dekNonce []byte) error {
	return store.ReWrapKeyVersion(ctx, s.db, version, dekEnc, dekNonce)
}
func (s pgStore) RetireUnusedKeyVersions(ctx context.Context, current int32) (int64, error) {
	return store.RetireUnusedKeyVersions(ctx, s.db, current)
}
func (s pgStore) LinksNeedingReEncrypt(ctx context.Context, current int32, afterSteamID int64, limit int) ([]store.LichessLink, error) {
	return store.LinksNeedingReEncrypt(ctx, s.db, current, afterSteamID, limit)
}
func (s pgStore) ReEncryptLinkToken(ctx context.Context, steamID int64, tokenEnc, tokenNonce []byte, toVersion, fromVersion int32) (bool, error) {
	return store.ReEncryptLinkToken(ctx, s.db, steamID, tokenEnc, tokenNonce, toVersion, fromVersion)
}
