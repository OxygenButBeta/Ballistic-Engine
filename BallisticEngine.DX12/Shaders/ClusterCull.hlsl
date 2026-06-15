// GPU froxel (clustered) punctual-light cull — one thread per cluster. Byte-identical to the CPU
// Dx12ClusteredLights.Cull: the SAME sphere-vs-AABB test (exact per-axis SqDist), over the SAME
// CPU-computed view-space light positions + cluster AABBs (uploaded), appending light indices in ascending
// order per cluster. The flat index list's GLOBAL layout differs (clusters race for the atomic offset), but
// each cluster's {offset,count} grid entry points at its own run, so the deferred lighting reads identically.

struct CullParams { uint LightCount, ClusterCount, MaxIndices, MaxPerCluster; };
ConstantBuffer<CullParams> P : register(b0);

StructuredBuffer<float4> LightViewPos : register(t0);   // xyz view-space pos, w range
StructuredBuffer<float4> ClusterMin   : register(t1);
StructuredBuffer<float4> ClusterMax   : register(t2);
RWByteAddressBuffer Grid      : register(u0);   // [c*8] offset(int), [c*8+4] count(int) — matches Buffer<int2>
RWByteAddressBuffer IndexList : register(u1);   // uint per element
RWByteAddressBuffer Counter   : register(u2);   // [0] = global cursor

// Exact replica of the CPU SqDistPointAabb (per-axis branch + sequential accumulate) for byte-identical culling.
float SqDistPointAabb(float3 p, float3 lo, float3 hi) {
    float d = 0.0;
    if (p.x < lo.x) d += (lo.x - p.x) * (lo.x - p.x); else if (p.x > hi.x) d += (p.x - hi.x) * (p.x - hi.x);
    if (p.y < lo.y) d += (lo.y - p.y) * (lo.y - p.y); else if (p.y > hi.y) d += (p.y - hi.y) * (p.y - hi.y);
    if (p.z < lo.z) d += (lo.z - p.z) * (lo.z - p.z); else if (p.z > hi.z) d += (p.z - hi.z) * (p.z - hi.z);
    return d;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint c = id.x;
    if (c >= P.ClusterCount) return;
    float3 lo = ClusterMin[c].xyz, hi = ClusterMax[c].xyz;

    uint localIdx[128];   // MaxLightsPerCluster
    uint n = 0;
    for (uint i = 0; i < P.LightCount && n < P.MaxPerCluster; i++) {
        float4 lv = LightViewPos[i];
        if (SqDistPointAabb(lv.xyz, lo, hi) <= lv.w * lv.w) localIdx[n++] = i;
    }

    uint offset = 0;
    if (n > 0) {
        Counter.InterlockedAdd(0, n, offset);
        if (offset + n <= P.MaxIndices) {
            for (uint k = 0; k < n; k++) IndexList.Store((offset + k) * 4, localIdx[k]);
        } else {
            n = 0;   // overflow: this cluster gets sun + ambient only (CPU parity)
        }
    }
    Grid.Store(c * 8, offset);
    Grid.Store(c * 8 + 4, n);
}
