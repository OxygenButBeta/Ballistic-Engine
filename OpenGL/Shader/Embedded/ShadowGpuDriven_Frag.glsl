#version 460 core
#extension GL_ARB_bindless_texture : require
in vec2 uv;
flat in uint vMaterialId;
struct GpuMaterial { uvec2 dH;uvec2 nH;uvec2 mH;uvec2 rH;uvec2 aH;uvec2 eH;
    vec4 bcf;vec4 ef;float mm;float rm;float ns;float op;uint fl;uint a;uint b;uint c; };
layout(std430, binding = 6) readonly buffer GpuMaterialBuf { GpuMaterial gpuMats[]; };
void main() {
    GpuMaterial m = gpuMats[vMaterialId];
    if ((m.fl & 64u) != 0u && texture(sampler2D(m.dH), uv).a < 0.5)
        discard;
}
