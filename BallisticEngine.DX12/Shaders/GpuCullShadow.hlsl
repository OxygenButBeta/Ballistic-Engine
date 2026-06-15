// GPU-driven compute cull for the DX12 sun shadow cascades (depth-only). Same order-preserving,
// bit-identical-to-CPU positive-vertex AABB test as GpuCull.hlsl, but per cascade: tests the world AABB
// against THAT cascade's light frustum planes and outputs the per-draw LightMvp (model * cascade view*proj).
// One slice per (cascade, mesh-group); InstanceCount 0/1 keeps the draw order = the CPU shadow path.

struct ShadowMeta {
    float4x4 LightMvp;     // Transpose(model * cascade(view*proj)) — per cascade
    float4 AabbMin; float4 AabbMax;   // world-space AABB (shared across cascades)
    uint FirstIndex, IndexCount, Pad0, Pad1;
};
struct DrawCommand {
    uint DrawIndex;
    uint IndexCountPerInstance; uint InstanceCount; uint StartIndexLocation;
    int  BaseVertexLocation; uint StartInstanceLocation;
};
struct ShadowPerDraw { float4x4 LightMvp; };

cbuffer CullParams : register(b0) {
    float4 Planes[6];      // this cascade's light frustum (normalized, from ExtractFrustumPlanes)
    uint SubmeshCount; uint OutBase; uint _pad0, _pad1;
};

StructuredBuffer<ShadowMeta> Metas       : register(t0);
RWStructuredBuffer<DrawCommand> Commands : register(u0);
RWStructuredBuffer<ShadowPerDraw> PerDraws : register(u1);

bool AabbInFrustum(float3 mn, float3 mx) {
    [unroll] for (int i = 0; i < 6; i++) {
        float4 pl = Planes[i];
        float3 pv = float3(pl.x >= 0.0 ? mx.x : mn.x, pl.y >= 0.0 ? mx.y : mn.y, pl.z >= 0.0 ? mx.z : mn.z);
        if (pl.x * pv.x + pl.y * pv.y + pl.z * pv.z + pl.w < 0.0) return false;
    }
    return true;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;
    if (i >= SubmeshCount) return;
    uint slot = OutBase + i;
    ShadowMeta m = Metas[slot];
    bool visible = (m.IndexCount != 0u) && AabbInFrustum(m.AabbMin.xyz, m.AabbMax.xyz);

    DrawCommand c;
    c.DrawIndex = slot;
    c.IndexCountPerInstance = m.IndexCount;
    c.InstanceCount = visible ? 1u : 0u;
    c.StartIndexLocation = m.FirstIndex;
    c.BaseVertexLocation = 0;
    c.StartInstanceLocation = 0u;
    Commands[slot] = c;

    ShadowPerDraw pd; pd.LightMvp = m.LightMvp; PerDraws[slot] = pd;
}
