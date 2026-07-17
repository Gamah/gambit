using Gambit.Chess;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Why is the seated terry doing that? (M13.) Same rationale as <c>gambit_tv</c> and
/// <c>gambit_music</c>: a feature that never fires looks exactly like one that isn't wired
/// up, and NONE of this chain is visible from outside — which avatar resolved, what its
/// seat thinks, what crossed the wire, what the state machine made of it.
///
/// <para><b>But the reason it earns its keep is the RULER.</b> M13's own risk list says the
/// sit pose is "the biggest unknown by far", and that five numbers rest on estimates of
/// human proportion scaled to a 72-unit citizen: seated eye height, hip offset, torso
/// depth, arm reach. The skeleton lives in the compiled model, which is in neither repo —
/// so they could not be derived on the dev host and were guessed. <c>gambit_terry</c> reads
/// the real bones off a live seated citizen and prints them in STATION-LOCAL space, next to
/// the constants that guessed at them. Every [GUESS] in M13 becomes a measurement the first
/// time anyone sits down and runs this.</para>
/// </summary>
public static class TerryCommands
{
	[ConCmd( "gambit_terry" )]
	public static void TerryStatus()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene == null )
		{
			Log.Warning( "[Gambit] no active scene." );
			return;
		}

		var ring = ChessRing.Instance;
		if ( ring == null )
		{
			Log.Warning( "[Gambit] no ChessRing — the world hasn't built. Nothing else here can be true." );
			return;
		}

		Log.Info( $"[Gambit] TerrySeated={ring.TerrySeated} (false = the kill switch: no pose, no trim, "
			+ $"plant at the walk-up spot — the whole of M13 off)" );
		Log.Info( $"[Gambit] camera: SeatEyeBlend={ring.SeatEyeBlend:0.##} "
			+ $"(1 = today's orbit anchor exactly, 0 = the seated eye at back {ring.SeatEyeBack}, "
			+ $"height {ring.SeatEyeHeight}) · SeatOrbitRadius={ring.SeatOrbitRadius} SeatPitch={ring.SeatPitch}" );
		Log.Info( $"[Gambit] plant: SeatSitBack={ring.SeatSitBack} SeatSitZ={ring.SeatSitZ} "
			+ $"SitOffsetHeight={ring.SitOffsetHeight} (inches, ±12 hard-clamped by the animgraph)" );
		Log.Info( $"[Gambit] chair: centre={ring.ChairCenterX:0.##} (= SeatSitBack — where the person IS) "
			+ $"seat {ring.ChairSeatDepth}x{ring.ChairSeatWidth} top z={ring.ChairSeatTopZ} "
			+ $"· tuck {ring.ChairTuckInset:0.##} of max {ring.ChairMaxTuck:0.##}" );
		Log.Info( $"[Gambit] hands: idle at station-local ({ring.HandIdleX}, ±{ring.HandIdleY}, {ring.HandIdleZ})" );

		int stations = 0;
		foreach ( var station in scene.GetAllComponents<ChessStation>() )
		{
			stations++;
			DumpStation( ring, station );
		}

		if ( stations == 0 )
			Log.Warning( "[Gambit] no ChessStations in the scene." );

		DumpRuler( ring );
	}

	static void DumpStation( ChessRing ring, ChessStation station )
	{
		bool any = station.AnySeatTaken;
		Log.Info( $"── {station.GameObject.Name} {( any ? "" : "(empty — SeatedTerry early-outs, nothing runs)" )}" );
		if ( !any ) return;

		var terry = station.Components.Get<SeatedTerry>();
		var chairs = station.Components.GetAll<StationChair>();
		int chairCount = 0;
		foreach ( var _ in chairs ) chairCount++;
		Log.Info( $"   SeatedTerry={( terry == null ? "MISSING — no hands will ever move" : "ok" )}"
			+ $" · StationChair x{chairCount}{( chairCount == 2 ? "" : " — EXPECTED 2" )}" );

		foreach ( var seat in new[] { ChessSeat.White, ChessSeat.Black } )
		{
			ulong id = station.SeatSteamId( seat );
			bool mine = ChessStation.Active == station && ChessStation.ActiveSeat == seat;
			if ( id == 0 && !mine ) { Log.Info( $"   {seat}: free" ); continue; }

			var avatar = FindAvatar( station, seat, mine );
			string who = station.SeatName( seat ) ?? "?";

			if ( avatar == null )
			{
				Log.Warning( $"   {seat}: {who} ({id}) — NO AVATAR RESOLVED. The seat is taken but no "
					+ "LobbyPlayer owns that SteamId, so nothing poses it." );
				continue;
			}

			LobbyPlayer.UnpackHand( avatar.HandState, out int hover, out int selected );
			var body = avatar.GameObject.GetComponentInChildren<SkinnedModelRenderer>();
			int sit = body?.GetInt( "sit" ) ?? -1;
			var ctrl = avatar.Components.Get<PlayerController>();

			Log.Info( $"   {seat}: {who} ({id}){( mine ? " [me]" : "" )}{( avatar.IsProxy ? " [proxy]" : "" )}" );
			Log.Info( $"      sit={sit} ({( sit == 1 ? "SITTING" : "NOT sitting — if this is a proxy, its "
				+ "PlayerController is stomping it: MoveMode.OnUpdateAnimatorState does Set(\"sit\", 0) every frame" )})"
				+ $" · controller {( ctrl == null ? "(none)" : ctrl.Enabled ? "enabled" : "disabled" )}"
				+ $" animator={( ctrl?.UseAnimatorControls.ToString() ?? "?" )}" );
			Log.Info( $"      HandState={avatar.HandState} -> hover={Name( hover )} selected={Name( selected )}"
				+ $"{( avatar.IsProxy ? " (over the wire)" : " (ours, published locally)" )}" );

			var local = station.WorldTransform.PointToLocal( avatar.WorldPosition );
			Log.Info( $"      planted at station-local ({local.x:0.##}, {local.y:0.##}, {local.z:0.##})"
				+ $" — expected (±{ring.SeatSitBack}, 0, {ring.SeatSitZ})" );
		}
	}

	/// <summary>
	/// <b>The ruler.</b> Read the real bones off whichever seated citizen we can find and
	/// print them in station-local space beside the constants that guessed them.
	///
	/// <para>Sit down, run this, and M13's five guesses stop being guesses. The pelvis's z
	/// IS the seat height the pose wants (dial ChairSeatTopZ or SitOffsetHeight until it
	/// matches the pad); the eye's x/z IS SeatEyeBack/SeatEyeHeight; the hand's reach is
	/// what says whether IK can make rank 8.</para>
	///
	/// <para>Bones are read here and NOT by the camera, deliberately — see SeatEyeBack. A
	/// one-shot console read has no ordering hazard; a camera welded to a breathing,
	/// blinking head bone makes the board swim.</para>
	/// </summary>
	static void DumpRuler( ChessRing ring )
	{
		var station = ChessStation.Active;
		LobbyPlayer avatar = null;
		ChessSeat seat = ChessSeat.White;

		if ( station != null )
		{
			avatar = LobbyPlayer.Local;
			seat = ChessStation.ActiveSeat;
		}
		else
		{
			// Not seated ourselves — measure anyone who is.
			foreach ( var s in Sandbox.Game.ActiveScene.GetAllComponents<ChessStation>() )
			{
				foreach ( var st in new[] { ChessSeat.White, ChessSeat.Black } )
				{
					if ( s.SeatSteamId( st ) == 0 ) continue;
					var a = FindAvatar( s, st, false );
					if ( a == null ) continue;
					station = s;
					seat = st;
					avatar = a;
					break;
				}
				if ( avatar != null ) break;
			}
		}

		if ( avatar == null || station == null )
		{
			Log.Info( "[Gambit] ruler: nobody is sitting anywhere, so there is nothing to measure. "
				+ "Sit down and run this again — it is what turns M13's sit-pose guesses into numbers." );
			return;
		}

		var body = avatar.GameObject.GetComponentInChildren<SkinnedModelRenderer>();
		if ( body == null )
		{
			Log.Warning( "[Gambit] ruler: that avatar has no SkinnedModelRenderer." );
			return;
		}

		float side = seat == ChessSeat.White ? -1f : +1f;
		Log.Info( $"── ruler: measuring {seat}'s citizen against the guesses ──" );

		// "pelvis" and "hand_R" are CONFIRMED — the engine's own SkinnedModelRendererTests
		// resolve both on this exact model. The rest are educated guesses at the citizen's
		// naming, so Measure prints a miss rather than lying, and gambit_terry_bones below
		// lists the real names if any of these come back empty.
		Measure( station, body, "pelvis", $"the seat height the POSE wants. Compare ChairSeatTopZ={ring.ChairSeatTopZ}"
			+ " — that gap is exactly what SitOffsetHeight (±12) exists to close." );
		Measure( station, body, "head", "vs the frame's bottom edge. M13 guessed the head is out of frame by 0.37." );
		Measure( station, body, "eyes", $"THE eye. Compare SeatEyeBack={ring.SeatEyeBack} SeatEyeHeight={ring.SeatEyeHeight}"
			+ " (|x| and z) — M13 guessed both from human proportion." );
		Measure( station, body, "hand_R", $"the working hand. Idle target is ({side * ring.HandIdleX:0.#}, "
			+ $"{side * ring.HandIdleY:0.#}, {ring.HandIdleZ})." );
		Measure( station, body, "ankle_R", "feet. At SeatSitZ=0 these should be near the floor; if they dangle, "
			+ "the pose's own seat height is above our pad." );

		Log.Info( "   (x is toward the board — the near rank is at ±17.06, the tabletop's edge at ±30; "
			+ "z is height — tabletop surface 30, underside 27, chair pad 18.)" );
		Log.Info( "   Any bone above reading \"no such bone\" is my guess at the name, not a missing bone: "
			+ "run gambit_terry_bones for the real list." );
	}

	static void Measure( ChessStation station, SkinnedModelRenderer body, string bone, string why )
	{
		if ( !body.TryGetBoneTransform( bone, out var tx ) )
		{
			Log.Info( $"   {bone,-8} — no such bone on this model" );
			return;
		}

		var l = station.WorldTransform.PointToLocal( tx.Position );
		Log.Info( $"   {bone,-8} station-local ({l.x,7:0.##}, {l.y,6:0.##}, {l.z,6:0.##})   {why}" );
	}

	/// <summary>Every bone on the local citizen, with its height. The escape hatch for the
	/// ruler above: only "pelvis" and "hand_R" are names this repo can prove, so if the
	/// others miss, this says what they are really called — once, rather than by guessing
	/// again next session.</summary>
	[ConCmd( "gambit_terry_bones" )]
	public static void TerryBones()
	{
		var body = LobbyPlayer.Local?.GameObject.GetComponentInChildren<SkinnedModelRenderer>();
		if ( body?.Model is not { } model )
		{
			Log.Warning( "[Gambit] no local citizen to read bones off." );
			return;
		}

		var station = ChessStation.Active;
		Log.Info( $"[Gambit] {model.Bones.AllBones.Count} bones on {model.Name}"
			+ $" ({( station == null ? "STANDING — sit down for seated numbers" : "seated, station-local" )}):" );

		foreach ( var bone in model.Bones.AllBones )
		{
			if ( !body.TryGetBoneTransform( bone.Name, out var tx ) ) continue;
			var p = station == null ? body.WorldTransform.PointToLocal( tx.Position )
				: station.WorldTransform.PointToLocal( tx.Position );
			Log.Info( $"   {bone.Name,-24} ({p.x,7:0.##}, {p.y,7:0.##}, {p.z,7:0.##})" );
		}
	}

	static LobbyPlayer FindAvatar( ChessStation station, ChessSeat seat, bool mine )
	{
		if ( mine && LobbyPlayer.Local.IsValid() ) return LobbyPlayer.Local;

		ulong id = station.SeatSteamId( seat );
		if ( id == 0 ) return null;

		foreach ( var p in Sandbox.Game.ActiveScene.GetAllComponents<LobbyPlayer>() )
			if ( p.Network.Owner?.SteamId == id )
				return p;

		return null;
	}

	static string Name( int square )
	{
		if ( square is < 0 or > 63 ) return "none";
		return $"{(char)( 'a' + ( square & 7 ) )}{( square >> 3 ) + 1}";
	}
}
