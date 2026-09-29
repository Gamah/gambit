# Server

Operating gamchess: configuration, make targets, hosts and ports, and the deploy rules.

## Configuration

Read in `cmd/server/main.go` and nowhere else (`os.Getenv`, no config library). `.env` is
gitignored and is the only copy; `.env.example` lists the keys. Test and prod share one `.env`.

| Key | Effect |
|---|---|
| `DATABASE_URL` | required; the only fatal one |
| `PUBLIC_BASE_URL` | the public root: the Steam OpenID realm and return, and the byte-exact lichess `redirect_uri`. Blank disables web sign-in |
| `SESSION_SECRET` | signs sessions. Blank is a random key per process: sessions work but a restart signs everyone out |
| `FRONTEND_DIR` | the archive viewer's static files. Blank disables the web UI only |
| `PORT` | default `6464` |
| `TEST_PORT`, `TEST_PG_PORT`, `TEST_PUBLIC_BASE_URL` | the test instance's equivalents |

gamchess holds no lichess secret: lichess issues no client id or secret, and the player's token
lives on their own machine (`docs/lichess.md`).

Migrations run in-process at startup and are fatal on failure, so a deploy is
`git pull && make up`.

## Make targets

Every Go target runs in a `golang:1.22` container with the module cache in a named volume, so
deploying needs only Docker. `make dev` is the one target that wants a local Go.

| Target | What it does |
|---|---|
| `up` / `rebuild` | build and start prod (`rebuild` without cache) |
| `down` / `logs` / `ps` / `psql` | prod compose housekeeping |
| `testinst BRANCH=<ref>` | build a pushed branch or tag in a worktree and run it as the test instance (`ALLOW_LOCAL_REF=1` for an unpushed ref) |
| `testinst-down` / `-logs` / `-ps` | test instance housekeeping |
| `test` / `vet` / `fmt` / `lint` / `build` / `tidy` | Go, containerised |
| `migrate-up` / `-down` / `-status` | host-side `goose`, for inspection and stepping back |
| `keys` | checks `.env` exists; `up`, `rebuild` and `testinst` depend on it |

This dev host has no Docker. Run the suite directly instead:

```
PATH=~/.local/share/toolchains/go1.22.6/bin:$PATH go test ./... -race    # in server/
```

The DB-integration paths need a Postgres and run under `make test`.

## Hosts and ports

| | Host | App | Postgres |
|---|---|---|---|
| prod | `chess.gamah.net` | 6464 | 5435 |
| test | `testchess.gamah.net` | 6465 | 5436 |

Both are single-label subdomains so the `*.gamah.net` wildcard covers them; DNS wildcards match one
label. Ports and vhosts are allocated in the deploy host's Caddyfile, which is not in this repo.
Already taken there by other services: `1337`, `5432`–`5436`, `6969`, `6970`, `8080`, `8081`. Check
the Caddyfile before allocating anything new.

**Everything binds `127.0.0.1`; never open a port in ufw.** Docker's iptables chains are evaluated
before ufw, so a `0.0.0.0` publish is internet-reachable even with ufw denying the port. Loopback
binding with Caddy in front is the whole mechanism.

**Add no `log` directive to these vhosts.** `/auth/steam/return` and `/lichess/callback` carry
credentials in the query string (a Steam assertion, an OAuth code). Caddy writes no access log
unless configured to. Any new auth-callback route inherits this rule.

**Test and prod are two separate apps to lichess.** lichess records `clientOrigin` (the redirect
URI's scheme and host) per token, so a player who links on both holds two grants. Linking on test is
a real grant against a real account.
