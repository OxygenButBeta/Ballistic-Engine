// Minimal forward OPAQUE shader for the DX12 backend (Phase 2d first light on a real scene).
// Diffuse-texture * (directional N·L + ambient). NOT full PBR yet — normal/roughness/metallic/IBL/
// shadows layer on in later milestones, same staging the GL renderer went through. The point of this
// pass is to prove the real path end-to-end: engine mesh buffers -> input layout -> MVP/Model CBV ->
// per-material diffuse SRV+sampler -> depth-tested draw -> readback.
//
// CONVENTIONS (locked, see DX12Migration.md):
//  - System.Numerics matrices are row-major; HLSL float4x4 is column-major by default. The CPU
//    TRANSPOSES on upload, so here mul(float4(pos,1), MVP) matches the CPU math.
//  - Vertex attributes arrive in SEPARATE input slots (the engine keeps position/normal/uv/tangent in
//    separate GPU buffers, like GL attrib locations 0-3), not interleaved.

cbuffer DrawConstants : register(b0) {
    float4x4 Mvp;          // model * view * proj  (transposed on upload)
    float4x4 Model;        // model               (transposed on upload) — for world-space normals
    float3   LightDir;     // TO the light, normalized, world space
    float    _pad0;
    float3   LightColor;   // sun radiance (already pre-exposed-ish for the minimal path)
    float    _pad1;
    float3   Ambient;      // flat ambient fill (stands in for IBL until that phase)
    float    Exposure;     // linear pre-tonemap scale (the sun radiance is HDR/lux-scaled)
    float4   BaseColorFactor; // glTF base-color tint (rgb used; a alpha for later cutout)
};

Texture2D    DiffuseMap : register(t0);
SamplerState LinearWrap : register(s0);

struct VSInput {
    float3 Pos     : POSITION;   // slot 0
    float3 Normal  : NORMAL;     // slot 1
    float2 Uv      : TEXCOORD0;  // slot 2
    float4 Tangent : TANGENT;    // slot 3 (unused this milestone; kept so the layout matches the mesh)
};

struct VSOutput {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float2 Uv       : TEXCOORD0;
};

VSOutput VSMain(VSInput v) {
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), Mvp);
    // World-space normal: rotate by Model's upper-3x3. Uniform/rigid transforms dominate scene content;
    // a proper inverse-transpose comes with the PBR milestone (matches GL's normalMatrix handling).
    o.NormalW = normalize(mul(float4(v.Normal, 0.0), Model).xyz);
    o.Uv = v.Uv;
    return o;
}

// Hill ACES filmic tonemap (the same operator the GL composite uses), so HDR sun radiance lands in a
// viewable LDR range instead of clipping to white. Proper auto-exposure + the full composite come later;
// a fixed Exposure scale is enough for first light.
float3 ACESFilm(float3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

float4 PSMain(VSOutput i) : SV_Target {
    // Diffuse map is sRGB-typed (the SRV format linearizes on sample), so albedo is already linear.
    float3 albedo = DiffuseMap.Sample(LinearWrap, i.Uv).rgb * BaseColorFactor.rgb;
    float3 N = normalize(i.NormalW);
    float  ndotl = saturate(dot(N, LightDir));
    float3 litHdr = albedo * (Ambient + LightColor * ndotl);

    float3 mapped = ACESFilm(litHdr * Exposure);
    // Back to sRGB for the UNORM (non-sRGB) backbuffer the readback writes as a BMP.
    float3 srgb = pow(mapped, 1.0 / 2.2);
    return float4(srgb, 1.0);
}
