//
// Gambit 2D play-mode piece glyph (M16).
//
// The flat-board play mode renders every piece as a top-down glyph sprite instead of a
// lathed 3D body. ChessSetBuilder.BuildFlatPiece builds a single quad per (type, colour)
// with its UVs baked to that piece's cell in the 6×2 colour atlas (chess_glyphs_2d.png,
// set as the "GlyphAtlas" attribute), so ONE atlas and ONE material serve all 12 pieces.
//
// UNLIT and ALPHA-CLIPPED, deliberately:
//   - Unlit — the atlas already carries fill AND a contrasting outline per colour (the
//     thing that lets both colours read on both the cream and brown squares). Running it
//     through the lit model would re-tint it with the room light and defeat that.
//   - Alpha-clip (discard), not alpha-blend — order-independent and needs no blend/sort
//     render state, so it renders correctly with no per-material tuning. A top-down glyph
//     off a hard-edged font raster reads fine clipped; the edges are slightly aliased.
//
// Two-sidedness is handled in the MESH (BuildFlatPiece emits both windings), not here, so
// this shader carries no cull-state dependency. Modelled on floor_checker.shader — same
// FEATURES/MODES/VS scaffold; only the pixel shader differs.
//

FEATURES
{
    #include "common/features.hlsl"
}

MODES
{
    Forward();
    Depth();
}

COMMON
{
    #include "common/shared.hlsl"
}

struct VertexInput
{
    #include "common/vertexinput.hlsl"
};

struct PixelInput
{
    #include "common/pixelinput.hlsl"
};

VS
{
    #include "common/vertex.hlsl"

    PixelInput MainVs( VertexInput i )
    {
        PixelInput o = ProcessVertex( i );
        return FinalizeVertex( o );
    }
}

PS
{
    #include "common/pixel.hlsl"

    Texture2D GlyphAtlas < Attribute( "GlyphAtlas" ); >;   // the 6×2 filled+outlined glyph atlas
    SamplerState BilinearClamp < Filter( Bilinear ); AddressU( Clamp ); AddressV( Clamp ); >;

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float4 c = GlyphAtlas.Sample( BilinearClamp, i.vTextureCoords.xy );
        // Alpha-clip: outside the glyph the atlas is fully transparent, so drop those
        // fragments and let the board square show through. No blend, no sort.
        if ( c.a < 0.5 ) discard;

        // Go through the standard shading model (a Forward pass must — returning a bare
        // float4 does not link, which rendered the error/pink material). Unlit is faked the
        // usual way: zero albedo so lighting adds nothing, the atlas colour in Emission so
        // the glyph reads as its baked fill+outline regardless of the room light.
        Material m = Material::Init( i );
        m.Albedo = float3( 0.0, 0.0, 0.0 );
        m.Emission = c.rgb;
        m.Opacity = 1.0;
        m.Roughness = 1.0;
        m.Metalness = 0.0;
        m.AmbientOcclusion = 1.0;
        return ShadingModelStandard::Shade( m );
    }
}
