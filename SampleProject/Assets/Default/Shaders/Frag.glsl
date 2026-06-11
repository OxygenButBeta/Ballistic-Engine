#version 330 core

// --- Inputs from vertex shader ---
in vec2 texCoord;
in vec3 fragNormal;
in vec3 fragPos;
in mat3 fragTBN;
in vec4 fragPosLightSpace;

out vec4 FragColor;

// --- Camera ---
uniform vec3 CameraPos;

// --- Sun (directional) ---
uniform vec3 LightDirection;   // toward the light
uniform vec3 LightColor;       // color * intensity
uniform vec3 AmbientLight;     // non-IBL ambient fallback

// --- Punctual lights ---
const int MAX_POINT_LIGHTS = 8;
const int MAX_SPOT_LIGHTS = 4;
uniform int PointLightCount;
uniform vec3 PointLightPosition[MAX_POINT_LIGHTS];
uniform vec3 PointLightColor[MAX_POINT_LIGHTS];
uniform float PointLightRange[MAX_POINT_LIGHTS];
uniform int SpotLightCount;
uniform vec3 SpotLightPosition[MAX_SPOT_LIGHTS];
uniform vec3 SpotLightDirection[MAX_SPOT_LIGHTS];
uniform vec3 SpotLightColor[MAX_SPOT_LIGHTS];
uniform float SpotLightRange[MAX_SPOT_LIGHTS];
uniform float SpotLightCosInner[MAX_SPOT_LIGHTS];
uniform float SpotLightCosOuter[MAX_SPOT_LIGHTS];

// --- Texture maps ---
uniform sampler2D Diffuse;
uniform sampler2D Normal;
uniform sampler2D Metallic;
uniform sampler2D Roughness;
uniform sampler2D AO;
uniform sampler2D Emissive;
uniform samplerCube Skybox;
uniform samplerCube IrradianceMap;
uniform samplerCube PrefilteredEnvMap;
uniform sampler2D BRDF_LUT;

// --- Shadows ---
uniform sampler2DShadow ShadowMap;
uniform float ShadowBias;

// --- IBL controls ---
uniform bool UseIBL;
uniform float MaxPrefilterMips;
uniform float SkyExposure;
uniform mat4 SkyRotation;      // same matrix the skybox shader rotates by

// --- Material controls ---
uniform int renderMode;
uniform float MetallicMultiplier;
uniform float RoughnessMultiplier;
uniform float minRoughness;
uniform float NormalStrength;
uniform bool NormalFlipY;
uniform vec3 EmissiveFactor;   // color * intensity
uniform bool HasEmissive;
uniform bool AlphaBlend;
uniform float Opacity;

// --- Extra features ---
uniform bool EnableAtmosphericScattering;

const float PI = 3.14159265359;
const float EPS = 1e-6;

// ---------------- PBR helpers ----------------
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + EPS);
}

float GeometrySchlickGGX(float NdotV, float roughness)
{
    float k = (roughness + 1.0);
    k = (k * k) / 8.0;
    return NdotV / max(NdotV * (1.0 - k) + k, EPS);
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    return GeometrySchlickGGX(max(dot(N, V), 0.0), roughness) *
           GeometrySchlickGGX(max(dot(N, L), 0.0), roughness);
}

vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

vec3 FresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness)
{
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(1.0 - cosTheta, 5.0);
}

// Cook-Torrance contribution of one light. radiance = light color already attenuated.
vec3 ShadeLight(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo, float metallic, float roughness, vec3 F0)
{
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0)
        return vec3(0.0);

    vec3 H = normalize(V + L);
    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    float NdotV = max(dot(N, V), 0.0);
    vec3 specular = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);

    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    return (kD * albedo / PI + specular) * radiance * NdotL;
}

// UE-style windowed inverse-square falloff.
float DistanceAttenuation(float dist, float range)
{
    float window = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
    return window * window / max(dist * dist, 1e-4);
}

// ---------------- Shadows ----------------
float SunShadow(vec3 N, vec3 L)
{
    vec3 proj = fragPosLightSpace.xyz / fragPosLightSpace.w;
    proj = proj * 0.5 + 0.5;
    if (proj.z > 1.0)
        return 1.0;

    float bias = max(ShadowBias * (1.0 - dot(N, L)), ShadowBias * 0.1);
    vec2 texel = 1.0 / vec2(textureSize(ShadowMap, 0));

    // 5x5 PCF on top of hardware compare (sampler2DShadow), so each tap is already 2x2.
    float lit = 0.0;
    for (int x = -2; x <= 2; x++)
        for (int y = -2; y <= 2; y++)
            lit += texture(ShadowMap, vec3(proj.xy + vec2(x, y) * texel, proj.z - bias));
    return lit / 25.0;
}

// ---------------- Normal map helper ----------------
vec3 GetNormalFromMap(vec2 uv, vec3 geomNormal, mat3 TBN, float strength)
{
    vec3 n = texture(Normal, uv).rgb;
    if (NormalFlipY) n.g = 1.0 - n.g;
    vec3 tangentNormal = normalize(n * 2.0 - 1.0);
    vec3 mapped = normalize(TBN * tangentNormal);
    return normalize(mix(geomNormal, mapped, clamp(strength, 0.0, 1.0)));
}

// ---------------- IBL helpers ----------------
// The skybox geometry is rotated by SkyRotation, so world directions sample the
// cubemap (and the maps convolved from it) through the inverse rotation.
vec3 SkyDir(vec3 d)
{
    return transpose(mat3(SkyRotation)) * d;
}

// ---------------- Main ----------------
void main()
{
    // --- Debug only modes ---
    if (renderMode == 1) { FragColor = vec4(texture(Diffuse, texCoord).rgb, 1.0); return; }
    if (renderMode == 2) {
        vec3 n = GetNormalFromMap(texCoord, normalize(fragNormal), fragTBN, NormalStrength);
        FragColor = vec4(n * 0.5 + 0.5, 1.0);
        return;
    }
    if (renderMode == 3) { float ao = texture(AO, texCoord).r; FragColor = vec4(vec3(ao), 1.0); return; }
    if (renderMode == 4) { float m = clamp(texture(Metallic, texCoord).r * MetallicMultiplier, 0.0, 1.0); FragColor = vec4(vec3(m), 1.0); return; }
    if (renderMode == 5) { float r = clamp(texture(Roughness, texCoord).r * RoughnessMultiplier, 0.0, 1.0); FragColor = vec4(vec3(r), 1.0); return; }

    // --- Sample maps ---
    vec4 albedoSample = texture(Diffuse, texCoord);
    vec3 albedo = albedoSample.rgb;
    float metallic = clamp(texture(Metallic, texCoord).r * MetallicMultiplier, 0.0, 1.0);
    float roughness = clamp(texture(Roughness, texCoord).r * RoughnessMultiplier, minRoughness, 1.0);
    float ao = texture(AO, texCoord).r;

    vec3 N = GetNormalFromMap(texCoord, normalize(fragNormal), fragTBN, NormalStrength);
    vec3 V = normalize(CameraPos - fragPos);
    float NdotV = max(dot(N, V), 0.0);

    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // --- Sun with shadows ---
    vec3 L = normalize(LightDirection);
    float shadow = SunShadow(N, L);
    vec3 Lo = ShadeLight(N, V, L, LightColor, albedo, metallic, roughness, F0) * shadow;

    if (renderMode == 6) { FragColor = vec4(vec3(shadow), 1.0); return; }

    // --- Point lights ---
    for (int i = 0; i < PointLightCount; i++) {
        vec3 toLight = PointLightPosition[i] - fragPos;
        float dist = length(toLight);
        if (dist > PointLightRange[i])
            continue;
        vec3 radiance = PointLightColor[i] * DistanceAttenuation(dist, PointLightRange[i]);
        Lo += ShadeLight(N, V, toLight / dist, radiance, albedo, metallic, roughness, F0);
    }

    // --- Spot lights ---
    for (int i = 0; i < SpotLightCount; i++) {
        vec3 toLight = SpotLightPosition[i] - fragPos;
        float dist = length(toLight);
        if (dist > SpotLightRange[i])
            continue;
        vec3 Ls = toLight / dist;
        float cosAngle = dot(-Ls, normalize(SpotLightDirection[i]));
        float cone = clamp((cosAngle - SpotLightCosOuter[i]) /
                           max(SpotLightCosInner[i] - SpotLightCosOuter[i], 1e-4), 0.0, 1.0);
        if (cone <= 0.0)
            continue;
        vec3 radiance = SpotLightColor[i] * DistanceAttenuation(dist, SpotLightRange[i]) * cone * cone;
        Lo += ShadeLight(N, V, Ls, radiance, albedo, metallic, roughness, F0);
    }

    // --- Ambient: full split-sum IBL when baked maps exist, flat sky ambient otherwise ---
    vec3 ambient;
    vec3 F = FresnelSchlickRoughness(NdotV, F0, roughness);
    if (UseIBL) {
        vec3 R = reflect(-V, N);
        float mip = clamp(roughness * MaxPrefilterMips, 0.0, MaxPrefilterMips);
        vec3 prefiltered = textureLod(PrefilteredEnvMap, SkyDir(R), mip).rgb * SkyExposure;
        vec2 brdf = texture(BRDF_LUT, vec2(NdotV, roughness)).rg;
        vec3 specularIBL = prefiltered * (F * brdf.x + brdf.y);

        vec3 irradiance = texture(IrradianceMap, SkyDir(N)).rgb * SkyExposure;
        vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
        ambient = (kD * irradiance * albedo + specularIBL) * ao;
    }
    else {
        vec3 R = reflect(-V, N);
        vec3 envColor = textureLod(Skybox, SkyDir(R), 0.0).rgb * SkyExposure;
        vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
        ambient = (AmbientLight * kD * albedo + F * envColor * (1.0 - roughness)) * ao;
    }

    vec3 color = Lo + ambient;

    // --- Emissive (unlit, unoccluded; bloom picks it up) ---
    if (HasEmissive)
        color += texture(Emissive, texCoord).rgb * EmissiveFactor;

    // --- Atmospheric scattering (simple fog) ---
    if (EnableAtmosphericScattering) {
        float dist = length(CameraPos - fragPos);
        float fogFactor = clamp(1.0 - exp(-dist * 0.0015), 0.0, 1.0);
        vec3 fogColor = mix(vec3(0.6, 0.7, 0.9), AmbientLight, 0.3);
        color = mix(color, fogColor, fogFactor);
    }

    float alpha = AlphaBlend ? clamp(Opacity * albedoSample.a, 0.0, 1.0) : 1.0;
    FragColor = vec4(color, alpha); // linear HDR out; tonemap happens in the composite pass
}
