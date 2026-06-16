// P7.2 NO-RT DDGI probe update — per-probe G-buffer RASTER (the no-hardware-ray-tracing far-field).
//
// Renders the scene geometry from a PROBE's position into a small cube-face G-buffer (albedo + world-normal +
// depth), so a later relight pass (P7.2b) can shade it like the deferred pass and a resolve pass (P7.2c) can
// sample 144 ray directions out of the relit cube into the existing DDGI rayData buffer. The octahedral
// irradiance/depth-moments atlas projection + gather are 100% the existing RT DDGI path (Dx12Ddgi) — P7.2 only
// swaps the PRODUCER of rayData (raster instead of inline RayQuery), reusing ~99% of the world cache.
//
// THIS sub-phase (P7.2a) is MEASUREMENT-ONLY: render ONE probe at the camera position, 6 cube faces, so the
// per-probe geometry cost is known before any grid wiring (the user-demanded go/no-go gate — the naive full grid
// is 12,288 geometry passes/frame, impossible; even amortized it is borderline on a GTX-1660). No rayData, no
// blend, no grid yet. A debug blit shows one relit-less albedo face so we can SEE the rasterized probe is correct.
//
// The vertex stage + DrawConstants CBV + 6-material SRV table are IDENTICAL to GBuffer.hlsl so the existing
// per-submesh draw loop drives it unchanged (only the Mvp/Model differ — built from the probe's view-projection).
// We write only albedo + world-normal (2 MRT) + depth — the relight needs albedo, world normal, and world pos
// (reconstructed from depth). No motion/ORM/emissive MRTs (the probe G-buffer is throwaway, relit immediately).

cbuffer DrawConstants : register(b0) {
    float4x4 Mvp;            // model * probeView * probeProj (transposed) — built per face
    float4x4 Model;          // model (transposed) — world normal/pos
    float3   LightDir;       float Exposure;
    float3   LightColor;     float Metallic;
    float3   Ambient;        float Roughness;
    float3   CameraPos;      float SpecularReflectance;
    float4   BaseColorFactor;
    float3   EmissiveFactor; float HasEmissive;
    float    NormalStrength; float NormalFlipY; float HasMetallicMap; float HasRoughnessMap;
    float    PackedOrm;      float Cutout;       float UseIBL; float PrefilterMaxMip;
};

Texture2D DiffuseMap   : register(t0);
Texture2D NormalMap    : register(t1);
Texture2D MetallicMap  : register(t2);
Texture2D RoughnessMap : register(t3);
Texture2D AOMap        : register(t4);
Texture2D EmissiveMap  : register(t5);
SamplerState LinearWrap : register(s0);

struct VSInput {
    float3 Pos : POSITION; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; float4 Tangent : TANGENT;
};
struct VSOutput {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float2 Uv       : TEXCOORD0;
    float3 PosW     : TEXCOORD1;
};
struct ProbeGBufferOut {
    float4 Albedo : SV_Target0;   // rgb albedo (a = emissive flag — relight reads it), linear-ish via UNORM
    float4 Normal : SV_Target1;   // rgb world normal packed [0,1] (a = unused)
};

VSOutput VSMain(VSInput v) {
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), Mvp);
    o.PosW = mul(float4(v.Pos, 1.0), Model).xyz;
    o.NormalW = normalize(mul(float4(v.Normal, 0.0), Model).xyz);
    o.Uv = v.Uv;
    return o;
}

ProbeGBufferOut PSMain(VSOutput i) {
    float4 albedoSample = DiffuseMap.Sample(LinearWrap, i.Uv);
    if (Cutout > 0.5 && albedoSample.a < 0.5) discard;
    float3 albedo = albedoSample.rgb * BaseColorFactor.rgb;

    // Geometric world normal only (no normal-map detail — the probe G-buffer is coarse 24px; tangent-space
    // detail is irrelevant to the cosine-weighted octahedral irradiance and saves a tangent attribute).
    float3 N = normalize(i.NormalW);

    ProbeGBufferOut o;
    o.Albedo = float4(albedo, HasEmissive);
    o.Normal = float4(N * 0.5 + 0.5, 1.0);
    return o;
}
