#version 460 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec3 aNormal;
layout(location = 3) in vec4 aTangent; // xyz tangent, w = bitangent handedness (+1/-1)

// Skinning attributes (this is the ONLY difference in vertex inputs from Vert.glsl). Locations 4-7
// are the instance matrix on the static path; a skinned mesh never instances, so 8/9 are free.
layout(location = 8) in vec4 aBoneIndices; // 4 bone indices, sent as floats (rounded below)
layout(location = 9) in vec4 aBoneWeights; // 4 weights, sum 1

out vec2 texCoord;
out vec3 fragNormal;
out vec3 fragPos;
out mat3 fragTBN;

uniform bool isInstanced; // always false for skinned draws; kept for shared declarations
uniform mat4 model;

// Bone matrices for THIS draw (one skinned mesh). std430 packs mat4[] tightly. The renderer binds
// this SSBO at binding 1 in BOTH the main pass and the depth prepass with identical contents, so
// gl_Position is bit-identical between passes (z-prepass invariance).
layout(std430, binding = 1) readonly buffer BoneMatrices {
    mat4 bones[];
};

// --- Pass constants (std140, binding 0 via the renderer) ---
// MUST be textually identical to Vert.glsl / Frag.glsl (GLSL link rule).
const int MAX_POINT_LIGHTS = 8;
const int MAX_SPOT_LIGHTS = 4;
const int MAX_CASCADES = 4;
const int MAX_SHADOWED_SPOTS = 4;
const int MAX_SHADOWED_POINTS = 2;

layout(std140) uniform PassData {
    mat4 view;
    mat4 projection;
    mat4 SkyRotation;
    mat4 CascadeMatrices[MAX_CASCADES];
    mat4 SpotShadowMatrix[MAX_SHADOWED_SPOTS];
    mat4 PointShadowMatrix[MAX_SHADOWED_POINTS * 6];

    vec4 CascadeBias;
    vec4 CascadeTexelWorld;
    vec4 CascadeDepthRangeW;

    vec3 CameraPos;               float ShadowStrength;
    vec3 LightDirection;          float SunAngularRadius;
    vec3 LightColor;              float CascadeBlend;
    vec3 AmbientLight;            float ShadowSoftness;
    vec3 ShadowColor;             float minRoughness;
    vec3 AmbientTint;             float ReflectionIntensity;
    vec3 FogColor;                float FogDensity;
    vec3 ProbeVolumeMin;          float ProbeExposure;
    vec3 ProbeVolumeInvSize;      float SkyExposure;
    vec3 ReflectionVolumeMin;     float MaxPrefilterMips;
    vec3 ReflectionVolumeInvSize; float ReflectionMaxMips;

    vec3 PointLightPosition[MAX_POINT_LIGHTS];
    vec3 PointLightColor[MAX_POINT_LIGHTS];
    float PointLightRange[MAX_POINT_LIGHTS];
    vec3 SpotLightPosition[MAX_SPOT_LIGHTS];
    vec3 SpotLightDirection[MAX_SPOT_LIGHTS];
    vec3 SpotLightColor[MAX_SPOT_LIGHTS];
    float SpotLightRange[MAX_SPOT_LIGHTS];
    float SpotLightCosInner[MAX_SPOT_LIGHTS];
    float SpotLightCosOuter[MAX_SPOT_LIGHTS];
    int SpotShadowSlot[MAX_SPOT_LIGHTS];
    float SpotShadowBias[MAX_SHADOWED_SPOTS];
    int PointShadowSlot[MAX_POINT_LIGHTS];
    float PointShadowBias[MAX_SHADOWED_POINTS];

    vec2 ScreenSize;
    int PointLightCount;
    int SpotLightCount;
    int CascadeCount;
    int ShadowFiltering;
    int renderMode;
    int ReflectionGridX;
    int ReflectionGridY;
    int ReflectionGridZ;
    bool UseIBL;
    bool UseProbeVolume;
    bool UseReflectionVolume;
    bool HasScreenAO;
    bool ReflectionBlendWithSky;
    bool EnableAtmosphericScattering;
    float ReflectionIntensityLocal;
};

void main()
{
    // Blend the 4 influencing bone matrices by weight, then transform position/normal/tangent by the
    // result BEFORE the model matrix — exactly the math the depth prepass companion runs too.
    ivec4 bi = ivec4(aBoneIndices + 0.5); // floats back to ints
    mat4 skin =
        aBoneWeights.x * bones[bi.x] +
        aBoneWeights.y * bones[bi.y] +
        aBoneWeights.z * bones[bi.z] +
        aBoneWeights.w * bones[bi.w];

    vec3 skinnedPos = (skin * vec4(aPosition, 1.0)).xyz;
    mat3 skin3 = mat3(skin);
    vec3 skinnedNormal = skin3 * aNormal;
    vec3 skinnedTangent = skin3 * aTangent.xyz;

    // A skinned mesh never instances; `model` is the entity's world matrix (kept the same shape as
    // Vert.glsl so the PrepassShaderFor companion compiles identically).
    mat4 modelMatrix = model;

    mat3 normalMatrix = mat3(transpose(inverse(modelMatrix)));
    vec3 N = normalize(normalMatrix * skinnedNormal);
    vec3 T = normalize(mat3(modelMatrix) * skinnedTangent);
    T = normalize(T - dot(T, N) * N);
    vec3 B = cross(N, T) * aTangent.w;
    fragTBN = mat3(T, B, N);

    texCoord = aTexCoord;
    fragNormal = N;
    fragPos = vec3(modelMatrix * vec4(skinnedPos, 1.0));
    gl_Position = projection * view * vec4(fragPos, 1.0);
}
