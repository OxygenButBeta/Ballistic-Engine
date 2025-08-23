#version 330 core

//Debug
uniform int renderMode; //Bare minimum

// Inputs
in vec2 texCoord;
in vec3 fragNormal;
in vec3 fragPos;
in mat3 fragTBN;
in vec4 fragPosLightSpace;

// Outputs
out vec4 FragColor;

//Properties
uniform vec3 CameraPos;
uniform float MetallicMultiplier;
uniform float SmoothnessMultiplier;
uniform bool EnableAtmosphericScattering;
uniform float NormalStrength;
uniform float rimPower;

//Lighting
uniform vec3 LightPos;
uniform vec3 LightColor;
uniform vec3 AmbientLight;

// shadow map
uniform sampler2D shadowMap;

// Maps
uniform sampler2D Diffuse;
uniform sampler2D Normal;
uniform sampler2D Metallic;
uniform sampler2D Roughness;
uniform sampler2D AO;
uniform samplerCube Skybox;

// --- Shadow calculation ---
float ShadowCalculation(vec4 fragPosLightSpace, vec3 normal, vec3 lightDir)
{
    // perspective divide
    vec3 projCoords = fragPosLightSpace.xyz / fragPosLightSpace.w;
    projCoords = projCoords * 0.5 + 0.5; 

    // check if outside light frustum
    if(projCoords.z > 1.0) return 0.0;

    // shadow map depth
    float closestDepth = texture(shadowMap, projCoords.xy).r; 
    float currentDepth = projCoords.z;

    // bias based on surface angle
    float bias = max(0.005 * (1.0 - dot(normal, lightDir)), 0.0005);

    // simple shadow
    float shadow = currentDepth - bias > closestDepth ? 1.0 : 0.0;
    return shadow;
}


void main() 
{
    // --- Render modes for debugging ---
    if (renderMode == 1) { // Diffuse
        FragColor = vec4(texture(Diffuse, texCoord).rgb, 1.0);
        return;
    } else if (renderMode == 2) { // Normal visualization
        vec3 n = normalize(fragTBN * (texture(Normal, texCoord).rgb * 2.0 - 1.0));
        FragColor = vec4(n * 0.5 + 0.5, 1.0);
        return;
    } else if (renderMode == 3) { // AO
        float ao = texture(AO, texCoord).r;
        FragColor = vec4(vec3(ao), 1.0);
        return;
    } else if (renderMode == 4) { // Metallic
        float metallic = clamp(texture(Metallic, texCoord).r * MetallicMultiplier, 0.0, 1.0);
        FragColor = vec4(vec3(metallic), 1.0);
        return;
    } else if (renderMode == 5) { // Roughness
        float roughness = 1.0 - clamp(texture(Roughness, texCoord).r * SmoothnessMultiplier, 0.0, 1.0);
        FragColor = vec4(vec3(roughness), 1.0);
        return;
    }
   else if  (renderMode == 6) {
    vec3 normalTex = texture(Normal, texCoord).rgb;
    normalTex = normalize(mix(vec3(0.0, 0.0, 1.0), normalTex, NormalStrength));
    vec3 tangentNormal = normalTex * 2.0 - 1.0;
    vec3 normal = normalize(mix(fragNormal, fragTBN * tangentNormal, NormalStrength));
    vec3 lightDir = normalize(LightPos - fragPos);
    float shadow = ShadowCalculation(fragPosLightSpace, normal, lightDir);
    FragColor = vec4(vec3(1.0 - shadow), 1.0); 
    return;
}

    // --- Sample textures ---
    vec3 albedo = texture(Diffuse, texCoord).rgb;
    float roughnessTex = texture(Roughness, texCoord).r;
    float metallicTex = texture(Metallic, texCoord).r;
    float ao = texture(AO, texCoord).r;

    vec3 normalTex = texture(Normal, texCoord).rgb;
    normalTex = normalize(mix(vec3(0.0, 0.0, 1.0), normalTex, NormalStrength));
    vec3 tangentNormal = normalTex * 2.0 - 1.0;

    // Apply multipliers
    float metallic = clamp(metallicTex * MetallicMultiplier, 0.0, 1.0);
    float smoothness = clamp((1.0 - roughnessTex) * SmoothnessMultiplier, 0.0, 1.0);
    float roughness = 1.0 - smoothness;

    // --- Lighting basis ---
    vec3 normal = normalize(mix(fragNormal, fragTBN * tangentNormal, NormalStrength));
    vec3 viewDir = normalize(CameraPos - fragPos);
    vec3 lightDir = normalize(LightPos - fragPos);
    vec3 reflectDir = reflect(-viewDir, normal);

    // --- Base reflectance ---
    vec3 F0 = vec3(0.04);
    F0 = mix(F0, albedo, metallic);

    float cosTheta = max(dot(viewDir, normal), 0.0);
    vec3 F = F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);

    // --- Ambient ---
    vec3 ambient = (1.0 - metallic) * albedo * AmbientLight * ao;

    // --- Diffuse ---
    float diff = max(dot(normal, lightDir), 0.0);
    vec3 diffuse = (1.0 - metallic) * albedo * diff * LightColor * ao;

    // --- Specular ---
    float NdotH = max(dot(normal, normalize(lightDir + viewDir)), 0.0);
    float shininess = pow(2.0, 2.0 + smoothness*8.0);
    float spec = pow(NdotH, shininess);
    vec3 specular = spec * F * LightColor * ao;

    // --- Shadow ---
    float shadow = ShadowCalculation(fragPosLightSpace, normal, lightDir);

    // --- Combine lighting with shadow ---
    vec3 lighting = ambient + (1.0 - shadow) * (diffuse + specular);

    // --- Reflection ---
    vec3 envColor = texture(Skybox, reflectDir).rgb;
    vec3 reflection = envColor * F * (0.5 + smoothness * 0.5);

    // --- Final color mix ---
    vec3 color = mix(lighting, reflection, 0.1 + metallic * 0.9);

    // --- Atmospheric scattering / fog ---
    if (EnableAtmosphericScattering) 
    {
        float distance = length(CameraPos - fragPos);
        float fogFactor = 1.0 - exp(-distance * 0.005); 
        fogFactor = clamp(fogFactor, 0.0, 1.0);
        vec3 fogColor = mix(vec3(0.6,0.7,0.9), AmbientLight, 0.3);
        color = mix(color, fogColor, fogFactor); 
    }

    // --- Output ---
    FragColor = vec4(color, 1.0);
}
