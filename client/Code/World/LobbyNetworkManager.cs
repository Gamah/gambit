using System;
using System.Collections.Generic;
using System.Linq;
using Gambit.Game;
using Sandbox;
using Sandbox.Network;

namespace Gambit.World;

/// <summary>
/// Makes the lobby a networked space.
/// - On the host path (editor play / fresh launch) it creates a lobby so others can join;
///   when joining someone else's lobby ISceneStartup.OnHostInitialize never runs, so this
///   is create-or-join with no extra logic.
/// - Spawns a player clone for every connection (including the local one) from the disabled
///   PlayerTemplate GameObject in the scene — no .prefab asset needed.
/// - Network-spawns every ChessStation so their [Sync] seat occupancy replicates, and
///   clears seats when the occupant disconnects.
/// </summary>
public sealed class LobbyNetworkManager : Component, Component.INetworkListener, ISceneStartup
{
	/// <summary>Disabled in-scene Player GameObject used as the spawn template.</summary>
	[Property] public GameObject PlayerTemplate { get; set; }

	/// <summary>Sideways gap between consecutive spawn positions so players don't stack.</summary>
	[Property] public float SpawnSpacing { get; set; } = 40f;

	/// <summary>Player cap for the hosted lobby. Two players can share each chess
	/// table, so this can exceed the ring's StationCount.</summary>
	[Property] public int MaxPlayers { get; set; } = 16;

	/// <summary>Permission claim that marks a connection as allowed to run host settings.
	/// On a dedicated server, grant it per-SteamId in the server's <c>config/users.json</c>
	/// (see the dedicated-server docs). On a listen-host the host player has every claim by
	/// default, so nothing extra is needed there.</summary>
	public const string HostSettingsClaim = "hostsettings";

	/// <summary>Space-separated SteamIds of every connected player allowed to change host
	/// settings — i.e. everyone holding the <see cref="HostSettingsClaim"/> claim. Built
	/// host-side (the only place permissions can be read) and replicated to all peers so each
	/// client can tell whether its own player may use the host board. On a listen-host the host
	/// holds every permission, so it is always included; a dedicated server excludes its own
	/// headless connection (not a player). Host-authoritative; rebuilt in OnActive/OnDisconnected.</summary>
	[Sync( SyncFlags.FromHost )] public string AdminSteamIds { get; set; }

	/// <summary>The lichess TV channel this lobby SUGGESTS for the north wall (M9).
	///
	/// <para>A suggestion, not a setting: a client that has picked its own channel keeps
	/// it, and a client with TV off ignores this entirely. Per-client choice is
	/// affordable because gamchess holds one upstream stream per channel — the cost is
	/// bounded by the channel count, not the player count.</para>
	///
	/// <para>Blank means the default (blitz). Host-authoritative and admin-gated like
	/// the station count; see <see cref="RequestSetSuggestedTvChannel"/>.</para></summary>
	[Sync( SyncFlags.FromHost )] public string SuggestedTvChannel { get; set; } = LichessTv.DefaultChannel;

	public static LobbyNetworkManager Instance { get; private set; }

	/// <summary>True on a client whose local player holds the host-settings claim.</summary>
	public static bool LocalIsAdmin
	{
		get
		{
			var inst = Instance;
			ulong local = Connection.Local?.SteamId ?? 0UL;
			return inst != null && local != 0 && inst.IsAdmin( local );
		}
	}

	/// <summary>Whether the given SteamId is in the replicated host-settings admin set.</summary>
	public bool IsAdmin( ulong steamId ) =>
		!string.IsNullOrEmpty( AdminSteamIds )
		&& AdminSteamIds.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Contains( steamId.ToString() );

	int _spawnCount;
	// Server-only: connections in join order. Stored as Connection (not SteamId) so we can read
	// each one's permission claims when rebuilding the admin set.
	readonly List<Connection> _conns = new();

	protected override void OnEnabled() => Instance = this;
	protected override void OnDisabled() { if ( Instance == this ) Instance = null; }

	protected override void OnStart()
	{
		// A dedicated server has no audio device, so the Skafinity music player's SoundStream
		// is invalid and spams "PushTransition failed: Invalid sound stream" every tick. The
		// library auto-plays, so disable it on the server (clients keep their own music).
		if ( Application.IsDedicatedServer )
		{
			var music = Scene.GetAllComponents<Skafinity.SkafinityPlayer>().FirstOrDefault();
			if ( music != null ) music.Enabled = false;
		}
	}

	// Host-validated entry point — host settings are triggered by an admin player, who
	// is not the network host on a dedicated server, so the writes must run host-side.
	[Rpc.Host]
	public void RequestSetStationCount( int count )
	{
		if ( !CanChangeHostSettings( Rpc.Caller ) ) return;
		foreach ( var ring in Scene.GetAllComponents<ChessRing>() )
			ring.HostSetStationCount( count );
	}

	/// <summary>Set the lobby's suggested TV channel (M9). Admin-gated, and the check
	/// runs HOST-side for the same reason the station count's does: permissions can only
	/// be read on the host, and the admin may not be the network host on a dedi. The
	/// client-side <see cref="LocalIsAdmin"/> gate is UI only and is never authority.</summary>
	[Rpc.Host]
	public void RequestSetSuggestedTvChannel( string channel )
	{
		if ( !CanChangeHostSettings( Rpc.Caller ) ) return;
		// Coerce rather than trust: this arrives from a client and is [Sync]ed to every
		// peer, so a junk value would put every wall in the lobby on a dead channel.
		// gamchess validates independently — this just keeps the lobby coherent.
		SuggestedTvChannel = LichessTv.Coerce( channel );
	}

	/// <summary>Host-side authority check for host-settings actions. Permissions can only be
	/// read on the host, so this must run host-side. Allowed for anyone holding the
	/// <see cref="HostSettingsClaim"/> claim, plus the listen-host player who has blanket
	/// control of their own lobby. On a dedicated server the headless host connection is not a
	/// player, so it is never granted this — only real claim holders are.</summary>
	bool CanChangeHostSettings( Connection c )
	{
		if ( c == null ) return false;
		if ( c == Connection.Host && !Application.IsDedicatedServer ) return true; // listen-host: blanket
		return c.HasPermission( HostSettingsClaim );
	}

	void RefreshAdmins()
	{
		// Build the set of every connected player allowed to change host settings (see
		// CanChangeHostSettings). This is the only place permissions are read — host-side.
		var ids = _conns
			.Where( CanChangeHostSettings )
			.Select( c => c.SteamId.ToString() )
			.ToList();

		AdminSteamIds = string.Join( ' ', ids );
	}

	void ISceneStartup.OnHostInitialize()
	{
		// Explicit config: the parameterless overload's defaults leave the lobby
		// unlisted in the server browser and capped below our station count.
		Networking.CreateLobby( new LobbyConfig
		{
			MaxPlayers = MaxPlayers,
			Privacy    = LobbyPrivacy.Public,
			Hidden     = false,
			Name       = "Terry's Gambit v0.1 alpha",
		} );

		// Stations only exist once the ring builds them — host-only, so joining clients
		// get exactly one copy of each via NetworkSpawn instead of building their own.
		// [Sync] occupancy only replicates on NetworkMode.Object, so each station is
		// network-spawned after the build (shared with the board-count rebuild).
		foreach ( var ring in Scene.GetAllComponents<ChessRing>() )
		{
			ring.Build();
			ring.NetworkSpawnStations();
		}
	}

	// The host's own avatar must NOT be network-spawned inside its own OnActive: that
	// fires during Networking.CreateLobby, before the snapshot system is ready to include
	// the object, so late joiners never receive it (they see every other player but the
	// host). Deferred to the first OnUpdate once networking is active — by then we're off
	// the CreateLobby call stack and the host avatar lands in the join snapshot like
	// everything else. Joiner avatars come from genuinely-remote OnActive calls that are
	// already past bring-up, so they spawn inline.
	bool _hostSpawnPending;
	Connection _hostSpawnConnection;

	/// <summary>Called on the host when a connection finishes joining (including the local player).</summary>
	public void OnActive( Connection connection )
	{
		// Track join order, then rebuild the set of claim-holding admins.
		if ( !_conns.Contains( connection ) )
			_conns.Add( connection );
		RefreshAdmins();

		if ( PlayerTemplate == null )
		{
			Log.Warning( "[Gambit] LobbyNetworkManager has no PlayerTemplate — nobody can spawn" );
			return;
		}

		// The host's own connection activates during lobby creation — defer it (see note
		// above). Everyone else spawns right here.
		if ( connection == Connection.Local )
		{
			_hostSpawnConnection = connection;
			_hostSpawnPending = true;
			return;
		}

		SpawnPlayer( connection );
	}

	protected override void OnUpdate()
	{
		// Flush the deferred host spawn on the first frame after lobby creation — by now
		// we're off the CreateLobby call stack, so the host avatar network-spawns into the
		// snapshot late joiners receive. Guarded on IsHost since only the host defers here.
		if ( _hostSpawnPending && Networking.IsHost )
		{
			_hostSpawnPending = false;
			var conn = _hostSpawnConnection;
			_hostSpawnConnection = null;
			SpawnPlayer( conn );
		}
	}

	/// <summary>Clone the PlayerTemplate for a connection and network-spawn it owned by
	/// that connection. Shared by the inline joiner path and the deferred host path.</summary>
	void SpawnPlayer( Connection connection )
	{
		if ( connection == null || PlayerTemplate == null ) return;

		// Fan spawns out along Y so simultaneous joins don't overlap: 0, +1, -1, +2, -2…
		int slot = ( _spawnCount + 1 ) / 2 * ( _spawnCount % 2 == 0 ? 1 : -1 );
		_spawnCount++;

		var spawnPos = WorldPosition + new Vector3( 0, slot * SpawnSpacing, 0 );
		var player = PlayerTemplate.Clone( spawnPos );
		player.Name = $"Player - {connection.DisplayName}";
		player.Enabled = true;
		player.NetworkSpawn( connection );
	}

	/// <summary>Called on the host when a connection drops — free any seat they occupied.</summary>
	public void OnDisconnected( Connection connection )
	{
		foreach ( var station in Scene.GetAllComponents<ChessStation>() )
			station.HostHandleDisconnect( connection.SteamId );

		// Drop the leaver from the admin set.
		_conns.Remove( connection );
		RefreshAdmins();
	}
}
