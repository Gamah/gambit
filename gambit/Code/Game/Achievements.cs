using System;
using System.Collections.Generic;
using Sandbox;

namespace Gambit.Game;

/// <summary>Thin wrapper over <see cref="Sandbox.Services.Achievements"/> so every
/// unlock site shares one entry point. Idents are configured as manual achievements
/// in the s&box dashboard; we just call <c>Unlock</c> at the right hook.
///
/// Locally deduped so a hook that fires every frame / every solve only hits the
/// platform once per session — the platform is idempotent anyway, this just avoids
/// the redundant network chatter (matters for the server-signalled ones, which
/// arrive on every qualifying solve).</summary>
public static class Achievements
{
	// Manual one-shots unlocked directly from code. Everything else is stat-based
	// (see PlayerStats) and auto-unlocks without an Unlock call.
	public const string DiscordMod  = "discordmod";  // opened the host settings panel
	public const string Dj          = "dj";          // opened the music settings panel
	public const string Comfy       = "comfy";       // opened the world settings panel

	static readonly HashSet<string> _unlocked = new();

	public static void Unlock( string ident )
	{
		if ( string.IsNullOrEmpty( ident ) || !_unlocked.Add( ident ) ) return;
		try
		{
			Sandbox.Services.Achievements.Unlock( ident );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit] achievement unlock '{ident}' failed: {e.Message}" );
		}
	}
}
