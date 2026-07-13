//
// Rotaliate decorative floor: a procedural N×N checkerboard with palette "pops".
//
// The whole board (black/white checker + popped palette cells) is baked into a tiny
// BoardDim×BoardDim texture by C# (FloorCheckerboard.cs) and set as the "PopMap"
// attribute. This shader just point-samples it across the slab — one texel per cell —
// so there's no per-cell attribute limit and any number of pops is free. C# rebuilds
// the texture when the pops change, the palette changes, or BoardDim changes.
//
// Lit: the baked colour is used as Albedo and run through the standard shading model,
// so the floor behaves like the default primitive material (responds to room light
// colour/brightness and receives cabinet shadows). Sampled from the model's UV
// (a flat slab whose top face is 0..1).
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

    Texture2D PopMap < Attribute( "PopMap" ); >;
    float g_flBoardDim < Attribute( "BoardDim" ); Default( 20.0 ); >;
    float g_flBorderWidth < Attribute( "BorderWidth" ); Default( 0.05 ); >;
    float g_flBevelWidth < Attribute( "BevelWidth" ); Default( 0.12 ); >;    // bevel band width as a fraction of the cell
    float g_flBevelStrength < Attribute( "BevelStrength" ); Default( 0.6 ); >; // how far the edge normals tilt outward
    float g_flRoughness < Attribute( "FloorRoughness" ); Default( 0.5 ); >;     // lower = glossier, so grooves catch specular and read as tile
    SamplerState PointClamp < Filter( Point ); AddressU( Clamp ); AddressV( Clamp ); >;

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float3 col = PopMap.Sample( PointClamp, i.vTextureCoords.xy ).rgb;

        // Black gridline border: BorderWidth fraction of each square's dimension on every edge.
        float2 cellUv = frac( i.vTextureCoords.xy * g_flBoardDim );
        if ( g_flBorderWidth > 0.0 && ( any( cellUv < g_flBorderWidth ) || any( cellUv > 1.0 - g_flBorderWidth ) ) )
            col = float3( 0.0, 0.0, 0.0 );

        Material m = Material::Init( i );
        m.Albedo = col;
        m.Roughness = g_flRoughness;
        m.Metalness = 0.0;
        m.AmbientOcclusion = 1.0;   // no AO texture — default to fully lit, else floor is black
        m.Opacity = 1.0;

        // Procedural sunken grout: tilt the surface normal inward in a band just inside each
        // edge (starting at the grout border) so the border reads as a recessed groove between
        // flat tiles. Tangent-space slope is built from per-axis distance-into-tile, then
        // rotated into world space using the model's tangent basis. No textures — purely from
        // the cell-local UV.
        if ( g_flBevelWidth > 0.0 && g_flBevelStrength > 0.0 )
        {
            float bw = g_flBorderWidth;
            float bevel = g_flBevelWidth;
            float dxL = cellUv.x - bw;             // distance into the tile from the left edge
            float dxR = ( 1.0 - bw ) - cellUv.x;   // ...from the right edge
            float dyB = cellUv.y - bw;             // ...from the bottom edge
            float dyT = ( 1.0 - bw ) - cellUv.y;   // ...from the top edge

            // Nearest in-tile edge on each axis, and which side it is (the wall faces it).
            float dxN = min( dxL, dxR );
            float dyN = min( dyB, dyT );
            float sgnX = ( dxL < dxR ) ? -1.0 : 1.0;   // left edge -> wall faces -x (out toward the groove)
            float sgnY = ( dyB < dyT ) ? -1.0 : 1.0;

            // Uniform-width bevel frame: the slope is set by the distance to the NEAREST edge,
            // so the recessed groove keeps a constant cross-section all the way around and the
            // flat tile top stays a sharp-cornered square. Summing both axes instead (the old
            // approach) overlapped the two walls at corners and bulged/creased them. The wall
            // steers toward whichever edge is nearest; along the 45 degree diagonal the two
            // meet in a clean mitre, like a picture frame. smoothstep eases the wall foot.
            float d = min( dxN, dyN );
            float sx = 0.0, sy = 0.0;
            if ( d >= 0.0 && d < bevel )
            {
                float mag = 1.0 - smoothstep( 0.0, bevel, d );
                if ( dxN <= dyN ) sx = sgnX * mag;
                else              sy = sgnY * mag;
            }

            float3 tangentN = normalize( float3( sx * g_flBevelStrength, sy * g_flBevelStrength, 1.0 ) );
            m.Normal = normalize( tangentN.x * m.WorldTangentU + tangentN.y * m.WorldTangentV + tangentN.z * m.Normal );
        }

        return ShadingModelStandard::Shade( m );
    }
}
