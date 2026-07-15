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
// answers to a request, not files: the callback page has to name the account
// that was just linked. They reuse the viewer's stylesheet (/style.css), so they
// look like the rest of the site without duplicating any of it.
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
<p>One permission, and nothing else: <span class="scope">{{.Scope}}</span>.</p>
<ul>
  <li><strong>It lets us:</strong> start a game between you and the player sitting
      opposite you, play your moves, resign, and offer or accept a draw — all
      only for games you started from a Gambit table.</li>
  <li><strong>It does not let us:</strong> read your email, read or send your
      lichess messages, see or change who you follow, touch your teams or
      studies, or change anything about your account.</li>
</ul>
<p>Lichess has no smaller permission than this — <span class="scope">{{.Scope}}</span>
is all-or-nothing, and it is the only one that can play a game.</p>

<h2>Where the key is kept</h2>
<p>The access token lichess gives us is stored on our server, encrypted. It has to
live there rather than in the game: playing a lichess game means holding a live
connection open for the whole game, and the s&amp;box client can't do that — so
our server plays on your behalf while you sit at the board.</p>

<p class="actions"><a href="/lichess/start"><strong>Continue to lichess →</strong></a> ·
<a href="/">Cancel</a></p>`))

// linkedTmpl is the AFTER page: what you just granted, and how to take it back.
var linkedTmpl = template.Must(template.New("linked").Parse(`
<h1>Linked <strong>{{.Username}}</strong></h1>
<p>Your lichess account <strong>{{.Username}}</strong> is now linked to your Steam
account. Head back in-game — the lichess board will show you as linked within a
few seconds.</p>

<h2>What you just granted</h2>
<p>One permission: <span class="scope">{{.Scope}}</span>. It lets Terry's Gambit
play games on lichess as you — moves, resign, draw offers — and nothing else. It
cannot read your email or messages, change your account, or see anything private.</p>
<p>We stored the access token on our server, encrypted at rest. It is used only to
run games you start from a Gambit table.</p>

<div class="warn">
  <p><strong>Changing your lichess password will not unlink Gambit.</strong> A
  password change (and "log out everywhere") only ends browser sessions — it
  leaves API tokens like this one working. This surprises people, so it's worth
  saying plainly.</p>
  <p>The real off switches are below.</p>
</div>

<h2>How to unlink</h2>
<ul>
  <li><strong>Here:</strong> the button below — we revoke the token at lichess and
      forget it.</li>
  <li><strong>In-game:</strong> the lichess board has an unlink button.</li>
  <li><strong>On lichess:</strong> <a href="https://lichess.org/account/security"
      rel="noopener noreferrer">lichess.org/account/security</a> — revoke it there
      and it dies no matter what we do.</li>
</ul>

<!-- POST, not a link: a GET that unlinks would fire on any prefetch or crawl.
     SameSite=Lax keeps the session cookie off cross-site POSTs. -->
<form method="POST" action="/lichess/unlink">
  <button type="submit">Unlink {{.Username}}</button>
</form>
<p><em>Note:</em> lichess's <code>/account/oauth/token</code> page will <strong>not</strong>
show this grant — that page lists personal API tokens only. Use
<code>/account/security</code>, or you'll look at an empty list and conclude
nothing is linked.</p>

<p class="actions"><a href="/">Back to Terry's Gambit</a></p>`))

// renderLichessConsent is the pre-flight page shown before the OAuth bounce.
func (h *handler) renderLichessConsent(w http.ResponseWriter) {
	var buf htmlBuffer
	if err := consentTmpl.Execute(&buf, map[string]string{"Scope": lichess.Scope}); err != nil {
		h.log.Error("could not render the lichess consent page", zap.Error(err))
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	h.writePage(w, http.StatusOK, "Link your lichess account", template.HTML(buf.String()))
}

// renderLichessLinked is the callback success page.
func (h *handler) renderLichessLinked(w http.ResponseWriter, username string) {
	var buf htmlBuffer
	if err := linkedTmpl.Execute(&buf, map[string]string{
		"Username": username,
		"Scope":    lichess.Scope,
	}); err != nil {
		h.log.Error("could not render the lichess linked page", zap.Error(err))
		http.Error(w, "internal error", http.StatusInternalServerError)
		return
	}
	h.writePage(w, http.StatusOK, "Linked", template.HTML(buf.String()))
}

// htmlBuffer is a tiny strings.Builder shim so the templates above can render
// into something before being embedded in the shell.
type htmlBuffer struct{ b []byte }

func (h *htmlBuffer) Write(p []byte) (int, error) {
	h.b = append(h.b, p...)
	return len(p), nil
}
func (h *htmlBuffer) String() string { return string(h.b) }
