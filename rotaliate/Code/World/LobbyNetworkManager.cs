using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Network;

namespace Rotaliate.World;

/// <summary>
/// Makes the lobby a networked space.
/// - On the host path (editor play / fresh launch) it creates a lobby so others can join;
///   when joining someone else's lobby ISceneStartup.OnHostInitialize never runs, so this
///   is create-or-join with no extra logic.
/// - Spawns a player clone for every connection (including the local one) from the disabled
///   PlayerTemplate GameObject in the scene — no .prefab asset needed.
/// - Network-spawns every ArcadeStation so their [Sync] occupancy replicates, and clears
///   occupancy when the occupant disconnects.
/// </summary>
public sealed class LobbyNetworkManager : Component, Component.INetworkListener, ISceneStartup
{
	/// <summary>Disabled in-scene Player GameObject used as the spawn template.</summary>
	[Property] public GameObject PlayerTemplate { get; set; }

	/// <summary>Sideways gap between consecutive spawn positions so players don't stack.</summary>
	[Property] public float SpawnSpacing { get; set; } = 40f;

	/// <summary>Player cap for the hosted lobby. Should be at least the arcade ring's StationCount.</summary>
	[Property] public int MaxPlayers { get; set; } = 8;

	/// <summary>Backend URL the host has chosen for the whole lobby. Replicated host→client;
	/// every peer applies it to <see cref="Rotaliate.Api.ApiClient.BaseUrl"/> in OnUpdate.
	/// Empty until the host picks one, leaving each client on its own default.
	/// The next cabinet entry re-validates the player against the new backend
	/// (ArcadeStation.ValidateIdentity), so switching instances re-enrolls if needed.
	/// Only ever one of the trusted endpoints below — see <see cref="IsTrustedUrl"/>.</summary>
	[Sync( SyncFlags.FromHost )] public string TargetUrl { get; set; }

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

	// TEMP host-settings diagnostics — remove once the dedi admin works.
	float _nextAdminLog;

	protected override void OnUpdate()
	{
		if ( !string.IsNullOrEmpty( TargetUrl ) && TargetUrl != Rotaliate.Api.ApiClient.BaseUrl )
			Rotaliate.Api.ApiClient.BaseUrl = TargetUrl;

		// Unconditional heartbeat on every peer so we can tell "code not running" from
		// "value never replicates". Throttled to once every 2s.
		if ( Time.Now >= _nextAdminLog )
		{
			_nextAdminLog = Time.Now + 2f;
			Log.Info( $"[Rotaliate] heartbeat AdminSteamIds='{AdminSteamIds}' " +
				$"local={Connection.Local?.SteamId} LocalIsAdmin={LocalIsAdmin} " +
				$"dedicated={Application.IsDedicatedServer}" );
		}
	}

	/// <summary>An admin can only redirect the whole lobby to one of the known Rotaliate
	/// backends. Admin is a low bar in a *public* lobby, so an arbitrary URL would let a
	/// hostile admin harvest every peer's X-Player-ID GUID and a freshly minted (replayable)
	/// Facepunch auth token by pointing them at their own server. Restricting it to the
	/// trusted prod/test endpoints removes that path.</summary>
	static bool IsTrustedUrl( string url ) =>
		url == Rotaliate.Api.ApiClient.ProdUrl || url == Rotaliate.Api.ApiClient.TestUrl;

	// Host-validated entry points — host settings are triggered by an admin player, who
	// is not the network host on a dedicated server, so the writes must run host-side.
	[Rpc.Host]
	public void RequestSetTargetUrl( string url )
	{
		// Permissions are authoritative host-side, so gate on the caller's actual claim.
		if ( !CanChangeHostSettings( Rpc.Caller ) ) return;
		if ( !IsTrustedUrl( url ) ) return; // never send credentials to a host-chosen endpoint
		TargetUrl = url; // [Sync] replicates; OnUpdate applies it on every peer
	}

	[Rpc.Host]
	public void RequestSetStationCount( int count )
	{
		if ( !CanChangeHostSettings( Rpc.Caller ) ) return;
		foreach ( var ring in Scene.GetAllComponents<ArcadeRing>() )
			ring.HostSetStationCount( count );
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

		// TEMP host-settings diagnostics — remove once the dedi admin works.
		Log.Info( $"[Rotaliate] RefreshAdmins: dedicated={Application.IsDedicatedServer} " +
			$"conns={_conns.Count} host={Connection.Host?.SteamId} -> admins='{AdminSteamIds}'" );
		foreach ( var c in _conns )
			Log.Info( $"[Rotaliate]   conn '{c.DisplayName}' steam={c.SteamId} " +
				$"isHost={c == Connection.Host} hasClaim={c.HasPermission( HostSettingsClaim )} " +
				$"allowed={CanChangeHostSettings( c )}" );
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
			Name       = "Rotaliate v0.1 alpha",
		} );

		// Stations only exist once the ring builds them — host-only, so joining clients
		// get exactly one copy of each via NetworkSpawn instead of building their own.
		// [Sync] occupancy only replicates on NetworkMode.Object, so each station is
		// network-spawned after the build (shared with the cabinet-count rebuild).
		foreach ( var ring in Scene.GetAllComponents<ArcadeRing>() )
		{
			ring.Build();
			ring.NetworkSpawnStations();
		}
	}

	/// <summary>Called on the host when a connection finishes joining (including the local player).</summary>
	public void OnActive( Connection connection )
	{
		// Track join order, then rebuild the set of claim-holding admins.
		if ( !_conns.Contains( connection ) )
			_conns.Add( connection );
		RefreshAdmins();

		if ( PlayerTemplate == null )
		{
			Log.Warning( "[Rotaliate] LobbyNetworkManager has no PlayerTemplate — nobody can spawn" );
			return;
		}

		// Fan spawns out along Y so simultaneous joins don't overlap: 0, +1, -1, +2, -2…
		int slot = ( _spawnCount + 1 ) / 2 * ( _spawnCount % 2 == 0 ? 1 : -1 );
		_spawnCount++;

		var spawnPos = WorldPosition + new Vector3( 0, slot * SpawnSpacing, 0 );
		var player = PlayerTemplate.Clone( spawnPos );
		player.Name = $"Player - {connection.DisplayName}";
		player.Enabled = true;
		player.NetworkSpawn( connection );
	}

	/// <summary>Called on the host when a connection drops — free any station they occupied.</summary>
	public void OnDisconnected( Connection connection )
	{
		foreach ( var station in Scene.GetAllComponents<ArcadeStation>() )
			station.HostHandleDisconnect( connection.SteamId );

		// Drop the leaver from the admin set.
		_conns.Remove( connection );
		RefreshAdmins();
	}
}
