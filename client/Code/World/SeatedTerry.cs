using System.Collections.Generic;
using Gambit.Chess;
using Gambit.Game;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Poses the two people sitting at this table (M13), watched off the
/// <see cref="IBoardGame"/> seam — so it covers a local two-seat game and a real lichess
/// game with one implementation and no per-source branching.
///
/// <para><b><see cref="Gambit.Audio.TableSounds"/> is the template, and the shape is the
/// point.</b> Sound used to hang off LocalGameController, which meant the M8 headline
/// feature — a real lichess game at this table — played in COMPLETE silence for two
/// milestones with nothing wrong in any diff. Resolving <see cref="Source"/> with the
/// identical expression is what makes that class of bug impossible here rather than merely
/// avoided: a third kind of game gets seated hands BY EXISTING. What you see, what you
/// hear, and what the hands do are the same game.</para>
///
/// <para><b>Nothing here is networked, and that is the whole authority story.</b> This runs
/// on every client for every station, resolves each seat's avatar from the
/// <c>[Sync(FromHost)]</c> occupancy, and writes animation parameters locally — the same
/// doctrine as clothing. What IS networked is the avatar's transform (owner → everyone,
/// which BeginEngage already relies on) and one packed int of hover/selection per player.
/// A missed frame costs one frame of hand position.</para>
///
/// <para><b>The HANDS only — the sit pose is not here, and that split is deliberate.</b>
/// Sitting belongs to <see cref="LobbyPlayer"/>, which derives it from the seat occupancy
/// alone: someone sitting at an idle table with no game on it is still sitting, so a pose
/// that needed an IBoardGame would be wrong about the commonest case in the room. What
/// needs the seam is what the hands are DOING, which is chess.</para>
///
/// <para>The state machine itself is <see cref="TerryPose"/>, under Code/Chess, so it can
/// be driven through real games in a harness on a host with no engine — which is where its
/// carry bug was found. Everything here is the part that genuinely needs Sandbox: which
/// avatar, and where a square is in the world.</para>
/// </summary>
public sealed class SeatedTerry : Component
{
	/// <summary>The station, and the two candidate sources — wired by ChessRing at build,
	/// exactly as TableSounds is.</summary>
	[Property] public ChessStation Station { get; set; }
	[Property] public LocalGameController Controller { get; set; }
	[Property] public LichessGameController Lichess { get; set; }

	/// <summary>Whichever game owns this board — resolved exactly as
	/// <see cref="ChessBoardView.Source"/> and <see cref="Gambit.Audio.TableSounds"/> do. If
	/// these ever disagree, the board and the hands are describing different games.</summary>
	IBoardGame Source => BoardGame.Source( Controller, Lichess );

	/// <summary>Runtime override (M16 2D play mode): when true, the seated bodies are suppressed —
	/// they are noise under the top-down nadir camera. Set by ChessRing off
	/// <see cref="Gambit.Game.PlayerData.PlayMode"/> == "2d".
	///
	/// <para>Deliberately a SEPARATE static from the authored <see cref="ChessRing.TerrySeated"/>
	/// <c>[Property]</c> kill switch, ANDed into the body-enable gate in <see cref="LobbyPlayer"/>
	/// (<c>effectiveOn = TerrySeated &amp;&amp; !ForceHidden</c>) — same shape as
	/// <see cref="SeatedHandSpikes.HandsOn"/>. This keeps the three-deep kill-switch discipline
	/// intact and stays console-settable, rather than overwriting an authored property at
	/// runtime.</para></summary>
	public static bool ForceHidden;

	object _lastSource;
	HandPose _white = HandPose.None;
	HandPose _black = HandPose.None;

	// The board's last classified change, latched. BoardDiff answers "was that a move, who
	// played it, did it take something" from the FEN and the ply — the same classifier
	// TableSounds uses, so the hands and the sounds cannot disagree about what happened.
	// One per STATION, not per seat: both seats are looking at one board.
	string _lastFen;
	int _lastPly;
	bool _whiteMoved;
	bool _capture;

	// Resolved avatars, cached on change rather than scanned per frame.
	LobbyPlayer _whitePlayer;
	LobbyPlayer _blackPlayer;
	ulong _whiteId;
	ulong _blackId;

	// ─────────────────────────────────────────────────────────────────────────────
	// DEBUG PROBE — rip out once the hand path is tuned (M13).
	//
	// gambit_terry_probe drives the LOCAL seated hand to a Selected-grasp pose on every
	// square in turn, so you can WATCH the arm try to reach each one with no game running,
	// and it logs what the IK actually ACHIEVED vs what it was asked for. That is a better
	// measurement than the geometric reach grid: it is the solver's real behaviour, not a
	// distance-vs-arm-length estimate. Delete this block, the two fields, and the concmd
	// when the lean/clamp lands.
	// ─────────────────────────────────────────────────────────────────────────────

	/// <summary>Toggled by <c>gambit_terry_probe</c>. Only the ACTIVE (local-seated) station
	/// acts on it; every other SeatedTerry ignores it.</summary>
	public static bool Probe;

	bool _probeActive;
	int _probeSquare = -1;
	float _probeHeld;
	bool _probeMeasured;
	readonly float[] _probeMiss = new float[64];

	// The probe FORCES the arm to strain at every square (wide band, no sphere clamp) so the grid
	// is the true reach envelope — otherwise Approach A idles the far squares and the probe
	// measures reach-to-idle, printing a fake "ok" for ranks it never touches. Saved here, restored
	// when the probe ends or is aborted.
	float _probeSavedBand;
	bool _probeSavedSphere;

	const float ProbeHold = 0.6f;    // seconds parked on each square — enough to watch it settle
	const float ProbeSettle = 0.35f; // measure once the chase easing has arrived

	void ProbeTick( ChessStation station )
	{
		var avatar = LobbyPlayer.Local;
		var seat = ChessStation.ActiveSeat;
		var ring = ChessRing.Instance;
		if ( ring == null || !avatar.IsValid() ) return;

		if ( !_probeActive )
		{
			_probeActive = true;
			_probeSquare = 0;
			_probeHeld = 0f;
			_probeMeasured = false;
			for ( int i = 0; i < 64; i++ ) _probeMiss[i] = -1f;

			// Force the arm to strain at every square, so the grid is the true envelope and not
			// Approach A's idle-for-far-squares (which reads as a fake "ok").
			_probeSavedBand = SeatedHandSpikes.ReachBandX;
			_probeSavedSphere = SeatedHandSpikes.UseSphereClamp;
			SeatedHandSpikes.ReachBandX = -999f;
			SeatedHandSpikes.UseSphereClamp = false;

			Log.Info( "[Gambit] probe: sweeping the hand over all 64 squares at grasp height, forcing a "
				+ "reach at each (not Approach A idle). Watch the arm; the miss per square lands in the grid at the end." );
		}

		_probeHeld += Time.Delta;

		// A Selected-grasp pose on the current square: on the board, full weight, grasp
		// height and fingers — the exact target the real path uses when you pick a piece.
		var pose = new HandPose( HandPhase.Selected, _probeSquare, _probeSquare, false, 0f,
			TerryPose.GraspHeight, TerryPose.FingersGrasping, 1f, 0, false, 0f, 0f );
		avatar.ApplyHandPose( station, seat, pose );

		if ( !_probeMeasured && _probeHeld >= ProbeSettle )
		{
			_probeMeasured = true;
			var body = avatar.GameObject.GetComponentInChildren<SkinnedModelRenderer>();
			if ( body != null && body.TryGetBoneTransform( "hand_R", out var tx )
				&& avatar.LastHandIkTarget is { } asked )
			{
				var got = station.WorldTransform.PointToLocal( tx.Position );
				float miss = ( got - asked ).Length;
				_probeMiss[_probeSquare] = miss;
				Log.Info( $"   {(char)( 'a' + ( _probeSquare & 7 ) )}{( _probeSquare >> 3 ) + 1}"
					+ $"  ask ({asked.x,6:0.#},{asked.y,6:0.#},{asked.z,5:0.#})"
					+ $"  got ({got.x,6:0.#},{got.y,6:0.#},{got.z,5:0.#})  miss {miss,5:0.0}" );
			}
		}

		if ( _probeHeld >= ProbeHold )
		{
			_probeSquare++;
			_probeHeld = 0f;
			_probeMeasured = false;
			if ( _probeSquare >= 64 )
			{
				ProbeGrid( seat );
				Probe = false;
				_probeActive = false;
				_probeSquare = -1;
				RestoreProbeLevers();
				avatar.ClearHandPose();
			}
		}
	}

	/// <summary>The empirical reach map: how far the IK fell short on each square, as an 8×8
	/// grid. Under ~2 the hand made it; a big number is where the arm ran out.</summary>
	void ProbeGrid( ChessSeat seat )
	{
		Log.Info( "── probe reach grid (miss units, what the IK ACHIEVED) ──" );
		Log.Info( seat == ChessSeat.White
			? "   White at −X; rank 8 (far) top, rank 1 (near) bottom."
			: "   Black at +X; rank 8 (near) top, rank 1 (far) bottom." );
		Log.Info( "       a      b      c      d      e      f      g      h" );
		for ( int rank = 7; rank >= 0; rank-- )
		{
			string row = $"   {rank + 1} ";
			for ( int file = 0; file < 8; file++ )
			{
				float m = _probeMiss[rank * 8 + file];
				row += m < 0f ? "   ?   " : m <= 2f ? "  ok   " : $" {m,5:0.0} ";
			}
			Log.Info( row );
		}
		Log.Info( "   ok = IK landed it (miss ≤ 2); a number = units short. '?' = never measured." );
	}

	void RestoreProbeLevers()
	{
		SeatedHandSpikes.ReachBandX = _probeSavedBand;
		SeatedHandSpikes.UseSphereClamp = _probeSavedSphere;
	}

	// ─────────────────────────────────────────────────────────────────────────────
	// M14 SWEEP — rip out with the probe. One command that runs every quantitative spike.
	//
	// gambit_terry_sweep drives the local seated hand at a fixed FAR square and measures how far
	// the arm actually gets under each config — baseline (no lean), the natural graded lean (the
	// shipping default), sit=2, and both together — settling between each, then prints one table.
	// It forces a controlled environment (hands on, sphere clamp off, reach band wide open) and
	// restores every lever afterwards. The visual taste call it cannot answer — does the lean read
	// as leaning — stays an eyeball, but the reach NUMBERS are here in one pasteable block.
	// ─────────────────────────────────────────────────────────────────────────────

	/// <summary>Toggled by <c>gambit_terry_sweep</c>. Local-seated station only.</summary>
	public static bool Sweep;

	// ─────────────────────────────────────────────────────────────────────────────
	// THE DOCTOR — one command, no knobs. gambit_terry_doctor strains the hand at the
	// far-rank centre under each candidate ReachMargin in turn, measures everything the
	// knobs exist for (planned rise, applied rise, shoulder travel, miss to the true
	// ask), fires the one-shot pipeline dump mid-run, prints ONE verdict table, and
	// APPLIES the winning margin itself. Exists because turning levers by hand against
	// an animation you can't see clearly is a chore nobody should be assed to do.
	// ─────────────────────────────────────────────────────────────────────────────

	/// <summary>Toggled by <c>gambit_terry_doctor</c>. Local-seated station only.</summary>
	public static bool Doctor;

	static readonly float[] DocMargins = { 2f, 4f, 6f, 8f };
	const float DocSettle = 1.2f;   // rise chase at 6/s is ~99.9% settled by here
	const float DocHold = 1.5f;

	bool _docActive;
	int _docPhase;
	float _docHeld;
	bool _docMeasured;

	readonly float[] _docPlan = new float[4];
	readonly float[] _docResid = new float[4];
	readonly float[] _docApplied = new float[4];
	readonly float[] _docShoulder = new float[4];
	readonly float[] _docMiss = new float[4];

	bool _docSavedHands, _docSavedSphere, _docSavedNatural, _docSavedRise;
	float _docSavedBand, _docSavedMargin;

	void DoctorTick( ChessStation station )
	{
		var avatar = LobbyPlayer.Local;
		var seat = ChessStation.ActiveSeat;
		if ( !avatar.IsValid() ) return;

		if ( !_docActive )
		{
			_docActive = true;
			_docPhase = 0;
			_docHeld = 0f;
			_docMeasured = false;

			_docSavedHands = SeatedHandSpikes.HandsOn;
			_docSavedSphere = SeatedHandSpikes.UseSphereClamp;
			_docSavedBand = SeatedHandSpikes.ReachBandX;
			_docSavedNatural = SeatedHandSpikes.NaturalLean;
			_docSavedRise = SeatedHandSpikes.HalfRiseOn;
			_docSavedMargin = SeatedHandSpikes.ReachMargin;

			SeatedHandSpikes.HandsOn = true;
			SeatedHandSpikes.UseSphereClamp = false;
			SeatedHandSpikes.ReachBandX = -999f;
			SeatedHandSpikes.NaturalLean = true;
			SeatedHandSpikes.HalfRiseOn = true;
			SeatedHandSpikes.ReachMargin = DocMargins[0];

			Log.Info( $"[Gambit] doctor: straining at the far-rank centre under margins "
				+ $"{string.Join( "/", DocMargins )} (~{DocMargins.Length * DocHold:0}s). One verdict table lands at the end; "
				+ "the winner is applied automatically. Hold still." );
		}

		_docHeld += Time.Delta;

		int target = SweepTarget( seat );
		var pose = new HandPose( HandPhase.Selected, target, target, false, 0f,
			TerryPose.GraspHeight, TerryPose.FingersGrasping, 1f, 0, false, 0f, 0f );
		avatar.ApplyHandPose( station, seat, pose );

		if ( !_docMeasured && _docHeld >= DocSettle )
		{
			_docMeasured = true;

			// The full pipeline dump, once, at the first (shipping-default) margin — so
			// the paste carries the planner inputs and the bone anim-vs-final table too.
			if ( _docPhase == 0 ) SeatedHandSpikes.RiseDebug = true;

			var plan = avatar.RisePlanDebug;
			_docPlan[_docPhase] = plan?.PelvisDelta.Length ?? 0f;
			_docResid[_docPhase] = plan?.Residual ?? -1f;
			_docApplied[_docPhase] = avatar.RiseAppliedDebug;

			var body = avatar.GameObject.GetComponentInChildren<SkinnedModelRenderer>();
			if ( body != null )
			{
				if ( body.TryGetBoneTransform( "arm_upper_R", out var sh ) )
					_docShoulder[_docPhase] = station.WorldTransform.PointToLocal( sh.Position ).x;
				if ( body.TryGetBoneTransform( "hand_R", out var hand )
					&& avatar.LastHandIkTarget is { } asked )
					_docMiss[_docPhase] = ( station.WorldTransform.PointToLocal( hand.Position ) - asked ).Length;
			}
		}

		if ( _docHeld >= DocHold )
		{
			_docPhase++;
			_docHeld = 0f;
			_docMeasured = false;
			if ( _docPhase >= DocMargins.Length )
			{
				DoctorReport( seat );
				Doctor = false;
				_docActive = false;
				RestoreDoctorLevers();
				avatar.ClearHandPose();
			}
			else
			{
				SeatedHandSpikes.ReachMargin = DocMargins[_docPhase];
			}
		}
	}

	void DoctorReport( ChessSeat seat )
	{
		Log.Info( "── gambit_terry_doctor: the far-rank centre under each reach margin ──" );
		Log.Info( "   margin  plan|Δ|  resid  applied  shldrX    miss" );
		// The SMALLEST margin that lands the hand wins, not the minimum miss: a bigger
		// margin drags the whole body further over the table (the planner rises until the
		// ask fits the shortened arm), and margin-8's plank was the first screenshot's
		// complaint. With the servo converged, small margins land clean too.
		int best = -1, minMiss = 0;
		for ( int i = 0; i < DocMargins.Length; i++ )
		{
			Log.Info( $"   {DocMargins[i],5:0.0}  {_docPlan[i],7:0.0}  {_docResid[i],5:0.0}"
				+ $"  {_docApplied[i],7:0.0}  {_docShoulder[i],6:0.0}  {_docMiss[i],6:0.0}" );
			if ( best < 0 && _docMiss[i] > 0f && _docMiss[i] <= 1.5f ) best = i;
			if ( _docMiss[i] > 0f && ( _docMiss[minMiss] <= 0f || _docMiss[i] < _docMiss[minMiss] ) ) minMiss = i;
		}
		if ( best < 0 ) best = minMiss;

		SeatedHandSpikes.ReachMargin = DocMargins[best];
		Log.Info( $"   VERDICT: margin {DocMargins[best]} wins (smallest landing the hand; miss {_docMiss[best]:0.0}) — APPLIED. "
			+ $"({( seat == ChessSeat.White ? "White" : "Black" )} seat; the harness expects plan|Δ|≈35, resid≈3 at this square.)" );
		Log.Info( "── reading it ──" );
		Log.Info( "   plan|Δ| far below ~35        → the PLANNER under-asks: read the 'rise debug' inputs above" );
		Log.Info( "                                  (pelvis/ankle heights, leg budget) — the live skeleton disagrees" );
		Log.Info( "                                  with the harness numbers somewhere." );
		Log.Info( "   applied ≪ plan|Δ|            → the easing/override never landed: bone-override problem." );
		Log.Info( "   shldrX barely moves          → the pelvis override isn't carrying the skeleton this run." );
		Log.Info( "   miss stays ~6-9 at EVERY margin → not extension slack: the solver or the pre-compensation" );
		Log.Info( "                                  is losing a constant — paste this whole block back." );
	}

	void RestoreDoctorLevers()
	{
		SeatedHandSpikes.HandsOn = _docSavedHands;
		SeatedHandSpikes.UseSphereClamp = _docSavedSphere;
		SeatedHandSpikes.ReachBandX = _docSavedBand;
		SeatedHandSpikes.NaturalLean = _docSavedNatural;
		SeatedHandSpikes.HalfRiseOn = _docSavedRise;
		// ReachMargin deliberately NOT restored: the doctor's whole job is to leave the
		// winning value applied.
	}

	const int SweepPhases = 4;          // baseline, natural lean, sit=2, natural+sit=2
	const float SweepSettle = 1.0f;     // seconds before measuring — long enough for a sit-pose blend
	const float SweepHold = 1.3f;       // seconds per phase total

	bool _sweepActive;
	int _sweepPhase;
	float _sweepHeld;
	bool _sweepMeasured;

	readonly float[] _swArm = new float[SweepPhases];
	readonly float[] _swShoulderX = new float[SweepPhases];
	readonly float[] _swHandX = new float[SweepPhases];
	readonly float[] _swMiss = new float[SweepPhases];

	// Levers snapshotted at sweep start, restored at the end.
	bool _savedHands, _savedSphere, _savedLean, _savedNatural, _savedRise;
	int _savedSit;
	float _savedBand, _savedLeanF, _savedScale;
	string _savedLeanBone;

	/// <summary>The far target the sweep strains at: the FAR-RANK centre (e8 for White, e1 for
	/// Black). It was rank 5 when the lean was the only lever; the half-rise claims the far
	/// rank itself (harness residual ~2.4 at e8), so that is what the sweep must measure.</summary>
	static int SweepTarget( ChessSeat seat ) => ( seat == ChessSeat.White ? 7 : 0 ) * 8 + 4;

	void SweepTick( ChessStation station )
	{
		var avatar = LobbyPlayer.Local;
		var seat = ChessStation.ActiveSeat;
		if ( !avatar.IsValid() ) return;

		if ( !_sweepActive )
		{
			_sweepActive = true;
			_sweepPhase = 0;
			_sweepHeld = 0f;
			_sweepMeasured = false;

			_savedHands = SeatedHandSpikes.HandsOn;
			_savedSphere = SeatedHandSpikes.UseSphereClamp;
			_savedBand = SeatedHandSpikes.ReachBandX;
			_savedSit = SeatedHandSpikes.SitPose;
			_savedLean = SeatedHandSpikes.LeanOn;
			_savedLeanBone = SeatedHandSpikes.LeanBone;
			_savedLeanF = SeatedHandSpikes.LeanForward;
			_savedScale = SeatedHandSpikes.ArmScale;
			_savedNatural = SeatedHandSpikes.NaturalLean;
			_savedRise = SeatedHandSpikes.HalfRiseOn;

			// The controlled environment: hands live, no clamp, band wide open so every phase
			// strains at the SAME far target and the misses are comparable.
			SeatedHandSpikes.HandsOn = true;
			SeatedHandSpikes.UseSphereClamp = false;
			SeatedHandSpikes.ReachBandX = -999f;

			Log.Info( "[Gambit] sweep: measuring reach under every spike config (~7s) — hold still, don't stand up." );
			ConfigureSweepPhase( 0 );
		}

		_sweepHeld += Time.Delta;

		// Drive the hand at the far target every frame so the chase settles and the bone override
		// (lean/scale) re-applies before we read the bone.
		int target = SweepTarget( seat );
		var pose = new HandPose( HandPhase.Selected, target, target, false, 0f,
			TerryPose.GraspHeight, TerryPose.FingersGrasping, 1f, 0, false, 0f, 0f );
		avatar.ApplyHandPose( station, seat, pose );

		if ( !_sweepMeasured && _sweepHeld >= SweepSettle )
		{
			_sweepMeasured = true;
			MeasureSweepPhase( station, avatar, _sweepPhase );
		}

		if ( _sweepHeld >= SweepHold )
		{
			_sweepPhase++;
			_sweepHeld = 0f;
			_sweepMeasured = false;
			if ( _sweepPhase >= SweepPhases )
			{
				SweepReport( seat );
				RestoreSweepLevers();
				Sweep = false;
				_sweepActive = false;
				avatar.ClearHandPose();
			}
			else
			{
				ConfigureSweepPhase( _sweepPhase );
			}
		}
	}

	/// <summary>Set the FULL lever config for a phase (not incremental — so phase N never inherits
	/// phase N−1's sit pose or lean).</summary>
	static void ConfigureSweepPhase( int phase )
	{
		// Isolate each config: everything off/upright unless this phase turns it on. The manual
		// spike levers (Approach B/C) stay off — the sweep ladders up the SHIPPING stack:
		// nothing → the M14 lean → the half-rise → half-rise + sitting_02.
		SeatedHandSpikes.NaturalLean = false;
		SeatedHandSpikes.HalfRiseOn = false;
		SeatedHandSpikes.SitPose = 1;
		SeatedHandSpikes.LeanOn = false;
		SeatedHandSpikes.ArmScale = 1f;

		switch ( phase )
		{
			case 1: SeatedHandSpikes.NaturalLean = true; break;              // the seated lean alone (M14's ceiling)
			case 2: SeatedHandSpikes.NaturalLean = true;
				SeatedHandSpikes.HalfRiseOn = true; break;                   // the shipping default
			case 3: SeatedHandSpikes.NaturalLean = true;
				SeatedHandSpikes.HalfRiseOn = true;
				SeatedHandSpikes.SitPose = 2; break;                         // + the free pose lean
			// case 0: baseline — upright, seated, nothing.
		}
	}

	void MeasureSweepPhase( ChessStation station, LobbyPlayer avatar, int phase )
	{
		var body = avatar.GameObject.GetComponentInChildren<SkinnedModelRenderer>();
		if ( body == null ) return;

		// Arm length off the skeleton (real names first, un-suffixed fallback — same as the ruler).
		float arm = 0f;
		if ( TryBone( body, out var up, "arm_upper_R" )
			&& TryBone( body, out var lo, "arm_lower_R1", "arm_lower_R" )
			&& TryBone( body, out var hn, "hand_R1", "hand_R" ) )
			arm = ( up.Position - lo.Position ).Length + ( lo.Position - hn.Position ).Length;

		float shoulderX = TryBone( body, out var sh, "arm_upper_R" )
			? station.WorldTransform.PointToLocal( sh.Position ).x : 0f;

		float handX = 0f, miss = 0f;
		if ( body.TryGetBoneTransform( "hand_R", out var hand ) )
		{
			var local = station.WorldTransform.PointToLocal( hand.Position );
			handX = local.x;
			if ( avatar.LastHandIkTarget is { } asked )
				miss = ( local - asked ).Length;
		}

		_swArm[phase] = arm;
		_swShoulderX[phase] = shoulderX;
		_swHandX[phase] = handX;
		_swMiss[phase] = miss;
	}

	void SweepReport( ChessSeat seat )
	{
		string[] names = { "baseline", "lean only", "half-rise", "half-rise+sit2" };

		Log.Info( "── gambit_terry_sweep: reach to the FAR-RANK centre under each config ──" );
		Log.Info( seat == ChessSeat.White ? "   (White seat, target e8: handX must reach +17.1. Baseline dies ~−13.)"
			: "   (Black seat, target e1: handX must reach −17.1. Baseline dies ~+13.)" );
		Log.Info( "   phase           arm    shldrX    handX     miss" );
		for ( int i = 0; i < SweepPhases; i++ )
			Log.Info( $"   {names[i],-14} {_swArm[i],5:0.0}  {_swShoulderX[i],7:0.0}  {_swHandX[i],7:0.0}  {_swMiss[i],7:0.0}" );

		// Verdicts, as miss-reduction vs the upright baseline (positive = the hand got closer).
		Log.Info( "── reading it ──" );
		Log.Info( $"   lean-only gain:     {_swMiss[0] - _swMiss[1]:+0.0;-0.0}u  (M14's seated ceiling — expect ~5)" );
		Log.Info( $"   half-rise gain:     {_swMiss[0] - _swMiss[2]:+0.0;-0.0}u  ← THE VERDICT. Harness says e8 lands ~2.4 short;" );
		Log.Info( "                                a big gain with miss ≤ ~4 = the pelvis override carried the legs' and arm's solve" );
		Log.Info( "                                (the engine unknown). A gain of ~0 = the rise never moved the skeleton — check" );
		Log.Info( "                                gambit_terry_spikes and whether the terry visibly lifts off the chair." );
		Log.Info( $"   half-rise + sit=2:  {_swMiss[0] - _swMiss[3]:+0.0;-0.0}u  (is sitting_02's free lean still worth having?)" );
		Log.Info( "   Also LOOK while it runs: do the feet stay planted? Does the off hand find the table?" );
		Log.Info( "   (arm should read ~19.9, not 24; if it says 24 the bone names missed.)" );
	}

	void RestoreSweepLevers()
	{
		SeatedHandSpikes.HandsOn = _savedHands;
		SeatedHandSpikes.UseSphereClamp = _savedSphere;
		SeatedHandSpikes.ReachBandX = _savedBand;
		SeatedHandSpikes.SitPose = _savedSit;
		SeatedHandSpikes.LeanOn = _savedLean;
		SeatedHandSpikes.LeanBone = _savedLeanBone ?? "spine_2";
		SeatedHandSpikes.LeanForward = _savedLeanF;
		SeatedHandSpikes.ArmScale = _savedScale;
		SeatedHandSpikes.NaturalLean = _savedNatural;
		SeatedHandSpikes.HalfRiseOn = _savedRise;
	}

	/// <summary>First bone name that resolves — arm_lower_R1 vs arm_lower_R, hand_R1 vs hand_R —
	/// so a naming variant doesn't silently miss (mirrors TerryCommands.TryBone).</summary>
	static bool TryBone( SkinnedModelRenderer body, out Transform tx, params string[] names )
	{
		foreach ( var n in names )
			if ( body.TryGetBoneTransform( n, out tx ) )
				return true;
		tx = default;
		return false;
	}

	// ─────────────────────────────────────────────────────────────────────────────
	// SCHOLAR'S MATE DEMO — gambit_terry_scholars (M14 tuning aid).
	//
	// Drive THIS seat's own hand through White's four mating moves (e2e4 · f1c4 · d1h5 ·
	// h5xf7) on an idle board, then snap the board back a few seconds after the mate. Unlike
	// a real game this ignores turns, colours and legality — the point is to WATCH the hand
	// land relative to the moved piece, and to run the same four reaches from EITHER seat
	// (the Black seat is the hard reach). It reuses the shipping gesture machine (TerryPose)
	// and rides a REAL piece each move (the bounds-top grasp path the placement knobs tune),
	// so what you see is exactly what a real move does — only the trigger is fake.
	//
	// The board view is FEN-authoritative and dormant on an idle board, so we borrow its
	// piece meshes (PieceAt), track the moves in our OWN square map, and restore every piece
	// and un-hide every victim on the way out. Rip out with the rest of the M14 spikes.
	// ─────────────────────────────────────────────────────────────────────────────

	/// <summary>Toggled by <c>gambit_terry_scholars</c>. Local-seated station only.</summary>
	public static bool Scholars;

	static readonly (string Uci, bool Capture)[] ScholarsMoves =
	{
		("e2e4", false), ("f1c4", false), ("d1h5", false), ("h5f7", true),
	};
	const float ScholarsPause = 0.6f;  // rest between moves so each reads as its own gesture
	const float ScholarsHold = 2.5f;   // hold the mate before the board resets ("a few seconds after")

	bool _scActive;
	int _scMove;                       // moves completed / index into ScholarsMoves
	HandPose _scPose;
	GameObject[] _scBoard;             // our own square→piece map (the view's is FEN-only)
	readonly Dictionary<GameObject, Vector3> _scHome = new();   // original local pos of every touched piece
	readonly List<GameObject> _scHidden = new();                // victims disabled during the demo
	GameObject _scPiece;               // the piece the current move carries
	Vector3 _scFrom, _scTo;            // its local endpoints
	bool _scMoveStarted;               // the current move's gesture has begun animating
	float _scPauseHeld, _scHoldHeld;
	bool _scSavedHands;

	void ScholarsTick( ChessStation station )
	{
		var avatar = LobbyPlayer.Local;
		var seat = ChessStation.ActiveSeat;
		var ring = ChessRing.Instance;
		if ( ring == null || !avatar.IsValid() ) return;

		_view ??= station.GameObject.GetComponent<ChessBoardView>();
		if ( _view == null ) { StopScholars( "no board view on this station" ); return; }

		if ( !_scActive )
		{
			// e2 pawn (index 12) present = the view has built its piece set. Wait a frame if not.
			if ( _view.PieceAt( 12 ) == null ) return;

			_scActive = true;
			_scMove = 0;
			_scPose = HandPose.None;
			_scMoveStarted = false;
			_scPauseHeld = 0f;
			_scHoldHeld = 0f;
			_scHome.Clear();
			_scHidden.Clear();
			_scBoard = new GameObject[64];
			for ( int i = 0; i < 64; i++ ) _scBoard[i] = _view.PieceAt( i );

			_scSavedHands = SeatedHandSpikes.HandsOn;
			SeatedHandSpikes.HandsOn = true; // the demo drives the hand; the IK must be live

			LogScholarsKnobs( ring );
			BeginScholarsMove( ring );
		}

		// All four moves played: hold the mate, hand back on the table, then snap the board back.
		if ( _scMove >= ScholarsMoves.Length )
		{
			_scHoldHeld += Time.Delta;
			_scPose = TerryPose.Advance( _scPose, new HandInput( _scMove, null, false, false, true ), Time.Delta );
			avatar.ApplyHandPose( station, seat, _scPose );
			if ( _scHoldHeld >= ScholarsHold )
				StopScholars( "done — board reset" );
			return;
		}

		// Ply = move index + 1 fires a fresh Timeline the frame the index advances; SeatMoved is
		// always true so the gesture plays on THIS seat whatever colour legally moves — that is
		// the whole point of running it from the Black seat.
		var (uci, capture) = ScholarsMoves[_scMove];
		_scPose = TerryPose.Advance( _scPose,
			new HandInput( _scMove + 1, uci, SeatMoved: true, Capture: capture, GameLive: true ), Time.Delta );

		if ( _scPose.Animating )
		{
			_scMoveStarted = true;
			// Slide the real piece along the gesture's own Travel and let the wrist ride it — the
			// piece-child grasp path, which is exactly what the placement knobs tune.
			if ( _scPiece.IsValid() )
				_scPiece.LocalPosition = Vector3.Lerp( _scFrom, _scTo, _scPose.Travel );
			avatar.ApplyHandPose( station, seat, _scPose, _scPiece );
			return;
		}

		// Gesture finished (or not yet started this move): hand eases back, then — once it HAS
		// run — settle the piece exactly, pause a beat, and advance to the next move.
		avatar.ApplyHandPose( station, seat, _scPose );
		if ( !_scMoveStarted ) return;

		if ( _scPiece.IsValid() ) _scPiece.LocalPosition = _scTo;

		_scPauseHeld += Time.Delta;
		if ( _scPauseHeld < ScholarsPause ) return;

		_scMove++;
		_scPauseHeld = 0f;
		_scMoveStarted = false;
		if ( _scMove < ScholarsMoves.Length )
			BeginScholarsMove( ring );
	}

	/// <summary>Set up the piece for <c>ScholarsMoves[_scMove]</c>: find it in our own map, record
	/// its home for the reset, hide any victim, and re-point the map — the view's array is never
	/// touched.</summary>
	void BeginScholarsMove( ChessRing ring )
	{
		var (uci, capture) = ScholarsMoves[_scMove];
		if ( !TerryPose.TryParseUci( uci, out int from, out int to ) ) { _scPiece = null; return; }

		_scPiece = _scBoard[from];
		if ( !_scPiece.IsValid() )
		{
			Log.Warning( $"[Gambit] scholars: no piece on {uci[..2]} to move — the board isn't at the start position." );
			_scPiece = null;
			return;
		}

		if ( !_scHome.ContainsKey( _scPiece ) ) _scHome[_scPiece] = _scPiece.LocalPosition;
		_scFrom = _scPiece.LocalPosition;
		_scTo = ring.SquareLocalPosition( to & 7, to >> 3 );

		// A capture: hide the victim so the mover doesn't land on top of it (restored on reset).
		if ( capture && _scBoard[to].IsValid() )
		{
			var victim = _scBoard[to];
			if ( !_scHome.ContainsKey( victim ) ) _scHome[victim] = victim.LocalPosition;
			victim.Enabled = false;
			_scHidden.Add( victim );
		}

		_scBoard[to] = _scPiece;
		_scBoard[from] = null;
	}

	/// <summary>End the demo: put every moved piece back, un-hide every victim, release the hand,
	/// and restore the hands switch. Safe to call from any exit — toggle-off, standing up, or the
	/// natural finish — because the idle view will not do any of it for us.</summary>
	void StopScholars( string why )
	{
		foreach ( var kv in _scHome )
			if ( kv.Key.IsValid() ) kv.Key.LocalPosition = kv.Value;
		foreach ( var go in _scHidden )
			if ( go.IsValid() ) go.Enabled = true;
		_scHome.Clear();
		_scHidden.Clear();
		_scBoard = null;
		_scPiece = null;

		SeatedHandSpikes.HandsOn = _scSavedHands;
		LobbyPlayer.Local?.ClearHandPose();

		Scholars = false;
		_scActive = false;
		if ( why != null ) Log.Info( $"[Gambit] scholars: {why}." );
	}

	/// <summary>The whole point of the command: name every knob that decides where the hand ends
	/// up relative to the moved piece, its live value, and WHERE to edit it. Printed once at the
	/// start so it's on screen while the demo plays and you drag.</summary>
	static void LogScholarsKnobs( ChessRing ring )
	{
		Log.Info( "── gambit_terry_scholars: Terry plays the Scholar's Mate (his hand only) ──" );
		Log.Info( "   e2e4 · f1c4 · d1h5 · h5xf7 on THIS seat's hand — real pieces, real gesture, no" );
		Log.Info( "   turns/legality. Run it from the BLACK seat to test the far reach. Board resets after." );
		Log.Info( "── where the hand ends up RELATIVE TO THE MOVED PIECE ──" );
		Log.Info( "   wrist target while carrying = piece-bounds-TOP + GraspClearance + HandLift," );
		Log.Info( "   pulled back by HandGripOffset, angled by WristDrop/HandRoll. Tune these:" );
		Log.Info( "   EDITOR SLIDERS — TerryTuning GO in lobby.scene (drag live while this runs):" );
		Log.Info( $"     GraspClearance = {SeatedHandSpikes.GraspClearance:0.##}u   ← THE vertical offset above the piece's top" );
		Log.Info( $"     WristDrop      = {SeatedHandSpikes.WristDrop:0.#}°    grasp curl past the forearm bearing (cap {ring.HandPitch:0.#}°)" );
		Log.Info( $"     HandRoll       = {SeatedHandSpikes.HandRoll:0.#}°    elbow-out barrel twist (t-rex fix)" );
		Log.Info( $"     GestureSpeed   = {TerryPose.SpeedScale:0.##}×    whole-gesture tempo" );
		Log.Info( "   CODE DEFAULTS — ChessRing is runtime-built, so edit + hotload (not the inspector):" );
		Log.Info( $"     ring.HandLift       = {ring.HandLift:0.##}u   extra lift added on top of GraspClearance" );
		Log.Info( $"     ring.HandGripOffset = {ring.HandGripOffset}   wrist pull-back so the GRIP, not the palm, lands on the piece" );
		Log.Info( $"     ring.HandPitch      = {ring.HandPitch:0.#}°   nose-down cap WristDrop rides under" );
		Log.Info( "   NOTE: GraspHeight/HoverHeight/LiftHeight are INERT for a real move (grasp height is" );
		Log.Info( "   piece-relative, not board-surface) — don't chase them here. Consoles: gambit_terry_grasp" );
		Log.Info( "   <u> · gambit_terry_wristdrop <deg> · gambit_terry_roll <deg>." );
	}

	protected override void OnUpdate()
	{
		if ( Station is not { } station ) return;

		// SCHOLAR'S MATE DEMO (gambit_terry_scholars): scripted hand demo on an idle board. Same
		// local-seated gate as the others; StopScholars restores the board on any exit.
		if ( Scholars && ChessStation.Active == station && LobbyPlayer.Local.IsValid() )
		{
			ScholarsTick( station );
			return;
		}
		if ( _scActive )
			StopScholars( "stopped" );

		// THE DOCTOR (gambit_terry_doctor): the one-command knob turner. Same gates as the sweep.
		if ( Doctor && ChessStation.Active == station && LobbyPlayer.Local.IsValid() )
		{
			DoctorTick( station );
			return;
		}
		if ( _docActive )
		{
			_docActive = false;
			RestoreDoctorLevers();
			LobbyPlayer.Local?.ClearHandPose();
		}

		// M14 one-shot SWEEP (gambit_terry_sweep): steps every reach spike's config in turn,
		// settling between each, and dumps ONE table so the whole quantitative test pastes in one
		// block. Local-seated station only, same gate as the probe.
		if ( Sweep && ChessStation.Active == station && LobbyPlayer.Local.IsValid() )
		{
			SweepTick( station );
			return;
		}
		if ( _sweepActive )
		{
			_sweepActive = false;
			RestoreSweepLevers();
			LobbyPlayer.Local?.ClearHandPose();
		}

		// DEBUG PROBE (see above). Only the local-seated station drives it; when it turns
		// off (or you stand up) release the hand and fall back to the normal path.
		if ( Probe && ChessStation.Active == station && LobbyPlayer.Local.IsValid() )
		{
			ProbeTick( station );
			return;
		}
		if ( _probeActive )
		{
			_probeActive = false;
			_probeSquare = -1;
			RestoreProbeLevers();
			LobbyPlayer.Local?.ClearHandPose();
		}

		var src = Source;

		// Nobody here: no avatars to pose, and nothing to spend a frame on. Six empty
		// tables in a room must cost nothing.
		if ( !station.AnySeatTaken )
		{
			// BASELINE, not None — the ply has to be carried, exactly as everywhere else
			// here does it. HandPose.None resets the ply to 0 while _whiteMoved/_capture
			// stay latched at the last move's values, so a seat refilling before the
			// vacated table's [Sync] Phase has landed would make Advance's abandon rule
			// read "40 != 0, 40 > 0, this seat moved" and replay the PREVIOUS occupant's
			// whole pickup on the new sitter's hand. A narrow window — the local game
			// drives an empty table to Over/Idle — but it is one RTT wide and free to shut.
			_white = Baseline( src );
			_black = Baseline( src );
			return;
		}

		// The board changed hands (a lichess game engaged, or was handed back when it
		// ended). Every tracked value describes the OLD game, and comparing across the swap
		// would invent a transition out of the difference between two unrelated games — the
		// FEN jump between them is exactly the phantom move TableSounds warns about, and
		// here it would be a hand carrying a piece that never moved.
		//
		// ADOPT the new source's state rather than zeroing: a swap onto a game that is
		// already over must not re-trigger a pickup.
		if ( !ReferenceEquals( src, _lastSource ) )
		{
			_lastSource = src;
			_white = Baseline( src );
			_black = Baseline( src );
			_lastFen = src?.Game?.Fen;
			_lastPly = src?.Game?.MoveCount ?? 0;
			return;
		}

		Classify( src );

		Drive( station, ChessSeat.White, src, ref _white, ref _whitePlayer, ref _whiteId );
		Drive( station, ChessSeat.Black, src, ref _black, ref _blackPlayer, ref _blackId );
	}

	/// <summary>
	/// What just happened on this board, once, for both seats.
	///
	/// <para>Latched rather than recomputed per seat: the classification is a property of
	/// the BOARD, and a move lands on exactly one frame — the same frame both seats' poses
	/// see their ply change. Keeping the last answer means the flags are still right on that
	/// frame no matter which seat is driven first.</para>
	///
	/// <para><b>BoardDiff, not our own FEN reading.</b> It is the same classifier
	/// TableSounds uses, it is Sandbox-free and harness-proven against real games, and it
	/// gets capture right for en passant — where the victim isn't on the destination square
	/// and "is something standing there" quietly says no.</para></summary>
	void Classify( IBoardGame src )
	{
		if ( src?.Game is not { } game ) return;

		string fen = game.Fen;
		int ply = game.MoveCount;
		int plyBefore = _lastPly;

		var change = BoardDiff.Between( _lastFen, _lastPly, fen, ply,
			out bool whiteMoved, out bool capture );

		_lastFen = fen;
		_lastPly = ply;

		// Only a MOVE updates the flags. A rewind/reset carries no "who moved" at all, and
		// TerryPose abandons on it anyway.
		if ( change != BoardChange.Move ) return;

		_whiteMoved = whiteMoved;
		_capture = capture;

		// A ≥2-ply jump means BOTH seats moved inside one observation — the canonical case
		// is a premove firing on the very frame its trigger move applies, which happens ON
		// THE PREMOVER'S OWN MACHINE (the other machine sees the plies a network-gap
		// apart). LastMoveUci only names the reply, so without this the TRIGGER move's
		// hand never fired there (owner report: host never saw Black's arm). Latch the
		// earlier move for the other seat; Drive feeds it as that seat's own move.
		_prevUci = ply - plyBefore >= 2 ? game.UciFromEnd( 1 ) : null;
	}

	/// <summary>The OTHER seat's move when the last classify jumped ≥2 plies (see
	/// Classify), else null.</summary>
	string _prevUci;

	/// <summary>Take the source's ply as read, silently — so a hand never animates a move
	/// that was already played when we started watching. TableSounds.Baseline's rule, and
	/// the same reason: the first sight of a game is not an event in it.</summary>
	static HandPose Baseline( IBoardGame src ) =>
		HandPose.None with { Ply = src?.Game?.MoveCount ?? 0 };

	void Drive( ChessStation station, ChessSeat seat, IBoardGame src,
		ref HandPose pose, ref LobbyPlayer player, ref ulong cachedId )
	{
		var avatar = ResolveAvatar( station, seat, ref player, ref cachedId );

		// The BODY is already sitting — LobbyPlayer does that off the seat occupancy, with
		// no game involved. This only decides what the hands are doing on the board.
		if ( !station.SeatTaken( seat ) || src?.Game is not { } game )
		{
			pose = HandPose.None with { Ply = src?.Game?.MoveCount ?? pose.Ply };
			return;
		}

		// A hand only fires while its player is actually SITTING here — LobbyPlayer.SeatedAt,
		// not mere occupancy. Post-M17 you can keep a live game and roam, so the IK must not
		// reach across the room to a board you stood up from, nor fire on a SECOND table you
		// also hold. SeatedAt is the camera for the local player and the NETWORKED sitting
		// station for a proxy — so this holds on every screen, not just the one that stood
		// up. Clear the hand and rest the pose so a mid-gesture doesn't freeze mid-air.
		if ( avatar.IsValid()
			&& !( avatar.SeatedAt is { } sa && sa.Station == station && sa.Seat == seat ) )
		{
			avatar.ClearHandPose();
			pose = HandPose.None with { Ply = game.MoveCount };
			return;
		}

		// This seat played either the LAST classified move, or — on a ≥2-ply jump (a
		// premove firing the frame its trigger applies) — the EARLIER one (_prevUci).
		// Each seat gets its own move as its own gesture; the capture flag belongs to
		// the last move only (the reply is what took the piece).
		bool seatIsLast = _whiteMoved == ( seat == ChessSeat.White );
		pose = TerryPose.Advance( pose, new HandInput(
			Ply: game.MoveCount,
			LastMoveUci: seatIsLast ? game.LastMoveUci ?? src.LastMoveUci : _prevUci,
			SeatMoved: seatIsLast || _prevUci != null,
			Capture: seatIsLast && _capture,
			GameLive: src.Playing ), Time.Delta );

		if ( avatar.IsValid() )
		{
			// The hand is a CHILD of the piece (M14, owner decision): while this pose is
			// replaying a move, hand the driver the live performed-piece GameObject and
			// the wrist derives from ITS position — approaching while the piece holds,
			// glued above it while it slides. The old reverse channel (ReportHandCarry:
			// the piece riding the hand bone) is gone; the view's slide is the one clock.
			GameObject piece = null;
			if ( pose.Animating )
			{
				_view ??= station.GameObject.GetComponent<ChessBoardView>();
				piece = _view?.PerformedPiece( seat == ChessSeat.White );
			}
			avatar.ApplyHandPose( station, seat, pose, piece );
		}
	}

	/// <summary>The board view on this same station, resolved lazily — the source of the
	/// performed piece the hand rides (see <see cref="ChessBoardView.PerformedPiece"/>).</summary>
	ChessBoardView _view;

	/// <summary>
	/// <c>gambit_terry_net</c>'s per-station dump: the whole hand chain AS THIS MACHINE
	/// sees it. Exists because "a joined client doesn't see any of the animation" is the
	/// gambit_tv situation again — none of this chain is visible from
	/// outside, and a driver that never fires looks identical to one that isn't wired up.
	/// Run it on the machine whose view is wrong and read which link is dead.
	/// </summary>
	public void DumpNet()
	{
		string refs = $"refs: Station={( Station != null ? "ok" : "NULL" )}"
			+ $" Controller={( Controller != null ? "ok" : "NULL" )}"
			+ $" Lichess={( Lichess != null ? "ok" : "NULL" )}"
			+ $" view={( _view != null ? "ok" : "unresolved" )}";

		if ( Station is not { } station )
		{
			Log.Info( $"── {GameObject.Name}: STATION REF NULL — the [Property] wiring did not reach this client. {refs}" );
			return;
		}

		if ( !station.AnySeatTaken )
		{
			Log.Info( $"── {GameObject.Name}: empty ({refs})" );
			return;
		}

		var src = Source;
		var game = src?.Game;
		Log.Info( $"── {GameObject.Name} — {refs}" );
		Log.Info( $"   source={( src == null ? "NULL" : src.GetType().Name )}"
			+ $"  game={( game == null ? "NULL" : $"ply {game.MoveCount}, last {game.LastMoveUci ?? "-"}" )}"
			+ $"  playing={src?.Playing}  latched: lastPly={_lastPly} wMoved={_whiteMoved} cap={_capture}" );
		// The spectator mirror is the newest link and the one a joiner lives or dies by.
		int mirrorPlies = string.IsNullOrEmpty( Lichess?.MirrorMoves )
			? 0 : Lichess.MirrorMoves.Split( ' ', System.StringSplitOptions.RemoveEmptyEntries ).Length;
		Log.Info( $"   lichess: engaged={Lichess?.Engaged}  mirroring={Lichess?.Mirroring}"
			+ $"  mirrorLive={Lichess?.MirrorLive}  mirrorPlies={mirrorPlies}" );
		Log.Info( $"   seats: W={station.WhiteSteamId} '{station.WhiteName}'  B={station.BlackSteamId} '{station.BlackName}'" );
		DumpSeat( station, ChessSeat.White, _white );
		DumpSeat( station, ChessSeat.Black, _black );
	}

	void DumpSeat( ChessStation station, ChessSeat seat, in HandPose pose )
	{
		LobbyPlayer avatar = null;
		ulong cached = 0;
		avatar = ResolveAvatar( station, seat, ref avatar, ref cached );

		string who = !avatar.IsValid() ? "NOT RESOLVED"
			: $"{( avatar.IsProxy ? "proxy" : "local" )}, body={( avatar.HasBody ? "ok" : "MISSING" )},"
				+ $" rise={avatar.RiseAppliedDebug:0.0}u";
		Log.Info( $"   {seat}: id={station.SeatSteamId( seat )}  avatar: {who}" );
		Log.Info( $"      pose: {pose.Phase} from={pose.FromSquare} to={pose.ToSquare}"
			+ $" travel={pose.Travel:0.00} w={pose.Weight:0.00} ply={pose.Ply} since={pose.Since:0.00}" );
	}

	/// <summary>
	/// Whose avatar is in this seat.
	///
	/// <para>The local player is checked FIRST and by <c>ChessStation.Active</c>, not by
	/// SteamId — that covers the optimistic-claim window between pressing E and the host's
	/// [Sync] landing, which is exactly when you are looking at your own hands.</para>
	///
	/// <para>Otherwise scan for the owner, and cache on the SteamId changing rather than
	/// re-scanning every frame: GetAllComponents walks the scene, and this runs per seat per
	/// station.</para>
	/// </summary>
	LobbyPlayer ResolveAvatar( ChessStation station, ChessSeat seat,
		ref LobbyPlayer player, ref ulong cachedId )
	{
		bool localHere = ChessStation.Active == station && LobbyPlayer.Local.IsValid();

		if ( localHere && ChessStation.ActiveSeat == seat )
			return LobbyPlayer.Local;

		ulong id = station.SeatSteamId( seat );
		if ( id == 0 )
		{
			player = null;
			cachedId = 0;
			return null;
		}

		// We're sitting at this table, but in the OTHER seat — so a seat here still carrying
		// our SteamId is the host not having processed our SwitchActiveSeat yet. Believe
		// Active/ActiveSeat (the local truth) over the stale [Sync], or for one round trip we
		// resolve to BOTH seats and drive one avatar's hand twice in a frame.
		if ( localHere && id == ( Connection.Local?.SteamId ?? 0 ) )
		{
			player = null;
			cachedId = 0;
			return null;
		}

		if ( id == cachedId && player.IsValid() ) return player;

		cachedId = id;
		player = null;
		foreach ( var p in Scene.GetAllComponents<LobbyPlayer>() )
		{
			if ( p.Network.Owner?.SteamId != id ) continue;
			player = p;
			break;
		}
		return player;
	}
}
