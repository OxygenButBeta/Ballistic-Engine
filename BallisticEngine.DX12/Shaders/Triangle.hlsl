// Phase 1 smoke triangle: no vertex buffer — positions + colors generated from SV_VertexID, so the
// raster pipeline (root signature, PSO, draw) is exercised with zero buffer plumbing. Replaced by the
// real mesh shaders in Phase 2.

struct VSOut {
    float4 pos   : SV_Position;
    float3 color : COLOR;
};

VSOut VSMain(uint id : SV_VertexID) {
    // A centered triangle in clip space; one primary color per corner.
    float2 verts[3] = { float2(0.0, 0.6), float2(0.6, -0.6), float2(-0.6, -0.6) };
    float3 cols[3]  = { float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1) };
    VSOut o;
    o.pos = float4(verts[id], 0.0, 1.0);
    o.color = cols[id];
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    return float4(i.color, 1.0);
}
