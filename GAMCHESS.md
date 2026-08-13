# GAMCHESS.md — the backend, its auth, and its deployment

`server/` in this repo. Go/Postgres, deployed at `chess.gamah.net`. The full API contract is in
the root **`README.md`** — hand-mirrored in C# with no codegen, so a contract change is one
commit across both halves.

## Rules

- **Identity is only ever what Steam/Facepunch says it is.** In-game the client mints a Facepunch
  auth token (`Sandbox.Services.Auth.GetToken`), gamchess verifies it at
  `POST https://public.facepunch.com/sbox/auth/token` and trusts **only the echoed `SteamId`**.
  On the web: Steam **OpenID 2.0** (`steamcommunity.com/openid/login` — Steam has no OAuth2
  endpoint, whatever it gets called). Both **fail closed**. A SteamID from a header, body or
  query string is an unverified *claim* and authorises nothing — which is why the archive has no
  `?steam_id=`.
- **The archive is private.** You only ever see games you sat in. Seat SteamIDs in a POST are
  claims, so you may only archive a game you sat in; `GET /games/{id}` 404s (not 403s) for
  someone else's game so ids aren't probeable.
- **`client_game_id`** is a UUID the host mints at game start and `[Sync]`s. Move history lives in
  each seated client's own `ChessGame`, not the host's, so the host usually has no PGN — **both
  seats POST** and the second is a no-op. A client whose history came from a FEN resync stays
  quiet rather than archive a stub.
- **SteamIDs cross the wire as strings.** A SteamID64 (~7.6e16) is past JavaScript's 2^53, so a
  bare JSON number is silently corrupted by the web viewer.
- **gamchess is never required.** If it's down the game plays exactly the same — `GamchessApi` has
  an 8s timeout, never throws, and a 60s circuit breaker so a dead host costs one timeout rather
  than one per call. Nothing may block scene load, `OnStart`, or a game ending.

## Identity / auth primitives (`server/internal/steam/`)

Both halves are lifted from `../rotaliate` essentially verbatim, with their tests. Deviating from
them is how un-compilable mistakes get in.

- **In-game**: `await Sandbox.Services.Auth.GetToken( "gamchess" )`. The service-name argument is
  **cosmetic** (Facepunch validates `{steamid, token}` without it). Returns null rather than
  throwing on non-Steam builds. Verified server-side → `{"SteamId", "Status"}` — **no persona name
  comes back, SteamID only**. Two rules: **fail closed** on any error, and **trust only the echoed
  `SteamId`** (`Status == "ok" && vr.SteamID == steamID`), which is what stops a valid token for
  account Y authorising as account X. Confirmed in-editor 2026-07-15; the token's real TTL is an
  open spike (we cache 120s and re-mint once on a 401).
- **Web**: Steam's browser login is **OpenID 2.0, not OAuth2**. Keeps rotaliate's `op_endpoint`
  pinning, `return_to` scheme+host+path matching, and single-use nonce (the nonce store is ours —
  `steam.Verify` only shape-checks it and documents that single-use is the caller's job).
- Sessions are stateless HMAC-signed cookies, so a deploy doesn't sign everyone out.
  `SESSION_SECRET` blank = random per-process key (works with no config, dies on restart).
  `SameSite=Lax` is load-bearing: the OpenID return is a top-level cross-site GET and Strict would
  drop the cookie on exactly that hop.
- Display names come from **Steam** (`Connection.DisplayName`) — Gambit has no username of its own
  and no name picking. The FP path returns no name, so a server-side name would need
  `ISteamUser/GetPlayerSummaries/v0002` (Steamworks key, 100k/day — cache it). Not needed: the PGN
  carries the names.
- The same FP token authenticates `Sandbox.WebSocket` — `Connect(uri, headers)` accepts an
  `Authorization` header, so one mechanism covers both.

## The game session: one Facepunch call an hour, not one per request (M9)

gamchess used to verify the FP token against Facepunch on EVERY authed request — a live HTTP call
per request. `POST /api/v1/session` (**FP-gated only**) trades an FP token for a `gcs_` bearer
verified with a local HMAC and **zero** network.

- **Nothing about it is user-visible and it adds no dependency.** Minted from the FP token the
  client already holds — no web sign-in, no lichess link. A mint failure falls back to the FP
  path, which works identically and just costs a round-trip: **it degrades performance, never
  function.**
- **A session may not mint a session** (`requireFacepunch`, separate from `requireSteam`), or a
  client renews itself forever and the TTL is a fiction.
- **The audience is inside the MAC** (`aud|steamID|expiry|MAC`). Sign `steamID|expiry` alone and a
  30-day web cookie and a 1h game bearer are the same bytes under the same key — a leaked cookie
  replayed as `gcs_<value>` would authorise the game API for a month. This is why the payload
  format changed, and why the M9 deploy signs every web session out once.
- **One hour, and it's the real tradeoff**: a session authorises everything that SteamID can do,
  including playing lichess games as them, and sessions are stateless — **there is no revoking
  one** short of rotating `SESSION_SECRET`, which signs everyone out.
- **Memory only, never `FileSystem.Data`** — same rule as the FP token.

## HTTP: there is no allowlist (the old "D8" was folklore)

**`HttpAllowList` gates nothing.** Verified by reading the shipped engine (`sbox-public` @
`ca96c2a9`): `Http.IsAllowed` checks only the scheme (http/https/ws/wss), loopback-port rules,
IP-literal rejection and DNS-rebinding into private ranges. **There is no per-package host
allowlist anywhere in the engine.** The entries in `gambit.sbproj` are inert and "add a host to
the allowlist" is a zero-cost non-step. They are kept as a **declaration of intent**, not a
control — do not diagnose against them: the old "blocked before connecting → the allowlist is
wrong" advice diagnosed a mechanism that does not exist.

`Sandbox.WebSocket.Connect` goes through the **same** `Http.IsAllowedAsync`, so the URL policy
does cover WS — and since that policy is only scheme/IP checks, `wss://chess.gamah.net` is allowed.

Reading a `gambit_gamchess_ping` failure (verified in-editor 2026-07-15):
- **TLS/SSL error** → the request reached a handshake; Caddy has no cert for that host (vhost
  down/not configured).
- **any HTTP status** → we reached gamchess; read the status.

## Deployment facts

**Never deployed from this host** (no Docker here). The Go DOES compile and test here with the
shared toolchain — `make test` runs `go test ./... -race` in a container on a machine with
Docker, and the same suite passes locally with Go 1.22. If you are changing the server, run the
tests; "can't build it here" is no longer true for Go.

**gamchess holds NO lichess secrets.** lichess issues nothing — no client id, no secret, no API
key, no client registration — and since HTTPFIX there is nothing of our own either: the token
lives on the player's machine, so there is no ciphertext to key, no data key to rotate and no
store to audit. `LICHESS_TOKEN_KEY`, `LICHESS_TOKEN_KEY_OLD`, `LICHESS_KEY_ROTATION_DAYS` and
`LICHESS_AUDIT_KEY` are **retired and ignored**; `make keys` is a deliberate no-op (kept because
`up`/`rebuild`/`testinst` depend on it) and `keys-note` says there is nothing to back up.
`internal/keyring`, `lichess_key_versions` and the whole KEK→DEK→token envelope are deleted, along
with `test.sh`, which existed only to validate key rotation.

**The only lichess-relevant config left is `PUBLIC_BASE_URL`**, and it is load-bearing: it derives
the byte-for-byte `redirect_uri`, and keeps the test instance pointing at itself rather than prod.
`SESSION_SECRET` is ours, optional, unaffected.

> **Rolling BACK to a pre-HTTPFIX binary needs `LICHESS_TOKEN_KEY` again** — keep a copy until
> you're sure. But the migration DELETED every link row, so a rollback gets a working old binary
> and no links; players re-link either way.
>
> **The deploy has one manual step and it must happen BEFORE it.** Every linked player should
> unlink in-game (which still revokes while the old binary can sign it) or revoke Gambit on
> lichess's `/account/security`. Deleting rows does not revoke anything, and once they are gone
> the grants stay live for up to a year, revokable only by each player. No sweep tool was built:
> lichess has no bulk revoke, so a sweep is N serial calls, and N was 1.

Test and prod share `.env`. What differs, and always mattered more, is the redirect ORIGIN —
lichess records `clientOrigin` per token, so `testchess` and `chess` are **two separate apps** to
lichess. A player who links on both has two grants and two `/account/security` entries. **Linking
on test is a real grant against a real account**, not a sandbox.

Ports/hosts are allocated in the server's Caddyfile (host-side, unversioned — not in this repo):

| | Host | App | Postgres |
|---|---|---|---|
| prod | `chess.gamah.net` | 6464 | 5435 |
| test | `testchess.gamah.net` | 6465 | 5436 |

Both are plain subdomains (a `*.gamah.net` wildcard covers them; a sub-subdomain like
`test.chess.gamah.net` would need its own record — DNS wildcards match one label).

All bind `127.0.0.1` only — **never punch through ufw**. Docker's iptables chains are evaluated
*before* ufw, so a `0.0.0.0` publish is internet-reachable even with ufw denying the port;
loopback binding + Caddy fronting is the whole mechanism (rotaliate documents this at
`docker/docker-compose.yml`).

Ports already taken on that host by other services: `1337`, `5432`–`5436`, `6969`, `6970`, `8080`,
`8081`. Check the host's Caddyfile before allocating anything new.

**Deploying needs only Docker** — every Go make target runs in a container (`golang:1.22`, module
cache in a named volume). `make up` builds and migrates in-process at startup. `make dev` is the
one target that wants a local Go.

**Add no `log` directive to these vhosts.** Auth returns land on `/auth/steam/return` **and
`/lichess/callback`** with credentials in the query string (a Steam assertion, an OAuth code), and
Caddy would write them to disk. Caddy writes no access log unless configured, so the default is
already safe — the job is not to start. Any future auth-callback route inherits this rule.
