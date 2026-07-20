using System;
using Sandbox;

namespace Gambit.World;

/// <summary>Which side of a chess table a player occupies.</summary>
public enum ChessSeat { White = 0, Black = 1 }

/// <summary>
/// One chess table in the ring, with two claimable seats (CLAUDE.md D1). The
/// GameObject is network-spawned by the host (see LobbyNetworkManager /
/// ChessRing) and the seat fields are host-authoritative [Sync] properties
/// driven by Rpc.Host requests, so everyone in the lobby sees who sits where
/// and two players can't take the same side. There is a small (~RTT) race if
/// two players press Use on the same seat — the host picks the winner and the
/// loser's client notices via the synced fields and disengages (known
/// limitation, inherited from the arcade single-seat flow).
///
/// M1 scope: occupancy + camera lock only. Game state (FEN relay, spectator
/// snapshots, turn enforcement) arrives with LocalGameController in M2.
/// </summary>
public sealed class ChessStation : Component
{
	/// <summary>The station the local player is currently seated at, if any.</summary>
	public static ChessStation Active { get; private set; }

	/// <summary>Which seat the local player took at <see cref="Active"/>.</summary>
	public static ChessSeat ActiveSeat { get; private set; }

	/// <summary>Camera lock target for the White seat (outward side). Set by
	/// ChessRing at build time.</summary>
	[Property] public GameObject WhiteAnchor { get; set; }

	/// <summary>Camera lock target for the Black seat (inward side).</summary>
	[Property] public GameObject BlackAnchor { get; set; }

	/// <summary>Top-down (nadir) camera targets for 2D play mode (M16), one per seat. Set by
	/// ChessRing at build time, looking straight down at the board from each player's side. Used
	/// instead of <see cref="WhiteAnchor"/>/<see cref="BlackAnchor"/> when PlayMode is "2d".</summary>
	[Property] public GameObject WhiteTopAnchor { get; set; }
	[Property] public GameObject BlackTopAnchor { get; set; }

	/// <summary>SteamId of the player in the White seat, 0 if free. Host-authoritative.</summary>
	[Sync( SyncFlags.FromHost )] public ulong WhiteSteamId { get; set; }
	[Sync( SyncFlags.FromHost )] public string WhiteName { get; set; }

	/// <summary>SteamId of the player in the Black seat, 0 if free. Host-authoritative.</summary>
	[Sync( SyncFlags.FromHost )] public ulong BlackSteamId { get; set; }
	[Sync( SyncFlags.FromHost )] public string BlackName { get; set; }


	public bool SeatTaken( ChessSeat seat ) =>
		SeatSteamId( seat ) != 0 || ( Active == this && ActiveSeat == seat );

	public bool AnySeatTaken =>
		WhiteSteamId != 0 || BlackSteamId != 0 || Active == this;

	public ulong SeatSteamId( ChessSeat seat ) =>
		seat == ChessSeat.White ? WhiteSteamId : BlackSteamId;

	public string SeatName( ChessSeat seat ) =>
		seat == ChessSeat.White ? WhiteName : BlackName;

	public GameObject SeatAnchor( ChessSeat seat ) =>
		seat == ChessSeat.White ? WhiteAnchor : BlackAnchor;

	/// <summary>The top-down (nadir) camera target for this seat — the 2D play-mode view (M16).
	/// Mirrors <see cref="SeatAnchor"/>; LobbyPlayer picks this over the orbit anchor when 2D is on.
	/// Falls back to the orbit anchor if the top anchors weren't built (e.g. an old scene).</summary>
	public GameObject TopAnchor( ChessSeat seat ) =>
		( seat == ChessSeat.White ? WhiteTopAnchor : BlackTopAnchor ) ?? SeatAnchor( seat );

	/// <summary>Where a player stands to claim this seat — used for the "Press E"
	/// proximity pick. The camera anchor's ground position is exactly that spot.
	///
	/// <para><b>This is the WALK-UP spot and nothing else.</b> It is not where a seated
	/// player's body goes — see <see cref="SeatSitWorldPosition"/>. Two different
	/// questions, two methods: this one answers "am I close enough to sit here", which
	/// <see cref="LobbyPlayer.FindNearbySeat"/> asks against InteractRange and which the
	/// info board's "walk up to either side and press E" is checked against. Moving it to
	/// suit the seated pose would quietly change how close you must stand to a table.</para></summary>
	public Vector3 SeatWorldPosition( ChessSeat seat )
	{
		var anchor = SeatAnchor( seat );
		if ( anchor == null ) return WorldPosition;
		var pos = anchor.WorldPosition;
		pos.z = WorldPosition.z;
		return pos;
	}

	/// <summary>
	/// Where a SEATED player's avatar is planted (M13) — on the chair, not at the walk-up
	/// spot they arrived from.
	///
	/// <para><b>Why it is further back than the walk-up spot.</b> The walk-up spot (|x| =
	/// 32.12) is the chair's centre, and hips at a chair's centre put the citizen's belly
	/// about 3.4 units INSIDE the tabletop slab. Sitting back of centre is how a chair
	/// actually works, and <c>SeatSitBack</c> = 36 is the middle of the only band that
	/// works at all: much further back and the elbows-on-table idle becomes physically
	/// impossible (at |x| = 42 the elbow reach is 32.0, already off the table's 30 edge).
	/// It is pinned from both ends, which is why it is a knob rather than a constant.</para>
	///
	/// <para><b>The Z is the FLOOR, and that is not an oversight.</b> The citizen's origin
	/// is at its FEET, not its hips: the sit pose carries its own seat height above the
	/// origin, and <c>sit_offset_height</c> trims it by ±12 inches. (citizen.vanmgrph's own
	/// comment — "30 units at the source, 12 after scaling to inches. Feet IK disables
	/// through tag on +12 node" — only makes sense if the feet reach the floor at offset 0;
	/// dangling feet on a high stool is exactly what that tag turns IK off for.) Planting
	/// the origin on the pad's top surface would float the terry a whole seat-height into
	/// the air. Both numbers are [Property] on ChessRing and the editor settles them.</para>
	/// </summary>
	public Vector3 SeatSitWorldPosition( ChessSeat seat )
	{
		var ring = ChessRing.Instance;
		float back = ring?.SeatSitBack ?? 36f;
		float z = ring?.SeatSitZ ?? 0f;
		float side = seat == ChessSeat.White ? -1f : +1f;
		return WorldTransform.PointToWorld( new Vector3( side * back, 0f, z ) );
	}

	/// <summary>Take a seat at this table. Local half of the claim: optimistic —
	/// the host request races other players, and OnUpdate reconciles if we lose.</summary>
	public void Enter( ChessSeat seat )
	{
		if ( Active != null || SeatTaken( seat ) ) return;
		// This GO is about to be destroyed mid-slide — don't lock onto it
		if ( ChessRing.Instance?.Rebuilding ?? false ) return;

		Active = this;
		ActiveSeat = seat;
		// Fully qualified — "Game" alone can resolve to Sandbox.Game under `using Sandbox`
		// DisplayName is the Steam persona name, so the seat label matches the name tag.
		RequestEnter( (int)seat, Gambit.Game.PlayerData.Load()?.DisplayName() );
	}

	public void Leave()
	{
		if ( Active != this ) return;
		Active = null;
		RequestLeave( (int)ActiveSeat );
	}

	/// <summary>Move the local occupant to the OTHER seat at this table, without leaving
	/// the station — used when a shareable-link player picks their colour so the board
	/// shows them where they'll play. Optimistic like <see cref="Enter"/>: releases the
	/// old seat and claims the new one host-side (ordered RPCs), and OnUpdate reconciles
	/// if the claim loses a race. No-op if the target seat is occupied by someone else.</summary>
	public void SwitchActiveSeat( ChessSeat seat )
	{
		if ( Active != this || ActiveSeat == seat ) return;
		if ( SeatTaken( seat ) ) return;                  // someone else holds it
		if ( ChessRing.Instance?.Rebuilding ?? false ) return;

		int old = (int)ActiveSeat;
		ActiveSeat = seat;
		RequestLeave( old );
		RequestEnter( (int)seat, Gambit.Game.PlayerData.Load()?.DisplayName() );
	}

	protected override void OnUpdate()
	{
		// Seat-claim race reconciliation: if the host gave our seat to someone else
		// while we were optimistically locked in, stand back up.
		if ( Active == this )
		{
			ulong owner = SeatSteamId( ActiveSeat );
			ulong local = Connection.Local?.SteamId ?? 0;
			if ( owner != 0 && owner != local )
				LobbyPlayer.Local?.Disengage();
		}
	}

	/// <summary>Host calls this per-table as it starts a slide leg (issue #54) so
	/// every client hears the positional slide SFX, not just the host.</summary>
	[Rpc.Broadcast]
	public void NetSlideSfx( string variant, bool ascend )
	{
		Audio.SoundPlayer.PlaySlide( GameObject, variant, ascend );
	}

	[Rpc.Host]
	void RequestEnter( int seatIndex, string name )
	{
		var seat = (ChessSeat)seatIndex;
		// First request wins; lets the current occupant refresh their own info
		ulong current = SeatSteamId( seat );
		if ( current != 0 && current != Rpc.Caller.SteamId ) return;
		// One player can't hold both sides of a table
		ulong other = SeatSteamId( seat == ChessSeat.White ? ChessSeat.Black : ChessSeat.White );
		if ( other == Rpc.Caller.SteamId ) return;

		// Fall back to the caller's Steam name if they sent nothing — the host reads
		// it from the connection, so a client can't spoof a name it doesn't own.
		SetSeat( seat, Rpc.Caller.SteamId,
			string.IsNullOrEmpty( name ) ? Rpc.Caller.DisplayName : name );
	}

	[Rpc.Host]
	void RequestLeave( int seatIndex )
	{
		var seat = (ChessSeat)seatIndex;
		if ( SeatSteamId( seat ) != Rpc.Caller.SteamId ) return;
		SetSeat( seat, 0, null );
	}

	/// <summary>Host-side: free any seat the disconnecting player occupied.</summary>
	internal void HostHandleDisconnect( ulong steamId )
	{
		if ( WhiteSteamId == steamId ) SetSeat( ChessSeat.White, 0, null );
		if ( BlackSteamId == steamId ) SetSeat( ChessSeat.Black, 0, null );
	}

	void SetSeat( ChessSeat seat, ulong steamId, string name )
	{
		if ( seat == ChessSeat.White )
		{
			WhiteSteamId = steamId;
			WhiteName = name;
		}
		else
		{
			BlackSteamId = steamId;
			BlackName = name;
		}
	}

	protected override void OnDestroy()
	{
		// Station destroyed under us (ring rebuild / host migration): release the
		// local lock so the player isn't stuck staring at a missing table.
		if ( Active == this )
			LobbyPlayer.Local?.Disengage();
	}
}
