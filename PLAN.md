# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

---

## M8: Lichess account linking — the auth flow, and only the auth flow

Lichess comes back here, as the clean-slate rebuild CLAUDE.md describes. **The `lichess-final`
tag is reference-only — do not restore those files.** M8 builds exactly one thing: a
trustworthy link between a lichess account and a Terry's Gambit (Steam) account. Puzzles,
TV and play-on-lichess all sit on top of that link and are explicitly **not** in scope.

Two constraints drive the whole design:

1. **s&box cannot open a browser** (no documented URL/overlay API — CLAUDE.md), so linking
   is click-to-copy a URL and the player pastes it themselves.
2. **The user must be able to revoke us, and gamchess must be structurally unable to act on
   their lichess account** — not merely trusted not to.

Done looks like: walk up to a board next to the info station, press E, click a button,
paste the URL in a browser, sign in to Steam and lichess, and the panel flips to "linked as
&lt;username&gt;". gamchess stores **no lichess credential at all**.

### Lichess API facts — re-derived 2026-07-15 from live sources

Per CLAUDE.md's re-derive rule these were read from lichess's current docs, not from repo
history. Sources: the `lichess-org/api` OpenAPI spec (`doc/specs/lichess-api.yaml`),
`tors42/lichess-oauth-pkce-app`, and lichess's OAuth docs. **Re-check them before M9** — a
stale constraint is worse than no constraint.

- OAuth 2.0 **Authorization Code + PKCE**, `S256` only. Public/unregistered clients:
  **no client secret, no client registration** — `client_id` is any constant string we pick.
- Authorize: `https://lichess.org/oauth` — `response_type=code`, `client_id`, `redirect_uri`,
  `scope`, `state`, `code_challenge`, `code_challenge_method=S256`.
- Token: `POST https://lichess.org/api/token`, `application/x-www-form-urlencoded` —
  `grant_type=authorization_code`, `code`, `code_verifier`, `redirect_uri`, `client_id`.
- Revoke: `DELETE https://lichess.org/api/token`, `Authorization: Bearer <token>`.
- Identity: `GET https://lichess.org/api/account` → `{ id, username, ... }`. `id` is the
  canonical lowercase key; `username` is display casing.
- Tokens are **long-lived (~1 year), and there are no refresh tokens.**
- 21 scopes exist (`board:play`, `puzzle:read`, `email:read`, …). **M8 requests none.**

### The trust path, and why it closes

The only real question is *who gets bound to whom*. Three independent attestations, and
gamchess writes the link row only when it holds all three:

| Claim | Attested by | Never trusted from |
|---|---|---|
| "this browser is SteamID N" | Steam OpenID 2.0 → signed session cookie | a header/body/query SteamID |
| "this is lichess user X" | lichess `/api/account`, via a token *we* exchanged | anything the client says |
| "N wants to link" | the Steam session on the callback | the copied URL |

**The copied URL carries no secret and no capability.** It is the constant string
`https://chess.gamah.net/lichess/link`. Losing it, sharing it, or pasting it on stream costs
nothing — whoever opens it links *their own* accounts, because the page is gated on *their*
Steam session.

This is why we **do not** mint an in-game `state` bound to the FP-verified SteamID and paste
that instead. It would skip the Steam web sign-in, but it turns the URL into a bearer
capability: get someone else to complete it and **their** lichess binds to **your** Steam
account — and lichess's consent screen only says "Terry's Gambit", so the victim cannot see
the mismatch. The Steam session gate makes that impossible rather than merely unlikely.

Cost of the gate: one Steam web sign-in per browser per 30 days (`sessionTTL`), reusing
`/auth/steam/login` exactly as it already exists.

### Why gamchess cannot act on your lichess account

- **Zero scopes requested.** A no-scope lichess token cannot play, challenge, message,
  follow, or read email — it can only read the public account. Enforced by lichess, not by
  our code.
- **Revoked immediately.** `DELETE /api/token` fires in the same handler, right after
  `/api/account` returns.
- **Nothing stored.** The `lichess_links` row holds `lichess_id` and `username`. No token
  column. No secret to leak, encrypt, rotate, or subpoena.

Three guarantees, and the strongest (scope) is lichess's rather than ours.

### Server (`server/`)

Existing conventions are non-negotiable: `os.Getenv` in `main.go` only, new keys degrade to
feature-off with a warning (never fatal), SteamIDs are `BIGINT`/`int64` and **string on the
JSON wire**, all SQL in `store.go`, fail closed everywhere.

**`server/migrations/00002_lichess_links.sql`** (new; goose runs it at startup):

```sql
CREATE TABLE lichess_links (
    steam_id   BIGINT      PRIMARY KEY REFERENCES players(steam_id),
    lichess_id TEXT        NOT NULL UNIQUE,   -- canonical lowercase id from /api/account
    username   TEXT        NOT NULL,          -- display casing, cosmetic only
    linked_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

Deliberately **no token column**. `PRIMARY KEY` on `steam_id` + `UNIQUE` on `lichess_id`
make the link 1:1 both ways: one Steam account can't hoard lichess identities, one lichess
account can't be claimed by two Steam accounts. Re-linking replaces
(`ON CONFLICT … DO UPDATE`); a `lichess_id` already bound to a *different* `steam_id` is a
**409, not a silent steal**.

Pending link state (verifier + state + SteamID) goes in a **new in-memory TTL store**
modelled on `nonceStore` (`server/internal/api/web_auth.go:30-56`) — same mutex + lazy
sweep-on-write, 10-minute TTL, check-and-burn in one method. In-memory is right: one
container, and a restart mid-link just means "click the button again".

**`server/internal/lichess/oauth.go`** (new package, mirroring `internal/steam/`):

```go
var authorizeEndpoint = "https://lichess.org/oauth"        // package vars so tests stub them
var tokenEndpoint     = "https://lichess.org/api/token"
var accountEndpoint   = "https://lichess.org/api/account"
var client = &http.Client{Timeout: 10 * time.Second}

func AuthorizeURL(clientID, redirectURI, state, challenge string) string
func Exchange(ctx context.Context, clientID, redirectURI, code, verifier string) (token string, err error)
func Account(ctx context.Context, token string) (id, username string, err error)
func Revoke(ctx context.Context, token string) error       // best-effort; log on failure
func NewVerifier() (verifier, challenge string, err error) // crypto/rand 32B, base64url, S256
```

Reuse `internal/steam/`'s shape verbatim: package-var endpoints for test stubbing,
`io.LimitReader` on bodies, fail closed on every error. A `Revoke` failure is logged, not
fatal — the token is scopeless and the link is already written.

Routes (`server/internal/api/router.go`, new `lichess.go`):

| Route | Gate | Notes |
|---|---|---|
| `GET /lichess/link` | `sessions.read`; 302 → `/auth/steam/login` if absent | mint verifier+state bound to the session SteamID, 302 → lichess |
| `GET /lichess/callback` | the `state` → SteamID map (burned on use) | exchange, `/api/account`, revoke, upsert, render result page |
| `GET /api/v1/lichess` | `callerSteamID` (session **or** FP) | `{linked, lichess_id, username}` — the client polls this |
| `DELETE /api/v1/lichess` | `callerSteamID` | unlink; deletes the row |

Register above the `GET /` file server (Go 1.22's mux makes `/` least-specific so ordering is
safe, but keep them grouped with the other auth routes).

New env in `main.go` + `.env.example` + `docker/docker-compose.yml`: `LICHESS_CLIENT_ID`
(default `terrys-gambit`). The redirect URI derives from the existing `PUBLIC_BASE_URL`
(`+ "/lichess/callback"`) exactly as `steamReturnURL()` does — so the test instance points at
itself, never at prod. **`redirect_uri` must match byte-for-byte** between authorize and
token exchange (lichess enforces it): derive it once, never hand-build it twice.

**Caddy**: `/lichess/callback` takes an OAuth `code` **in the query string**, so CLAUDE.md's
existing *"add no `log` directive to these vhosts"* rule — written for `/auth/steam/return` —
now covers it too. Caddy logs nothing by default; the job is not to start.

**No client allowlist change.** The s&box client still only talks to `chess.gamah.net`;
gamchess is the only thing that talks to lichess.org. `HttpAllowList` is untouched in M8.

### Web page (`server/frontend/`)

The callback renders a real page, not a bare redirect. This is where the **heavy verbiage**
lives — it's the only surface that can tell the player what they actually granted:

- **Before** (`/lichess/link` when it needs a Steam sign-in): what's about to happen, in
  order; that Steam and lichess each ask for *their own* password on *their own* domain; that
  **Terry's Gambit never sees, asks for, or stores either password**; that we request **no
  lichess permissions at all** and will only learn the username.
- **After** (callback success): "Linked **&lt;username&gt;** to your Steam account." Then,
  explicitly: what gamchess stored (username + id, nothing else); that the access token was
  **already revoked** and gamchess kept no credential; that we cannot play, message, or
  challenge as them; and how to unlink (in-game, or the button on this page).
- **Failure**: detail-free, matching `steamReturn`'s `/?error=signin` discipline.

Reuse the existing frontend CSS — and note the standing warning below: the frontend is baked
into the Docker image, so changes need `git pull && make rebuild`, not a restart.

### Client (`client/`)

**`Code/Api/LichessApi.cs`** (new, thin — all calls go to gamchess). Follow
`Code/Api/GamchessApi.cs` exactly: the `Result` struct, `SendAuthed` (which already re-mints
the FP token once on 401), the 8s timeout, the shared circuit breaker.

```csharp
public const string LinkUrl = GamchessApi.Base + "/lichess/link";   // no secret, constant
public static Task<Result> Status();    // GET    /api/v1/lichess
public static Task<Result> Unlink();    // DELETE /api/v1/lichess
```

Add `LichessLink { bool linked; string lichess_id; string username; }` to
`Code/Api/GamchessModels.cs` — snake_case props matching the wire, no attributes, as the
existing models do.

**`Code/World/LichessButton.cs`** (new, ~15 lines): copy `Code/World/DiscordButton.cs`
verbatim in shape — `Clipboard.SetText( LichessApi.LinkUrl )` plus `RealTimeSince SinceCopied`
for the 2s "✓ copied" feedback. That is the whole clipboard mechanism; there is no other.

**Polling**: the link completes in a browser, so the game must poll. `Status()` on a
`RealTimeUntil` gate (~3s), **only while the lichess station is engaged**, never in the
background. Copy the in-flight guard lesson from `LocalGameController.TryArchive()` — *claim
before you await*, or `OnUpdate` fires a request per frame. Cache in a static; stop once
linked. gamchess unreachable ⇒ say so and keep playing (**gamchess is never required** —
nothing here may block scene load, `OnStart`, or a game ending).

**UI — a third board next to the info station.** `InfoStation.StationKind` already exists for
exactly this:

- `Code/World/InfoStation.cs` — add `Lichess` to the enum.
- `Code/World/InfoWall.cs` — one more board GameObject + one more
  `AddStation( "LichessStation", LichessYFrac, facing, InfoStation.StationKind.Lichess )`,
  beside the info board. The existing `AddStation` helper is unchanged.
- `Code/UI/Screens/InfoScreen.razor` — branch on `Kind == Lichess`. Reuse `WallTheme` tokens,
  the `.screen-fit` wrapper (centering must **not** go on `root`), `pointer-events: all` only
  on the interactive card, and the `BuildHash()` discipline — add `LichessButton.SinceCopied
  < 2f` and the cached status, which is what makes the label revert with no timer.

In-game copy (the other half of the heavy verbiage). One div per line — panels are flex
containers and a div's auto height does not grow for wrapped text:

```
LICHESS                                    [ not linked ]

Link your lichess account to Terry's Gambit.

You'll sign in to Steam and to lichess, each on their own
site. Terry's Gambit never sees your password for either.

We ask lichess for NO permissions. We learn your username.
We cannot play, message, or challenge as you.

  [ ✓  Link copied to clipboard  ]
  Paste it into your browser to finish.

  [ copy again ]
```

Linked state: `linked as <username>` + `[ unlink ]` + one line stating gamchess holds no
lichess credential.

No scene rewire: `InfoWall` builds its own boards, `InfoStation` self-registers, `InfoScreen`
is already attached by `LobbyPlayer`, and `InfoStation` is already in `LobbyPlayer.Engaged` /
`BoardEngaged` — so a new `StationKind` needs no `LobbyPlayer` change at all. That's why this
is three small edits rather than a new station type. Keep `Editor/HotloadRebuild.cs` current
if `InfoWall` gains a builder entry.

### Docs — same commit, both halves

The contract is hand-mirrored with no codegen, so a contract change is one atomic commit.

- **`README.md`** — the API contract section is *"the one place it is written down"*: add the
  four routes, the no-scope / no-token-stored rule, the `client_id` constant. Replace the
  "There is no lichess in the tree" paragraph; that invariant ends with this commit.
- **`CLAUDE.md`** — rewrite the "Lichess: gone" section into what now exists, but **keep the
  re-derive-the-API-facts rule** (it still governs puzzles/TV/play). Add `/lichess/callback`
  to the no-Caddy-log rule. Record the M9 custody design and the open spikes below.

### Verification

This host has no s&box toolchain, no Go, and no Docker — **nothing in M8 compiles or runs
here, and no session may claim otherwise.** Verify by careful review + grep; the user tests.

Provable in the `golang:1.22` container (`make test`):

1. **`internal/lichess` unit tests** — stub `authorizeEndpoint`/`tokenEndpoint`/
   `accountEndpoint` the way `steam/auth_test.go` already stubs `endpoint`. Cover:
   `NewVerifier` produces a correct `S256` challenge (RFC 7636 test vector), `Exchange` posts
   the right form fields, `Account` parses, every error path fails closed.
2. **State-store tests** — mirror `openid_test.go`: burn-on-use, TTL expiry, replay false,
   unknown state false.
3. **Handler tests** — mirror `auth_test.go` / `games_test.go`: `/lichess/link` with no
   session 302s to Steam login; `/lichess/callback` with unknown/replayed `state` fails
   closed; a `lichess_id` bound to another `steam_id` 409s; `/api/v1/lichess` returns
   `{linked:false}` for a stranger and never leaks another player's link.
4. **Grep gates** — no token column in the migration; no `scope=` with a non-empty value;
   `redirect_uri` derived from `PUBLIC_BASE_URL` in exactly one place.

Needs the user, in the editor and on the deploy host:

5. `make testinst BRANCH=<branch>` → `testchess.gamah.net` (`TEST_PUBLIC_BASE_URL` must be the
   test URL or the callback returns to prod).
6. Walk up to the new board, press E, click copy, paste in a browser. Expect: Steam sign-in →
   lichess consent naming **no permissions** → success page → the panel flips to "linked as
   &lt;username&gt;" within ~3s.
7. Confirm on lichess that the token is **already gone** (this also resolves the
   `/account/oauth/token` spike below).
8. `[ unlink ]` → back to "not linked"; re-linking works.
9. Kill gamchess → the board says so, and local chess still plays. Non-negotiable.

---

## M9+: playing on lichess — decide token custody before writing any of it

Not M8, and nothing in M8 forecloses it. **A scope change forces a re-link regardless** —
tokens can't be re-scoped and there are no refresh tokens — so M8 storing nothing costs the
user nothing later.

The intended shape, recorded so M9 doesn't default to the easy-but-worse option: the **s&box
client** generates `code_verifier`, never transmits it, and builds the authorize URL with
`redirect_uri` pointing at gamchess. lichess redirects to gamchess with the `code`; gamchess
relays that `code` back over the FP-gated channel and **cannot exchange it — PKCE requires
the verifier gamchess never saw**. The token lives only in the client. gamchess is *unable*
to play as the user rather than trusted not to. This costs adding `lichess.org` to
`HttpAllowList`, and means the client holds a credential it currently never does (the FP
token is 120s-cached, never persisted).

**Open spikes — resolve before shipping M9; do not guess:**

- **Can a rogue lobby host read a client-held token?** Analysis says no: s&box peers all run
  the same sandboxed package code, a host has no code execution on clients, `FileSystem.Data`
  is per-package, and we would never `[Sync]`/RPC the token. Residual risk is plaintext on
  local disk (as with any local credential) against a ~1-year no-refresh token. This is a
  Facepunch platform question, not a gambit one — **confirm it, don't assume it.**
- **Does `lichess.org/account/oauth/token` list OAuth-app-issued tokens** for user-initiated
  revocation? That page documents **personal** tokens; live docs did not confirm it covers app
  grants. Checkable during M8 step 7 above. Doesn't block M8, which revokes its own token
  server-side and stores none.

---

## The web viewer needs a lot of work

`server/frontend/` (`index.html` / `app.js` / `chess.js` / `style.css`) — the archive
viewer at chess.gamah.net. It works, but it has never had a design pass, and **nobody has
ever looked at it on anything but a desktop browser**. Treat the list below as observations,
not a spec — the real first step is to open it and decide what it should be.

What's already known to be weak:

- **The CSS has barely been exercised.** The board squares resized to fit whichever piece
  stood on them until 2026-07-15 — an 8×8 grid that wasn't actually holding a grid. That
  bug surviving this long says the styling has had no real scrutiny.
- **Never checked narrow / mobile.** The board is `width: min(28rem, 100%)` next to a
  `flex: 1 1 14rem` side panel, and the games table has four fixed columns. What that does
  under 400px is unknown.
- **The games list shows Played / White / Black / Result only.** No time control, though
  the PGN now carries one. Adding a column means touching `index.html`'s `<thead>` too.
- **Game meta is one text line** — date, then the time control tacked on after a `·`
  (`loadGame` in `app.js`). Fine as a stopgap; not a design.
- **The per-move `%clk` display is brand new and unseen.** `shortClk` trims the leading
  `0:` so a bullet clock reads `0:51.63`; that call was made without ever seeing it
  rendered next to the SAN.
- **Sign-in is a bare button.** The Steam OpenID round trip works, but the signed-out and
  error states have had no thought. M8 adds the lichess link page alongside it — worth
  doing these together.

Constraints worth knowing before starting:

- **The frontend is baked into the Docker image** (`COPY --from=builder /src/frontend
  /frontend`). A restart won't pick up CSS changes — the server needs `git pull && make
  rebuild`.
- **Zero image assets, and it should stay that way.** Pieces are Unicode glyphs with
  U+FE0E forcing text presentation; that's what keeps the viewer CC0-clean with nothing to
  attribute. The s&box client can't render these glyphs at all (they come out as colour
  emoji — see CLAUDE.md), but a browser can, so this is the one place they're allowed.
- **`chess.js` is rules code, not view code.** It is gated by
  `node scripts/chess_js_perft.mjs`, which runs on the dev host. Re-run it after touching
  that file — it holds the viewer's rules and PGN parsing to the same reference positions
  and real C# writer output as the client.
