using Gambit.Game;
using Sandbox;

namespace Gambit.UI;

/// <summary>
/// Runtime palette for the lobby wall boards and their engage screens (the old
/// dark-green-on-black music-board look), derived from the "room theme" hue
/// (Settings → ROOM THEME — stored in <c>WorldLightColor</c>). Everything keys off one
/// <see cref="Accent"/> hue, so changing the room theme retints the whole wall UI; the
/// room light itself stays white. AUTO/empty falls back to the canonical green so the
/// default look is unchanged.
///
/// Panels bind these as inline <c>style=</c> values (which re-render on change, unlike a
/// compiled &lt;style&gt; block) — see WallTheme.scss for the static font/radius tokens.
/// The cabinet UI is intentionally NOT themed from this.
/// </summary>
public static class WallTheme
{
	static readonly Color DefaultAccent = Color.Parse( "#2f9450" ) ?? Color.White;

	/// <summary>The hue everything derives from: the room-light colour, or the default
	/// green when the room light is AUTO/empty.</summary>
	public static Color Accent
	{
		get
		{
			var hex = PlayerData.Load()?.WorldLightColor;
			return string.IsNullOrEmpty( hex ) ? DefaultAccent : ( Color.Parse( hex ) ?? DefaultAccent );
		}
	}

	// Derived palette — factors chosen so the default green reproduces the original
	// hardcoded values (#030d07 bg, #2f9450 accent, green-tinted text, etc.).
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
