#version 460 core

// GPU JUMP-FLOOD (JFA) — the RESOLVE pass: turn the flooded nearest-seed field into the final signed
// world-distance field (R16F, world metres, negative inside) + the albedo field (RGBA8), matching
// EXACTLY what the CPU bake produced so the march (SdfTrace_Comp) and the inject (GlobalRadianceInject)
// read an identical-format field — only the build path changed.
//
// Each voxel holds, from the flood, the nearest seed's SURFACE POINT (voxel coords) + SIGN. Distance =
// |voxelCenter - seedPoint| in voxel units, scaled to world metres by the cell size, signed by the
// seed. A voxel that never received a seed (the flood reaches everything in log2(res) passes, so this
// is only the empty-scene case) writes a large positive distance.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(binding = 0) uniform sampler3D SeedField;              // flooded nearest-seed (xyz=pt voxel, w=sign)
layout(binding = 2) uniform sampler3D SeedAlbedoField;        // flooded nearest-seed albedo (rgb)
layout(r16f,  binding = 1) uniform writeonly image3D DistOut; // signed world-metre distance
layout(rgba8, binding = 3) uniform writeonly image3D AlbedoOut;

uniform int   Res;       // voxels per axis
uniform float CellWorld; // world size of one (cubic) cell, metres
uniform float FarDist;   // distance written where no seed reached (large positive)

void main() {
    ivec3 v = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(v, ivec3(Res))))
        return;

    vec4 seed = texelFetch(SeedField, v, 0);
    if (seed.w == 0.0) {
        // No seed (empty scene / unreached) -> far positive distance, no albedo.
        imageStore(DistOut, v, vec4(FarDist));
        imageStore(AlbedoOut, v, vec4(0.0));
        return;
    }

    vec3 selfCenter = vec3(v);                  // voxel center in voxel coords
    float distVox = length(seed.xyz - selfCenter);
    float distWorld = distVox * CellWorld;      // voxel units -> world metres
    float sign = seed.w >= 0.0 ? 1.0 : -1.0;
    imageStore(DistOut, v, vec4(sign * distWorld));

    vec3 alb = texelFetch(SeedAlbedoField, v, 0).rgb;
    imageStore(AlbedoOut, v, vec4(alb, 1.0));
}
