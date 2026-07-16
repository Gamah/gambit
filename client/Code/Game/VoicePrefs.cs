using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Gambit.Game;

/// <summary>
/// Client-local, <see cref="Cookie"/>-backed proximity-voice preferences (M12) — copied from
/// terryball's VoicePrefs, minus its first-run-help flag (Gambit's welcome board is its own thing).
/// Purely per-user state that never touches the network: the master on/off switch and the set of
/// players this user has muted (by SteamID). Every getter reads its cookie and every setter writes
/// it straight back, so choices persist across sessions the moment they change.
///
/// This is the local side of proximity voice: <see cref="Gambit.World.GambitVoice.ShouldHearVoice"/>
/// reads <see cref="VoiceEnabled"/> + <see cref="IsMuted"/> on the RECEIVER to decide who it hears,
/// and <see cref="Gambit.World.VoiceScreen"/> reads/writes these from the keyboard. Mute needs no
/// sync, no authority and no server state precisely because it is a receiver-side decision.
///
/// The per-client HEARING RANGE is NOT here — it lives in <see cref="PlayerData"/> so it can sit on
/// the world-settings board with the other room sliders. Range is a room-tuning knob; enabled/muted
/// are a transient roster the keyboard drives, so they stay cookie-light.
/// </summary>
public static class VoicePrefs
{
	private const string EnabledKey = "gambit.voice.enabled";
	private const string MutedKey = "gambit.voice.muted";

	/// <summary>Master voice switch. Default OFF — while false we neither transmit our mic nor hear anyone.</summary>
	public static bool VoiceEnabled
	{
		get => Cookie.Get( EnabledKey, false );
		set => Cookie.Set( EnabledKey, value );
	}

	// The muted set is stored as a long[] (SteamId.Value) so it round-trips cleanly through the cookie's
	// JSON serializer. We load it into a HashSet on each read for O(1) membership — the roster is tiny so
	// this is cheap even per-frame.
	private static HashSet<long> LoadMuted()
		=> new HashSet<long>( Cookie.Get( MutedKey, Array.Empty<long>() ) );

	private static void SaveMuted( HashSet<long> set )
		=> Cookie.Set( MutedKey, set.ToArray() );

	/// <summary>Is this SteamID currently muted? SteamId implicitly converts to long.</summary>
	public static bool IsMuted( long steamId ) => LoadMuted().Contains( steamId );

	/// <summary>Flip the muted state for a SteamID and persist immediately.</summary>
	public static void ToggleMute( long steamId )
	{
		var set = LoadMuted();
		if ( !set.Remove( steamId ) )
			set.Add( steamId );
		SaveMuted( set );
	}

	/// <summary>How many players are muted — used only for the panel's BuildHash / label.</summary>
	public static int MutedCount => LoadMuted().Count;
}
