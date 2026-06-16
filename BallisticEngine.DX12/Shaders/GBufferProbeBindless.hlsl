// P7.2b NO-RT DDGI raster-probe LEAN G-buffer geometry shader (the reduced-geometry proxy for the no-hardware-
// ray-tracing far-field). See Docs/Plans/dx12-lumen-gi-plan.md Phase 7 + Dx12GpuDrivenRenderer.RenderIntoProbe.
//
// This is GBufferBindless.hlsl STRIPPED to what a probe-cube G-buffer needs: it reuses the EXACT same compute
// cull + ExecuteIndirect + command signature + bindless material table as the camera GPU-driven draw, but the
// PIXEL stage emits only 2 MRTs (albedo + world-normal) into the lean 24px probe cube (Dx12RasterProbe) instead
// of the camera's fat 5-MRT G-buffer. NO motion (the probe G-buffer is throwaway, relit immediately), NO
// metallic/roughness/AO/emissive MRTs (the relight pass — P7.2b relight — reconstructs world pos from depth and
// shades albedo*irradiance; tangent-space normal detail is irrelevant to the cosine-weighted octahedral
// irradiance at 24px, so geometric normal only, matching RasterProbe.hlsl's per-submesh probe shader).
//
// Bindings MIRROR GBufferBindless.hlsl so the SAME drawRootSig + cmdSig drive it (the lean PSO is built against
// drawRootSig): root const b0 (DrawIndex per indirect command), SRV t0 PerDraws (Vertex), SRV t1 GpuMaterials
// (Pixel), bindless ResourceDescriptorHeap, sampler s0. Root param 3 (CBV b1 MotionConstants) STAYS in the root
// sig but is LEFT UNBOUND by the probe draw — legal because NO stage here reads b1 (no ScreenMotion). The VS is
// byte-identical to GBufferBindless.VSMain MINUS the tangent/motion outputs the lean PS doesn't consume.

cbuffer DrawIndexCB : register(b0) { uint DrawIndex; uint3 _pad0; };   // set per indirect command (cmdSig Constant)

struct PerDraw { float4x4 Mvp; float4x4 Model; uint MaterialId; uint3 _pad; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor;
    float4 EmissiveFactor;     // xyz = emissiveColor * intensity
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
StructuredBuffer<PerDraw>     PerDraws     : register(t0);
StructuredBuffer<GpuMaterial> GpuMaterials : register(t1);
SamplerState LinearWrap : register(s0);

struct VSInput {
    float3 Pos : POSITION; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; float4 Tangent : TANGENT;
};
struct VSOutput {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float2 Uv       : TEXCOORD0;
    nointerpolation uint MaterialId : TEXCOORD1;
};
struct ProbeGBufferOut {
    float4 Albedo : SV_Target0;   // rgb albedo (a = emissive flag — relight reads it)
    float4 Normal : SV_Target1;   // rgb world normal packed [0,1] (a = unused)
};

VSOutput VSMain(VSInput v) {
    PerDraw pd = PerDraws[DrawIndex];
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), pd.Mvp);          // identical op to GBufferBindless mul(pos, Mvp)
    o.NormalW  = normalize(mul(float4(v.Normal, 0.0), pd.Model).xyz);
    o.Uv = v.Uv;
    o.MaterialId = pd.MaterialId;
    return o;
}

ProbeGBufferOut PSMain(VSOutput i) {
    GpuMaterial m = GpuMaterials[i.MaterialId];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];

    float4 albedoSample = diffuseMap.Sample(LinearWrap, i.Uv);
    if (m.Cutout > 0.5 && albedoSample.a < 0.5) discard;
    float3 albedo = albedoSample.rgb * m.BaseColorFactor.rgb;

    // Geometric world normal only — no normal-map detail (coarse 24px probe G-buffer; irrelevant to the cosine-
    // weighted octahedral irradiance, and saves the tangent attribute + a sample). Matches RasterProbe.hlsl.
    float3 N = normalize(i.NormalW);

    ProbeGBufferOut o;
    o.Albedo = float4(albedo, m.HasEmissive);
    o.Normal = float4(N * 0.5 + 0.5, 1.0);
    return o;
}
