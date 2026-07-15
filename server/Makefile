.PHONY: dev build test tidy fmt lint migrate-up migrate-down migrate-status \
        up rebuild down logs ps psql \
        testinst testinst-down testinst-logs testinst-ps

# ── Local dev (no Docker) ───────────────────────────────────────────────────
dev:
	go run ./cmd/server

build:
	go build -ldflags="-s -w" -o bin/gamchess ./cmd/server

test:
	go test ./... -v -race

# Run this FIRST on a fresh checkout. go.sum was seeded from rotaliate (whose
# module graph is a superset of ours) because the machine that wrote this server
# has no Go toolchain — tidy prunes it to what gamchess actually imports and
# fills in anything missing.
tidy:
	go mod tidy

fmt:
	gofmt -w .
	goimports -w . 2>/dev/null || true

lint:
	golangci-lint run ./...

# ── Database ────────────────────────────────────────────────────────────────
# The server also runs migrations in-process at startup, so these are for
# host-side inspection and for stepping a migration back.
migrate-up:
	goose -dir migrations postgres "$(DATABASE_URL)" up

migrate-down:
	goose -dir migrations postgres "$(DATABASE_URL)" down

migrate-status:
	goose -dir migrations postgres "$(DATABASE_URL)" status

# ── Docker Compose ──────────────────────────────────────────────────────────
# GIT_HASH is computed on the host and passed as a Docker build arg so the
# version stamp is reliably per-commit. Computing it inside the container is
# unreliable for the test instance (its build context is a worktree whose .git
# lives outside the context).
up:
	GIT_HASH=$$(git rev-parse --short HEAD 2>/dev/null || echo unknown) \
	docker compose -f docker/docker-compose.yml --env-file .env up -d --build

rebuild:
	GIT_HASH=$$(git rev-parse --short HEAD 2>/dev/null || echo unknown) \
	docker compose -f docker/docker-compose.yml --env-file .env build --no-cache app
	docker compose -f docker/docker-compose.yml --env-file .env up -d

down:
	docker compose -f docker/docker-compose.yml down

logs:
	docker compose -f docker/docker-compose.yml logs -f app

ps:
	docker compose -f docker/docker-compose.yml ps

# ── DB shell ────────────────────────────────────────────────────────────────
psql:
	docker compose -f docker/docker-compose.yml exec postgres \
		psql -U gamchess -d gamchess

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

testinst:
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

testinst-down:
	$(TEST_COMPOSE) down
	-git worktree remove --force $(TEST_WORKTREE) 2>/dev/null || true
	git worktree prune

testinst-logs:
	$(TEST_COMPOSE) logs -f app

testinst-ps:
	$(TEST_COMPOSE) ps
