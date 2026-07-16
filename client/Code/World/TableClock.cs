using System;
using Sandbox;
using Gambit.Chess;
using Gambit.Game;
using Gambit.UI;

namespace Gambit.World;

/// <summary>
/// Drives the clock lying in each table's −Y margin: two times and a material bar.
///
/// <para><b>Why this is a Component and not a panel's @code block.</b> The clock was one
/// WorldPanel composing three things in CSS, and it cost five rounds of bugs — every one a
/// layout bug, none a data bug (gambit_clock printed correct seconds the whole time). This
/// repo has exactly one WorldPanel shape that renders (root at 100% + ONE absolutely-
/// positioned 100% child) and it is inherently one-string-per-panel; it cannot be composed.
/// So the composition moved into 3D, where it is arithmetic: two mesh plates each carrying a
/// one-string <see cref="TableClockTextPanel"/>, and a mesh bar between them. PLAN.md decided
/// this originally ("the board is real meshes and the clock should be too") and CLAUDE.md
/// says it as a rule — reach for meshes over panel art.</para>
///
/// <para><b>Everything reads the IBoardGame seam, never LocalGameController.</b> During a
/// lichess game the host FREEZES the local controller's clocks on purpose (HostTickClocks
/// early-returns on LichessGame, so it cannot flag a player who is fine on lichess's clock)
/// — a clock reading it would simply never move.</para>
///
/// <para><b>Local, never networked.</b> Same rule as the board view: every client drives its
/// own from state that is already replicated. Nothing here decides anything.</para>
/// </summary>
public sealed class TableClock : Component
{
	/// <summary>Wired by ChessRing at build, exactly as the board view and the sounds are —
	/// same refs, same seam, so all three answer for the same game.</summary>
	[Property] public LocalGameController Controller { get; set; }
	[Property] public LichessGameController Lichess { get; set; }

	// The plates. Text panel + the plate mesh behind it, per seat — the mesh is here so the
	// ticking side's plate can lighten with its text.
	[Property] public TableClockTextPanel WhiteText { get; set; }
	[Property] public TableClockTextPanel BlackText { get; set; }
	[Property] public ModelRenderer WhitePlate { get; set; }
	[Property] public ModelRenderer BlackPlate { get; set; }

	/// <summary>The bar's moving part. Scaled along its length and slid off centre; see
	/// <see cref="DriveBar"/>. The TRACK behind it has no reference here on purpose — it never
	/// changes and never hides, so nothing needs to hold it.</summary>
	[Property] public GameObject BarFill { get; set; }
	[Property] public ModelRenderer BarFillRenderer { get; set; }

	/// <summary>The lead badge's one string — the real material difference, always drawn.</summary>
	[Property] public TableClockTextPanel LeadText { get; set; }

	/// <summary>The fill's scale at full extension, captured at build. The driver scales this
	/// rather than recomputing from Model.Bounds — <c>ChessRing.AddBoxGo</c> already did that
	/// arithmetic once, and doing it twice is how the two come to disagree.</summary>
	[Property] public Vector3 BarFillFullScale { get; set; }

	/// <summary>Half the fill's travel: dead centre → fully one side, in world units.</summary>
	[Property] public float BarFillHalfLength { get; set; }

	/// <summary>The fill's built position. Only its LENGTH axis moves — the other two carry
	/// the fill's lift off its track and the drop that levels the bar's bottom edge with the
	/// plates', and writing a bare Vector3 here would silently discard both: the fill would
	/// sink into the track and float back up to the bar's centre. Slide along Y from this,
	/// never from zero.</summary>
	[Property] public Vector3 BarFillBasePosition { get; set; }

	IBoardGame Source => BoardGame.Source( Controller, Lichess );

	protected override void OnUpdate()
	{
		DriveFace( ChessSeat.White, WhiteText, WhitePlate );
		DriveFace( ChessSeat.Black, BlackText, BlackPlate );
		DriveBar();
	}

	void DriveFace( ChessSeat seat, TableClockTextPanel text, ModelRenderer plate )
	{
		// IsValid(), not a null check — the repo's idiom (Streetlights guards a ModelRenderer
		// Tint exactly this way). A destroyed component is non-null and would throw here.
		if ( !text.IsValid() ) return;

		var state = FaceState( seat );
		text.Text = Face( seat );
		text.State = state;

		// The plate lightens under the running side's text. Both plates are opaque, so the
		// ticking one is told apart by going brighter rather than by getting less
		// see-through — the stronger signal anyway, and the reason the plate is a mesh with
		// a tint rather than a div with an alpha.
		if ( plate.IsValid() )
			plate.Tint = state == "" ? ChessRing.ClockPlateColor : ChessRing.ClockPlateOnColor;
	}

	/// <summary>
	/// A seat's clock face.
	///
	/// <para><b>An idle table shows the BANK the next game will start with</b>, not a dash.
	/// This is what a real chess clock does: you set it, it reads 3:00, and it does that
	/// until someone presses it. The first version returned SeatClock directly, which is
	/// null whenever nothing is live — so a table nobody had started yet showed "— —" and
	/// read as broken. That was, in fact, the entire reason the clock looked like it had
	/// nothing on it: an idle table is the state you find every table in.</para>
	///
	/// <para>Reads the local controller for the bank on purpose: only an idle table has one
	/// to advertise, and an idle table by definition has no lichess game on it, so there is
	/// no source to disagree with. Once a game IS live, the seam answers and this branch
	/// never runs.</para>
	///
	/// <para><b>An UNLIMITED game says so — "∞", not a dash.</b> There is genuinely no
	/// clock, so the honest thing is to say there is no clock; a dash says nothing and reads
	/// as a clock that failed. This one already cost a round of "the clock isn't displaying
	/// anything" on a table that was working perfectly and simply had no time control. <b>A
	/// blank-looking readout and a broken readout must not look the same</b> — it is the TV
	/// fanfare's lesson (a feature that never fires looks exactly like one that isn't wired
	/// up) arriving on a different wall.</para>
	/// </summary>
	string Face( ChessSeat seat )
	{
		// TimeControl.Format, never our own formatting. It TRUNCATES rather than rounds,
		// which is the house rule: a live clock must never read higher than the time
		// actually left. "{seconds:0.0}" would render 59.96 as "60.0".
		if ( Source?.SeatClock( seat ) is float left ) return TimeControl.Format( left );

		// No live clock. Either the table is idle (advertise the bank the next game starts
		// with, like a real clock does) or the game is untimed (say so).
		var tc = Controller?.Tc;
		if ( tc is null ) return "—";                       // no table at all: nothing to claim
		if ( tc.Value.IsUnlimited ) return "∞";
		return TimeControl.Format( tc.Value.InitialSeconds );
	}

	/// <summary>Whose clock is running, off the seam. Not LocalGameController.TickingSeat:
	/// during a lichess game its ChessGame never advances, so it answers "White" for the
	/// whole game.</summary>
	ChessSeat? Ticking =>
		Source is { Playing: true, Game: { } game } && Source.SeatClock( ChessSeat.White ) != null
			? ( game.WhiteToMove ? ChessSeat.White : ChessSeat.Black )
			: null;

	string FaceState( ChessSeat seat )
	{
		if ( Ticking != seat ) return "";
		return Source?.SeatClock( seat ) is float left && left < TimeControl.PanicSeconds
			? "panic" : "on";
	}

	/// <summary>
	/// The material bar: a fill running from dead centre toward whoever is ahead, and the
	/// number in front of it.
	///
	/// <para><b>The number always draws, and it is the REAL difference — never clamped.</b>
	/// That is the whole division of labour here: the bar saturates at <see cref="BarFullAt"/>
	/// and from there on it is lying by omission (a +10 and a +25 draw identically), and the
	/// number is what stays true. Clamp the number to match the bar and the pair becomes
	/// useless at exactly the moment the position is most lopsided.</para>
	///
	/// <para><b>At level, the fill goes and the track stays.</b> A centred, empty track is what
	/// a material bar at zero should look like — the object saying "nobody is ahead", not the
	/// object being absent. This used to hide the bar entirely, which made "not there" the
	/// normal state of a thing whose normal state is level.</para>
	///
	/// <para>The number no longer sits in front of the fill — the bar moved down onto the base's
	/// upright face and the badge stayed in the plates' plane above it. Nothing here changed for
	/// it: the two were never coupled in code, only in space.</para>
	/// </summary>
	void DriveBar()
	{
		if ( !BarFill.IsValid() ) return;

		// Null (no position to read at all) reads as level: an idle table shows a centred
		// track and a zero, exactly as its clocks show the bank they will start from.
		int lead = Advantage() ?? 0;

		if ( LeadText.IsValid() )
		{
			// Magnitude only — the FILL's direction says who, so a sign here would be the
			// same fact twice. "0" rather than "+0" or a dash: a dash reads as broken, and
			// this is a working gauge reporting a level game.
			LeadText.Text = lead == 0 ? "0" : $"+{Math.Abs( lead )}";
			// Bright: a readout, not an idle clock face. TableClockTextPanel's states are
			// display states ("" dim / "on" bright / "panic" red), not game states.
			LeadText.State = "on";
		}

		if ( lead == 0 )
		{
			BarFill.Enabled = false;
			return;
		}

		BarFill.Enabled = true;

		// Clamped at BarFullAt rather than scaled to the theoretical maximum (~103 pawns).
		// Ten pawns up is a won game and should look like one; scaling to the maximum would
		// draw it as a sliver. Past ten the bar pins and the number carries it.
		float frac = Math.Min( Math.Abs( lead ), BarFullAt ) / (float)BarFullAt;

		// Local +Y is White's end of the bar, and that is derived, not guessed: the bar is
		// yawed 90°, and yaw 90 maps local +Y onto table −X — which BuildStationPlaque's own
		// comment fixes as White's side (+X is Black's, radially inward). Unchanged when the
		// bar moved off the plates' tilted plane onto the base's upright face: it lost the
		// PITCH, and pitch never touched the local Y axis. The old panel had to reason about a
		// WorldPanel's content-space handedness for this same fact and got it backwards on the
		// first try, rendering each player their OPPONENT's clock. In table space it is a sign.
		float sign = lead > 0 ? 1f : -1f;

		// Grow from the centre: scale the length axis by frac, then slide the fill's centre
		// out by half of what it now spans, so its inner end stays pinned at dead centre.
		//
		// Vector3s spelled out rather than WithY() — this host has no s&box toolchain and no
		// engine reference to grep, so an API recalled from memory is an unverifiable claim.
		// A constructor is a fact. The x and z come from the built position and must survive:
		// they carry the fill's lift off its track and the drop that levels the bar's bottom
		// edge with the plates'.
		BarFill.LocalScale = new Vector3(
			BarFillFullScale.x, BarFillFullScale.y * frac, BarFillFullScale.z );
		BarFill.LocalPosition = new Vector3(
			BarFillBasePosition.x,
			sign * frac * BarFillHalfLength * 0.5f,
			BarFillBasePosition.z );

		if ( BarFillRenderer.IsValid() )
			BarFillRenderer.Tint = lead > 0 ? ChessRing.ClockBarWhiteColor : ChessRing.ClockBarBlackColor;
	}

	/// <summary>Material balance in pawns, positive for White — or null when there is no
	/// position to read.
	///
	/// <para>Derived from the FEN through CapturedMaterial, never from a tally of captures:
	/// this has no history, so a late joiner or a resync would show a level bar in a game
	/// somebody is a rook up in. Same rule the trays follow, and the same reason.</para>
	/// </summary>
	int? Advantage()
	{
		var game = Source?.Game;
		if ( game == null ) return null;

		var squares = new char[64];
		for ( int rank = 0; rank < 8; rank++ )
			for ( int file = 0; file < 8; file++ )
				squares[rank * 8 + file] = game.PieceAt( file, rank );

		return CapturedMaterial.Advantage( squares );
	}

	/// <summary>Material lead at which the bar is fully extended. Past this it pins, and the
	/// number in front of it is the only thing still reporting the truth — which is why the
	/// number is never clamped to match.</summary>
	const int BarFullAt = 10;
}
