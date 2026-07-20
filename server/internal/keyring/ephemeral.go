package keyring

import (
	"context"
	"sort"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
	"github.com/gamah/gambit/server/internal/store"
	"go.uber.org/zap"
)

// NewEphemeral builds a keyring backed by memory instead of Postgres: it mints a
// single data key at construction and forgets everything on exit. It exists for
// the DB-less tests in this repo (the dev host has no Postgres), where the code
// under test refuses requests BEFORE any DB touch and only needs a working
// SealToken/OpenToken. A blank KEK still returns ErrNoKey.
//
// It is NOT a production mode: an ephemeral key can't decrypt anything a previous
// process sealed, so a real server must always use New.
func NewEphemeral(kekKey string) (*KeyRing, error) {
	kek, err := lichess.NewCipher(kekKey)
	if err != nil {
		return nil, err
	}
	return newRing(context.Background(), newMemStore(), kek, nil, zap.NewNop())
}

// memStore is a full in-memory keyStore: it backs NewEphemeral and the keyring
// unit tests, which drive rotation and the re-encrypt sweep with no database.
type memStore struct {
	mu       sync.Mutex
	versions map[int32]store.KeyVersion
	links    map[int64]store.LichessLink
}

func newMemStore() *memStore {
	return &memStore{
		versions: map[int32]store.KeyVersion{},
		links:    map[int64]store.LichessLink{},
	}
}

func (m *memStore) AllKeyVersions(ctx context.Context) ([]store.KeyVersion, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]store.KeyVersion, 0, len(m.versions))
	for _, v := range m.versions {
		out = append(out, v)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Version < out[j].Version })
	return out, nil
}

func (m *memStore) InsertKeyVersion(ctx context.Context, version int32, dekEnc, dekNonce []byte) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.versions[version] = store.KeyVersion{Version: version, DEKEnc: dekEnc, DEKNonce: dekNonce}
	return nil
}

func (m *memStore) ReWrapKeyVersion(ctx context.Context, version int32, dekEnc, dekNonce []byte) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	v := m.versions[version]
	v.DEKEnc, v.DEKNonce = dekEnc, dekNonce
	m.versions[version] = v
	return nil
}

func (m *memStore) RetireUnusedKeyVersions(ctx context.Context, current int32) (int64, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	used := map[int32]bool{}
	for _, l := range m.links {
		used[l.KeyVersion] = true
	}
	var n int64
	for ver, v := range m.versions {
		if ver != current && v.RetiredAt == nil && !used[ver] {
			now := time.Now().UTC()
			v.RetiredAt = &now
			m.versions[ver] = v
			n++
		}
	}
	return n, nil
}

func (m *memStore) LinksNeedingReEncrypt(ctx context.Context, current int32, afterSteamID int64, limit int) ([]store.LichessLink, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	var out []store.LichessLink
	for _, l := range m.links {
		if l.KeyVersion != current && l.SteamID > afterSteamID {
			out = append(out, l)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].SteamID < out[j].SteamID })
	if len(out) > limit {
		out = out[:limit]
	}
	return out, nil
}

func (m *memStore) ReEncryptLinkToken(ctx context.Context, steamID int64, tokenEnc, tokenNonce []byte, toVersion, fromVersion int32) (bool, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	l, ok := m.links[steamID]
	if !ok || l.KeyVersion != fromVersion { // the optimistic guard, mirroring the SQL
		return false, nil
	}
	l.TokenEnc, l.TokenNonce, l.KeyVersion = tokenEnc, tokenNonce, toVersion
	m.links[steamID] = l
	return true, nil
}
