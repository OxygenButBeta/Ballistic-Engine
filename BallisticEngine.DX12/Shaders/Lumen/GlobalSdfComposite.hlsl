// Lumen FAZ 2 — Global Distance Field COMPOSITE (the sphere-trace debug view is in GlobalSdfDebug.hlsl).
//
// The global distance field (GDF) is a camera-centered 3D clipmap volume texture that UE5's Lumen software ray
// tracing sphere-marches against.
//
//   CSComposite — per CLIPMAP VOXEL (3D dispatch): transform the voxel's world-space center into each overlapping
//     mesh's LOCAL space, trilinearly sample that mesh's per-mesh SDF, and take the MIN signed distance across all
//     overlapping instances (union of solids = min distance). Writes the result into the clipmap 3D texture (R16F,
//     world-unit distances, clamped to ±the clip half-extent). v1 = ONE clip level; the structure leaves room to
//     nest more (UE uses 4). A simple loop over all instances whose mesh-SDF world bounds overlap the clipmap cell
//     neighborhood (instance count capped on the CPU side).
//
// NaN-safe: every divide guards its denominator.

// ============================================================================================================
//  Shared types + constants
// ============================================================================================================

// Per-instance record the composite loops over. World→local maps the voxel into the mesh's SDF grid; the grid
// origin/extent/res describe that mesh's SDF (MeshSdf). SdfTexIndex is the bindless slot of the mesh's 3D SDF
// texture in the ResourceDescriptorHeap (HeapDirectlyIndexed). WorldMin/Max are the instance's SDF world AABB
// (the mesh grid bounds transformed to world) for the cheap overlap reject.
struct SdfInstance {
    float4x4 WorldToLocal;   // world → mesh-local (column-major on the GPU, transposed on upload)
    float3   GridOrigin;     // mesh-local min corner of the SDF grid
    uint     SdfTexIndex;    // bindless SRV index of the Texture3D<float> SDF (uint.MaxValue = no SDF → skip)
    float3   GridExtent;     // mesh-local full size of the SDF grid
    float    MaxLocalDist;   // largest representable |distance| in this grid (for clamping out-of-grid samples)
    float3   WorldMin;       // instance SDF world AABB min (overlap reject)
    float    Pad0;
    float3   WorldMax;       // instance SDF world AABB max
    float    Pad1;
};

cbuffer CompositeConstants : register(b0) {
    float3 ClipOrigin;       // world-space min corner of the clipmap volume (snapped to voxel)
    float  VoxelSize;        // world size of one clipmap voxel (cubic)
    uint3  ClipRes;          // clipmap resolution (voxels per axis)
    uint   InstanceCount;    // number of SdfInstance records (already capped on the CPU)
    float  ClipHalfExtent;   // half the clipmap world extent (the band clamp / "far" distance)
    float3 CompPad;
};

// ============================================================================================================
//  CSComposite — build the clipmap (3D dispatch, one thread per voxel)
// ============================================================================================================

StructuredBuffer<SdfInstance> Instances : register(t0);
RWTexture3D<float>            Clipmap    : register(u0);

// Sample a mesh's per-mesh SDF (bindless Texture3D<float>) at a mesh-LOCAL point, trilinear, clamped to the grid.
// Mirrors MeshSdf.Sample on the CPU: voxel-center space, clamp to [0,res-1], linear filter via a clamp sampler.
SamplerState LinearClamp : register(s0);

float SampleMeshSdfRaw(uint texIndex, float3 gridOrigin, float3 gridExtent, float3 localP) {
    // Normalized [0,1] grid coordinate. The per-mesh SDF stores voxel CENTERS; a clamp-sampled 3D texture maps
    // texel centers to (i+0.5)/res, which matches MeshSdf's voxel-center convention, so a straight normalized
    // sample is the trilinear voxel-center interpolation (clamped at the borders, like MeshSdf.Sample).
    float3 rel = localP - gridOrigin;
    float3 denom = max(gridExtent, float3(1e-6, 1e-6, 1e-6));
    float3 uvw = saturate(rel / denom);
    Texture3D<float> tex = ResourceDescriptorHeap[texIndex];
    return tex.SampleLevel(LinearClamp, uvw, 0);
}

// Signed distance from `p` to the axis-aligned box [bmin,bmax] (exact outside, conservative inside as the
// negative inset). Used as the per-instance distance when the voxel is OUTSIDE that instance's mesh-SDF grid —
// the surface lives inside this box, so this is a guaranteed lower bound on the true distance, hence sphere-
// trace-safe (it can never overshoot the geometry the way a saturated "far" value would).
float BoxSignedDistance(float3 p, float3 bmin, float3 bmax) {
    float3 c = 0.5 * (bmin + bmax);
    float3 h = 0.5 * (bmax - bmin);
    float3 q = abs(p - c) - h;
    float outside = length(max(q, 0.0));
    float inside  = min(max(q.x, max(q.y, q.z)), 0.0);
    return outside + inside;
}

[numthreads(4, 4, 4)]
void CSComposite(uint3 id : SV_DispatchThreadID) {
    if (any(id >= ClipRes)) return;

    // World-space center of this clipmap voxel.
    float3 worldP = ClipOrigin + (float3(id) + 0.5) * VoxelSize;

    // Union of all overlapping mesh SDFs = MIN signed distance. Start at the clip "far" value (the band clamp).
    // CRITICAL for sphere tracing: a voxel OUTSIDE every mesh grid must NOT store ClipHalfExtent (that would let
    // the trace leap a whole clip-extent and skip small geometry — the FAZ 2 fidelity bug). Instead each instance
    // contributes a VALID conservative distance everywhere: its world-AABB exterior distance is a guaranteed lower
    // bound on the true surface distance (the surface is inside the AABB), so the min over instances is a true,
    // monotone, sphere-trace-safe global SDF. Inside an instance's grid we upgrade to the exact mesh-SDF sample.
    float best = ClipHalfExtent;

    for (uint i = 0; i < InstanceCount; ++i) {
        SdfInstance inst = Instances[i];
        if (inst.SdfTexIndex == 0xFFFFFFFFu) continue;   // mesh has no SDF → skip

        // Conservative distance to this instance from its world AABB (valid for ALL voxels, near and far).
        float boxD = BoxSignedDistance(worldP, inst.WorldMin, inst.WorldMax);
        // Coarse cull: if even the AABB exterior distance can't beat the running best, the exact in-grid sample
        // can only be larger (the surface is inside the box) → this instance can't win. Cheap + correct.
        if (boxD >= best) continue;

        float d = boxD;

        // Inside the mesh grid → use the EXACT trilinear mesh-SDF distance. The per-mesh SDF stores the true signed
        // distance to the surface throughout its (padded) grid — negative inside the solid, POSITIVE in hollow
        // interiors (e.g. the empty space inside a Cornell box reads +distance-to-nearest-wall). We must take this
        // value VERBATIM, NOT min(meshD, boxD): boxD is the distance to the object's AABB, which is NEGATIVE
        // anywhere inside the bounding box — min()-ing it in would mark the entire hollow interior as solid (the
        // FAZ 2 "flat blob" bug: the trace then hits the AABB shell and the real walls vanish). boxD is only valid
        // OUTSIDE the grid, where the mesh SDF has no data.
        float3 localP = mul(float4(worldP, 1.0), inst.WorldToLocal).xyz;
        float3 rel = localP - inst.GridOrigin;
        if (all(rel >= 0.0) && all(rel <= inst.GridExtent)) {
            d = SampleMeshSdfRaw(inst.SdfTexIndex, inst.GridOrigin, inst.GridExtent, localP);
        }

        // NOTE (v1 limitation): the mesh SDF is in MESH-LOCAL units. A non-uniformly scaled instance would need
        // the distance rescaled by the world scale; v1 assumes ~uniform scale (true for the GI test scenes). A
        // proper per-axis world-scale correction is a TODO once nested clip levels land.
        best = min(best, d);
    }

    // Clamp to the representable band so the texture never stores ±Inf and the trace reads bounded steps.
    Clipmap[id] = clamp(best, -ClipHalfExtent, ClipHalfExtent);
}

// The sphere-trace DEBUG view lives in GlobalSdfDebug.hlsl (separate TU — two cbuffers can't share register(b0)
// in one file).
