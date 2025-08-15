namespace BallisticEngine.Rendering;

//Just a text structure for the shaders
// TODO: Implement a shader loader
public struct DefaultShader {
    public const string VertexShader = @"#version 330 core
layout (location = 0) in vec3 aPosition; // vertex coordinates
layout (location = 1) in vec2 aTexCoord; // texture coordinates
layout(location = 2) in vec3 aNormal; // normal vector
layout(location = 3) in vec3 aTangent; // tangent vector

layout(location = 4) in vec4 instance_matrix_0;
layout(location = 5) in vec4 instance_matrix_1;
layout(location = 6) in vec4 instance_matrix_2;
layout(location = 7) in vec4 instance_matrix_3;

out vec2 texCoord;
out mat3 TBN;

uniform bool isInstanced;
uniform mat4 view;
uniform mat4 projection;
uniform mat4 model;

void main() 
{
    mat4 modelMatrix = isInstanced
        ? transpose(mat4(instance_matrix_0, instance_matrix_1, instance_matrix_2, instance_matrix_3))
        : model;
   // texCoord = aTexCoord;
   //gl_Position = vec4(aPosition, 1.0) * modelMatrix * view * projection;
//return;
    vec3 T = normalize(mat3(modelMatrix) * aTangent);
    vec3 N = normalize(mat3(modelMatrix) * aNormal);
    vec3 B = normalize(cross(N, T)); // basit bitangent

    TBN = mat3(T, B, N);

    gl_Position = vec4(aPosition, 1.0) * modelMatrix * view * projection;

    texCoord = aTexCoord;
}";

    public const string FragmentShader = @"#version 330 core
in vec2 texCoord;
in mat3 TBN;

out vec4 FragColor;

uniform vec3 LightPos;     
uniform vec3 LightColor;

uniform sampler2D texture0; // diffuse
uniform sampler2D texture1; // normal map
uniform vec3 AmbientColor;

void main()
{
  // FragColor = texture(texture0, texCoord);
//return;
    vec3 normal = texture(texture1, texCoord).rgb;
    normal = normalize(normal * 2.0 - 1.0);


    vec3 worldNormal = normalize(TBN * normal);

    vec3 lightDir = normalize(LightPos);

    float diff = max(dot(worldNormal, lightDir), 0.0);

    vec4 diffuseColor = texture(texture0, texCoord);

    vec3 ambient = AmbientColor * diffuseColor.rgb;
    vec3 diffuse = diff * diffuseColor.rgb * LightColor;

    vec3 lighting = ambient + diffuse;

    FragColor = vec4(lighting, 1.0);
}
";
}