// R5 — VISIBILITY BUFFER raster (mesh-shader). Reuses R4's amplification + mesh-shader meshlet pipeline, but the
// pixel shader writes ONLY a visibility id { DrawIndex, GlobalMeshletIndex, LocalPrimIndex } (packed into RG32_UINT)
// + depth — no shading, no fat-G-buffer MRT writes. A later compute pass (VisResolve.hlsl) reads the id buffer,
// fetches the hit triangle's verts, perspective-correctly interpolates attributes, computes UV gradients manually,
// decodes the material EXACTLY like GBufferBindless::PSMain, and fills the SAME fat G-buffer the deferred lighting
// reads — so downstream (lighting / Lumen / SSR) is unchanged. Bandwidth win: geometry writes 1 RG32 RT, not 5.

cbuffer DrawIndexCB : register(b0) { uint DrawIndex; uint MeshletBase; uint MeshletCount; uint _pad0; };
cbuffer CullCB : register(b2) {
    float4 Planes[6];
    float4 CameraPosCull;
    float4x4 HizViewProj;
    float4x4 HizView;
    float4 HizParams;
    float4 HizFar;
};

struct PerDraw { float4x4 Mvp; float4x4 Model; uint MaterialId; uint3 _pad; };
struct Meshlet { uint VertOffset, VertCount, PrimOffset, PrimCount; };
struct MeshletBounds { float4 Sphere; float4 Cone; };

StructuredBuffer<PerDraw>       PerDraws     : register(t0);
StructuredBuffer<Meshlet>       Meshlets     : register(t2);
StructuredBuffer<MeshletBounds> Bounds       : register(t3);
StructuredBuffer<uint>          MeshletVerts : register(t4);
StructuredBuffer<uint>          MeshletPrims : register(t5);
StructuredBuffer<float3>        Positions    : register(t6);
SamplerState PointClamp : register(s1);

struct VOut {
    float4 Position : SV_Position;
    nointerpolation uint MeshletIdx : TEXCOORD0;   // GLOBAL meshlet index (for the resolve to fetch verts)
};

struct Payload { uint MeshletIndices[32]; };
groupshared Payload s_payload;

bool SphereInFrustum(float4 sphere, float4x4 model) {
    float3 c = mul(float4(sphere.xyz, 1.0), model).xyz;
    float sx = length(model._m00_m01_m02), sy = length(model._m10_m11_m12), sz = length(model._m20_m21_m22);
    float r = sphere.w * max(sx, max(sy, sz));
    [unroll] for (int i = 0; i < 6; i++)
        if (dot(Planes[i].xyz, c) + Planes[i].w < -r) return false;
    return true;
}
bool ConeBackface(float4 sphere, float4 cone, float4x4 model) {
    if (cone.w < 0.0 || CameraPosCull.w < 0.5) return false;
    float3 axisW = normalize(mul(float4(cone.xyz, 0.0), model).xyz);
    float3 centerW = mul(float4(sphere.xyz, 1.0), model).xyz;
    float3 centerDir = normalize(centerW - CameraPosCull.xyz);
    float sinSpread = sqrt(saturate(1.0 - cone.w * cone.w));
    return dot(centerDir, axisW) > sinSpread;
}

[numthreads(32, 1, 1)]
void ASMain(uint dtid : SV_DispatchThreadID) {
    PerDraw pd = PerDraws[DrawIndex];
    bool visible = false;
    uint mi = MeshletBase + dtid;
    if (dtid < MeshletCount)
        visible = SphereInFrustum(Bounds[mi].Sphere, pd.Model) && !ConeBackface(Bounds[mi].Sphere, Bounds[mi].Cone, pd.Model);
    uint slot = WavePrefixCountBits(visible);
    if (visible) s_payload.MeshletIndices[slot] = mi;
    uint count = WaveActiveCountBits(visible);
    DispatchMesh(count, 1, 1, s_payload);
}

[numthreads(128, 1, 1)]
[outputtopology("triangle")]
void MSMain(uint gtid : SV_GroupThreadID, uint gid : SV_GroupID, in payload Payload pl,
            out vertices VOut verts[64], out indices uint3 tris[124], out primitives uint primMeshlet[124] : MESHLETIDX) {
    uint mi = pl.MeshletIndices[gid];
    Meshlet m = Meshlets[mi];
    SetMeshOutputCounts(m.VertCount, m.PrimCount);
    PerDraw pd = PerDraws[DrawIndex];
    if (gtid < m.VertCount) {
        uint gv = MeshletVerts[m.VertOffset + gtid];
        VOut o;
        o.Position = mul(float4(Positions[gv], 1.0), pd.Mvp);
        o.MeshletIdx = mi;
        verts[gtid] = o;
    }
    if (gtid < m.PrimCount) {
        uint packed = MeshletPrims[m.PrimOffset + gtid];
        tris[gtid] = uint3(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF);
        // Pack { meshletIndex (24b) | localPrim (8b) } so the resolve can recover BOTH the meshlet (for VertOffset)
        // and the local triangle (for the packed local-vert triple). localPrim < 124 fits in 8 bits.
        primMeshlet[gtid] = (mi << 8) | (gtid & 0xFF);
    }
}

// PS: write the visibility id. RG32_UINT = { DrawIndex, (meshletIndex<<8)|localPrim }.
struct VisOut { uint2 Id : SV_Target0; };
VisOut PSMain(VOut i, uint primMeshlet : MESHLETIDX) {
    VisOut o;
    o.Id = uint2(DrawIndex, primMeshlet);
    return o;
}
