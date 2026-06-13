#version 460 core
// Voxelization vertex stage. Transforms positions to WORLD space (the voxel grid is world-aligned)
// and forwards world position + normal + uv. The geometry stage picks the dominant axis; the
// fragment stage scatters the surface's direct-lit radiance into the 3D voxel texture via imageStore.
//
// GPU-driven: model comes from the per-draw SSBO (same as the lit path), so the SAME whole-mesh
// renderer voxelizes via one MDI call. materialId flows through for the albedo lookup (bindless).

#extension GL_ARB_bindless_texture : require
#extension GL_ARB_shader_draw_parameters : require

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec3 aNormal;

struct GpuPerDraw { mat4 model; uint materialId; uint _p0; uint _p1; uint _p2; };
layout(std430, binding = 5) readonly buffer GpuPerDrawBuf { GpuPerDraw gpuDraws[]; };

out VsOut {
    vec3 worldPos;
    vec3 normal;
    vec2 uv;
    flat uint materialId;
} vs;

void main() {
    GpuPerDraw d = gpuDraws[gl_DrawIDARB];
    vec4 wp = d.model * vec4(aPosition, 1.0);
    vs.worldPos = wp.xyz;
    vs.normal = normalize(mat3(d.model) * aNormal);
    vs.uv = aTexCoord;
    vs.materialId = d.materialId;
    gl_Position = wp; // the geometry stage does the ortho projection per dominant axis
}
