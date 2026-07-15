using Gambit.Game;
using Sandbox;

namespace Gambit.Audio;

public static class SoundPlayer
{
	// World-settings gates (issue #49): the 2D set is the cabinet the local player
	// is engaged at; the positional set is everyone else's cabinets.
	static bool MyOn => PlayerData.Load()?.MyCabinetSounds ?? true;
	static bool RemoteOn => PlayerData.Load()?.RemoteCabinetSounds ?? true;

	public static void PlayTick()  { if ( MyOn ) Sound.Play( "sounds/tick.sound" ); }
	public static void PlayTock()  { if ( MyOn ) Sound.Play( "sounds/tock.sound" ); }
	public static void PlayWoosh() { if ( MyOn ) Sound.Play( "sounds/woosh.sound" ); }
	public static void PlayPop()   { if ( MyOn ) Sound.Play( "sounds/pop.sound" ); }

	// Positional variants (non-UI .sound assets) for other players' stations
	public static void PlayTickAt( Vector3 pos )  { if ( RemoteOn ) Sound.Play( "sounds/tick3d.sound", pos ); }
	public static void PlayWooshAt( Vector3 pos ) { if ( RemoteOn ) Sound.Play( "sounds/woosh3d.sound", pos ); }
	public static void PlayPopAt( Vector3 pos )   { if ( RemoteOn ) Sound.Play( "sounds/pop3d.sound", pos ); }

	// Cabinet slide (issue #54) — emitted by each cabinet as the ring sinks through
	// the floor (descend) and rises back (ascend, same WAV reversed). The sound must
	// FOLLOW the cabinet and ignore occlusion: the ascend fires while the cabinet is
	// still below the floor, so a fixed/occluded source there gets muffled and cut.
	public static void PlaySlide( GameObject go, string variant, bool ascend )
	{
		if ( !RemoteOn || !go.IsValid() ) return;
		var h = Sound.Play( $"sounds/{variant}{(ascend ? "_rev" : "")}.sound", go.WorldPosition );
		if ( h is null ) return;
		h.Parent = go;
		h.FollowParent = true;          // travel with the cabinet through the floor
		h.OcclusionEnabled = false;     // cabinets are partly underground by design
	}
}
