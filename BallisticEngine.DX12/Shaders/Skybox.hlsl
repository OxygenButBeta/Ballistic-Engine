// Skybox background for the DX12 backend. Draws a unit cube centered on the camera (translation
// stripped from the view), sampling the environment cubemap by world direction. Rendered AFTER opaque
// with depth test LEqual + depth writes off, so it fills only the pixels geometry didn't cover (the
// far-plane background). Mirrors the GL Skybox shader: a SkyRotation orients the cube, Exposure scales
// the HDR texels, ACES tonemap to match the opaque pass's LDR output.
//
// The DX12 renderer owns this PSO + draw + constants directly (no per-name uniform API) — the constant
// layout below MUST match SkyboxConstants in DX12HDRenderer byte-for-byte.

cbuffer SkyboxConstants : register(b0) {
    float4x4 ViewProjNoTranslate;  // (rotation-only view) * proj, transposed on upload
    float4x4 SkyRotation;          // orientation of the sky cube, transposed
    float    Exposure;             // HDR texel scale (sky.Exposure * preExposure stand-in)
    float3   _pad;
};

TextureCube SkyMap : register(t0);
SamplerState LinearClamp : register(s0);

struct VSOutput {
    float4 Position : SV_Position;
    float3 Dir      : TEXCOORD0;   // world-space sample direction
};

// A unit cube from SV_VertexID (36 verts) — no vertex buffer needed.
static const float3 CubeVerts[36] = {
    // +Z
    float3(-1,-1, 1), float3( 1,-1, 1), float3( 1, 1, 1), float3( 1, 1, 1), float3(-1, 1, 1), float3(-1,-1, 1),
    // -Z
    float3(-1,-1,-1), float3(-1, 1,-1), float3( 1, 1,-1), float3( 1, 1,-1), float3( 1,-1,-1), float3(-1,-1,-1),
    // -X
    float3(-1,-1,-1), float3(-1,-1, 1), float3(-1, 1, 1), float3(-1, 1, 1), float3(-1, 1,-1), float3(-1,-1,-1),
    // +X
    float3( 1,-1,-1), float3( 1, 1,-1), float3( 1, 1, 1), float3( 1, 1, 1), float3( 1,-1, 1), float3( 1,-1,-1),
    // +Y
    float3(-1, 1,-1), float3(-1, 1, 1), float3( 1, 1, 1), float3( 1, 1, 1), float3( 1, 1,-1), float3(-1, 1,-1),
    // -Y
    float3(-1,-1,-1), float3( 1,-1,-1), float3( 1,-1, 1), float3( 1,-1, 1), float3(-1,-1, 1), float3(-1,-1,-1),
};

float3 ACESFilm(float3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

VSOutput VSMain(uint vid : SV_VertexID) {
    float3 p = CubeVerts[vid];
    VSOutput o;
    // z = w in clip space → depth 1.0 (far plane), so LEqual lets it fill only uncovered pixels.
    float4 pos = mul(float4(p, 1.0), ViewProjNoTranslate);
    o.Position = pos.xyww;
    o.Dir = mul(float4(p, 0.0), SkyRotation).xyz;   // orient the sample dir
    return o;
}

float4 PSMain(VSOutput i) : SV_Target {
    float3 hdr = SkyMap.Sample(LinearClamp, normalize(i.Dir)).rgb * Exposure;
    float3 srgb = pow(ACESFilm(hdr), 1.0 / 2.2);
    return float4(srgb, 1.0);
}
