
#version 330 core
in vec2 vTex;
in vec3 vWorldPos;
in mat3 vTBN;

out vec4 FragColor;

uniform sampler2D Diffuse;   
uniform sampler2D Normal;    
uniform sampler2D Metallic;    
uniform sampler2D Roughness;   
uniform sampler2D AO;     

#define USE_ORM 0
#if USE_ORM
uniform sampler2D ORM;       
#endif

uniform vec3 LightPos;     
uniform vec3 LightColor;
uniform vec3 AmbientLight;
uniform vec3 CameraPos;

const float PI = 3.14159265359;

vec3 fresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

float distributionGGX(float NdotH, float a)
{
    float a2 = a * a;
    float denom = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + 1e-7);
}

float geometrySchlickGGX(float NdotV, float k)
{
    return NdotV / (NdotV * (1.0 - k) + k + 1e-7);
}

float geometrySmith(float NdotV, float NdotL, float k)
{
    float ggx1 = geometrySchlickGGX(NdotV, k);
    float ggx2 = geometrySchlickGGX(NdotL, k);
    return ggx1 * ggx2;
}

void main()
{
    vec3 albedo = texture(Diffuse, vTex).rgb;           
    vec3 nMap = texture(Normal, vTex).rgb * 2.0 - 1.0;   
    vec3 N = normalize(vTBN * normalize(nMap));         

    float metallic, roughness, ao;
#if USE_ORM
    vec3 orm = texture(ORM, vTex).rgb;
    ao        = orm.r;
    roughness = clamp(orm.g, 0.04, 1.0); 
    metallic  = clamp(orm.b, 0.0, 1.0);
#else
    metallic  = clamp(texture(Metallic,  vTex).r, 0.0, 1.0);
    roughness = clamp(texture(Roughness, vTex).r, 0.04, 1.0);
    ao        = texture(AO,       vTex).r;  
#endif

    vec3 V = normalize(CameraPos - vWorldPos);
    vec3 L = normalize(LightPos  - vWorldPos);
    vec3 H = normalize(V + L);

    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float HdotV = max(dot(H, V), 0.0);

    vec3 F0 = vec3(0.04);               
    F0 = mix(F0, albedo, metallic);      

    float alpha = roughness * roughness;

    float  D = distributionGGX(NdotH, alpha);
    float  k = (roughness + 1.0);
           k = (k * k) / 8.0;            
    float  G = geometrySmith(NdotV, NdotL, k);
    vec3   F = fresnelSchlick(HdotV, F0);

    vec3 numerator   = D * G * F;
    float denom      = 4.0 * max(NdotV, 0.0) * max(NdotL, 0.0) + 1e-7;
    vec3 specular    = numerator / denom;

    vec3 kS = F;        
    vec3 kD = (vec3(1.0) - kS);    
    kD *= (1.0 - metallic);         

    vec3 diffuse = (albedo / PI) * kD;

    vec3 direct = (diffuse + specular) * LightColor * NdotL;

    vec3 ambient = albedo * ao * AmbientLight;

    vec3 color = ambient + direct;

    color = max(color, 0.0);
    color = pow(color, vec3(1.0/2.2));

    FragColor = vec4(color, 1.0);
}