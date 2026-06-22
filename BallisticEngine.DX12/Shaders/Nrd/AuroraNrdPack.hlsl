// Packs Aurora's G-buffer signals into the exact formats NRD/ReBLUR expects for its non-noisy guides:
//   OUT_VIEWZ            (R16F)          — linear view-space Z (NOT NDC depth)
//   OUT_NORMAL_ROUGHNESS (R10G10B10A2)   — NRD_FrontEnd_PackNormalAndRoughness (oct-packed, roughness=1 diffuse)
//   OUT_MV               (RGBA16F)       — 2.5D screen motion: .xy = prevUv - uv (pixels), .z = viewZprev - viewZ
//
// NRD.hlsli + NRDConfig.hlsli are PREPENDED to this source at compile time (Dx12NrdPackPipeline), so the encoding
// matches NRD's back-end byte-for-byte — no risk of a hand-rolled oct-pack disagreeing with the library.

cbuffer NrdPackConstants : register(b0) {
    float4x4 InvViewProj;      // current  NDC → world (jittered out: use unjittered)
    float4x4 PrevViewProj;     // previous world → NDC (unjittered)
    float4x4 ViewMatrix;       // world → view (for linear viewZ)
    float2   InvResolution;    // 1 / (w,h)
    float2   _Pad0;
};

Texture2D<float>  Depth   : register(t0);   // NDC depth (R32F / depth SRV)
Texture2D<float4> Normal  : register(t1);   // world normal packed [0,1] (RT2)
RWTexture2D<float4> OutMv            : register(u0);   // RGBA16F 2.5D motion
RWTexture2D<float4> OutNormalRough   : register(u1);   // R10G10B10A2 packed normal+roughness
RWTexture2D<float>  OutViewZ         : register(u2);   // R16F linear viewZ

float3 WorldFromNdc(float2 uv, float depth) {
    float4 ndc = float4(uv * float2(2, -2) + float2(-1, 1), depth, 1);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint w, h; OutViewZ.GetDimensions(w, h);
    uint2 px = dtid.xy;
    if (px.x >= w || px.y >= h) return;

    float2 uv = (float2(px) + 0.5) * InvResolution;
    float depth = Depth.Load(int3(px, 0));

    // Sky / background: NRD wants viewZ > denoisingRange for invalid pixels. Use a large value.
    if (depth >= 1.0) {
        OutViewZ[px] = 1e6;
        OutNormalRough[px] = NRD_FrontEnd_PackNormalAndRoughness(float3(0, 0, 1), 1.0, 0.0);
        OutMv[px] = 0.0.xxxx;
        return;
    }

    float3 worldPos = WorldFromNdc(uv, depth);
    float viewZ = mul(float4(worldPos, 1.0), ViewMatrix).z;   // linear view-space Z
    OutViewZ[px] = viewZ;

    // Normal: G-buffer stores world normal as [0,1]; un-bias. Aurora GI is diffuse → roughness 1, materialID 0.
    float3 N = normalize(Normal.Load(int3(px, 0)).rgb * 2.0 - 1.0);
    OutNormalRough[px] = NRD_FrontEnd_PackNormalAndRoughness(N, 1.0, 0.0);

    // 2.5D motion: reproject this world point through the PREVIOUS view-proj → prevUv. mv.xy = prevUv - uv (in UV).
    // NRD's motionVectorScale defaults to (1,1,0) with mv = prev - current; we feed UV-space delta and set z to the
    // viewZ delta (2.5D). Static camera + static scene → mv ≈ 0 (the convergence case we care about).
    float4 prevClip = mul(float4(worldPos, 1.0), PrevViewProj);
    float2 prevUv = (prevClip.w > 1e-6) ? (prevClip.xy / prevClip.w) * float2(0.5, -0.5) + 0.5 : uv;
    // (We can't know viewZprev without the previous depth buffer; for a static-scene convergence test the world
    // point is the same, so viewZprev == viewZ → .z = 0. A moving object would need prev viewZ; deferred.)
    OutMv[px] = float4((prevUv - uv), 0.0, 0.0);
}
