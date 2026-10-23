using System;
using System.Collections.Generic;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// The seated player's controls in VR: a display board beside the table, and the game's verbs
/// as plates on the tabletop in front of them.
///
/// <para><b>This is what replaces the desktop HUD on a quad.</b> That quad is a 1920x1080
/// screen layout with edge-anchored rows, a crosshair and keyboard hints, and at a size big
/// enough to read it covers most of the view — <see cref="VrHud"/> still carries it, parked,
/// for anything that lives nowhere else (the pre-game setup flows, the lichess start, the
/// promotion picker), and <c>gambit_vr_hud</c> brings it back.</para>
///
/// <para><b>Reading and doing are split, because they want opposite things.</b> What a player
/// reads wants to be a glance away and never in front of the board: a wall-board-shaped panel
/// off to the seat's right (<see cref="ChessRing.VrSeatBoardLocal"/>), built the way the wall
/// boards are. What a player presses wants to be where their hands already are and big enough
/// to hit with a beam: a flat plate on the tabletop, which is the one world control this room
/// already proves (<see cref="SeatSettingsPlate"/>, issue #28).</para>
///
/// <para><b>Client-local and parented to NOTHING</b>, both for the reason the settings plate
/// records: a <see cref="ChessStation"/> is NetworkSpawned, so a child of one rides the host's
/// snapshot — transform and enabled state included. Driven in world space off the station's
/// transform instead.</para>
///
/// <para>Built lazily by <see cref="VrRig"/> rather than once at spawn, so a code hotload into
/// a running session still gets it (docs/engine.md).</para>
/// </summary>
public sealed class VrTableUi : Component
{
	public static VrTableUi Instance { get; private set; }

	/// <summary>Whether the seated board and plates are drawn at all (<c>gambit_vr_board</c>).
	/// On: they are the VR game UI, not an instrument.</summary>
	public static bool Shown { get; private set; } = true;

	/// <summary>A multiplier on <see cref="WallBoardGeometry.TableBoardScale"/>, so the board
	/// can be dialled from inside the headset without touching the shared geometry.</summary>
	public static float BoardScale { get; set; } = 1f;

	/// <summary>Why there is no board, when there is no board. A per-frame throw in a VR
	/// component is invisible in a headset: the thing is simply absent, which looks exactly
	/// like never having been asked for. The state board reads this.</summary>
	public static string LastError { get; private set; }

	/// <summary>The one client-local root everything here hangs under: unparented, NotSaved,
	/// NotNetworked. Never a child of the player or of the station — both are networked, and a
	/// child of a networked object rides the host's snapshot (docs/engine.md, issue #12).</summary>
	GameObject _root;

	GameObject _board;
	readonly List<Gambit.UI.SeatSettingsPanel> _plates = new();
	readonly List<GameObject> _plateObjects = new();

	/// <summary>The build failed once, so don't try again every frame: the only way it fails is
	/// a missing box model, which is a permanent condition, and retrying would spin a
	/// create/destroy pair per frame forever. Same rule as the settings plate.</summary>
	bool _buildFailed;

	/// <summary>What each plate does. Allocated once per verb — a lambda handed to a panel
	/// every frame is a delegate allocation every frame.</summary>
	Action _standUp;
	Action _resign;
	Action _draw;
	Action _newGame;
	Action _menu;

	protected override void OnStart() => Instance = this;

	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;

		_root?.Destroy();
		_root = null;
		_board = null;
		_plateObjects.Clear();
		_plates.Clear();
	}

	protected override void OnUpdate()
	{
		try
		{
			Drive();
			LastError = null;
		}
		catch ( Exception e )
		{
			LastError = e.Message;
		}
	}

	void Drive()
	{
		// Only in a headset, only for the player sitting at a chess seat. The desktop has its
		// own HUD and its own settings plate; nothing here is an improvement on either.
		var station = ChessStation.Active;
		bool show = Shown && Sandbox.Game.IsRunningInVR && station != null;

		if ( !show )
		{
			SetEnabled( false );
			return;
		}

		var ring = ChessRing.Instance;
		if ( ring == null || _buildFailed ) return;
		if ( !_board.IsValid() && !Build( ring ) ) return;

		SetEnabled( true );
		PlaceBoard( ring, station );
		DriveVerbs( ring, station );
	}

	void SetEnabled( bool on )
	{
		if ( _board.IsValid() ) _board.Enabled = on;
		if ( on ) return;

		// Plates are enabled per-verb while shown, so switching off is the only place that
		// needs to sweep them all.
		foreach ( var go in _plateObjects )
			if ( go.IsValid() ) go.Enabled = false;
	}

	void PlaceBoard( ChessRing ring, ChessStation station )
	{
		var (localPos, localRot) = ring.VrSeatBoardLocal( ChessStation.ActiveSeat );

		// World space every frame, off the station's own transform — the ring SLIDES when the
		// admin changes the board count, and an unparented object has nothing to carry it
		// along. A seat switch is exactly the moment this must not lag a frame behind the view.
		_board.WorldPosition = station.WorldPosition + station.WorldRotation * localPos;
		_board.WorldRotation = station.WorldRotation * localRot;
		_board.WorldScale = WallBoardGeometry.TableBoardScale * BoardScale;
	}

	/// <summary>Fill the plates for the state the board is in.
	///
	/// <para><b>Each verb keeps its own slot whether or not it is showing.</b> A plate that
	/// slid along the strip as the game changed state would move the resign plate under the
	/// hand that was reaching for "stand up" — so the slots are fixed by role and an
	/// inapplicable verb leaves a bare patch of table rather than closing the gap.</para></summary>
	void DriveVerbs( ChessRing ring, ChessStation station )
	{
		var local = Gambit.Game.LocalGameController.For( station );
		var lichess = Gambit.Game.LichessGameController.For( station );
		var relay = Gambit.Game.RelayGameController.For( station );
		var src = Gambit.Game.BoardGame.Source( local, lichess, relay );

		// Slot 0 — stand up. Always there: it is the way out of the seat, and the one control
		// a player must never have to hunt for. Standing up does NOT resign (M17).
		SetPlate( ring, station, 0, "STAND UP", _standUp ??= () => LobbyPlayer.Local?.RequestLeave() );

		// Slot 1 — clear a finished game, or offer/accept a draw in a live one. The draw plate
		// doubles as accept, because lichess's draw/yes does and the local table copies it
		// rather than inventing a second vocabulary for the same gesture.
		if ( src is { GameOver: true } )
			SetPlate( ring, station, 1, "NEW GAME", _newGame ??= NewGame );
		else if ( src is { Playing: true } && ShowOffer( src.DrawPending, src.DrawOffered ) )
			SetPlate( ring, station, 1, src.DrawOffered ? "ACCEPT DRAW" : "OFFER DRAW",
				_draw ??= () => Source()?.OfferDraw() );
		else
			// An idle table: the controls that start a game — the time control, Ready, the four
			// lichess flows — live on the desktop quad and nowhere else, and the quad is parked.
			// This is how a headset player reaches them, in the one state where they are what
			// they need; a console command is no use to someone wearing a headset.
			SetPlate( ring, station, 1, VrHud.Shown ? "HIDE MENU" : "GAME MENU",
				_menu ??= () => VrHud.Shown = !VrHud.Shown );

		// Slot 2 — resign, in its own two-stage press (M17): the second press inside the
		// confirm window is the one that resigns, and the plate says which press it is on.
		if ( src is { Playing: true } )
			SetPlate( ring, station, 2,
				( LobbyPlayer.Local?.LeaveArmed ?? false ) ? "SURE? RESIGN" : "RESIGN",
				_resign ??= Resign );
		else
			SetPlate( ring, station, 2, null, null );
	}

	/// <summary>Should an offer plate show at all? Hidden only while we are purely WAITING —
	/// our own offer stands and there is nothing of theirs to answer, so the press would do
	/// nothing and a control whose press does nothing is a lie. The <c>offered</c> half is what
	/// stops that hiding too much: with offers standing both ways, ours pending must not hide
	/// the plate that accepts theirs. Mirrors GameHud's own rule.</summary>
	static bool ShowOffer( bool pending, bool offered ) => !pending || offered;

	static Gambit.Game.IBoardGame Source() => Gambit.Game.BoardGame.Source(
		Gambit.Game.LocalGameController.For( ChessStation.Active ),
		Gambit.Game.LichessGameController.For( ChessStation.Active ),
		Gambit.Game.RelayGameController.For( ChessStation.Active ) );

	/// <summary>Drop a finished game and return the table to setup without standing up —
	/// GameHud's own NewGame/LeaveRelay pair, which differ only in who owns the finished
	/// game.</summary>
	static void NewGame()
	{
		var station = ChessStation.Active;
		if ( Gambit.Game.RelayGameController.For( station ) is { Engaged: true } relay )
		{
			relay.Disengage();
			return;
		}

		Gambit.Game.LichessGameController.For( station )?.DismissFinished();
		Gambit.Game.LocalGameController.For( station )?.RequestNewGame();
	}

	/// <summary>Resign whichever game owns the board: the relay resigns through gamchess,
	/// everything else through <see cref="LobbyPlayer.Resign"/>'s two-stage confirm.</summary>
	static void Resign()
	{
		if ( Gambit.Game.RelayGameController.For( ChessStation.Active ) is { Engaged: true } relay )
			relay.Resign();
		else
			LobbyPlayer.Local?.Resign();
	}

	/// <summary>Put one verb on one slot, or leave the slot bare. <paramref name="text"/> null
	/// disables the plate rather than blanking it — an empty plate is a control that looks
	/// broken.</summary>
	void SetPlate( ChessRing ring, ChessStation station, int slot, string text, Action onClick )
	{
		if ( slot >= _plateObjects.Count ) return;

		var go = _plateObjects[slot];
		if ( !go.IsValid() ) return;

		if ( text == null )
		{
			go.Enabled = false;
			return;
		}

		go.Enabled = true;

		var (localPos, localRot) = ring.SeatVerbPlateLocal( ChessStation.ActiveSeat, slot );
		go.WorldPosition = station.WorldPosition + station.WorldRotation * localPos;
		go.WorldRotation = station.WorldRotation * localRot;

		var panel = _plates[slot];
		if ( !panel.IsValid() ) return;

		panel.Text = text;
		panel.OnClick = onClick;
	}

	bool Build( ChessRing ring )
	{
		_root = new GameObject( true, "VR Table UI" )
		{
			Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked
		};
		_root.NetworkMode = NetworkMode.Never;

		_board = new GameObject( true, "VR Seat Board" ) { Parent = _root };
		_board.AddComponent<WorldPanel>();
		_board.AddComponent<Gambit.UI.VrSeatBoardPanel>();

		for ( int i = 0; i < ChessRing.VerbPlateCount; i++ )
		{
			var go = new GameObject( true, $"VR Verb Plate {i}" ) { Parent = _root };
			var panel = ring.BuildVerbPlate( go );

			if ( !panel.IsValid() )
			{
				// No box model — AddBox has always silently drawn nothing in that case. Drop
				// the husks rather than leave objects that report valid and render air.
				_root.Destroy();
				_root = null;
				_board = null;
				_plateObjects.Clear();
				_plates.Clear();
				_buildFailed = true;
				return false;
			}

			_plateObjects.Add( go );
			_plates.Add( panel );
		}

		return true;
	}

	/// <summary>What this built, for the state board — "no component", "a component that built
	/// nothing" and "a board nobody is looking at" are three faults with one symptom.</summary>
	public static string Status()
	{
		if ( Instance == null ) return "ui=NONE";

		return $"ui: board={Instance._board.IsValid()} plates={Instance._plates.Count}"
			+ $" shown={Shown}{(LastError == null ? "" : $" err: {LastError}")}";
	}

	/// <summary>Show or hide the seated board and its plates.</summary>
	[ConCmd( "gambit_vr_board" )]
	public static void ToggleBoard()
	{
		Shown = !Shown;
		Log.Info( $"[Gambit] vr: seated board {(Shown ? "ON" : "OFF")}" );
	}

	/// <summary>Where the seated board hangs, in station-local world units: toward the player
	/// (x), to their right (y), above the floor (z). Tune it from inside the headset.</summary>
	[ConCmd( "gambit_vr_board_pos" )]
	public static void SetBoardPos( float x, float y, float z )
	{
		if ( ChessRing.Instance is not { } ring ) { Log.Info( "[Gambit] vr: no ring" ); return; }

		ring.VrBoardX = x;
		ring.VrBoardY = y;
		ring.VrBoardZ = z;
		Log.Info( $"[Gambit] vr: seated board at {x},{y},{z}" );
	}

	/// <summary>How big the seated board is, as a multiplier on the shared table-board
	/// scale.</summary>
	[ConCmd( "gambit_vr_board_scale" )]
	public static void SetBoardScale( float scale )
	{
		BoardScale = scale;
		Log.Info( $"[Gambit] vr: seated board scale {scale}" );
	}
}
