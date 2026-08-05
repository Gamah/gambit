package api

import (
	"html/template"
	"net/http"

	"github.com/gamah/gambit/server/internal/lichess"
	"go.uber.org/zap"
)

// The lichess link flow's web pages.
//
// These are rendered here rather than served from frontend/ because they are
// answers to a request, not files, and because the redirect URI has to be a
// server route anyway (lichess matches it byte-for-byte and the s&box client
// cannot listen on a socket). They reuse the viewer's stylesheet (/style.css), so
// they look like the rest of the site without duplicating any of it.
//
// SINCE HTTPFIX THE CALLBACK PAGE CANNOT NAME THE ACCOUNT it just linked:
// identity costs a token, and gamchess no longer holds one at that moment — the
// client exchanges the code itself, moments later. The page says "linked" and
// tells the player to look in-game for the name. (An older note in PLAN.md
// claimed naming the account was WHY these pages are server-rendered. It was
// never the only reason and is no longer a reason at all.)
//
// This is where the FULLEST DISCLOSURE lives. It is the only surface in the
// whole flow that can show a player, in their own browser, exactly what they
// granted and how to take it back — the in-game panel is a summary, and lichess's
// own consent screen only names the scope. Two things here are load-bearing and
// must not be trimmed:
//
//  1. Changing your lichess password does NOT unlink Gambit. It touches web
//     sessions only; OAuth tokens are untouched. People reasonably assume the
//     opposite, and the assumption is dangerous.
//  2. lichess's /account/oauth/token page does NOT list this grant — it shows
//     personal tokens only. The real revoke is /account/security. Sending someone
//     to the wrong page and having them see nothing is worse than not telling them.
//
// html/template escapes every interpolation, so a lichess username can't inject
// markup here.

type lichessPage struct {
	Title string
	Body  string
}

// pageShell wraps the viewer's own CSS around a small centred card. `.wrap` and
// `.card` come from frontend/style.css; anything specific to these pages is
// inline below rather than added to a stylesheet the viewer also loads.
var pageShell = template.Must(template.New("page").Parse(`<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{.Title}} · Terry's Gambit</title>
<link rel="stylesheet" href="/style.css">
<style>
  .lichess-card {
    max-width: 40rem;
    margin: 3rem auto;
    padding: 1.5rem;
    line-height: 1.5;
  }
  .lichess-card h1 { margin-top: 0; }
  .lichess-card ul { padding-left: 1.2rem; }
  .lichess-card li { margin: 0.4rem 0; }
  .lichess-card .warn {
    border-left: 3px solid #d9a441;
    padding: 0.6rem 0.9rem;
    margin: 1rem 0;
  }
  .lichess-card .scope {
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-weight: bold;
  }
  .lichess-card .actions { margin-top: 1.5rem; }

  /* The button that leaves for lichess.
     It carries lichess's own logo and their own black-and-white, so it reads as
     "this hands you to lichess" rather than as one more Gambit control — which is
     the honest signal at the exact moment we send someone off to type their
     lichess password. See ATTRIBUTION.md: the logo is non-free, used under the
     one grant it carries ("Only use to refer to lichess.org"), which is precisely
     what this button does. It is NOT a claim of endorsement, so it never appears
     as a Gambit mark, only ever on a control that navigates to lichess. */
  .to-lichess {
    display: inline-flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.7rem 1.2rem;
    border-radius: 4px;
    background: #161512;      /* lichess's own dark-theme background */
    color: #fff;
    border: 1px solid #3d3a34;
    font-weight: bold;
    text-decoration: none;
    line-height: 1;
  }
  .to-lichess:hover { background: #262421; border-color: #6a655c; }
  .to-lichess svg { width: 1.5em; height: 1.5em; flex: none; }
  .to-lichess .go { opacity: 0.65; font-weight: normal; }
  .cancel { margin-left: 1rem; }
</style>
</head>
<body>
<div class="wrap lichess-card">
{{.Content}}
</div>
</body>
</html>`))

type pageData struct {
	Title   string
	Content template.HTML
}

func (h *handler) writePage(w http.ResponseWriter, code int, title string, content template.HTML) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(code)
	if err := pageShell.Execute(w, pageData{Title: title, Content: content}); err != nil {
		h.log.Error("could not render a lichess page", zap.Error(err))
	}
}

// renderLichessPage is the plain outcome page — errors, refusals, feature-off.
// Failures stay detail-free, matching steamReturn's /?error=signin discipline.
func (h *handler) renderLichessPage(w http.ResponseWriter, code int, p lichessPage) {
	body := template.Must(template.New("body").Parse(`
<h1>{{.Title}}</h1>
<p>{{.Body}}</p>
<p class="actions"><a href="/">Back to Terry's Gambit</a></p>`))

	var buf htmlBuffer
	if err := body.Execute(&buf, p); err != nil {
		h.log.Error("could not render the lichess page body", zap.Error(err))
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	h.writePage(w, code, p.Title, template.HTML(buf.String()))
}

// consentTmpl is the BEFORE page: what is about to happen, in order, before the
// player commits to anything.
var consentTmpl = template.Must(template.New("consent").Parse(`
<h1>Link your lichess account</h1>
<p>This connects your <strong>Steam</strong> account to your <strong>lichess</strong>
account, so games you play at a table in Terry's Gambit can be played for real on
lichess — and land in your real lichess history.</p>

<h2>What happens, in order</h2>
<ol>
  <li>Steam asks for your Steam password, <strong>on steamcommunity.com</strong>.</li>
  <li>Lichess asks for your lichess password, <strong>on lichess.org</strong>.</li>
  <li>You come back here and you're linked.</li>
</ol>
<p>Each site asks for its own password, on its own domain.
<strong>Terry's Gambit never sees either one.</strong></p>

<h2>What we ask lichess for</h2>
<p>These permissions: <span class="scope">{{.Scope}}</span>.</p>
<ul>
  <li><strong>Play games</strong> — start a game against the player sitting opposite
      you or against a stranger, play your moves, resign, and offer or accept draws
      and takebacks. Only ever for games you start from a Gambit table.
      (<span class="scope">board:play</span> is all-or-nothing: lichess has no
      smaller permission that can play a game.)</li>
  <li><strong>Puzzles</strong> — solve lichess puzzles in the lobby, against your
      real lichess puzzle record.</li>
  <li><strong>See which of your lichess friends are online.</strong> Reading only;
      we can't follow or unfollow anyone.</li>
  <li><strong>Send a lichess message</strong> — so you can say "good game" to
      someone you just played. <strong>Sending only: we can never read your
      messages</strong>, including a reply to one we sent. Lichess has no
      read-my-messages permission for apps like this at all.</li>
</ul>
<p><strong>What none of this lets us do:</strong> read your email address, change
anything about your account, change who you follow, or touch your teams, studies
or tournaments.</p>

<h2>Where the key is kept</h2>
<p><strong>On this PC.</strong> The access token lichess gives you is written to
Terry's Gambit's own data folder on the machine you're playing on, and the game
talks to lichess directly with it.</p>
<p>Our server sees it <strong>once</strong>, at this moment, for one request: it
asks lichess whose token it is, so it can record that this Steam account plays as
that lichess account. It doesn't store it, log it, or keep a copy.</p>
<ul>
  <li><strong>Play on another PC and you'll link again there.</strong> The key
      doesn't travel with your Steam account.</li>
  <li><strong>Deleting the game's data removes the key from this PC but does not
      revoke it at lichess.</strong> To really switch us off, use
      <a href="https://lichess.org/account/security" rel="noopener noreferrer">lichess.org/account/security</a>.</li>
</ul>

<p class="actions">
  <a class="to-lichess" href="/lichess/start">{{.Logo}}<span>Continue to lichess</span><span class="go">→</span></a>
  <a class="cancel" href="/">Cancel</a>
</p>`))

// lichessLogo is lichess's own knight mark, inlined.
//
// Inlined rather than shipped as a file for two reasons: the viewer has a
// zero-image-assets rule (it is what keeps it trivially CC0-auditable), and a
// 613-byte path costs less than the request would. currentColor makes it take the
// button's text colour, so it needs no second copy for a light theme.
//
// LICENCE: this is the one non-CC0 thing in the tree. lila's own COPYING.md lists
// public/logo under "Exceptions (non-free)" — author sadsnake1, terms "Only use to
// refer to lichess.org". Using it on a button whose entire job is to send you to
// lichess.org is exactly that grant and nothing more. Recorded in
// client/Assets/ATTRIBUTION.md. Do not reuse it as decoration, and do not let it
// appear anywhere it could read as endorsement — lichess has not endorsed Gambit.
const lichessLogo = template.HTML(`<svg viewBox="0 0 50 50" aria-hidden="true" focusable="false">` +
	`<path fill="currentColor" stroke="currentColor" stroke-linejoin="round" ` +
	`d="M38.956.5c-3.53.418-6.452.902-9.286 2.984C5.534 1.786-.692 18.533.68 29.364 3.493 50.214 31.918 55.785 41.329 41.7` +
	`c-7.444 7.696-19.276 8.752-28.323 3.084S-.506 27.392 4.683 17.567C9.873 7.742 18.996 4.535 29.03 6.405` +
	`c2.43-1.418 5.225-3.22 7.655-3.187l-1.694 4.86 12.752 21.37c-.439 5.654-5.459 6.112-5.459 6.112` +
	`-.574-1.47-1.634-2.942-4.842-6.036-3.207-3.094-17.465-10.177-15.788-16.207-2.001 6.967 10.311 14.152 14.04 17.663` +
	`3.73 3.51 5.426 6.04 5.795 6.756 0 0 9.392-2.504 7.838-8.927L37.4 7.171z"/></svg>`)

// linkedTmpl is the AFTER page: what you just granted, and how to take it back.
var linkedTmpl = template.Must(template.New("linked").Parse(`
<h1>Approved — finishing in-game</h1>
<p>Lichess has approved the link. <strong>Go back to Terry's Gambit</strong>: the
game collects the key, and the lichess board will show your account name within a
few seconds.</p>
<p>This page can't tell you which account you just linked, and that is deliberate
rather than a gap — the key goes straight to your PC and never to our server, so
at this moment our server doesn't know. The game does.</p>

<h2>What you just granted</h2>
<p><span class="scope">{{.Scope}}</span> — play games as you, solve puzzles on your
record, see which lichess friends are online, and send a lichess message (sending
only; we can never read one). It cannot read your email, change your account, or
change who you follow.</p>
<p>The key lives in the game's data folder <strong>on the PC you linked from</strong>.
Deleting the game's data removes it from that PC but does <strong>not</strong>
revoke it at lichess.</p>

<div class="warn">
  <p><strong>Changing your lichess password will not unlink Gambit.</strong> A
  password change (and "log out everywhere") only ends browser sessions — it
  leaves API tokens like this one working. This surprises people, so it's worth
  saying plainly.</p>
  <p>The real off switches are below.</p>
</div>

<h2>How to unlink</h2>
<ul>
  <li><strong>In-game:</strong> the lichess board has an unlink button. This is the
      thorough one — only the game holds the key, so only the game can ask lichess
      to revoke it. It deletes the key and revokes it.</li>
  <li><strong>Here:</strong> the button below makes us forget the link. It cannot
      revoke the key, because we don't have it.</li>
  <li><strong>On lichess:</strong> <a href="https://lichess.org/account/security"
      rel="noopener noreferrer">lichess.org/account/security</a> — revoke it there
      and it dies no matter what we do.</li>
</ul>

<!-- POST, not a link: a GET that unlinks would fire on any prefetch or crawl.
     SameSite=Lax keeps the session cookie off cross-site POSTs. -->
<form method="POST" action="/lichess/unlink">
  <button type="submit">Make this server forget the link</button>
</form>
<p><em>Note:</em> lichess's <code>/account/oauth/token</code> page will <strong>not</strong>
show this grant — that page lists personal API tokens only. Use
<code>/account/security</code>, or you'll look at an empty list and conclude
nothing is linked.</p>

<p class="actions"><a href="/">Back to Terry's Gambit</a></p>`))

// renderLichessConsent is the pre-flight page shown before the OAuth bounce.
func (h *handler) renderLichessConsent(w http.ResponseWriter) {
	var buf htmlBuffer
	if err := consentTmpl.Execute(&buf, map[string]any{
		"Scope": lichess.Scopes,
		"Logo":  lichessLogo,
	}); err != nil {
		h.log.Error("could not render the lichess consent page", zap.Error(err))
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	h.writePage(w, http.StatusOK, "Link your lichess account", template.HTML(buf.String()))
}

// renderLichessLinked is the callback page. It takes no username: see the file
// comment — gamchess has no token at this point and identity costs one.
func (h *handler) renderLichessLinked(w http.ResponseWriter) {
	var buf htmlBuffer
	if err := linkedTmpl.Execute(&buf, map[string]string{
		"Scope": lichess.Scopes,
	}); err != nil {
		h.log.Error("could not render the lichess linked page", zap.Error(err))
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	h.writePage(w, http.StatusOK, "Linked", template.HTML(buf.String()))
}

// renderLichessUnlinked is the web unlink outcome. It is its OWN page rather
// than a renderLichessPage one-liner because the honest answer is two-part and
// the second part is the important half: we forgot the link, and we could not
// revoke the key, because the key is on the player's PC and lichess requires the
// revoke to be signed by the key itself.
func (h *handler) renderLichessUnlinked(w http.ResponseWriter) {
	h.writePage(w, http.StatusOK, "Unlinked", template.HTML(`
<h1>Unlinked</h1>
<p>This server has forgotten your lichess account. Games you play here won't go to
lichess any more.</p>
<div class="warn">
  <p><strong>The key on your gaming PC is still valid.</strong> We can't revoke it
  from here — lichess only accepts a revoke signed by the key itself, and we don't
  have a copy. To really switch it off, do one of:</p>
  <ul>
    <li><strong>In-game:</strong> press unlink on the lichess board. That deletes
        the key and revokes it at lichess.</li>
    <li><strong>On lichess:</strong>
        <a href="https://lichess.org/account/security" rel="noopener noreferrer">lichess.org/account/security</a>
        — revoke Terry's Gambit there and it dies no matter what anyone else does.</li>
  </ul>
</div>
<p><em>Note:</em> lichess's <code>/account/oauth/token</code> page will <strong>not</strong>
show this grant — that page lists personal API tokens only. Use
<code>/account/security</code>, or you'll look at an empty list and conclude
nothing is linked.</p>
<p class="actions"><a href="/">Back to Terry's Gambit</a></p>`))
}

// htmlBuffer is a tiny strings.Builder shim so the templates above can render
// into something before being embedded in the shell.
type htmlBuffer struct{ b []byte }

func (h *htmlBuffer) Write(p []byte) (int, error) {
	h.b = append(h.b, p...)
	return len(p), nil
}
func (h *htmlBuffer) String() string { return string(h.b) }
