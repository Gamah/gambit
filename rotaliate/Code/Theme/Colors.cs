using Sandbox;

namespace Rotaliate.Theme;

public static class Colors
{
	public static readonly Color Background = Color.Parse( "#07051a" ).Value;

	public static Color[] GetPalette( string scheme ) => scheme switch
	{
		"deuteranopia" => Deuteranopia,
		"protanopia"   => Protanopia,
		"tritanopia"   => Tritanopia,
		_              => Normal,
	};

	// index 0 = black/solved, 1=Red, 2=Blue, 3=Green, 4=Yellow.
	// All palettes are scaled to 2/3 of full intensity (255 was too harsh on the
	// unlit cubes). Colorblind palettes are Okabe-Ito-based, verified by
	// dichromacy simulation (Viénot matrices, linear RGB): min pairwise ΔE under
	// the target deficiency ≥ 36 for every scheme (previously 17–36 —
	// orange/yellow collided for deuteranopes, blue/teal for tritanopes).
	public static readonly Color[] Normal = new[]
	{
		Background,
		Color.Parse( "#AA0000" ).Value,
		Color.Parse( "#0000AA" ).Value,
		Color.Parse( "#00AA00" ).Value,
		Color.Parse( "#AAAA00" ).Value,
	};

	public static readonly Color[] Deuteranopia = new[]
	{
		Background,
		Color.Parse( "#8E3F00" ).Value,
		Color.Parse( "#004C77" ).Value,
		Color.Parse( "#00694D" ).Value,
		Color.Parse( "#AAAA00" ).Value,
	};

	public static readonly Color[] Protanopia = new[]
	{
		Background,
		Color.Parse( "#8E3F00" ).Value,
		Color.Parse( "#004C77" ).Value,
		Color.Parse( "#1F8379" ).Value,
		Color.Parse( "#AAAA00" ).Value,
	};

	// Tritanopes keep the red/green axis — the primaries plus a softened
	// yellow (reads pink-ish but distinct from red at high luminance)
	public static readonly Color[] Tritanopia = new[]
	{
		Background,
		Color.Parse( "#AA0000" ).Value,
		Color.Parse( "#0000AA" ).Value,
		Color.Parse( "#00AA00" ).Value,
		Color.Parse( "#AA8F07" ).Value,
	};
}
