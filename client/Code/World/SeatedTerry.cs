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

	protected override void OnUpdate()
	{
		if ( Station is not { } station ) return;

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
			avatar.ApplyHandPose( station, seat, pose );
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
