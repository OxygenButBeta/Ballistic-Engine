#version 460 core

// CLUSTERED FORWARD step 1: compute each cluster's VIEW-SPACE AABB. One thread per cluster. The grid
// is XY screen tiles x Z logarithmic depth slices. A cluster's screen tile corners are unprojected to
// view space at the near plane, then the ray from the eye through each corner is intersected with the
// cluster's near/far Z planes to get the view-space AABB the light-cull pass tests spheres against.
//
// View-space AABBs are camera-RELATIVE, so they're invariant under camera movement — the C# side only
// re-runs this when the projection or viewport changes (resize / FOV), not every frame.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

struct ClusterAabb { vec4 minV; vec4 maxV; }; // view-space, .w unused
layout(std430, binding = 13) buffer AabbBuf { ClusterAabb clusters[]; };

uniform mat4  InvProjection;  // clip -> view
uniform vec2  ScreenSize;     // full-res pixels
uniform vec2  NearFar;        // x = near, y = far
uniform ivec3 ClusterDims;    // (ClusterX, ClusterY, ClusterZ)

// Unproject a screen-pixel point at a given NDC z into VIEW space.
vec3 ScreenToView(vec2 px, float ndcZ) {
    vec2 uv = px / ScreenSize;
    vec4 clip = vec4(uv * 2.0 - 1.0, ndcZ, 1.0);
    vec4 v = InvProjection * clip;
    return v.xyz / v.w;
}

// Intersect the eye->point ray with a constant view-space Z plane (eye at origin, looking down -Z).
vec3 LineZIntersect(vec3 dir, float z) {
    // Ray from origin: P = t * dir; want P.z == z  => t = z / dir.z.
    float t = z / dir.z;
    return t * dir;
}

void main() {
    ivec3 c = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(c, ClusterDims)))
        return;
    int idx = c.x + ClusterDims.x * (c.y + ClusterDims.y * c.z);

    // Screen-tile pixel bounds for this cluster's XY.
    vec2 tile = ScreenSize / vec2(ClusterDims.xy);
    vec2 minPx = vec2(c.xy) * tile;
    vec2 maxPx = vec2(c.xy + ivec2(1)) * tile;

    // The four tile corners at the near plane, unprojected to view space (eye-relative directions).
    vec3 vMin = ScreenToView(minPx, -1.0); // NDC z = -1 (near) in GL clip space
    vec3 vMax = ScreenToView(maxPx, -1.0);

    // Logarithmic depth slices: zNear * (zFar/zNear)^(slice / Zn). View Z is NEGATIVE.
    float near = NearFar.x, far = NearFar.y;
    float zNear = -near * pow(far / near, float(c.z)     / float(ClusterDims.z));
    float zFar  = -near * pow(far / near, float(c.z + 1) / float(ClusterDims.z));

    // The cluster's 8 corners = the 4 tile-corner rays intersected with the near/far Z planes.
    vec3 minNear = LineZIntersect(vMin, zNear);
    vec3 minFar  = LineZIntersect(vMin, zFar);
    vec3 maxNear = LineZIntersect(vMax, zNear);
    vec3 maxFar  = LineZIntersect(vMax, zFar);

    vec3 lo = min(min(minNear, minFar), min(maxNear, maxFar));
    vec3 hi = max(max(minNear, minFar), max(maxNear, maxFar));

    clusters[idx].minV = vec4(lo, 0.0);
    clusters[idx].maxV = vec4(hi, 0.0);
}
