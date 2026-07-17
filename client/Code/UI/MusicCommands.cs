using Sandbox;

namespace Gambit.UI;

/// <summary>
/// Why is the music board doing that? Exists for the same reason <c>gambit_tv</c> and
/// <c>gambit_clock</c> do: issue #12 was diagnosed from screenshots three times, and the
/// question that actually discriminates the causes — WHICH GameObject the panel lives on,
/// and what its NetworkMode is — is invisible from outside. One line per music-related
/// component, run on any machine (host or joined client), tells the whole story.
///
/// Healthy output on the fixed topology: exactly one SkafinityPlayer, one
/// SkafinityMusicPanel, one MusicBoardScreen, ALL on a GameObject named "LocalMusic" with
/// net=Never and flags containing NotSaved (built per-client by <see cref="LocalMusicSystem"/>),
/// plus two ScreenPanels (the scene's /UI and LocalMusic's own). A music component on any
/// OTHER GameObject — especially one at NetworkMode.Snapshot — means that machine is running
/// the pre-fix scene or a stale build, and is the smoking gun for the old snapshot leak.
/// </summary>
public static class MusicCommands
{
	[ConCmd( "gambit_music" )]
	public static void MusicStatus()
	{
		var scene = Game.ActiveScene;
		if ( scene == null )
		{
			Log.Warning( "[Gambit] no active scene." );
			return;
		}

		var active = Gambit.World.SettingsStation.Active;
		bool engaged = active?.Music ?? false;
		Log.Info( $"[Gambit] engage gate: SettingsStation.Active={( active == null ? "(none)" : active.Kind.ToString() )}"
			+ $" -> the music panel should be {( engaged ? "ENABLED + OPEN" : "disabled + closed" )} on this machine" );

		int screens = 0, players = 0, panels = 0, drivers = 0;

		// Walk EVERY GameObject including disabled ones: the panel is disabled by design
		// while roaming, so the enabled-only Scene.GetAllComponents would hide exactly the
		// component this command exists to find.
		foreach ( var go in scene.GetAllObjects( false ) )
		{
			foreach ( var c in go.Components.GetAll<Component>( FindMode.EverythingInSelf ) )
			{
				switch ( c )
				{
					case ScreenPanel sp:
						screens++;
						Log.Info( $"[Gambit]   ScreenPanel         {Where( go, sp )}" );
						break;

					case Skafinity.SkafinityPlayer pl:
						players++;
						Log.Info( $"[Gambit]   SkafinityPlayer     {Where( go, pl )} mixer={pl.MixerName}" );
						break;

					case Skafinity.SkafinityMusicPanel p:
						panels++;
						Log.Info( $"[Gambit]   SkafinityMusicPanel {Where( go, p )} open={p.IsOpen}"
							+ $" player={( p.Player != null ? "set" : "NULL" )} panelBuilt={( p.Panel != null ? "yes" : "no" )}" );
						if ( go.NetworkMode != NetworkMode.Never )
							Log.Warning( $"[Gambit]   -> the panel sits on a NetworkMode.{go.NetworkMode} GameObject: that is the"
								+ " PRE-FIX topology (issue #12). This machine is running a stale scene/build — the fixed"
								+ " client builds it on the runtime 'LocalMusic' GO at NetworkMode.Never." );
						if ( p.Enabled != engaged || ( p.IsOpen && !engaged ) )
							Log.Warning( "[Gambit]   -> panel state disagrees with the engage gate above — leaked or undriven state"
								+ " (is MusicBoardScreen alive on the same GameObject?)." );
						break;

					case MusicBoardScreen ms:
						drivers++;
						Log.Info( $"[Gambit]   MusicBoardScreen    {Where( go, ms )}" );
						break;
				}
			}
		}

		Log.Info( $"[Gambit] totals: {screens} ScreenPanel · {players} SkafinityPlayer"
			+ $" · {panels} SkafinityMusicPanel · {drivers} MusicBoardScreen" );

		if ( players == 0 || panels == 0 || drivers == 0 )
			Log.Warning( "[Gambit] the LocalMusic trio is incomplete — LocalMusicSystem never built it on this"
				+ " machine (stale assembly?, dedicated server?, editor scene?)." );
		if ( players > 1 || panels > 1 || drivers > 1 )
			Log.Warning( "[Gambit] DUPLICATE music components — a second copy arrived from the scene or the host"
				+ " snapshot. The one NOT on the LocalMusic/Never GameObject is the leak." );
	}

	static string Where( GameObject go, Component c ) =>
		$"@ {go.Name} (net={go.NetworkMode}, flags={go.Flags}) enabled={c.Enabled}";
}
