#version 460 core

// GPU JUMP-FLOOD (JFA) — one pass of the nearest-seed flood over a 3D grid.
//
// The Global Distance Field build's distance stage. Seeds (surface-shell voxels carrying their closest
// surface point + sign, from SdfSeedExtractor) are propagated across the whole volume: after log2(res)
// passes with halving step sizes (res/2, res/4, ..., 1) every voxel holds the nearest seed's surface
// point. SdfResolve_Comp then turns that into signed distance.
//
// Payload per voxel (RGBA32F): xyz = the propagated seed's SURFACE POINT in continuous voxel coords,
// w = the seed's SIGN (+1 outside, -1 inside; w == 0.0 means "no seed yet"). Each invocation keeps,
// among its 27 step-offset neighbours (incl. self), the seed whose surface point is nearest to ITS OWN
// voxel center — standard JFA. Sign rides along with the winning seed.
//
// Ping-pong: SrcSeed (sampler) -> DstSeed (image). The C# pass swaps them between passes.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(binding = 0) uniform sampler3D SrcSeed;            // current nearest-seed field (read)
layout(rgba32f, binding = 1) uniform writeonly image3D DstSeed; // next nearest-seed field (write)
// Albedo rides along in LOCKSTEP: the winning seed's albedo is copied so the resolve has the nearest
// surface's colour without a separate flood. rgb = linear albedo (a unused).
layout(binding = 2) uniform sampler3D SrcAlbedo;
layout(rgba16f, binding = 3) uniform writeonly image3D DstAlbedo;

uniform int Res;        // voxels per axis (cubic grid)
uniform int Step;       // jump distance in voxels for THIS pass

// texelFetch the seed at integer voxel coords (clamped). w == 0 => no seed there.
vec4 SeedAt(ivec3 v) {
    if (any(lessThan(v, ivec3(0))) || any(greaterThanEqual(v, ivec3(Res))))
        return vec4(0.0); // outside the grid: no seed
    return texelFetch(SrcSeed, v, 0);
}

void main() {
    ivec3 v = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(v, ivec3(Res))))
        return;

    vec3 selfCenter = vec3(v); // this voxel's center, in voxel coords (seed surface points are too)

    vec4 best = SeedAt(v);                 // keep our own seed as the starting candidate
    ivec3 bestV = v;                       // where the winning seed was sampled (for its albedo)
    float bestDistSq = best.w != 0.0 ? dot(best.xyz - selfCenter, best.xyz - selfCenter) : 3.4e38;

    // Sample the 26 neighbours at the current step offset (plus self already taken).
    for (int dz = -1; dz <= 1; ++dz)
        for (int dy = -1; dy <= 1; ++dy)
            for (int dx = -1; dx <= 1; ++dx) {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                ivec3 nv = v + ivec3(dx, dy, dz) * Step;
                vec4 s = SeedAt(nv);
                if (s.w == 0.0) continue;  // that neighbour has no seed to offer
                float dSq = dot(s.xyz - selfCenter, s.xyz - selfCenter);
                if (dSq < bestDistSq) { bestDistSq = dSq; best = s; bestV = nv; }
            }

    imageStore(DstSeed, v, best);
    // Carry the winning seed's albedo (clamp the source coord — bestV may be off-grid for self/edge).
    vec3 alb = (best.w != 0.0) ? texelFetch(SrcAlbedo, clamp(bestV, ivec3(0), ivec3(Res - 1)), 0).rgb : vec3(0.0);
    imageStore(DstAlbedo, v, vec4(alb, 1.0));
}
