package store

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// ErrConflict is returned when an atomic claim loses a race — e.g. two players
// join the same open match, or a move arrives out of turn. Callers map it to 409.
var ErrConflict = errors.New("conflict")

// ── Matchmaking directory ──

type Match struct {
	ID            string
	OpenerSteamID int64
	OpenerName    string
	LobbyID       string
	Mode          string // "join" | "relay"
	TimeControl   string
	Status        string // "open" | "matched" | "closed"
	WhiteSteamID  *int64
	BlackSteamID  *int64
	JoinerSteamID *int64
	GameID        *string
	OpenerColor   string // "w"/"b" once matched — the colour the OPENER plays
	CreatedAt     time.Time
	UpdatedAt     time.Time
}

const matchCols = `id, opener_steam_id, opener_name, lobby_id, mode, time_control,
                   status, white_steam_id, black_steam_id, joiner_steam_id, game_id,
                   opener_color, created_at, updated_at`

func scanMatch(row pgx.Row) (Match, error) {
	var m Match
	err := row.Scan(&m.ID, &m.OpenerSteamID, &m.OpenerName, &m.LobbyID, &m.Mode,
		&m.TimeControl, &m.Status, &m.WhiteSteamID, &m.BlackSteamID, &m.JoinerSteamID,
		&m.GameID, &m.OpenerColor, &m.CreatedAt, &m.UpdatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return Match{}, ErrNotFound
	}
	if err != nil {
		return Match{}, fmt.Errorf("scan match: %w", err)
	}
	return m, nil
}

// OpenMatch advertises a game. A player may hold only one OPEN row (the partial
// unique index enforces it), so any existing open row for this opener is closed
// first — a second "open" replaces the first rather than erroring.
func OpenMatch(ctx context.Context, db *pgxpool.Pool, m Match) (Match, error) {
	tx, err := db.Begin(ctx)
	if err != nil {
		return Match{}, fmt.Errorf("open match: %w", err)
	}
	defer tx.Rollback(ctx)

	if _, err := tx.Exec(ctx,
		`UPDATE matchmaking SET status='closed', updated_at=NOW()
		 WHERE opener_steam_id=$1 AND status='open'`, m.OpenerSteamID); err != nil {
		return Match{}, fmt.Errorf("close prior open: %w", err)
	}

	out, err := scanMatch(tx.QueryRow(ctx, `
		INSERT INTO matchmaking (opener_steam_id, opener_name, lobby_id, mode, time_control)
		VALUES ($1, $2, $3, $4, $5)
		RETURNING `+matchCols,
		m.OpenerSteamID, m.OpenerName, m.LobbyID, m.Mode, m.TimeControl))
	if err != nil {
		return Match{}, err
	}
	if err := tx.Commit(ctx); err != nil {
		return Match{}, fmt.Errorf("open match commit: %w", err)
	}
	return out, nil
}

// ListOpenMatches returns open advertisements, newest first. It NO LONGER excludes
// the caller's own: self-play (joining your own advert) is allowed, so you must be
// able to see it. The client marks its own row.
func ListOpenMatches(ctx context.Context, db *pgxpool.Pool, limit int) ([]Match, error) {
	rows, err := db.Query(ctx, `
		SELECT `+matchCols+` FROM matchmaking
		WHERE status='open'
		ORDER BY created_at DESC LIMIT $1`, limit)
	if err != nil {
		return nil, fmt.Errorf("list matches: %w", err)
	}
	defer rows.Close()

	out := make([]Match, 0, limit)
	for rows.Next() {
		m, err := scanMatch(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, m)
	}
	return out, rows.Err()
}

func MatchByID(ctx context.Context, db *pgxpool.Pool, id string) (Match, error) {
	return scanMatch(db.QueryRow(ctx, `SELECT `+matchCols+` FROM matchmaking WHERE id=$1`, id))
}

// ClaimMatch atomically flips an OPEN match to matched, recording the joiner, the
// (already coin-flipped) colours, the opener's colour and — for a relay game — its
// game_id. The WHERE status='open' is the whole point: two joiners race, exactly one
// row is updated, the loser gets ErrConflict.
//
// Self-join is ALLOWED (opener may equal joiner) — playing yourself is a feature and
// the one-machine test path. opener_color is what lets the two sides be told apart
// even when both SteamIDs are identical.
func ClaimMatch(ctx context.Context, db *pgxpool.Pool, id string, joiner, white, black int64, openerColor string, gameID *string) (Match, error) {
	m, err := scanMatch(db.QueryRow(ctx, `
		UPDATE matchmaking
		SET status='matched', joiner_steam_id=$2, white_steam_id=$3, black_steam_id=$4,
		    opener_color=$5, game_id=$6, updated_at=NOW()
		WHERE id=$1 AND status='open'
		RETURNING `+matchCols, id, joiner, white, black, openerColor, gameID))
	if errors.Is(err, ErrNotFound) {
		return Match{}, ErrConflict // gone or already matched
	}
	return m, err
}

// CloseMatch closes a match the opener owns (cancel). No-op-safe: closing an
// already-closed or missing row just affects no rows.
func CloseMatch(ctx context.Context, db *pgxpool.Pool, id string, opener int64) error {
	_, err := db.Exec(ctx,
		`UPDATE matchmaking SET status='closed', updated_at=NOW()
		 WHERE id=$1 AND opener_steam_id=$2`, id, opener)
	if err != nil {
		return fmt.Errorf("close match: %w", err)
	}
	return nil
}

// SetMatchGameID points a matched row at its relay game (mode 'relay'), so the
// opener's poll can hand the client the game to start relaying.
func SetMatchGameID(ctx context.Context, db *pgxpool.Pool, id, gameID string) error {
	_, err := db.Exec(ctx,
		`UPDATE matchmaking SET game_id=$2, updated_at=NOW() WHERE id=$1`, id, gameID)
	if err != nil {
		return fmt.Errorf("set match game: %w", err)
	}
	return nil
}

// TouchMatch bumps updated_at on the opener's open row — the heartbeat that keeps
// it out of the staleness sweep while the opener is still waiting.
func TouchMatch(ctx context.Context, db *pgxpool.Pool, id string, opener int64) error {
	_, err := db.Exec(ctx,
		`UPDATE matchmaking SET updated_at=NOW()
		 WHERE id=$1 AND opener_steam_id=$2 AND status='open'`, id, opener)
	if err != nil {
		return fmt.Errorf("touch match: %w", err)
	}
	return nil
}

// ExpireStaleMatches closes open/matched rows not touched within ttl. Presence
// lies when a client vanishes (crash, quit); the heartbeat is the live signal and
// this is the backstop, the same shape as the TV linger sweep.
func ExpireStaleMatches(ctx context.Context, db *pgxpool.Pool, ttl time.Duration) (int64, error) {
	cut := time.Now().Add(-ttl)
	tag, err := db.Exec(ctx,
		`UPDATE matchmaking SET status='closed', updated_at=NOW()
		 WHERE status IN ('open','matched') AND updated_at < $1`, cut)
	if err != nil {
		return 0, fmt.Errorf("expire matches: %w", err)
	}
	return tag.RowsAffected(), nil
}

// ── Relay games (mode 'relay') ──

type RelayGame struct {
	ID           string
	WhiteSteamID int64
	BlackSteamID int64
	TimeControl  string
	InitialMs    int64
	IncrementMs  int64
	WhiteMs      int64 // remaining AS OF last_move_at (not the live ticking value)
	BlackMs      int64
	Turn         string // "w" | "b"
	Moves        string // space-separated UCI
	Fen          string
	Status       string // "live" | "white_won" | "black_won" | "draw" | "aborted"
	Reason       string
	DrawOffer    string // "" | "w" | "b"
	LastMoveAt   time.Time
	CreatedAt    time.Time
	UpdatedAt    time.Time
}

const relayCols = `id, white_steam_id, black_steam_id, time_control, initial_ms,
                   increment_ms, white_ms, black_ms, turn, moves, fen, status,
                   reason, draw_offer, last_move_at, created_at, updated_at`

func scanRelay(row pgx.Row) (RelayGame, error) {
	var g RelayGame
	err := row.Scan(&g.ID, &g.WhiteSteamID, &g.BlackSteamID, &g.TimeControl, &g.InitialMs,
		&g.IncrementMs, &g.WhiteMs, &g.BlackMs, &g.Turn, &g.Moves, &g.Fen, &g.Status,
		&g.Reason, &g.DrawOffer, &g.LastMoveAt, &g.CreatedAt, &g.UpdatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return RelayGame{}, ErrNotFound
	}
	if err != nil {
		return RelayGame{}, fmt.Errorf("scan relay game: %w", err)
	}
	return g, nil
}

// Untimed marks a game with no clock (the '-' time control). InitialMs is -1 for
// these, and LiveClocks never flags them.
func (g RelayGame) Untimed() bool { return g.InitialMs < 0 }

func (g RelayGame) Over() bool { return g.Status != "live" }

// Ply is the number of half-moves played.
func (g RelayGame) Ply() int {
	if g.Moves == "" {
		return 0
	}
	return len(strings.Fields(g.Moves))
}

// LiveClocks is the DISPLAYED clock pair at time `now`: the side to move has its
// stored bank decremented by the time elapsed since last_move_at, the idle side is
// untouched. `flagged` is "w"/"b" if the ticking side has run out (0 there), else
// "". Pure and deterministic — the flag decision and the response both go through
// it, and it is unit-tested without a DB.
//
// This is the ONE thing gamchess adjudicates about a relay game (it holds no chess
// engine): the clock. Never reads HIGH — the ticking side only ever counts down —
// which is the same house rule the lichess/TV clocks follow client-side.
func (g RelayGame) LiveClocks(now time.Time) (whiteMs, blackMs int64, flagged string) {
	whiteMs, blackMs = g.WhiteMs, g.BlackMs
	if g.Over() || g.Untimed() {
		return whiteMs, blackMs, ""
	}
	elapsed := now.Sub(g.LastMoveAt).Milliseconds()
	if elapsed < 0 {
		elapsed = 0
	}
	if g.Turn == "w" {
		whiteMs -= elapsed
		if whiteMs <= 0 {
			whiteMs = 0
			flagged = "w"
		}
	} else {
		blackMs -= elapsed
		if blackMs <= 0 {
			blackMs = 0
			flagged = "b"
		}
	}
	return whiteMs, blackMs, flagged
}

func CreateRelayGame(ctx context.Context, db *pgxpool.Pool, g RelayGame) (RelayGame, error) {
	return scanRelay(db.QueryRow(ctx, `
		INSERT INTO relay_games (white_steam_id, black_steam_id, time_control,
		                         initial_ms, increment_ms, white_ms, black_ms)
		VALUES ($1,$2,$3,$4,$5,$6,$7)
		RETURNING `+relayCols,
		g.WhiteSteamID, g.BlackSteamID, g.TimeControl, g.InitialMs, g.IncrementMs,
		g.WhiteMs, g.BlackMs))
}

// ReadRelayGame reads a game and flags it if the ticking side has timed out. The
// flag is applied in the same transaction as the read so two concurrent pollers
// converge on one authoritative result. Returns the game with STORED clocks (the
// caller renders live values via LiveClocks); if it just flagged, Status reflects it.
func ReadRelayGame(ctx context.Context, db *pgxpool.Pool, id string) (RelayGame, error) {
	tx, err := db.Begin(ctx)
	if err != nil {
		return RelayGame{}, fmt.Errorf("read relay: %w", err)
	}
	defer tx.Rollback(ctx)

	g, err := scanRelay(tx.QueryRow(ctx, `SELECT `+relayCols+` FROM relay_games WHERE id=$1 FOR UPDATE`, id))
	if err != nil {
		return RelayGame{}, err
	}
	if !g.Over() {
		if _, _, flagged := g.LiveClocks(time.Now()); flagged != "" {
			g = endRelay(&g, flagged, "Out of time")
			if err := saveRelayEnd(ctx, tx, g); err != nil {
				return RelayGame{}, err
			}
			if err := tx.Commit(ctx); err != nil {
				return RelayGame{}, fmt.Errorf("read relay flag commit: %w", err)
			}
		}
	}
	return g, nil
}

// RelayMoveIn is a mover's report of one move. Over is set when the mover's own
// rules say the move ended the game (checkmate/stalemate/auto-draw) — gamchess
// trusts it, exactly as the two-seat host trusts a NetChessMove.
type RelayMoveIn struct {
	Uci    string
	Fen    string
	Over   bool
	Result string // "white_won" | "black_won" | "draw", when Over
	Reason string
}

// ApplyRelayMove appends a move if it's the mover's turn, ticks the mover's clock,
// applies the increment and flips the turn — all under a row lock so two POSTs
// can't interleave. A move that isn't the mover's turn (already played, or wrong
// player) is ErrConflict. A timeout discovered on the way in ends the game instead.
func ApplyRelayMove(ctx context.Context, db *pgxpool.Pool, id string, mover int64, in RelayMoveIn) (RelayGame, error) {
	tx, err := db.Begin(ctx)
	if err != nil {
		return RelayGame{}, fmt.Errorf("apply move: %w", err)
	}
	defer tx.Rollback(ctx)

	g, err := scanRelay(tx.QueryRow(ctx, `SELECT `+relayCols+` FROM relay_games WHERE id=$1 FOR UPDATE`, id))
	if err != nil {
		return RelayGame{}, err
	}
	if g.Over() {
		return RelayGame{}, ErrConflict
	}

	now := time.Now()
	// A timeout that happened before this move lands ends the game against the
	// clock, not with the move — the mover was already flagged.
	whiteMs, blackMs, flagged := g.LiveClocks(now)
	if flagged != "" {
		g = endRelay(&g, flagged, "Out of time")
		if err := saveRelayEnd(ctx, tx, g); err != nil {
			return RelayGame{}, err
		}
		return g, tx.Commit(ctx)
	}

	// The mover must own the side to move.
	moverWhite := mover == g.WhiteSteamID
	moverBlack := mover == g.BlackSteamID
	if (g.Turn == "w" && !moverWhite) || (g.Turn == "b" && !moverBlack) {
		return RelayGame{}, ErrConflict
	}

	// Bank the ticking side at its live value, add the increment, flip.
	if g.Turn == "w" {
		g.WhiteMs = whiteMs
		if !g.Untimed() {
			g.WhiteMs += g.IncrementMs
		}
		g.Turn = "b"
	} else {
		g.BlackMs = blackMs
		if !g.Untimed() {
			g.BlackMs += g.IncrementMs
		}
		g.Turn = "w"
	}
	g.Moves = strings.TrimSpace(g.Moves + " " + in.Uci)
	g.Fen = in.Fen
	g.LastMoveAt = now
	g.DrawOffer = "" // a move declines any standing offer

	if in.Over {
		g.Status, g.Reason = in.Result, in.Reason
	}

	out, err := scanRelay(tx.QueryRow(ctx, `
		UPDATE relay_games
		SET white_ms=$2, black_ms=$3, turn=$4, moves=$5, fen=$6, status=$7,
		    reason=$8, draw_offer='', last_move_at=$9, updated_at=NOW()
		WHERE id=$1
		RETURNING `+relayCols,
		id, g.WhiteMs, g.BlackMs, g.Turn, g.Moves, g.Fen, g.Status, g.Reason, g.LastMoveAt))
	if err != nil {
		return RelayGame{}, err
	}
	return out, tx.Commit(ctx)
}

// RelayAction applies a non-move: "resign", "abort", "draw-offer", "draw-accept",
// "draw-decline". actor must be one of the two players.
func RelayAction(ctx context.Context, db *pgxpool.Pool, id string, actor int64, action string) (RelayGame, error) {
	tx, err := db.Begin(ctx)
	if err != nil {
		return RelayGame{}, fmt.Errorf("relay action: %w", err)
	}
	defer tx.Rollback(ctx)

	g, err := scanRelay(tx.QueryRow(ctx, `SELECT `+relayCols+` FROM relay_games WHERE id=$1 FOR UPDATE`, id))
	if err != nil {
		return RelayGame{}, err
	}
	if g.Over() {
		return RelayGame{}, ErrConflict
	}
	actorWhite := actor == g.WhiteSteamID
	actorBlack := actor == g.BlackSteamID
	if !actorWhite && !actorBlack {
		return RelayGame{}, ErrConflict
	}
	side := "w"
	if actorBlack {
		side = "b"
	}

	switch action {
	case "resign":
		g = endRelay(&g, opposite(side), "Resignation")
	case "abort":
		// Only before either side has moved — an aborted game is one that never
		// happened, scored to nobody.
		if g.Ply() >= 2 {
			return RelayGame{}, ErrConflict
		}
		g.Status, g.Reason = "aborted", "Aborted"
	case "draw-offer":
		g.DrawOffer = side
	case "draw-accept":
		// Accept only the OPPONENT's standing offer.
		if g.DrawOffer == "" || g.DrawOffer == side {
			return RelayGame{}, ErrConflict
		}
		g.Status, g.Reason, g.DrawOffer = "draw", "Draw agreed", ""
	case "draw-decline":
		if g.DrawOffer == opposite(side) {
			g.DrawOffer = ""
		}
	default:
		return RelayGame{}, ErrConflict
	}

	out, err := scanRelay(tx.QueryRow(ctx, `
		UPDATE relay_games SET status=$2, reason=$3, draw_offer=$4, updated_at=NOW()
		WHERE id=$1 RETURNING `+relayCols,
		id, g.Status, g.Reason, g.DrawOffer))
	if err != nil {
		return RelayGame{}, err
	}
	return out, tx.Commit(ctx)
}

// endRelay sets the winner side (its clock stops mattering) and returns the game.
// loserSide is "w"/"b"; the winner is the opposite.
func endRelay(g *RelayGame, loserSide, reason string) RelayGame {
	if loserSide == "w" {
		g.Status = "black_won"
	} else {
		g.Status = "white_won"
	}
	g.Reason = reason
	g.DrawOffer = ""
	return *g
}

func saveRelayEnd(ctx context.Context, tx pgx.Tx, g RelayGame) error {
	_, err := tx.Exec(ctx,
		`UPDATE relay_games SET status=$2, reason=$3, draw_offer='', updated_at=NOW() WHERE id=$1`,
		g.ID, g.Status, g.Reason)
	if err != nil {
		return fmt.Errorf("save relay end: %w", err)
	}
	return nil
}

func opposite(side string) string {
	if side == "w" {
		return "b"
	}
	return "w"
}
