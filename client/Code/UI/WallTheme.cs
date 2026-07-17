using Gambit.Game;
using Sandbox;

namespace Gambit.UI;

/// <summary>
/// Runtime palette for the lobby wall boards and their engage screens, derived from the
/// "room theme" hue (Settings → ROOM THEME — stored in <c>WorldLightColor</c>). Everything
/// keys off one <see cref="Accent"/> hue, so changing the room theme retints the whole wall
/// UI; the room light itself stays white.
///
/// AUTO/empty falls back to a NEUTRAL (near-black) theme — the default is white-accented,
/// which the derived factors below turn into a dark panel with white text and borders. The
/// old default was a dark green carried over from the rotaliate/skafinity music board; a
/// black-and-white default is the intended look now. (A LITERAL black accent can't be the
/// default: every derived colour scales the accent, so a black hue would make borders,
/// headers and filled cells black-on-black — the accent has to be light for a dark theme.)
///
/// Panels bind these as inline <c>style=</c> values (which re-render on change, unlike a
/// compiled &lt;style&gt; block) — see WallTheme.scss for the static font/radius tokens.
/// The cabinet UI is intentionally NOT themed from this.
/// </summary>
public static class WallTheme
{
	// White: the neutral hue that the factors below turn into a near-black panel with white
	// text/borders — the default "black" theme. A picked swatch (green, red, …) replaces it.
	static readonly Color DefaultAccent = Color.White;

	/// <summary>The hue everything derives from: the room-light colour, or the default
	/// neutral (white → a near-black theme) when the room light is AUTO/empty.</summary>
	public static Color Accent
	{
		get
		{
			var hex = PlayerData.Load()?.WorldLightColor;
			return string.IsNullOrEmpty( hex ) ? DefaultAccent : ( Color.Parse( hex ) ?? DefaultAccent );
		}
	}

	// Derived palette — the accent is scaled/mixed so a light accent (the white default)
	// yields a near-black background with white text/borders, and a coloured accent tints
	// the same dark UI.
	public static string Bg        => Rgb( Scale( Accent, 0.09f ) );              // panel fill (near-black tint)
	public static string Border    => Rgba( Accent, 0.6f );                       // accent border
	public static string Divider   => Rgba( Accent, 0.25f );                      // hairline dividers
	public static string AccentCss => Rgb( Accent );                              // highlights / headline rows
	public static string Text      => Rgba( Mix( Accent, 0.8f ),  0.9f );         // primary text / titles
	public static string TextDim   => Rgba( Mix( Accent, 0.72f ), 0.7f );         // body lines
	public static string TextFaint => Rgba( Mix( Accent, 0.72f ), 0.4f );         // hints / muted labels
	public static string Cell      => Rgb( Scale( Accent, 0.04f ) );              // button / cell fill
	public static string CellFill  => Rgb( Scale( Accent, 0.6f ) );               // filled ticks / selected cells
	public static string AccentBg  => Rgba( Accent, 0.2f );                       // active toggle background

	// Component-wise helpers (avoid relying on Color operators), matching the codebase style.
	static Color Scale( Color c, float f ) => new Color( c.r * f, c.g * f, c.b * f );
	static Color Mix( Color c, float towardWhite ) => new Color(
		c.r + ( 1f - c.r ) * towardWhite,
		c.g + ( 1f - c.g ) * towardWhite,
		c.b + ( 1f - c.b ) * towardWhite );

	static string Rgb( Color c ) => $"rgb({To255( c.r )},{To255( c.g )},{To255( c.b )})";
	static string Rgba( Color c, float a ) => $"rgba({To255( c.r )},{To255( c.g )},{To255( c.b )},{a})";
	static int To255( float v ) => (int)System.MathF.Round( System.Math.Clamp( v, 0f, 1f ) * 255f );
}
