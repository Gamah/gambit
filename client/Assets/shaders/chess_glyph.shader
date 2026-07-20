//
// Gambit 2D play-mode piece glyph (M16).
//
// The flat-board play mode renders every piece as a top-down glyph sprite instead of a
// lathed 3D body. ChessSetBuilder.BuildFlatPiece builds a single quad per (type, colour)
// with its UVs baked to that piece's cell in the 6×2 colour atlas (chess_glyphs_2d.png,
// set as the "Color" attribute), so ONE atlas and ONE material serve all 12 pieces.
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

    Texture2D Color < Attribute( "Color" ); >;   // the 6×2 filled+outlined glyph atlas
    SamplerState BilinearClamp < Filter( Bilinear ); AddressU( Clamp ); AddressV( Clamp ); >;

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float4 c = Color.Sample( BilinearClamp, i.vTextureCoords.xy );
        // Alpha-clip: outside the glyph the atlas is fully transparent, so drop those
        // fragments and let the board square show through. No blend, no sort.
        if ( c.a < 0.5 ) discard;
        return float4( c.rgb, 1.0 );   // unlit; the atlas is the final colour
    }
}
