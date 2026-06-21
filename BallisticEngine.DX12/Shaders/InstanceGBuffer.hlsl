// Instanced bindless G-buffer geometry shader. The PS is a VERBATIM copy of GBufferBindless.hlsl's PSMain
// (shading byte-identical to GBuffer.hlsl). The ONLY difference is the VS input plumbing: instead of one
// PerDraw[DrawIndex], the instance's world matrix is read from Instances[VisibleIndices[SV_InstanceID]] —
// i.e. the COMPACTED survivor index the InstanceCull.hlsl compute pass produced. A single
// DrawIndexedInstancedIndirect draws InstanceCount survivors; each instance places itself via its matrix.
//
// The material is the SAME for all instances of a draw (one mesh + one material per RenderInstancing call), so
// MaterialId comes from a root constant (b0), not a per-instance buffer. ViewProj is supplied per pass (b2).

cbuffer InstDrawCB : register(b0) { uint MaterialId; uint3 _pad0; };

cbuffer MotionConstants : register(b1) {
    float4x4 ViewProjCur;    // current frame, UNJITTERED (transposed)
    float4x4 ViewProjPrev;   // previous frame, UNJITTERED (transposed)
    float    NormalLodBias;
    float3   _padMotion;
};

cbuffer InstViewCB : register(b2) {
    float4x4 ViewProj;       // jittered camera view*proj (transposed) — the on-screen MVP factor
    float4x4 _padView;
};

struct InstanceData { float4x4 Model; float4 AabbMin; float4 AabbMax; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor;
    float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
StructuredBuffer<InstanceData> Instances      : register(t0);
StructuredBuffer<uint>         VisibleIndices : register(t1);   // compacted survivor instance indices
StructuredBuffer<GpuMaterial>  GpuMaterials   : register(t2);
SamplerState LinearWrap : register(s0);

struct VSInput {
    float3 Pos : POSITION; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; float4 Tangent : TANGENT;
    uint InstanceID : SV_InstanceID;
};
struct VSOutput {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float4 TangentW : TANGENT;
    float2 Uv       : TEXCOORD0;
    float3 PosW     : TEXCOORD1;
    nointerpolation uint MatId : TEXCOORD2;
};
struct GBufferOut {
    float4 Albedo   : SV_Target0;
    float4 Normal   : SV_Target1;
    float4 Material  : SV_Target2;
    float4 Emissive : SV_Target3;
    float2 Motion   : SV_Target4;
};

float2 ScreenMotion(float3 posW) {
    float4 clipCur  = mul(float4(posW, 1.0), ViewProjCur);
    float4 clipPrev = mul(float4(posW, 1.0), ViewProjPrev);
    float2 uvCur  = (clipCur.xy  / clipCur.w)  * float2(0.5, -0.5) + 0.5;
    float2 uvPrev = (clipPrev.xy / clipPrev.w) * float2(0.5, -0.5) + 0.5;
    return uvPrev - uvCur;
}

VSOutput VSMain(VSInput v) {
    uint idx = VisibleIndices[v.InstanceID];     // compacted survivor -> original instance index
    float4x4 model = Instances[idx].Model;       // stored TRANSPOSED (mul(v, M) convention, like PerDraw.Model)
    float4x4 mvp   = mul(model, ViewProj);       // == Transpose(world*viewProj) used as mul(pos, mvp)
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), mvp);
    o.PosW     = mul(float4(v.Pos, 1.0), model).xyz;
    o.NormalW  = normalize(mul(float4(v.Normal, 0.0), model).xyz);
    o.TangentW = float4(normalize(mul(float4(v.Tangent.xyz, 0.0), model).xyz), v.Tangent.w);
    o.Uv = v.Uv;
    o.MatId = MaterialId;
    return o;
}

float3 NormalFromMap(Texture2D normalMap, float normalFlipY, float normalStrength,
                     float2 uv, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = normalMap.SampleBias(LinearWrap, uv, NormalLodBias).rg;
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
    GpuMaterial m = GpuMaterials[i.MatId];
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
