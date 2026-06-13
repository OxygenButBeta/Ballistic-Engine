#version 460 core
layout(location = 0) in vec3 position;
layout(location = 1) in vec2 aTexCoord;
layout(location = 8) in vec4 aBoneIndices;
layout(location = 9) in vec4 aBoneWeights;

out vec2 uv;

uniform mat4 model;
uniform mat4 lightSpaceMatrix;

layout(std430, binding = 1) readonly buffer BoneMatrices {
    mat4 bones[];
};

void main() {
    uv = aTexCoord;
    ivec4 bi = ivec4(aBoneIndices + 0.5);
    mat4 skin =
        aBoneWeights.x * bones[bi.x] +
        aBoneWeights.y * bones[bi.y] +
        aBoneWeights.z * bones[bi.z] +
        aBoneWeights.w * bones[bi.w];
    vec3 skinnedPos = (skin * vec4(position, 1.0)).xyz;
    gl_Position = lightSpaceMatrix * model * vec4(skinnedPos, 1.0);
}
