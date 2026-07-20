#!/usr/bin/env bash
#
# test.sh — host-side validation of M15 (lichess token key rotation).
#
# Run on the m15-key-rotation branch on the deploy host (which has Docker):
#
#     sudo ./test.sh
#
# This is SERVER-ONLY. Nothing here touches the s&box client, and nothing needs a
# browser, a lichess account, or the OAuth flow — the KEK-rotation and fail-closed
# paths operate on the DATA-KEY rows, which exist after the app bootstraps with
# zero linked tokens. It stands up throwaway Postgres + app containers, proves the
# M15 behaviour against them, and cleans everything up.
#
# What it proves, in three tiers:
#   A. The full Go suite passes under -race (all M15 crypto/rotation/sweep logic).
#   B. Migration 00003 is zero-downtime: a pre-M15 row gets key_version = 0 for
#      free, and the migration reverses cleanly.
#   C. The REAL app image boots against real Postgres and: bootstraps data key v1;
#      rotates the KEK via LICHESS_TOKEN_KEY_OLD (re-wraps the DEK, stays healthy);
#      runs on the new KEK alone afterwards; and FAILS CLOSED at boot when handed a
#      KEK that can't open the stored data key with no old key to fall back on.
#
# Env knobs:  KEEP=1 skips cleanup (leave containers up to poke at).
#             SKIP_GO=1 skips tier A (the slow one on a cold module cache).

set -Eeuo pipefail

# ── Layout & constants ──────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_DIR="$SCRIPT_DIR/server"

PREFIX="gambit-m15"
NET="$PREFIX-net"
PG_B="$PREFIX-pgb"       # tier B: manual-migration DB
PG_C="$PREFIX-pg"        # tier C: the DB the app migrates and rotates against
APP="$PREFIX-app"
IMG="$PREFIX-app:test"
GO_IMG="golang:1.22"
PG_IMG="postgres:16-alpine"   # matches docker/docker-compose.yml
APP_PORT="6479"               # host port for the app under test (loopback only)
DSN="postgres://postgres@pg:5432/postgres?sslmode=disable"

# Two distinct 32-byte keys, base64 — a real KEK and the "new" KEK for rotation.
KEK1="$(head -c 32 /dev/urandom | base64 | tr -d '\n')"
KEK2="$(head -c 32 /dev/urandom | base64 | tr -d '\n')"

# ── Pretty output ───────────────────────────────────────────────────────────
if [ -t 1 ]; then B=$'\e[1m'; G=$'\e[32m'; R=$'\e[31m'; Y=$'\e[33m'; Z=$'\e[0m'; else B=; G=; R=; Y=; Z=; fi
PASS=0; FAILS=0
step()  { printf '\n%s── %s ──%s\n' "$B" "$*" "$Z"; }
ok()    { printf '  %s✓%s %s\n' "$G" "$Z" "$*"; PASS=$((PASS+1)); }
bad()   { printf '  %s✗ %s%s\n' "$R" "$*" "$Z"; FAILS=$((FAILS+1)); }
die()   { printf '\n%sFATAL:%s %s\n' "$R" "$Z" "$*" >&2; exit 1; }
note()  { printf '  %s· %s%s\n' "$Y" "$*" "$Z"; }

# assert "<label>" <expected> <actual>
assert_eq() { if [ "$2" = "$3" ]; then ok "$1 ($2)"; else bad "$1: want [$2] got [$3]"; fi; }

# ── Cleanup ─────────────────────────────────────────────────────────────────
cleanup() {
  [ "${KEEP:-0}" = "1" ] && { note "KEEP=1 — leaving $PREFIX-* containers up"; return; }
  docker rm -f "$APP" "$PG_B" "$PG_C" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# ── Preflight ───────────────────────────────────────────────────────────────
command -v docker >/dev/null || die "docker not found"
docker info >/dev/null 2>&1 || die "cannot talk to the Docker daemon (run with sudo?)"
command -v curl   >/dev/null || die "curl not found (used for health checks)"
[ -d "$SERVER_DIR" ] || die "no server/ dir beside this script"
[ -f "$SERVER_DIR/migrations/00003_lichess_key_versions.sql" ] || die "00003 migration missing — wrong branch?"

printf '%sM15 host test%s  branch=%s  port=%s\n' "$B" "$Z" "$(git -C "$SCRIPT_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')" "$APP_PORT"

# Helpers to talk to a throwaway Postgres by container name.
wait_pg()  { local c=$1 i; for i in $(seq 1 60); do docker exec "$c" pg_isready -U postgres -q && return 0; sleep 0.5; done; die "$c never became ready"; }
sql()      { docker exec -i "$1" psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q; }      # SQL on stdin
scalar()   { docker exec "$1" psql -U postgres -d postgres -tAc "$2" 2>/dev/null | tr -d '[:space:]'; }
# Extract just the "+goose Up" (or Down) body from a migration file for raw psql.
goose_section() { awk -v want="$2" '
  /-- \+goose Up/   {sec="Up";   next}
  /-- \+goose Down/ {sec="Down"; next}
  sec==want {print}' "$1"; }

MIG="$SERVER_DIR/migrations"

# ── Tier A: the Go suite under -race ────────────────────────────────────────
step "A · Go suite (-race) — all M15 logic"
if [ "${SKIP_GO:-0}" = "1" ]; then
  note "SKIP_GO=1 — skipped"
else
  note "docker run $GO_IMG go test ./... -race  (first run warms the module cache)"
  if docker run --rm -v "$SERVER_DIR":/src -w /src -v gamchess-gomod:/go/pkg/mod "$GO_IMG" \
        go test ./... -race; then
    ok "go test ./... -race passed"
  else
    bad "go test ./... -race FAILED"
  fi
fi

# ── Tier B: migration is zero-downtime + reversible ─────────────────────────
step "B · Migration 00003 — pre-M15 rows default to key_version = 0"
docker rm -f "$PG_B" >/dev/null 2>&1 || true
docker run -d --name "$PG_B" -e POSTGRES_HOST_AUTH_METHOD=trust "$PG_IMG" >/dev/null
wait_pg "$PG_B"

# Apply the schema as it stood BEFORE M15 (00001 + 00002), via raw psql.
goose_section "$MIG/00001_schema.sql"        Up | sql "$PG_B"
goose_section "$MIG/00002_lichess_links.sql" Up | sql "$PG_B"
ok "pre-M15 schema (00001 + 00002) applied"

# A linked player from before M15: a real row, no key_version column yet.
sql "$PG_B" <<'SQL'
INSERT INTO players (steam_id) VALUES (76561190000000001);
INSERT INTO lichess_links (steam_id, lichess_id, username, token_enc, token_nonce, scopes)
VALUES (76561190000000001, 'legacyuser', 'LegacyUser', '\xdeadbeef'::bytea, '\xcafebabe'::bytea, 'board:play');
SQL
ok "seeded a pre-M15 lichess_links row"

# Now apply M15.
goose_section "$MIG/00003_lichess_key_versions.sql" Up | sql "$PG_B"
ok "00003 applied on top of a populated table (no error, no rewrite)"

assert_eq "legacy row key_version defaulted to 0" \
  "0" "$(scalar "$PG_B" "SELECT key_version FROM lichess_links WHERE steam_id=76561190000000001")"
assert_eq "lichess_key_versions table exists and is empty (app hasn't run)" \
  "0" "$(scalar "$PG_B" "SELECT count(*) FROM lichess_key_versions")"

# Down reverses cleanly.
goose_section "$MIG/00003_lichess_key_versions.sql" Down | sql "$PG_B"
assert_eq "Down dropped the key_version column" "0" \
  "$(scalar "$PG_B" "SELECT count(*) FROM information_schema.columns WHERE table_name='lichess_links' AND column_name='key_version'")"
assert_eq "Down dropped lichess_key_versions" "0" \
  "$(scalar "$PG_B" "SELECT count(*) FROM information_schema.tables WHERE table_name='lichess_key_versions'")"
docker rm -f "$PG_B" >/dev/null 2>&1 || true

# ── Tier C: the real app boots, bootstraps, rotates the KEK, fails closed ────
step "C · Real app image — bootstrap, KEK rotation, fail-closed"
docker network create "$NET" >/dev/null 2>&1 || true
docker rm -f "$PG_C" >/dev/null 2>&1 || true
docker run -d --name "$PG_C" --network "$NET" --network-alias pg \
  -e POSTGRES_HOST_AUTH_METHOD=trust "$PG_IMG" >/dev/null
wait_pg "$PG_C"
ok "throwaway Postgres up"

note "building the app image from docker/Dockerfile (matches prod)…"
docker build -q -f "$SERVER_DIR/docker/Dockerfile" -t "$IMG" "$SERVER_DIR" >/dev/null \
  && ok "app image built" || die "app image build failed"

# run_app <KEK> <OLD_KEK_or_empty> : (re)start the app; env-only, no code change.
run_app() {
  docker rm -f "$APP" >/dev/null 2>&1 || true
  local envs=(-e "DATABASE_URL=$DSN" -e "PORT=6464" -e "PUBLIC_BASE_URL=http://localhost:$APP_PORT"
              -e "LICHESS_TOKEN_KEY=$1")
  [ -n "${2:-}" ] && envs+=(-e "LICHESS_TOKEN_KEY_OLD=$2")
  docker run -d --name "$APP" --network "$NET" -p "127.0.0.1:$APP_PORT:6464" "${envs[@]}" "$IMG" >/dev/null
}
healthy() { local i; for i in $(seq 1 40); do
    curl -sf "http://127.0.0.1:$APP_PORT/health" 2>/dev/null | grep -q '"status":"ok"' && return 0
    docker inspect -f '{{.State.Running}}' "$APP" 2>/dev/null | grep -q true || return 1  # died early
    sleep 0.5; done; return 1; }
logs() { docker logs "$APP" 2>&1; }

# C1 — fresh DB: bootstrap data key v1.
note "C1: first boot on an empty DB (KEK1)"
run_app "$KEK1" ""
if healthy; then ok "app is healthy on /health"; else bad "app never became healthy"; logs | tail -20; fi
logs | grep -q "generated the first lichess data key" \
  && ok "logged: generated the first data key" || bad "no 'generated the first data key' log"
assert_eq "exactly one data key, version 1" "1|1" \
  "$(scalar "$PG_C" "SELECT count(*)||'|'||coalesce(max(version),0) FROM lichess_key_versions")"

# C2 — rotate the KEK: new key primary, old key as _OLD → re-wrap in place.
note "C2: restart with a NEW KEK + LICHESS_TOKEN_KEY_OLD (KEK rotation)"
run_app "$KEK2" "$KEK1"
if healthy; then ok "app healthy after KEK rotation"; else bad "app unhealthy after KEK rotation"; logs | tail -20; fi
logs | grep -q "re-wrapped a data key under the new KEK" \
  && ok "logged: re-wrapped the data key under the new KEK" || bad "no re-wrap log"
assert_eq "still one data key (re-wrapped, not re-issued)" "1" \
  "$(scalar "$PG_C" "SELECT count(*) FROM lichess_key_versions")"

# C3 — old KEK dropped: the new KEK alone opens the re-wrapped data key.
note "C3: restart with the NEW KEK alone (old key retired)"
run_app "$KEK2" ""
if healthy; then ok "app healthy on the new KEK with no old key"; else bad "app unhealthy on new KEK alone"; logs | tail -20; fi

# C4 — fail closed: a KEK that can't open the stored data key, and no old key.
note "C4: restart with a WRONG KEK and no old key — must fail at boot"
run_app "$KEK1" ""      # KEK1 no longer opens the DEK (it's under KEK2 now)
if healthy; then
  bad "app came up with a KEK that cannot open the data key — should have died"
else
  ok "app refused to serve (fail-closed)"
  st="$(docker inspect -f '{{.State.Running}}={{.State.ExitCode}}' "$APP" 2>/dev/null || echo '?')"
  case "$st" in false=0) bad "process exited 0 — expected a fatal non-zero exit";;
                false=*) ok "process exited fatally ($st)";;
                *)       bad "process still running ($st) — expected a fatal exit";; esac
  logs | grep -qi "unusable\|cannot decrypt data key" \
    && ok "logged the KEK/data-key mismatch" || bad "no mismatch reason in logs"
fi

# ── Summary ─────────────────────────────────────────────────────────────────
step "Summary"
printf '  %s%d passed%s' "$G" "$PASS" "$Z"
[ "$FAILS" -gt 0 ] && printf ', %s%d FAILED%s' "$R" "$FAILS" "$Z"
printf '\n'
if [ "$FAILS" -gt 0 ]; then die "$FAILS check(s) failed — see above"; fi
printf '%sM15 looks good on this host.%s\n' "$G" "$Z"
