.PHONY: dev build test tidy fmt vet lint migrate-up migrate-down migrate-status \
        up rebuild down logs ps psql keys keys-note \
        testinst testinst-down testinst-logs testinst-ps

# ── Go (all containerised) ──────────────────────────────────────────────────
# Every Go target runs in a container, deliberately: neither the deploy host nor
# the dev machine has a Go toolchain, and the whole point of the 2-stage
# Dockerfile is that deploying needs only Docker. A host `go` would be a hidden
# prerequisite that works on one machine and not the other.
#
# The named volume keeps the module cache warm between runs, so only the first
# invocation pays for the download.
#
# GO_IMG is Debian-based, not alpine: `go test -race` needs cgo + gcc, which the
# alpine image doesn't ship. Not worth two images to save a few MB on a target
# nobody runs in prod.
GO_IMG  := golang:1.22
GO_RUN  := docker run --rm -v "$(CURDIR)":/src -w /src -v gamchess-gomod:/go/pkg/mod $(GO_IMG)

# Runs the server directly on the host — needs a local Go and a reachable
# DATABASE_URL. `make up` is the normal path; this is for iterating without a
# rebuild.
dev:
	go run ./cmd/server

build:
	$(GO_RUN) go build -ldflags="-s -w" -o bin/gamchess ./cmd/server

test:
	$(GO_RUN) go test ./... -race

# Prune/repair go.mod + go.sum. Only needed if a build fails on a module error:
# go.sum was seeded from rotaliate (a superset of our graph) because the machine
# that wrote this server has no Go either.
tidy:
	$(GO_RUN) go mod tidy

fmt:
	$(GO_RUN) gofmt -w .

vet:
	$(GO_RUN) go vet ./...

lint:
	docker run --rm -v "$(CURDIR)":/src -w /src \
		-v gamchess-gomod:/go/pkg/mod golangci/golangci-lint:latest golangci-lint run ./...

# ── Database ────────────────────────────────────────────────────────────────
# The server also runs migrations in-process at startup, so these are for
# host-side inspection and for stepping a migration back.
migrate-up:
	goose -dir migrations postgres "$(DATABASE_URL)" up

migrate-down:
	goose -dir migrations postgres "$(DATABASE_URL)" down

migrate-status:
	goose -dir migrations postgres "$(DATABASE_URL)" status

# ── Secrets gamchess generates for itself ───────────────────────────────────
# LICHESS_TOKEN_KEY and LICHESS_AUDIT_KEY are OURS — nothing is requested from
# lichess, which issues no client id, no secret and no API key (it has no client
# registration at all). So there is no reason to make a human invent them: `up`
# and `testinst` mint any that .env doesn't already have, and say so.
#
# THE RULE HERE IS NEVER OVERWRITE. LICHESS_TOKEN_KEY is the KEK that wraps every
# rotating data key (M15) — regenerating it would leave those data keys (and so
# every token) undecryptable. Re-keying the KEK is a deliberate act via
# LICHESS_TOKEN_KEY_OLD (see M15.md), never an accident of `make up`. So a key
# with ANY value is left strictly alone; only a missing or blank one is filled in.
#
# /dev/urandom rather than `openssl rand`: openssl is not guaranteed on a host
# whose only stated dependency is Docker, and this must not become a hidden
# prerequisite of `make up`.
LICHESS_KEYS := LICHESS_TOKEN_KEY LICHESS_AUDIT_KEY

keys:
	@set -e; \
	if [ ! -f .env ]; then \
		echo "ERROR: no .env here. Copy it first:  cp .env.example .env" >&2; \
		exit 1; \
	fi; \
	made=""; \
	for key in $(LICHESS_KEYS); do \
		cur=$$(sed -n "s/^$$key=//p" .env | head -1); \
		if [ -n "$$cur" ]; then continue; fi; \
		val=$$(head -c 32 /dev/urandom | base64 | tr -d '\n'); \
		if grep -q "^$$key=" .env; then \
			tmp=$$(mktemp); \
			awk -v k="$$key" -v v="$$val" \
				'$$0 ~ "^" k "=" && !seen { print k "=" v; seen=1; next } { print }' \
				.env > "$$tmp"; \
			cat "$$tmp" > .env; rm -f "$$tmp"; \
		else \
			printf '%s=%s\n' "$$key" "$$val" >> .env; \
		fi; \
		made="$$made $$key"; \
	done; \
	if [ -n "$$made" ]; then \
		echo "Generated into .env:$$made"; \
	fi

# Printed after every up/testinst, deliberately unconditionally: anyone running a
# gamchess needs to know these exist, that they are self-generated rather than
# requested, and above all that TOKEN_KEY must be backed up.
keys-note:
	@echo ""
	@echo "── lichess keys ────────────────────────────────────────────────────"
	@echo "Both live in .env and are generated here — nothing is requested from"
	@echo "lichess (it has no client registration: no client id, no secret, no"
	@echo "API key)."
	@echo ""
	@echo "  LICHESS_TOKEN_KEY   the KEK: wraps the rotating data keys that seal"
	@echo "                      every linked player's token at rest (M15). BACK IT"
	@echo "                      UP — it is the one durable secret. Lose it and every"
	@echo "                      player must re-link; to CHANGE it, rotate via"
	@echo "                      LICHESS_TOKEN_KEY_OLD (see M15.md), don't just"
	@echo "                      regenerate. Blank switches lichess off entirely —"
	@echo "                      it never falls back to storing plaintext."
	@echo "  LICHESS_AUDIT_KEY   gates POST /api/v1/lichess/audit, which asks"
	@echo "                      lichess which of our stored tokens are still"
	@echo "                      live. That sweep is the ONLY fast incident lever"
	@echo "                      we have — lichess has no bulk revoke. Blank"
	@echo "                      hides the route."
	@echo ""
	@echo ".env is gitignored and must stay that way: it is the only copy."
	@echo "───────────────────────────────────────────────────────────────────"

# ── Docker Compose ──────────────────────────────────────────────────────────
# GIT_HASH is computed on the host and passed as a Docker build arg so the
# version stamp is reliably per-commit. Computing it inside the container is
# unreliable for the test instance (its build context is a worktree whose .git
# lives outside the context).
COMPOSE := docker compose -f docker/docker-compose.yml --env-file .env

up: keys
	GIT_HASH=$$(git rev-parse --short HEAD 2>/dev/null || echo unknown) \
	$(COMPOSE) up -d --build
	@$(MAKE) --no-print-directory keys-note

rebuild: keys
	GIT_HASH=$$(git rev-parse --short HEAD 2>/dev/null || echo unknown) \
	$(COMPOSE) build --no-cache app
	$(COMPOSE) up -d
	@$(MAKE) --no-print-directory keys-note

# All of these pass --env-file, like up/rebuild do. Without it, compose still
# works but prints a WARN per unset variable ("PUBLIC_BASE_URL is not set…") —
# which is pure noise, and worse, it's noise on exactly the commands you run when
# the server is already broken. It reads like the cause and isn't.
down:
	$(COMPOSE) down

logs:
	$(COMPOSE) logs -f app

ps:
	$(COMPOSE) ps

# ── DB shell ────────────────────────────────────────────────────────────────
psql:
	$(COMPOSE) exec postgres psql -U gamchess -d gamchess

# ── Test instance (parallel stack for a feature branch) ─────────────────────
# Spins up a SECOND, isolated copy of the app + Postgres alongside prod so you
# can smoke-test a branch before merging. Config comes from the TEST_* keys in
# .env (TEST_PORT, TEST_PG_PORT, TEST_PUBLIC_BASE_URL).
#
# The build depends ONLY on the resolved REMOTE ref — it is agnostic to which
# branch is checked out locally and whether the working tree is dirty, so two
# people running the same command get the same build.
#
# BRANCH is resolved to an immutable commit SHA, preferring the remote, in order:
#   1. origin/<BRANCH>  (a pushed branch — the common case; latest pushed state)
#   2. tag <BRANCH>     (a remote tag, fetched via --tags)
# If neither resolves, the target FAILS LOUDLY rather than silently building a
# local-only ref. To deliberately build a local branch / tag / raw SHA, opt in
# with ALLOW_LOCAL_REF=1 (it then resolves <BRANCH> against the local repo).
#
#   make testinst BRANCH=m7-gamchess-identity        # build + start a pushed branch
#   make testinst BRANCH=abc1234 ALLOW_LOCAL_REF=1   # build a local-only ref/SHA
#   make testinst-logs                               # follow app logs
#   make testinst-down                               # stop and remove the worktree
#
# The SHA is checked out DETACHED into a throwaway git worktree (no local branch
# is created, moved, or checked out), so your current checkout and any
# uncommitted work are left completely untouched, and a concurrent local branch
# move can't change what gets built.
#
# Note the worktree is a whole-repo checkout (the monorepo root, client/ and all)
# while the build context is its server/ subdir — hence the /server suffix below.
#
# Orchestration precedence: the compose file comes from the worktree (the branch
# under test) when it ships one, falling back to the local checkout's copy so old
# branches that predate docker-compose.test.yml still build. GIT_HASH and the
# build context (Dockerfile) already come from the worktree.
TEST_COMPOSE  := docker compose -f docker/docker-compose.test.yml --env-file .env
TEST_WORKTREE := .worktrees/testinst

testinst: keys
ifndef BRANCH
	$(error Usage: make testinst BRANCH=<branch-name>)
endif
	@set -e; \
	git fetch origin --prune --tags; \
	if git rev-parse --verify --quiet "origin/$(BRANCH)^{commit}" >/dev/null; then \
		sha=$$(git rev-parse "origin/$(BRANCH)^{commit}"); \
		echo "Resolved origin/$(BRANCH) -> $$sha"; \
	elif git rev-parse --verify --quiet "refs/tags/$(BRANCH)^{commit}" >/dev/null; then \
		sha=$$(git rev-parse "refs/tags/$(BRANCH)^{commit}"); \
		echo "Resolved tag $(BRANCH) -> $$sha"; \
	elif [ -n "$(ALLOW_LOCAL_REF)" ] && git rev-parse --verify --quiet "$(BRANCH)^{commit}" >/dev/null; then \
		sha=$$(git rev-parse "$(BRANCH)^{commit}"); \
		echo "ALLOW_LOCAL_REF set — resolved LOCAL ref '$(BRANCH)' -> $$sha"; \
	else \
		echo "ERROR: '$(BRANCH)' does not resolve to a commit on origin (no origin/$(BRANCH) branch or tag)." >&2; \
		echo "       Push the branch first, or pass ALLOW_LOCAL_REF=1 to build a local-only branch/tag/SHA." >&2; \
		exit 1; \
	fi; \
	git worktree remove --force $(TEST_WORKTREE) 2>/dev/null || true; \
	git worktree prune; \
	git worktree add --detach --force $(TEST_WORKTREE) "$$sha"; \
	echo "Checked out $$sha (detached) into $(TEST_WORKTREE)"; \
	compose="docker/docker-compose.test.yml"; \
	if [ -f "$(TEST_WORKTREE)/server/docker/docker-compose.test.yml" ]; then \
		compose="$(TEST_WORKTREE)/server/docker/docker-compose.test.yml"; \
		echo "Using compose file from worktree: $$compose"; \
	else \
		echo "Worktree has no docker-compose.test.yml — using local $$compose"; \
	fi; \
	GIT_HASH=$$(git -C $(TEST_WORKTREE) rev-parse --short HEAD 2>/dev/null || echo unknown) \
	TEST_BUILD_CONTEXT=$(abspath $(TEST_WORKTREE))/server \
		docker compose -f "$$compose" --env-file .env up -d --build; \
	echo "Test instance '$(BRANCH)' ($$sha) up — app on 127.0.0.1:$${TEST_PORT:-6465} (see TEST_PORT in .env)"
	@$(MAKE) --no-print-directory keys-note

testinst-down:
	$(TEST_COMPOSE) down
	-git worktree remove --force $(TEST_WORKTREE) 2>/dev/null || true
	git worktree prune

testinst-logs:
	$(TEST_COMPOSE) logs -f app

testinst-ps:
	$(TEST_COMPOSE) ps
