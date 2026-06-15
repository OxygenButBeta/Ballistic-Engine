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
    uint HizEnabled;       // 1 = run the Hi-Z occlusion test (0 = frustum only, e.g. after a big cam jump)
    uint HizIndex;         // bindless index of the Hi-Z pyramid SRV (ResourceDescriptorHeap)
    float4x4 ViewProj;     // UNJITTERED camera view*proj (matches the pyramid) — Hi-Z screen projection
    float4x4 View;         // world -> view, for the AABB's linear view distance
    float4 HizParams;      // x = pyramid width, y = height, z = mip count, w = camera near
    float4 HizFar;         // x = camera far
};

StructuredBuffer<SubmeshMeta> Metas    : register(t0);
RWStructuredBuffer<DrawCommand> Commands : register(u0);
RWStructuredBuffer<PerDraw> PerDraws     : register(u1);
SamplerState PointClamp                  : register(s0);   // point-clamp for the Hi-Z pyramid

bool AabbInFrustum(float3 mn, float3 mx) {
    [unroll] for (int i = 0; i < 6; i++) {
        float4 pl = Planes[i];
        // Positive vertex (farthest along the normal) — identical predicate to the CPU path.
        float3 pv = float3(pl.x >= 0.0 ? mx.x : mn.x, pl.y >= 0.0 ? mx.y : mn.y, pl.z >= 0.0 ? mx.z : mn.z);
        if (pl.x * pv.x + pl.y * pv.y + pl.z * pv.z + pl.w < 0.0) return false;
    }
    return true;
}

// DX window depth [0,1] -> positive linear view distance (clean for a RH z[0,1] perspective).
float linearViewDist(float d) {
    float n = HizParams.w, f = HizFar.x;
    return (n * f) / max(f - d * (f - n), 1e-6);
}

// Conservative Hi-Z occlusion (port of GpuCull_Comp.glsl occludedByHiZ). Projects the world AABB's 8
// corners; bails (visible) on the near plane / screen edge / big footprint / sky; else culls only when the
// nearest corner is strictly behind the MAX occluder depth over the footprint (+ bias). Never false-culls.
bool occludedByHiZ(float3 mn, float3 mx) {
    if (HizEnabled == 0u) return false;
    Texture2D<float> HiZ = ResourceDescriptorHeap[HizIndex];   // SM6.6 bindless
    float2 uvMin = 1e9, uvMax = -1e9;
    float nearDist = 1e9;
    [unroll] for (int c = 0; c < 8; c++) {
        float3 corner = float3((c & 1) == 0 ? mn.x : mx.x, (c & 2) == 0 ? mn.y : mx.y, (c & 4) == 0 ? mn.z : mx.z);
        float4 clip = mul(float4(corner, 1.0), ViewProj);
        if (clip.w <= 1e-5) return false;                 // near plane — don't risk it
        float3 ndc = clip.xyz / clip.w;
        if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0) return false;   // screen edge
        float2 uv = float2(ndc.x * 0.5 + 0.5, -ndc.y * 0.5 + 0.5);   // DX texture uv (y down)
        uvMin = min(uvMin, uv); uvMax = max(uvMax, uv);
        nearDist = min(nearDist, -mul(float4(corner, 1.0), View).z);
    }
    float2 sizePx = (uvMax - uvMin) * HizParams.xy;
    float maxSpanPx = max(sizePx.x, sizePx.y);
    if (maxSpanPx > 0.4 * max(HizParams.x, HizParams.y)) return false;   // big footprint — 5 taps can't bound it
    float level = clamp(ceil(log2(max(maxSpanPx * 0.5, 1.0))), 0.0, HizParams.z - 1.0);
    float o0 = HiZ.SampleLevel(PointClamp, float2(uvMin.x, uvMin.y), level);
    float o1 = HiZ.SampleLevel(PointClamp, float2(uvMax.x, uvMin.y), level);
    float o2 = HiZ.SampleLevel(PointClamp, float2(uvMin.x, uvMax.y), level);
    float o3 = HiZ.SampleLevel(PointClamp, float2(uvMax.x, uvMax.y), level);
    float oc = HiZ.SampleLevel(PointClamp, (uvMin + uvMax) * 0.5, level);
    float maxOcc = max(max(max(o0, o1), max(o2, o3)), oc);
    if (maxOcc >= 1.0) return false;                       // footprint includes sky — never occluded
    float occluderDist = linearViewDist(maxOcc);
    float bias = max(0.5, occluderDist * 0.03);
    return nearDist > occluderDist + bias;
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
    bool visible = (m.IndexCount != 0u) && AabbInFrustum(m.AabbMin.xyz, m.AabbMax.xyz)
                 && !occludedByHiZ(m.AabbMin.xyz, m.AabbMax.xyz);

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
