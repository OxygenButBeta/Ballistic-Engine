namespace BallisticEngine.Rendering;

//Just a text structure for the shaders
// TODO: Implement a shader loader
public struct DefaultShader
{
    public const string VertexShader = @"#version 330 core
    layout (location = 0) in vec3 aPosition; 
    layout (location = 1) in vec2 aTexCoord; 
    layout(location = 2) in vec3 aNormal; 
    layout(location = 3) in vec3 aTangent; 

    layout(location = 4) in vec4 instance_matrix_0;
    layout(location = 5) in vec4 instance_matrix_1;
    layout(location = 6) in vec4 instance_matrix_2;
    layout(location = 7) in vec4 instance_matrix_3;

    out vec2 texCoord;
    out vec3 fragNormal;
    out vec3 fragPos;

    uniform bool isInstanced;
    uniform mat4 view;
    uniform mat4 projection;
    uniform mat4 model;

    void main() 
    {
        mat4 modelMatrix = isInstanced
            ? transpose(mat4(instance_matrix_0, instance_matrix_1, instance_matrix_2, instance_matrix_3))
            : model;

        texCoord = aTexCoord;
        fragNormal = mat3(transpose(inverse(modelMatrix))) * aNormal; 
        fragPos = vec3(modelMatrix * vec4(aPosition,1.0));
        gl_Position = projection * view * modelMatrix * vec4(aPosition, 1.0);

    }";

    public const string FragmentShader = @"#version 330 core

    in vec2 texCoord;
    in vec3 fragNormal;
    in vec3 fragPos;

    out vec4 FragColor;

    uniform sampler2D Diffuse; 
    uniform sampler2D Normal; 
    uniform vec3 LightPos;
    uniform vec3 LightColor;
    uniform vec3 AmbientLight;
    uniform bool EnableAtmosphericScattering;

uniform vec3 CameraPos;

    void main()
{
    vec3 normal = normalize(fragNormal);
    vec3 lightDir = normalize(LightPos - fragPos);

    float diff = max(dot(normal, lightDir), 0.0);
    float bias = 0.05; 
    vec3 diffuse = texture(Diffuse, texCoord).rgb * (diff + bias) * LightColor;
    //vec3 ambient = texture(Diffuse, texCoord).rgb * AmbientLight;
    vec3 ambient = texture(Diffuse, texCoord).rgb * AmbientLight * vec3(0.8, 0.85, 1.0);
    vec3 color = ambient + diffuse;
    color = clamp(color, 0.0, 1.0); 
    color = pow(color, vec3(1.0/2.2));
    vec3 viewDir = normalize(CameraPos - fragPos);
    vec3 reflectDir = reflect(-lightDir, normal);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0), 32.0);
    vec3 specular = spec * LightColor * 0.3;

if (EnableAtmosphericScattering)
{
float distance = length(CameraPos - fragPos);
float fogFactor = clamp(exp(-distance * 0.02), 0.0, 1.0);
fogFactor = mix(1.0, fogFactor, 0.7);
vec3 fogColor = normalize(AmbientLight);
color = mix(fogColor, color, fogFactor);
}


    FragColor = vec4(color, 1.0);
}
    ";
}