# ── Build stage ────────────────────────────────────────────────────────────
FROM golang:1.22-alpine AS builder

RUN apk add --no-cache git ca-certificates tzdata

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download

COPY . .
# Version stamp, surfaced by /health. Prefer the hash passed from the host as a
# build arg: `make testinst` builds from a git worktree whose .git is a file
# pointing outside the build context, so an in-container `git rev-parse` there
# returns nothing. Fall back to in-container git (works for prod's full-repo
# context), then "unknown".
ARG GIT_HASH=""
RUN GIT_HASH="${GIT_HASH:-$(git rev-parse --short HEAD 2>/dev/null || echo unknown)}" && \
    CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
    go build -ldflags="-s -w -X main.version=${GIT_HASH}" -o /bin/gamchess ./cmd/server

# ── Runtime stage ───────────────────────────────────────────────────────────
FROM scratch

# CA certs are load-bearing, not boilerplate: gamchess makes outbound HTTPS to
# Facepunch (token verify). A scratch image has no trust store, so without these
# every verify fails — and since we fail closed, that denies every sign-in.
COPY --from=builder /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/
COPY --from=builder /usr/share/zoneinfo /usr/share/zoneinfo
COPY --from=builder /bin/gamchess /gamchess
# goose reads migrations from the filesystem at startup, not from embed.
COPY --from=builder /src/migrations /migrations

EXPOSE 6464
ENTRYPOINT ["/gamchess"]
