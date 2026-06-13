#version 460 core
// After the direct atomic-average inject, each voxel's alpha holds the SAMPLE COUNT (count/255),
// not the occupancy the cone tracer expects. This pass rewrites alpha to 1.0 (fully occupied)
// wherever any surface wrote the voxel, leaving RGB (the averaged radiance) untouched. Empty
// voxels (count 0) stay fully transparent so cones see through them.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba8) uniform image3D VoxelRadiance;
uniform int VoxelRes;

void main() {
    ivec3 vc = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(vc, ivec3(VoxelRes)))) return;
    vec4 c = imageLoad(VoxelRadiance, vc);
    if (c.a > 0.0)
        imageStore(VoxelRadiance, vc, vec4(c.rgb, 1.0));
}
