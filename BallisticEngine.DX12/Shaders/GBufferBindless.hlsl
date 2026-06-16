// Bindless GPU-driven G-buffer geometry shader. SHADING IS BYTE-IDENTICAL to GBuffer.hlsl — only the
// inputs move: the per-draw model (Mvp/Model) comes from PerDraws[DrawIndex] (DrawIndex = an ExecuteIndirect
// root constant) and the material factors + 6 textures come from GpuMaterials[MaterialId] via SM6.6
// ResourceDescriptorHeap (bindless). So ONE ExecuteIndirect draws all visible submeshes regardless of
// material — no per-draw CBV/descriptor-table rebinding. Output = the same fat 4-MRT G-buffer.

cbuffer DrawIndexCB : register(b0) { uint DrawIndex; uint3 _pad0; };   // set per indirect command

// Per-pass motion constants (b1): UNJITTERED current + previous frame view*proj (transposed). Identical
// to GBuffer.hlsl — the PS reprojects PosW through both for a jitter-free motion vector (TAA + FSR).
cbuffer MotionConstants : register(b1) {
    float4x4 ViewProjCur;    // current frame, UNJITTERED (transposed)
    float4x4 ViewProjPrev;   // previous frame, UNJITTERED (transposed)
};

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
    float4 TangentW : TANGENT;
    float2 Uv       : TEXCOORD0;
    float3 PosW     : TEXCOORD1;
    nointerpolation uint MaterialId : TEXCOORD2;
};
struct GBufferOut {
    float4 Albedo   : SV_Target0;
    float4 Normal   : SV_Target1;
    float4 Material  : SV_Target2;
    float4 Emissive : SV_Target3;
    float2 Motion   : SV_Target4;   // screen-space motion (prevUV - currUV), UNJITTERED
};

float2 ScreenMotion(float3 posW) {
    float4 clipCur  = mul(float4(posW, 1.0), ViewProjCur);
    float4 clipPrev = mul(float4(posW, 1.0), ViewProjPrev);
    float2 uvCur  = (clipCur.xy  / clipCur.w)  * float2(0.5, -0.5) + 0.5;
    float2 uvPrev = (clipPrev.xy / clipPrev.w) * float2(0.5, -0.5) + 0.5;
    return uvPrev - uvCur;
}

VSOutput VSMain(VSInput v) {
    PerDraw pd = PerDraws[DrawIndex];
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), pd.Mvp);          // identical op to CPU GBuffer mul(pos, Mvp)
    o.PosW     = mul(float4(v.Pos, 1.0), pd.Model).xyz;
    o.NormalW  = normalize(mul(float4(v.Normal, 0.0), pd.Model).xyz);
    o.TangentW = float4(normalize(mul(float4(v.Tangent.xyz, 0.0), pd.Model).xyz), v.Tangent.w);
    o.Uv = v.Uv;
    o.MaterialId = pd.MaterialId;
    return o;
}

float3 NormalFromMap(Texture2D normalMap, float normalFlipY, float normalStrength,
                     float2 uv, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = normalMap.Sample(LinearWrap, uv).rg;
    if (normalFlipY > 0.5) nxy.y = 1.0 - nxy.y;
    float2 xy = (nxy * 2.0 - 1.0) * max(normalStrength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    float3 tn = normalize(float3(xy, z));
    float3 N = normalize(Ngeom);
    float3 Tn = normalize(T - N * dot(N, T));
    float3 B = cross(N, Tn) * bitangentSign;
    return normalize(Tn * tn.x + B * tn.y + N * tn.z);
}

GBufferOut PSMain(VSOutput i) {
    GpuMaterial m = GpuMaterials[i.MaterialId];
    Texture2D diffuseMap   = ResourceDescriptorHeap[m.DiffuseIdx];
    Texture2D normalMap    = ResourceDescriptorHeap[m.NormalIdx];
    Texture2D metallicMap  = ResourceDescriptorHeap[m.MetallicIdx];
    Texture2D roughnessMap = ResourceDescriptorHeap[m.RoughnessIdx];
    Texture2D aoMap        = ResourceDescriptorHeap[m.AoIdx];
    Texture2D emissiveMap  = ResourceDescriptorHeap[m.EmissiveIdx];

    float4 albedoSample = diffuseMap.Sample(LinearWrap, i.Uv);
    if (m.Cutout > 0.5 && albedoSample.a < 0.5) discard;
    float3 albedo = albedoSample.rgb * m.BaseColorFactor.rgb;

    float3 mr = metallicMap.Sample(LinearWrap, i.Uv).rgb;
    float metallicSample = m.HasMetallicMap > 0.5 ? (m.PackedOrm > 0.5 ? mr.b : mr.r) : 1.0;
    float metallic = saturate(metallicSample * m.Metallic);
    float roughSample = m.HasRoughnessMap > 0.5 ? roughnessMap.Sample(LinearWrap, i.Uv).r
                                                : (m.PackedOrm > 0.5 ? mr.g : 1.0);
    float roughness = clamp(roughSample * m.Roughness, 0.045, 1.0);
    float ao = aoMap.Sample(LinearWrap, i.Uv).r;

    float3 N = NormalFromMap(normalMap, m.NormalFlipY, m.NormalStrength, i.Uv, i.NormalW, i.TangentW.xyz, i.TangentW.w);
    float3 emissive = (m.HasEmissive > 0.5) ? emissiveMap.Sample(LinearWrap, i.Uv).rgb * m.EmissiveFactor.rgb : 0.0.xxx;

    GBufferOut o;
    o.Albedo   = float4(albedo, m.SpecularReflectance);
    o.Normal   = float4(N * 0.5 + 0.5, 1.0);
    o.Material = float4(metallic, roughness, ao, m.Cutout > 0.5 ? 1.0 : 0.0);
    o.Emissive = float4(emissive, 1.0);
    o.Motion   = ScreenMotion(i.PosW);
    return o;
}
