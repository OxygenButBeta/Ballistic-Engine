// GPU-driven compute frustum cull for the DX12 whole-mesh renderer (clustered-deferred geometry pass).
// Mirrors the CPU AabbInFrustum bit-for-bit — positive-vertex test against the SAME normalized planes,
// over a WORLD-space AABB the CPU pre-transforms with the identical 8-corner loop — so the visible set
// (hence the G-buffer) is byte-identical to the CPU per-submesh cull. Compacts the survivors into an
// ExecuteIndirect command list + a per-draw buffer (Mvp/Model/MaterialId), indexed by the emitted draw.

struct SubmeshMeta {
    float4x4 Mvp;          // Transpose(model*viewProj) — per-draw, == CPU DrawConstants.Mvp (byte-identical)
    float4x4 Model;        // Transpose(model)
    float4 AabbMin;        // world-space AABB (CPU 8-corner transform); w unused
    float4 AabbMax;
    uint FirstIndex; uint IndexCount; uint MaterialId; uint Flags;
};
// [drawIndex root-constant][D3D12 DrawIndexedArguments] — matches the command-signature layout exactly.
struct DrawCommand {
    uint DrawIndex;
    uint IndexCountPerInstance; uint InstanceCount; uint StartIndexLocation;
    int  BaseVertexLocation; uint StartInstanceLocation;
};
struct PerDraw { float4x4 Mvp; float4x4 Model; uint MaterialId; uint3 _pad; };

cbuffer CullParams : register(b0) {
    float4 Planes[6];      // xyz = normal, w = d (normalized, from ExtractFrustumPlanes)
    uint SubmeshCount;     // submeshes in THIS mesh group
    uint OutBase;          // absolute base index of this group's slice in the shared buffers
    uint _pad0, _pad1;
};

StructuredBuffer<SubmeshMeta> Metas    : register(t0);
RWStructuredBuffer<DrawCommand> Commands : register(u0);
RWStructuredBuffer<PerDraw> PerDraws     : register(u1);

bool AabbInFrustum(float3 mn, float3 mx) {
    [unroll] for (int i = 0; i < 6; i++) {
        float4 pl = Planes[i];
        // Positive vertex (farthest along the normal) — identical predicate to the CPU path.
        float3 pv = float3(pl.x >= 0.0 ? mx.x : mn.x, pl.y >= 0.0 ? mx.y : mn.y, pl.z >= 0.0 ? mx.z : mn.z);
        if (pl.x * pv.x + pl.y * pv.y + pl.z * pv.z + pl.w < 0.0) return false;
    }
    return true;
}

// ORDER-PRESERVING (non-compacted): each submesh writes its OWN slot, with InstanceCount 1 (visible) or 0
// (culled — the GPU skips zero-instance indirect draws cheaply). This keeps the draw order identical to the
// CPU per-submesh loop, so the deferred G-buffer (depth Less + write, no z-prepass) is byte-identical even
// at coplanar/z-fighting seams (atomic compaction would race the order and flip those pixels).
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;
    if (i >= SubmeshCount) return;
    uint slot = OutBase + i;
    SubmeshMeta m = Metas[slot];
    bool visible = (m.IndexCount != 0u) && AabbInFrustum(m.AabbMin.xyz, m.AabbMax.xyz);

    DrawCommand c;
    c.DrawIndex = slot;
    c.IndexCountPerInstance = m.IndexCount;
    c.InstanceCount = visible ? 1u : 0u;
    c.StartIndexLocation = m.FirstIndex;
    c.BaseVertexLocation = 0;
    c.StartInstanceLocation = 0u;
    Commands[slot] = c;

    PerDraw pd;
    pd.Mvp = m.Mvp; pd.Model = m.Model; pd.MaterialId = m.MaterialId; pd._pad = uint3(0u, 0u, 0u);
    PerDraws[slot] = pd;
}
