// GPU-driven PER-INSTANCE frustum + Hi-Z cull (Unreal ISM/HISM instance-cull equivalent) for the DX12
// renderer. Mirrors GpuCull.hlsl bit-for-bit, but at INSTANCE granularity: today instanced rendering uploads
// ALL instance matrices and draws them with a fixed CPU InstanceCount; this compute pass transforms each
// instance's mesh-local AABB by its instance matrix (the SAME 8-corner loop as the CPU WorldAabb, byte-
// identical), frustum-tests it, Hi-Z occlusion-tests it (reusing GpuCull's pyramid logic), and APPENDS the
// survivor's instance index into a compacted buffer via an atomic counter. A single DrawIndexedInstancedIndirect
// then draws only the survivors (InstanceCount = the GPU counter); the VS reads the compacted instance index ->
// the instance matrix. When every instance is on-screen the survivor set == the full set, so the image matches
// the upload-all path exactly (culling only removes off-screen/occluded instances that contribute nothing).
//
// Compaction is ALLOWED to reorder instances here (unlike the whole-mesh GpuCull which is order-preserving):
// instanced geometry is the SAME mesh+material with depth Less+write, so per-instance draw order can only flip
// pixels at exactly-coplanar inter-instance z-seams — which an instanced field of distinct transforms does not
// produce (each instance occupies its own world AABB). The standard upload-all path also draws in array order
// with no z-seam guarantee, so the depth-resolved result is identical.

struct InstanceData {
    float4x4 Model;        // Transpose(instance world matrix) — read by the VS to place the instance
    float4 AabbMin;        // mesh LOCAL-space AABB min (w unused); transformed per-instance below
    float4 AabbMax;        // mesh LOCAL-space AABB max
};

cbuffer InstCullParams : register(b0) {
    float4 Planes[6];      // xyz = normal, w = d (normalized, from ExtractFrustumPlanes — SAME as the CPU cull)
    uint InstanceCount;    // total instances fed in
    uint HizEnabled;       // 1 = run the Hi-Z occlusion test
    uint HizIndex;         // bindless index of the Hi-Z pyramid SRV (ResourceDescriptorHeap)
    uint _pad0;
    float4x4 ViewProj;     // UNJITTERED camera view*proj (matches the pyramid) — Hi-Z screen projection
    float4x4 View;         // world -> view, for the AABB's linear view distance
    float4 HizParams;      // x = pyramid width, y = height, z = mip count, w = camera near
    float4 HizFar;         // x = camera far
};

StructuredBuffer<InstanceData> Instances    : register(t0);
RWStructuredBuffer<uint> VisibleIndices       : register(u0);   // compacted survivor instance indices
RWStructuredBuffer<uint> DrawArgs             : register(u1);   // [IndexCountPerInstance, InstanceCount, Start, Base, StartInst]
SamplerState PointClamp                        : register(s0);   // point-clamp for the Hi-Z pyramid

// Transform the LOCAL AABB by the instance matrix into a WORLD AABB, via the IDENTICAL 8-corner loop the CPU
// WorldAabb uses. The instance Model is stored TRANSPOSED for the VS (mul(v, M) convention); transpose it back
// here so the corner transform matches the CPU's Vector3.Transform(corner, model) exactly (bit-identical).
void worldAabb(float4x4 modelT, float3 lmn, float3 lmx, out float3 wmn, out float3 wmx) {
    float4x4 model = transpose(modelT);
    wmn = 1e30; wmx = -1e30;
    [unroll] for (int c = 0; c < 8; c++) {
        float3 corner = float3((c & 1) == 0 ? lmn.x : lmx.x, (c & 2) == 0 ? lmn.y : lmx.y, (c & 4) == 0 ? lmn.z : lmx.z);
        float3 w = mul(float4(corner, 1.0), model).xyz;
        wmn = min(wmn, w); wmx = max(wmx, w);
    }
}

bool AabbInFrustum(float3 mn, float3 mx) {
    [unroll] for (int i = 0; i < 6; i++) {
        float4 pl = Planes[i];
        float3 pv = float3(pl.x >= 0.0 ? mx.x : mn.x, pl.y >= 0.0 ? mx.y : mn.y, pl.z >= 0.0 ? mx.z : mn.z);
        if (pl.x * pv.x + pl.y * pv.y + pl.z * pv.z + pl.w < 0.0) return false;
    }
    return true;
}

float linearViewDist(float d) {
    float n = HizParams.w, f = HizFar.x;
    return (n * f) / max(f - d * (f - n), 1e-6);
}

// Project the world AABB's 8 corners -> screen; return pixel span + uv bounds + nearest view distance.
// Identical to GpuCull.hlsl's aabbPixelSpan (the Hi-Z footprint).
float aabbPixelSpan(float3 mn, float3 mx, out float2 uvMin, out float2 uvMax, out float nearDist, out bool offscreen) {
    uvMin = 1e9; uvMax = -1e9; nearDist = 1e9; offscreen = false;
    [unroll] for (int c = 0; c < 8; c++) {
        float3 corner = float3((c & 1) == 0 ? mn.x : mx.x, (c & 2) == 0 ? mn.y : mx.y, (c & 4) == 0 ? mn.z : mx.z);
        float4 clip = mul(float4(corner, 1.0), ViewProj);
        if (clip.w <= 1e-5) { offscreen = true; return 0.0; }
        float3 ndc = clip.xyz / clip.w;
        if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0) { offscreen = true; return 0.0; }
        float2 uv = float2(ndc.x * 0.5 + 0.5, -ndc.y * 0.5 + 0.5);
        uvMin = min(uvMin, uv); uvMax = max(uvMax, uv);
        nearDist = min(nearDist, -mul(float4(corner, 1.0), View).z);
    }
    float2 sizePx = (uvMax - uvMin) * HizParams.xy;
    return max(sizePx.x, sizePx.y);
}

// Conservative Hi-Z occlusion (verbatim port of GpuCull.hlsl occludedByHiZ). Never false-culls.
bool occludedByHiZ(float3 mn, float3 mx) {
    if (HizEnabled == 0u) return false;
    Texture2D<float> HiZ = ResourceDescriptorHeap[HizIndex];
    float2 uvMin, uvMax; float nearDist; bool offscreen;
    float maxSpanPx = aabbPixelSpan(mn, mx, uvMin, uvMax, nearDist, offscreen);
    if (offscreen) return false;
    if (maxSpanPx > 0.4 * max(HizParams.x, HizParams.y)) return false;
    float level = clamp(ceil(log2(max(maxSpanPx * 0.5, 1.0))), 0.0, HizParams.z - 1.0);
    float o0 = HiZ.SampleLevel(PointClamp, float2(uvMin.x, uvMin.y), level);
    float o1 = HiZ.SampleLevel(PointClamp, float2(uvMax.x, uvMin.y), level);
    float o2 = HiZ.SampleLevel(PointClamp, float2(uvMin.x, uvMax.y), level);
    float o3 = HiZ.SampleLevel(PointClamp, float2(uvMax.x, uvMax.y), level);
    float oc = HiZ.SampleLevel(PointClamp, (uvMin + uvMax) * 0.5, level);
    float maxOcc = max(max(max(o0, o1), max(o2, o3)), oc);
    if (maxOcc >= 1.0) return false;
    float occluderDist = linearViewDist(maxOcc);
    float bias = max(0.5, occluderDist * 0.03);
    return nearDist > occluderDist + bias;
}

// One thread per instance. Survivors atomically append their index into VisibleIndices; the atomic counter
// becomes the indirect draw's InstanceCount (DrawArgs[1]). The other DrawArgs fields are pre-seeded on the CPU
// (the mesh's index count etc.) and left untouched here.
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;
    if (i >= InstanceCount) return;
    InstanceData inst = Instances[i];
    float3 wmn, wmx;
    worldAabb(inst.Model, inst.AabbMin.xyz, inst.AabbMax.xyz, wmn, wmx);
    bool visible = AabbInFrustum(wmn, wmx) && !occludedByHiZ(wmn, wmx);
    if (!visible) return;
    uint slot;
    InterlockedAdd(DrawArgs[1], 1u, slot);   // DrawArgs[1] == InstanceCount; returns the pre-add value as our slot
    VisibleIndices[slot] = i;
}
