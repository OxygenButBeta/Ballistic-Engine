#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// GGX-prefiltered specular environment, one cube face per pass; the renderer maps
// roughness to mip level at shading time.

uniform samplerCube EnvironmentMap;
uniform int Face;
uniform float Roughness;
uniform float SourceResolution;

const float PI = 3.14159265359;
const uint SAMPLE_COUNT = 512u;

vec3 FaceDir(int face, vec2 uv) {
    vec2 st = uv * 2.0 - 1.0;
    if (face == 0) return vec3( 1.0, -st.y, -st.x);
    if (face == 1) return vec3(-1.0, -st.y,  st.x);
    if (face == 2) return vec3( st.x,  1.0,  st.y);
    if (face == 3) return vec3( st.x, -1.0, -st.y);
    if (face == 4) return vec3( st.x, -st.y,  1.0);
    return vec3(-st.x, -st.y, -1.0);
}

float RadicalInverse_VdC(uint bits) {
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

vec2 Hammersley(uint i, uint n) {
    return vec2(float(i) / float(n), RadicalInverse_VdC(i));
}

vec3 ImportanceSampleGGX(vec2 Xi, vec3 N, float roughness) {
    float a = roughness * roughness;
    float phi = 2.0 * PI * Xi.x;
    float cosTheta = sqrt((1.0 - Xi.y) / (1.0 + (a * a - 1.0) * Xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);

    vec3 H = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);

    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, N));
    vec3 bitangent = cross(N, tangent);
    return normalize(tangent * H.x + bitangent * H.y + N * H.z);
}

float DistributionGGX(float NdotH, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + 1e-7);
}

void main() {
    vec3 N = normalize(FaceDir(Face, TexCoords));
    vec3 R = N;
    vec3 V = R;

    vec3 prefiltered = vec3(0.0);
    float totalWeight = 0.0;
    for (uint i = 0u; i < SAMPLE_COUNT; i++) {
        vec2 Xi = Hammersley(i, SAMPLE_COUNT);
        vec3 H = ImportanceSampleGGX(Xi, N, Roughness);
        vec3 L = normalize(2.0 * dot(V, H) * H - V);

        float NdotL = max(dot(N, L), 0.0);
        if (NdotL <= 0.0)
            continue;

        // Sample the source mip matched to the sample's solid angle to avoid fireflies.
        float NdotH = max(dot(N, H), 0.0);
        float HdotV = max(dot(H, V), 0.0);
        float D = DistributionGGX(NdotH, Roughness);
        float pdf = D * NdotH / (4.0 * HdotV) + 1e-4;
        float saTexel = 4.0 * PI / (6.0 * SourceResolution * SourceResolution);
        float saSample = 1.0 / (float(SAMPLE_COUNT) * pdf + 1e-4);
        float mipLevel = Roughness == 0.0 ? 0.0 : 0.5 * log2(saSample / saTexel);

        vec3 radiance = min(textureLod(EnvironmentMap, L, mipLevel).rgb, vec3(500.0));
        prefiltered += radiance * NdotL;
        totalWeight += NdotL;
    }

    prefiltered /= max(totalWeight, 1e-4);
    FragColor = vec4(prefiltered, 1.0);
}
