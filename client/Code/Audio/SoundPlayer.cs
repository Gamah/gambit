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
	public static void PlayPop()   { if ( MyOn ) Sound.Play( "sounds/pop.sound" ); }

	// Positional variants (non-UI .sound assets) for other players' stations
	public static void PlayTickAt( Vector3 pos )  { if ( RemoteOn ) Sound.Play( "sounds/tick3d.sound", pos ); }
	public static void PlayTockAt( Vector3 pos )  { if ( RemoteOn ) Sound.Play( "sounds/tock3d.sound", pos ); }
	public static void PlayPopAt( Vector3 pos )   { if ( RemoteOn ) Sound.Play( "sounds/pop3d.sound", pos ); }

	// ── The board set (M11) ──
	//
	// One method per MOMENT, not per sound file, and every one of them takes `mine`
	// rather than leaving the caller to pick between the 2D and 3D asset. That is
	// the whole point of them being here: the "your table is 2D, the room's tables
	// are 3D" rule is a property of the rule, not of each call site, and a call site
	// that gets to choose is a call site that can get it half right.
	//
	// The room must not become a slot machine with six tables — so anything the
	// player only needs at their OWN board (check, an offer, their clock) has no 3D
	// variant at all and simply does not play elsewhere.

	/// <summary>A move landed on some board: tick for White, tock for Black, pop for
	/// a capture, plus the check pip when it gave check.
	///
	/// <para>Check is 2D-only by design — six tables announcing check across the room
	/// is noise, and the king is already tinted red on the board you're at.</para></summary>
	public static void PlayMove( bool whiteMoved, bool capture, bool check, bool mine, Vector3 pos )
	{
		if ( capture )
		{
			if ( mine ) PlayPop();
			else PlayPopAt( pos );
		}
		else if ( mine )
		{
			if ( whiteMoved ) PlayTick();
			else PlayTock();
		}
		else
		{
			if ( whiteMoved ) PlayTickAt( pos );
			else PlayTockAt( pos );
		}

		if ( check && mine && MyOn )
			Sound.Play( "sounds/check.sound" );
	}

	/// <summary>A game ended — however it ended. 2D at your table, and a quiet
	/// positional copy elsewhere: a game finishing across the room is worth a glance
	/// up, which is exactly as much attention as a game at 45% volume asks for.</summary>
	public static void PlayGameOver( bool mine, Vector3 pos )
	{
		if ( mine ) { if ( MyOn ) Sound.Play( "sounds/gameover.sound" ); }
		else { if ( RemoteOn ) Sound.Play( "sounds/gameover3d.sound", pos ); }
	}

	/// <summary>Your opponent offered a draw or a takeback. 2D only, and only ever
	/// fired for the player being asked: it's a text line on the HUD that is very
	/// easy to miss entirely, which is the whole reason it makes a sound.</summary>
	public static void PlayOffer() { if ( MyOn ) Sound.Play( "sounds/offer.sound" ); }

	/// <summary>Your clock is under <see cref="Gambit.Game.TimeControl.PanicSeconds"/>.
	/// Fires once per second, 2D only, and only for the player whose clock it is —
	/// this is the first per-second sound in the game and it stays at one table.</summary>
	public static void PlayPanic() { if ( MyOn ) Sound.Play( "sounds/panic.sound" ); }

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
