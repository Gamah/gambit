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
	bool _savedHands, _savedSphere, _savedLean, _savedNatural;
	int _savedSit;
	float _savedBand, _savedLeanF, _savedScale;
	string _savedLeanBone;

	/// <summary>The far target the sweep strains at: a mid-far centre square (rank 5 for White,
	/// rank 4 for Black — symmetric), well past the ~rank-2 baseline ceiling so a lean that
	/// composes visibly closes the gap.</summary>
	static int SweepTarget( ChessSeat seat ) => ( seat == ChessSeat.White ? 4 : 3 ) * 8 + 4;

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
		// spike levers (Approach B/C) stay off — the sweep is now about the shipping default (the
		// natural graded lean) and the free sit-pose gain, not the distortion hacks.
		SeatedHandSpikes.NaturalLean = false;
		SeatedHandSpikes.SitPose = 1;
		SeatedHandSpikes.LeanOn = false;
		SeatedHandSpikes.ArmScale = 1f;

		switch ( phase )
		{
			case 1: SeatedHandSpikes.NaturalLean = true; break;                            // the shipping default
			case 2: SeatedHandSpikes.SitPose = 2; break;                                   // sitting_02 alone
			case 3: SeatedHandSpikes.NaturalLean = true; SeatedHandSpikes.SitPose = 2; break; // both together
			// case 0: baseline — upright, no lean.
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
		string[] names = { "baseline", "natural lean", "sit=2", "natural+sit=2" };

		Log.Info( "── gambit_terry_sweep: reach to a far-centre square under each config ──" );
		Log.Info( seat == ChessSeat.White ? "   (White seat; handX rises toward the board as reach extends. rank5≈+2.4)"
			: "   (Black seat; handX falls toward the board as reach extends. rank4≈−2.4)" );
		Log.Info( "   phase           arm    shldrX    handX     miss" );
		for ( int i = 0; i < SweepPhases; i++ )
			Log.Info( $"   {names[i],-14} {_swArm[i],5:0.0}  {_swShoulderX[i],7:0.0}  {_swHandX[i],7:0.0}  {_swMiss[i],7:0.0}" );

		// Verdicts, as miss-reduction vs the upright baseline (positive = the hand got closer).
		Log.Info( "── reading it ──" );
		Log.Info( $"   natural lean gain:  {_swMiss[0] - _swMiss[1]:+0.0;-0.0}u  ← the shipping default; the terry leans in to reach" );
		Log.Info( $"   sit=2 alone gain:   {_swMiss[0] - _swMiss[2]:+0.0;-0.0}u  (free, straight from the pose)" );
		Log.Info( $"   natural + sit=2:    {_swMiss[0] - _swMiss[3]:+0.0;-0.0}u  closer to the far target" );
		Log.Info( "   Far squares beyond even this get the piece-slide for the last bit — a natural motion, not a miss." );
		Log.Info( "   (arm should read ~19.9, not 24; if it says 24 the bone names missed — run gambit_terry_bones.)" );
		Log.Info( "   The one thing this can't score is whether the lean READS as leaning — turn it on and LOOK: gambit_terry_hands." );
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

	protected override void OnUpdate()
	{
		if ( Station is not { } station ) return;

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

		var change = BoardDiff.Between( _lastFen, _lastPly, fen, ply,
			out bool whiteMoved, out bool capture );

		_lastFen = fen;
		_lastPly = ply;

		// Only a MOVE updates the flags. A rewind/reset carries no "who moved" at all, and
		// TerryPose abandons on it anyway.
		if ( change != BoardChange.Move ) return;

		_whiteMoved = whiteMoved;
		_capture = capture;
	}

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

		var packed = avatar.IsValid() ? avatar.HandState : -1;
		LobbyPlayer.UnpackHand( packed, out int hover, out int selected );

		pose = TerryPose.Advance( pose, new HandInput(
			Hover: hover,
			Selected: selected,
			Ply: game.MoveCount,
			LastMoveUci: game.LastMoveUci ?? src.LastMoveUci,
			SeatMoved: _whiteMoved == ( seat == ChessSeat.White ),
			Capture: _capture,
			GameLive: src.Playing ), Time.Delta );

		if ( avatar.IsValid() )
		{
			avatar.ApplyHandPose( station, seat, pose );

			// The piece rides the hand (M14): while this pose is replaying a move, tell
			// the view so the moved piece tracks the hand bone instead of its own slide.
			// Gated on the hands actually RENDERING — a piece must never ride a hand the
			// kill switches turned invisible.
			if ( pose.Animating && SeatedHandSpikes.HandsOn
				&& ChessRing.Instance is { TerrySeated: true } )
			{
				_view ??= station.GameObject.GetComponent<ChessBoardView>();
				_view?.ReportHandCarry( pose, avatar );
			}
		}
	}

	/// <summary>The board view on this same station, resolved lazily — it is the other
	/// half of the hand-carry handshake (see <see cref="ChessBoardView.ReportHandCarry"/>).</summary>
	ChessBoardView _view;

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
