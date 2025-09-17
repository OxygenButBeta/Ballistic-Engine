#version 330 core

// --- Inputs from vertex shader ---
in vec2 texCoord;
in vec3 fragNormal;
in vec3 fragPos;
in mat3 fragTBN;

out vec4 FragColor;

// --- Camera / lights ---
uniform vec3 CameraPos;
uniform vec3 LightPos;
uniform vec3 LightColor;
uniform vec3 AmbientLight;

// --- Texture maps ---
uniform sampler2D Diffuse;       
uniform sampler2D Normal;        
uniform sampler2D Metallic;      
uniform sampler2D Roughness;     
uniform sampler2D AO;            
uniform samplerCube Skybox;      
uniform samplerCube IrradianceMap;
uniform samplerCube PrefilteredEnvMap;
uniform sampler2D BRDF_LUT;

// --- Controls / multipliers ---
uniform int renderMode;
uniform float MetallicMultiplier;    
uniform float SmoothnessMultiplier;  
uniform float RoughnessMultiplier;   
uniform float NormalStrength;        
uniform bool NormalFlipY;
uniform bool UseIBL;                 
uniform float MaxPrefilterMips;      
uniform float minRoughness;          

// --- Extra features ---
uniform bool EnableAtmosphericScattering;
uniform float rimPower;

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
    k = (k*k) / 8.0;
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

// ---------------- Normal map helper ----------------
vec3 GetNormalFromMap(vec2 uv, vec3 geomNormal, mat3 TBN, float strength)
{
    vec3 n = texture(Normal, uv).rgb;
    if(NormalFlipY) n.g = 1.0 - n.g;
    vec3 tangentNormal = normalize(n * 2.0 - 1.0);
    vec3 mapped = normalize(TBN * tangentNormal);
    return normalize(mix(geomNormal, mapped, clamp(strength, 0.0, 1.0)));
}

// ---------------- IBL helpers ----------------
vec3 SamplePrefilteredEnv(vec3 R, float roughness)
{
    float mip = clamp(roughness * MaxPrefilterMips, 0.0, MaxPrefilterMips);
    return textureLod(PrefilteredEnvMap, R, mip).rgb;
}

vec3 SampleIrradiance(vec3 N)
{
    return texture(IrradianceMap, N).rgb;
}

// ---------------- Main ----------------
void main()
{
    // --- Debug only modes ---
    if(renderMode == 1) { FragColor = vec4(texture(Diffuse, texCoord).rgb, 1.0); return; }
    if(renderMode == 2) {
        vec3 n = GetNormalFromMap(texCoord, normalize(fragNormal), fragTBN, NormalStrength);
        FragColor = vec4(n * 0.5 + 0.5, 1.0);
        return;
    }
    if(renderMode == 3) { float ao = texture(AO, texCoord).r; FragColor = vec4(vec3(ao), 1.0); return; }
    if(renderMode == 4) { float m = clamp(texture(Metallic, texCoord).r * MetallicMultiplier, 0.0, 1.0); FragColor = vec4(vec3(m), 1.0); return; }
    if(renderMode == 5) { float r = clamp(texture(Roughness, texCoord).r * RoughnessMultiplier, 0.0, 1.0); FragColor = vec4(vec3(r), 1.0); return; }

    // --- Sample maps ---
    vec3 albedo = texture(Diffuse, texCoord).rgb;
    float metallicTex = clamp(texture(Metallic, texCoord).r * MetallicMultiplier, 0.0, 1.0);

    float rawR = texture(Roughness, texCoord).r;
    float roughness = rawR;
    if(RoughnessMultiplier > 0.0) {
        roughness = clamp(rawR * RoughnessMultiplier, minRoughness, 1.0);
    } else {
        roughness = clamp(1.0 - rawR * SmoothnessMultiplier, minRoughness, 1.0);
    }
    roughness = max(roughness, minRoughness);

    float ao = texture(AO, texCoord).r;

    // normals & vectors
    vec3 N = GetNormalFromMap(texCoord, normalize(fragNormal), fragTBN, NormalStrength);
    vec3 V = normalize(CameraPos - fragPos);
    vec3 L = normalize(LightPos - fragPos);
    vec3 H = normalize(V + L);

    // --- Fresnel base reflectance ---
    vec3 F0 = vec3(0.04);
    F0 = mix(F0, albedo, metallicTex);

    // --- Direct lighting ---
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    vec3 Lo = vec3(0.0);
    if(NdotL > 0.0) {
        float NDF = DistributionGGX(N, H, roughness);
        float G   = GeometrySmith(N, V, L, roughness);
        vec3 F    = FresnelSchlick(max(dot(H, V), 0.0), F0);

        vec3 numerator = NDF * G * F;
        float denom = max(4.0 * NdotV * NdotL, EPS);
        vec3 specular = numerator / denom;

        vec3 kS = F;
        vec3 kD = (vec3(1.0) - kS) * (1.0 - metallicTex);

        vec3 diffuse = kD * albedo / PI;
        Lo = (diffuse + specular) * LightColor * NdotL;
    }

    // --- IBL ---
    vec3 ambient = vec3(0.0);
    vec3 F = FresnelSchlickRoughness(max(dot(N, V), 0.0), F0, roughness);
    vec3 specularIBL = vec3(0.0);
    vec3 diffuseIBL = vec3(0.0);

    if(UseIBL) {
        vec3 R = reflect(-V, N);
        vec3 prefiltered = SamplePrefilteredEnv(R, roughness);
        vec2 brdf = texture(BRDF_LUT, vec2(NdotV, roughness)).rg;
        specularIBL = prefiltered * (F * brdf.x + brdf.y);

        vec3 irradiance = SampleIrradiance(N);
        vec3 kD = (vec3(1.0) - F) * (1.0 - metallicTex);
        diffuseIBL = irradiance * albedo;

        ambient = (kD * diffuseIBL * ao) + specularIBL;
    } else {
        vec3 R = reflect(-V, N);
        vec3 envColor = textureLod(Skybox, R, 0.0).rgb;
        vec3 kS = F;
        vec3 kD = (vec3(1.0) - kS) * (1.0 - metallicTex);
        diffuseIBL = kD * albedo * ao;
        specularIBL = kS * envColor;
        ambient = AmbientLight * albedo * ao * (1.0 - metallicTex) + diffuseIBL + specularIBL;
    }

    vec3 color = Lo + ambient;

    // --- Rim lighting ---
 //   float rim = pow(1.0 - max(dot(N, V), 0.0), rimPower);
   // color += rim * LightColor * 0.25; // 0.25 = intensity scale

    // --- Atmospheric scattering (simple fog) ---
    if(EnableAtmosphericScattering) {
        float dist = length(CameraPos - fragPos);
        float fogFactor = clamp(1.0 - exp(-dist * 0.0015), 0.0, 1.0);
        vec3 fogColor = mix(vec3(0.6, 0.7, 0.9), AmbientLight, 0.3);
        color = mix(color, fogColor, fogFactor);
    }

    FragColor = vec4(color, 1.0); // no tonemap, no gamma
}
