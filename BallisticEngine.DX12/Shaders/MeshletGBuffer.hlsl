// R4 — MESHLET G-buffer pipeline (amplification + mesh shader). The amplification shader frustum/sphere-culls
// meshlets and dispatches the mesh shader only for survivors; the mesh shader emits each surviving meshlet's
// vertices + primitives. SHADING (PSMain) IS BYTE-IDENTICAL to GBufferBindless.hlsl — the vertex transform
// (mul(pos, Mvp)) and material decode are copied verbatim, so a meshlet-drawn surface matches the ExecuteIndirect
// path to the bit. Output = the same fat 4-MRT G-buffer + motion.
//
// One DispatchMesh per submesh: a root constant selects the PerDraw (Mvp/Model/MaterialId) and the meshlet range.

cbuffer DrawIndexCB : register(b0) { uint DrawIndex; uint MeshletBase; uint MeshletCount; uint _pad0; };
cbuffer MotionConstants : register(b1) {
    float4x4 ViewProjCur;
    float4x4 ViewProjPrev;
    float    NormalLodBias;
    float3   _padMotion;
};
cbuffer CullCB : register(b2) {
    float4 Planes[6];   // frustum planes (unjittered viewProj), xyz=normal, w=d
};

struct PerDraw { float4x4 Mvp; float4x4 Model; uint MaterialId; uint3 _pad; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor;
    float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct Meshlet { uint VertOffset, VertCount, PrimOffset, PrimCount; };
struct MeshletBounds { float4 Sphere; float4 Cone; };

StructuredBuffer<PerDraw>       PerDraws     : register(t0);
StructuredBuffer<GpuMaterial>   GpuMaterials : register(t1);
StructuredBuffer<Meshlet>       Meshlets     : register(t2);
StructuredBuffer<MeshletBounds> Bounds       : register(t3);
StructuredBuffer<uint>          MeshletVerts : register(t4);
StructuredBuffer<uint>          MeshletPrims : register(t5);
// Vertex streams (raw — meshlet vert index is global into these): pos/normal/uv/tangent.
StructuredBuffer<float3>        Positions    : register(t6);
StructuredBuffer<float3>        Normals      : register(t7);
StructuredBuffer<float2>        UVs          : register(t8);
StructuredBuffer<float4>        Tangents     : register(t9);
SamplerState LinearWrap : register(s0);

struct VOut {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float4 TangentW : TANGENT;
    float2 Uv       : TEXCOORD0;
    float3 PosW     : TEXCOORD1;
    nointerpolation uint MaterialId : TEXCOORD2;
};

// ---------- amplification: cull meshlets (frustum vs bounding sphere), dispatch survivors ----------
struct Payload { uint MeshletIndices[32]; };
groupshared Payload s_payload;

bool SphereInFrustum(float4 sphere, float4x4 model) {
    // Transform the meshlet's local-space sphere center by the model matrix; radius scaled by the max axis length.
    float3 c = mul(float4(sphere.xyz, 1.0), model).xyz;
    float sx = length(model._m00_m01_m02), sy = length(model._m10_m11_m12), sz = length(model._m20_m21_m22);
    float r = sphere.w * max(sx, max(sy, sz));
    [unroll] for (int i = 0; i < 6; i++)
        if (dot(Planes[i].xyz, c) + Planes[i].w < -r) return false;
    return true;
}

[numthreads(32, 1, 1)]
void ASMain(uint dtid : SV_DispatchThreadID, uint gtid : SV_GroupThreadID) {
    PerDraw pd = PerDraws[DrawIndex];
    bool visible = false;
    uint mi = MeshletBase + dtid;
    if (dtid < MeshletCount) {
        visible = SphereInFrustum(Bounds[mi].Sphere, pd.Model);
    }
    // compact survivors into the payload
    uint slot = WavePrefixCountBits(visible);
    if (visible) s_payload.MeshletIndices[slot] = mi;
    uint count = WaveActiveCountBits(visible);
    DispatchMesh(count, 1, 1, s_payload);
}

// ---------- mesh: emit one meshlet's verts + prims ----------
[numthreads(128, 1, 1)]
[outputtopology("triangle")]
void MSMain(uint gtid : SV_GroupThreadID, uint gid : SV_GroupID, in payload Payload pl,
            out vertices VOut verts[64], out indices uint3 tris[124]) {
    uint mi = pl.MeshletIndices[gid];
    Meshlet m = Meshlets[mi];
    SetMeshOutputCounts(m.VertCount, m.PrimCount);
    PerDraw pd = PerDraws[DrawIndex];

    if (gtid < m.VertCount) {
        uint gv = MeshletVerts[m.VertOffset + gtid];   // global vertex index
        VOut o;
        float3 p = Positions[gv];
        o.Position = mul(float4(p, 1.0), pd.Mvp);        // VERBATIM GBufferBindless::VSMain
        o.PosW     = mul(float4(p, 1.0), pd.Model).xyz;
        o.NormalW  = normalize(mul(float4(Normals[gv], 0.0), pd.Model).xyz);
        float4 tan = Tangents[gv];
        o.TangentW = float4(normalize(mul(float4(tan.xyz, 0.0), pd.Model).xyz), tan.w);
        o.Uv = UVs[gv];
        o.MaterialId = pd.MaterialId;
        verts[gtid] = o;
    }
    if (gtid < m.PrimCount) {
        uint packed = MeshletPrims[m.PrimOffset + gtid];
        tris[gtid] = uint3(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF);
    }
}

// ---------- pixel: BYTE-IDENTICAL to GBufferBindless::PSMain ----------
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

GBufferOut PSMain(VOut i) {
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
