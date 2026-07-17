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

	// Resolved avatars, cached on change rather than scanned per frame.
	LobbyPlayer _whitePlayer;
	LobbyPlayer _blackPlayer;
	ulong _whiteId;
	ulong _blackId;

	protected override void OnUpdate()
	{
		if ( Station is not { } station ) return;

		// Nobody here: no avatars to pose, and nothing to spend a frame on. Six empty
		// tables in a room must cost nothing.
		if ( !station.AnySeatTaken )
		{
			_white = HandPose.None;
			_black = HandPose.None;
			return;
		}

		var src = Source;

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
			return;
		}

		Drive( station, ChessSeat.White, src, ref _white, ref _whitePlayer, ref _whiteId );
		Drive( station, ChessSeat.Black, src, ref _black, ref _blackPlayer, ref _blackId );
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

		// Whose move was the last one? BoardDiff's rule, from the FEN alone: the side to
		// move AFTER a move is the player who did NOT make it.
		bool whiteMoved = game.Fen is { } fen && SideToMoveIsBlack( fen );
		bool seatMoved = whiteMoved == ( seat == ChessSeat.White );

		pose = TerryPose.Advance( pose, new HandInput(
			Hover: hover,
			Selected: selected,
			Ply: game.MoveCount,
			LastMoveUci: game.LastMoveUci ?? src.LastMoveUci,
			SeatMoved: seatMoved,
			GameLive: src.Playing ), Time.Delta );

		if ( avatar.IsValid() )
			avatar.ApplyHandPose( station, seat, pose );
	}

	/// <summary>Read the FEN's side-to-move field. Spelled out rather than
	/// <c>fen.Contains(" b ")</c>, for BoardDiff.SideToMoveIsBlack's reason: the substring
	/// search is accidentally correct, not obviously correct.</summary>
	static bool SideToMoveIsBlack( string fen )
	{
		int sp = fen.IndexOf( ' ' );
		return sp >= 0 && sp + 1 < fen.Length && fen[sp + 1] == 'b';
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
		if ( ChessStation.Active == station && ChessStation.ActiveSeat == seat
			&& LobbyPlayer.Local.IsValid() )
			return LobbyPlayer.Local;

		ulong id = station.SeatSteamId( seat );
		if ( id == 0 )
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
