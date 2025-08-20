#version 330 core

//Debug
uniform int renderMode; //Bare minimum

// Inputs
in vec2 texCoord;
in vec3 fragNormal;
in vec3 fragPos;
in mat3 fragTBN;

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

// Maps
uniform sampler2D Diffuse;
uniform sampler2D Normal;
uniform sampler2D Metallic;
uniform sampler2D Roughness;
uniform sampler2D AO;
uniform samplerCube Skybox;

vec3 ACESFilm(vec3 x) {
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x*(a*x+b))/(x*(c*x+d)+e), 0.0, 1.0);
}

void main() {
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

    float ao = texture(AO, texCoord).r;

    // --- Texture sampling ---
    float roughnessTex = texture(Roughness, texCoord).r;
    float metallicTex = texture(Metallic, texCoord).r;
    vec3 normalTex = texture(Normal, texCoord).rgb;
    normalTex = normalize(mix(vec3(0.0, 0.0, 1.0), normalTex, NormalStrength));
    vec3 tangentNormal = normalTex * 2.0 - 1.0;

    vec3 albedo = texture(Diffuse, texCoord).rgb;

    // Apply multipliers
    float metallic = clamp(metallicTex * MetallicMultiplier, 0.0, 1.0);
    float smoothness = clamp((1.0 - roughnessTex) * SmoothnessMultiplier, 0.0, 1.0);
    float roughness = 1.0 - smoothness;
    normalTex = normalTex * 2.0 - 1.0; 
    

    // --- Lighting basis ---
    vec3 normal = normalize(mix(fragNormal, fragTBN * tangentNormal, NormalStrength));
    vec3 viewDir = normalize(CameraPos - fragPos);
    vec3 lightDir = normalize(LightPos - fragPos);
    vec3 reflectDir = reflect(-viewDir, normal);

    // --- Environment reflection (Skybox) ---
    vec3 envColor = texture(Skybox, reflectDir).rgb;

    // --- Base reflectance (F0) ---
    vec3 F0 = vec3(0.04); // dielectric base reflectance
    F0 = mix(F0, albedo, metallic); // if metallic, albedo drives F0

    // --- Fresnel-Schlick approximation ---
    float cosTheta = max(dot(viewDir, normal), 0.0);
    vec3 F = F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);

    // --- Ambient ---
    vec3 ambient = (1.0 - metallic) * albedo * AmbientLight*ao;

    // --- Diffuse (Lambert) ---
    float diff = max(dot(normal, lightDir), 0.0);
    vec3 diffuse = (1.0 - metallic) * albedo * diff * LightColor*ao;

    // --- Specular (Phong style for now) ---
    float NdotH = max(dot(normal, normalize(lightDir + viewDir)), 0.0);
   float shininess = pow(2.0, 2.0 + smoothness*8.0);
    float spec = pow(NdotH, shininess);
    vec3 specular = spec * F * LightColor*ao;

    // --- Reflection from environment ---
    vec3 reflection = envColor * F * (0.5 + smoothness * 0.5);

    // --- Final color ---
    vec3 color = ambient + diffuse + specular;
   // color = mix(color, reflection, metallic * smoothness);
    color = mix(color, reflection, 0.1 + metallic * 0.9); 

if (EnableAtmosphericScattering) {
    float distance = length(CameraPos - fragPos);

    float fogFactor = 1.0 - exp(-distance * 0.005); 
    fogFactor = clamp(fogFactor, 0.0, 1.0);


    vec3 fogColor = mix(vec3(0.6,0.7,0.9), AmbientLight, 0.3);


    color = mix(color, fogColor, fogFactor); 
}
    // gamma correction
    color = ACESFilm(color);
    color = pow(color, vec3(1.0/2.2));

    FragColor = vec4(color, 1.0);
}
