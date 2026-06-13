#version 460 core
#extension GL_ARB_shader_draw_parameters : require
layout(location = 0) in vec3 position;
layout(location = 1) in vec2 aTexCoord;
out vec2 uv;
flat out uint vMaterialId;
uniform mat4 lightSpaceMatrix;
struct GpuPerDraw { mat4 model; uint materialId; uint _p0; uint _p1; uint _p2; };
layout(std430, binding = 5) readonly buffer GpuPerDrawBuf { GpuPerDraw gpuDraws[]; };
void main() {
    GpuPerDraw d = gpuDraws[gl_DrawIDARB];
    vMaterialId = d.materialId;
    uv = aTexCoord;
    gl_Position = lightSpaceMatrix * d.model * vec4(position, 1.0);
}
