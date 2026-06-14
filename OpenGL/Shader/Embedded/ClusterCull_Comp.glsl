#version 460 core

// CLUSTERED FORWARD step 2: assign lights to clusters. One thread per cluster. For each light, test
// its bounding sphere (world pos transformed to VIEW space, radius = range) against this cluster's
// view-space AABB; if it overlaps, append the light index to a flat global index list (atomic bump)
// and record this cluster's (offset, count) in the grid. The lit shader then loops only the lights
// in the fragment's cluster.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

struct GpuLight {
    vec4 posRange;    // xyz world pos, w range
    vec4 color;       // xyz radiance, w type
    vec4 dirCosOuter; // xyz spot dir, w cosOuter
    vec4 extra;       // x cosInner, y shadowSlot, zw pad
};
struct ClusterAabb { vec4 minV; vec4 maxV; };

layout(std430, binding = 12) readonly buffer LightBuf  { GpuLight lights[]; };
layout(std430, binding = 13) readonly buffer AabbBuf   { ClusterAabb clusters[]; };
layout(std430, binding = 14) writeonly buffer GridBuf  { ivec2 grid[]; };       // (offset,count)/cluster
layout(std430, binding = 15) writeonly buffer IndexBuf { int lightIndices[]; };
layout(std430, binding = 16) buffer CounterBuf { uint globalCount; };           // atomic write cursor

uniform mat4  ViewMatrix;
uniform int   LightCount;
uniform vec2  NearFar;
uniform vec2  ScreenSize;
uniform ivec3 ClusterDims;

const int MAX_PER_CLUSTER = 128;
const int MAX_INDICES = 16 * 9 * 24 * 32; // must match GLClusteredLights.MaxLightIndices

// Squared distance from a point to an AABB (0 if inside) — the sphere-vs-AABB overlap test.
float SqDistPointAabb(vec3 p, vec3 lo, vec3 hi) {
    float d = 0.0;
    for (int i = 0; i < 3; i++) {
        float v = p[i];
        if (v < lo[i]) d += (lo[i] - v) * (lo[i] - v);
        if (v > hi[i]) d += (v - hi[i]) * (v - hi[i]);
    }
    return d;
}

void main() {
    ivec3 c = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(c, ClusterDims)))
        return;
    int cluster = c.x + ClusterDims.x * (c.y + ClusterDims.y * c.z);

    vec3 lo = clusters[cluster].minV.xyz;
    vec3 hi = clusters[cluster].maxV.xyz;

    // Collect this cluster's lights into a local list first (so we reserve one contiguous range
    // in the global index buffer with a single atomic add, not one atomic per light).
    int local[MAX_PER_CLUSTER];
    int n = 0;
    for (int i = 0; i < LightCount && n < MAX_PER_CLUSTER; i++) {
        vec3 posV = (ViewMatrix * vec4(lights[i].posRange.xyz, 1.0)).xyz;
        float r = lights[i].posRange.w;
        if (SqDistPointAabb(posV, lo, hi) <= r * r)
            local[n++] = i;
    }

    uint offset = 0u;
    if (n > 0) {
        offset = atomicAdd(globalCount, uint(n));
        if (int(offset) + n <= MAX_INDICES) {
            for (int k = 0; k < n; k++)
                lightIndices[int(offset) + k] = local[k];
        } else {
            n = 0; // overflow: this cluster gets no local lights (falls back to ambient + sun)
        }
    }
    grid[cluster] = ivec2(int(offset), n);
}
