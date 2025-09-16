#version 330 core

uniform int renderMode;

in vec2 texCoord;
in vec3 fragNormal;
in vec3 fragPos;
in mat3 fragTBN;

out vec4 FragColor;

uniform vec3 CameraPos;
uniform float MetallicMultiplier;
uniform float SmoothnessMultiplier;
uniform bool EnableAtmosphericScattering;
uniform float NormalStrength;
uniform float rimPower;

uniform vec3 LightPos;
uniform vec3 LightColor;
uniform vec3 AmbientLight;

uniform sampler2D Diffuse;
uniform sampler2D Normal;
uniform sampler2D Metallic;
uniform sampler2D Roughness;
uniform sampler2D AO;
uniform samplerCube Skybox;

uniform bool NormalFlipY;

const float PI = 3.14159265359;
const float minRoughness = 0.04;

// ---------------- PBR helpers ----------------
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness*roughness;
    float a2 = a*a;
    float NdotH = max(dot(N,H),0.0);
    float denom = NdotH*NdotH*(a2-1.0)+1.0;
    return a2/(PI*denom*denom+1e-6);
}

float GeometrySchlickGGX(float NdotV, float roughness)
{
    float k = (roughness+1.0)*(roughness+1.0)/8.0;
    return NdotV / max(NdotV*(1.0-k)+k,1e-6);
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    return GeometrySchlickGGX(max(dot(N,V),0.0), roughness) *
           GeometrySchlickGGX(max(dot(N,L),0.0), roughness);
}

vec3 FresnelSchlickRoughness(vec3 F0, float cosTheta, float roughness)
{
    return F0 + (max(vec3(1.0-roughness),F0)-F0) * pow(1.0-cosTheta,5.0);
}

// ---------------- Normal utility ----------------
vec3 GetNormalFromMap(vec2 uv, vec3 geomNormal, mat3 TBN, float strength)
{
    vec3 n = texture(Normal,uv).rgb;
    if(NormalFlipY) n.g = 1.0-n.g;
    vec3 tangentNormal = normalize(n*2.0-1.0);
    vec3 mapped = normalize(TBN * tangentNormal);
    return normalize(mix(geomNormal,mapped,clamp(strength,0.0,1.0)));
}

void main()
{
    // --- Debug ---
    if(renderMode==1){ FragColor=vec4(texture(Diffuse,texCoord).rgb,1.0); return; }
    if(renderMode==2){ 
        vec3 n = GetNormalFromMap(texCoord,normalize(fragNormal),fragTBN,NormalStrength);
        FragColor=vec4(n*0.5+0.5,1.0); return;
    }
    if(renderMode==3){ float ao=texture(AO,texCoord).r; FragColor=vec4(vec3(ao),1.0); return; }
    if(renderMode==4){ float m=clamp(texture(Metallic,texCoord).r*MetallicMultiplier,0.0,1.0); FragColor=vec4(vec3(m),1.0); return; }
    if(renderMode==5){ float r=1.0-clamp(texture(Roughness,texCoord).r*SmoothnessMultiplier,0.0,1.0); FragColor=vec4(vec3(r),1.0); return; }
    if(renderMode==6){ float s=clamp((1.0-texture(Roughness,texCoord).r)*SmoothnessMultiplier,0.0,1.0); FragColor=vec4(vec3(s),1.0); return; }
    if(renderMode==7){ vec3 viewDir=normalize(CameraPos-fragPos); FragColor=vec4(viewDir*0.5+0.5,1.0); return; }

    // --- Sample maps ---
    vec3 albedo = texture(Diffuse,texCoord).rgb;
    float roughnessTex = texture(Roughness,texCoord).r;
    float metallicTex = texture(Metallic,texCoord).r;
    float ao = texture(AO,texCoord).r;

    float smoothness = clamp((1.0-roughnessTex)*SmoothnessMultiplier,0.0,1.0);
    float roughness = clamp(1.0-smoothness,minRoughness,1.0);
    float metallic = clamp(metallicTex*MetallicMultiplier,0.0,1.0);

    vec3 N = GetNormalFromMap(texCoord,normalize(fragNormal),fragTBN,NormalStrength);
    vec3 V = normalize(CameraPos-fragPos);
    vec3 L = normalize(LightPos-fragPos);
    vec3 H = normalize(V+L);

    // --- Fresnel base ---
    vec3 F0 = vec3(0.04);
    F0 = mix(F0,albedo,metallic);

    // --- Cook-Torrance ---
    float NdotL = max(dot(N,L),0.0);
    float NdotV = max(dot(N,V),0.0);
    vec3 Lo = vec3(0.0);
    if(NdotL>0.0)
    {
        float NDF = DistributionGGX(N,H,roughness);
        float G = GeometrySmith(N,V,L,roughness);
        vec3 F = FresnelSchlickRoughness(F0,max(dot(H,V),0.0),roughness);

        vec3 spec = NDF*G*F / max(4.0*NdotV*NdotL,1e-6);
        vec3 kS = F;
        vec3 kD = (vec3(1.0)-kS)*(1.0-metallic);
        vec3 diffuse = kD*albedo/PI;

        Lo = (diffuse + spec)*LightColor*NdotL;
    }

    // --- IBL with roughness & metallic mask ---
    float maxMips=8.0;
    vec3 R = reflect(-V,N);
    float mipLevel = roughness*maxMips;
    vec3 envColor = textureLod(Skybox,R,mipLevel).rgb;

    vec3 F = FresnelSchlickRoughness(F0,max(dot(N,V),0.0),roughness);
    vec3 kSibl = F;
    vec3 kDibl = (vec3(1.0)-kSibl)*(1.0-metallic);

    // AO affects both diffuse & specular
    vec3 diffuseIBL = kDibl*albedo*ao;
    vec3 specularIBL = kSibl*envColor*ao;

    vec3 ambient = AmbientLight*albedo*ao*(1.0-metallic);

    vec3 color = Lo + ambient + diffuseIBL + specularIBL;

    // --- Fog ---
    if(EnableAtmosphericScattering){
        float dist = length(CameraPos-fragPos);
        float fogFactor = clamp(1.0-exp(-dist*0.0015),0.0,1.0);
        vec3 fogColor = mix(vec3(0.6,0.7,0.9),AmbientLight,0.3);
        color = mix(color,fogColor,fogFactor);
    }

    FragColor = vec4(color,1.0);
}
