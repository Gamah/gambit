using System;
using System.Collections.Generic;
using System.Linq;
using Gambit.Chess;
using Gambit.Game;
using Sandbox;
using Sandbox.Speech;

namespace Gambit.Audio;

/// <summary>
/// Speaks the moves played on the board you are seated at, via the engine's speech
/// synthesiser (M12). A client-local accessibility/flavour setting — <b>only your own
/// table</b>, never the spectator wall (TV) and never another player's board. Driven from
/// <see cref="TableSounds"/>'s move watcher, gated on <c>Mine</c>.
///
/// <para><b>Never required, and fails closed.</b> The synthesiser is
/// <see cref="Sandbox.Speech.Synthesizer"/>, which is SAPI-backed and Windows-only; on a
/// Linux/dedicated build (and this dev host) it has no voices and simply does nothing. Every
/// path here is wrapped so a missing or throwing synthesiser degrades to silence, exactly
/// like gamchess being down.</para>
///
/// <para><b>The voice list is enumerated once and cached.</b> Constructing a
/// <see cref="Synthesizer"/> queries the OS for installed voices, which is not free — the
/// settings panel rebuilds its rows every frame it is open, so re-enumerating there would be
/// a stutter. A speak still constructs a fresh synthesiser (the wrapper accumulates its text
/// and can't be reset), but a move is seconds apart, so that cost is paid rarely and only
/// when the feature is on.</para>
/// </summary>
public static class MoveTts
{
	static List<string> _voices;

	/// <summary>Voices installed on this machine, by full name. Empty on any platform
	/// without SAPI (Linux, dedicated server) — the feature is then a no-op. Cached: the
	/// first read enumerates, every later read is free.</summary>
	public static IReadOnlyList<string> Voices
	{
		get
		{
			if ( _voices != null ) return _voices;
			_voices = new List<string>();
			try
			{
				using var s = new Synthesizer();
				_voices = s.InstalledVoices.Select( v => v.Name ).ToList();
			}
			catch ( Exception e )
			{
				// No SAPI here — leave the list empty; the settings row says "none installed".
				Log.Info( $"[Gambit] speech synthesis unavailable: {e.Message}" );
			}
			return _voices;
		}
	}

	/// <summary>A short, panel-friendly label for a voice — "Microsoft David Desktop"
	/// reads as "David". Falls back to the raw name if it isn't in that shape.</summary>
	public static string Short( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return "default";
		var s = name;
		const string pre = "Microsoft ";
		const string post = " Desktop";
		if ( s.StartsWith( pre ) ) s = s[pre.Length..];
		if ( s.EndsWith( post ) ) s = s[..^post.Length];
		return s;
	}

	/// <summary>Speak the LAST move on this game, if the player has TTS on. Reads the setting
	/// itself so callers stay a one-liner. Silent when off, when there's no move yet, or when
	/// no synthesiser is available.</summary>
	public static void SpeakLastMove( ChessGame game )
	{
		var data = PlayerData.Current;
		if ( !data.MoveTtsEnabled ) return;

		var sans = game?.SanMoves;
		if ( sans == null || sans.Count == 0 ) return;

		Speak( sans[^1], data.MoveTtsVoice, PlayerData.ClampUnit( data.MoveTtsVolume ) );
	}

	/// <summary>Speak one SAN move at the given volume in the given voice. Never throws.</summary>
	public static void Speak( string san, string voiceName, float volume )
	{
		string text = MoveSpeech.Spoken( san );
		if ( string.IsNullOrEmpty( text ) ) return;

		try
		{
			// The synthesiser is only needed to render the audio; once Play() has written the
			// samples into the sound stream (synchronously) it can be disposed — the returned
			// handle plays on independently, so we set its volume after.
			SoundHandle handle;
			using ( var synth = new Synthesizer() )
			{
				if ( !string.IsNullOrEmpty( voiceName ) )
					synth.TrySetVoice( voiceName );
				handle = synth.WithText( text ).Play();
			}
			handle.Volume = Math.Clamp( volume, 0f, 1f );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Gambit] move TTS failed: {e.Message}" );
		}
	}
}
