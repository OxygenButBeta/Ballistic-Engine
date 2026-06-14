#version 460 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec3 aNormal;
layout(location = 3) in vec4 aTangent; // xyz tangent, w = bitangent handedness (+1/-1)

layout(location = 4) in vec4 instance_matrix_0;
layout(location = 5) in vec4 instance_matrix_1;
layout(location = 6) in vec4 instance_matrix_2;
layout(location = 7) in vec4 instance_matrix_3;

out vec2 texCoord;
out vec3 fragNormal;
out vec3 fragPos;
out mat3 fragTBN;

uniform bool isInstanced;
uniform mat4 model;

// --- Pass constants (std140, binding 0 via the renderer) ---
// One block shared by every lit program and uploaded once per pass. The declaration MUST be
// textually identical in Vert.glsl and Frag.glsl (GLSL link rule). Member names match the old
// plain uniforms so shading code is unchanged.
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
    float ProbeIntensity;            // GlobalIllumination volume: diffuse-probe ambient strength (1 = unchanged)
    float AmbientFloor;              // tiny shadow-fill so interiors never crush to pure black (default ~0.03)
};

void main()
{
    // Instance attributes carry the OpenTK row-major matrix as 4 vec4s; building the GLSL
    // matrix from them column-wise yields exactly what UniformMatrix4(transpose:false) does
    // for `model` — both paths produce the IDENTICAL matrix, which the z-prepass equality
    // (invariant gl_Position) depends on.
    mat4 modelMatrix = isInstanced
        ? mat4(instance_matrix_0, instance_matrix_1, instance_matrix_2, instance_matrix_3)
        : model;

    mat3 normalMatrix = mat3(transpose(inverse(modelMatrix)));
    vec3 N = normalize(normalMatrix * aNormal);
    vec3 T = normalize(mat3(modelMatrix) * aTangent.xyz);
    T = normalize(T - dot(T, N) * N); // Gram-Schmidt: keep the TBN orthogonal under non-uniform scale
    vec3 B = cross(N, T) * aTangent.w; // w restores handedness on mirrored UV islands
    fragTBN = mat3(T, B, N);

    texCoord = aTexCoord;
    fragNormal = N;
    fragPos = vec3(modelMatrix * vec4(aPosition, 1.0));
    gl_Position = projection * view * vec4(fragPos, 1.0);
}
