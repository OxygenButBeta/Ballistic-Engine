#version 330 core
layout (location = 0) in vec3 aPosition; 
layout (location = 1) in vec2 aTexCoord; 
layout (location = 2) in vec3 aNormal; 
layout (location = 3) in vec3 aTangent; 
layout (location = 4) in vec3 aBitangent; 

layout(location = 4) in vec4 instance_matrix_0;
layout(location = 5) in vec4 instance_matrix_1;
layout(location = 6) in vec4 instance_matrix_2;
layout(location = 7) in vec4 instance_matrix_3;

out vec2 vTex;
out vec3 vWorldPos;
out mat3 vTBN;

uniform bool isInstanced;
uniform mat4 view;
uniform mat4 projection;
uniform mat4 model;

void main() 
{
    mat4 modelMatrix = isInstanced
        ? transpose(mat4(instance_matrix_0, instance_matrix_1, instance_matrix_2, instance_matrix_3))
        : model;

    vTex = aTexCoord;
    vWorldPos = vec3(modelMatrix * vec4(aPosition,1.0));

    vec3 N = normalize(mat3(transpose(inverse(modelMatrix))) * aNormal);
    vec3 T = normalize(mat3(modelMatrix) * aTangent);
    vec3 B = normalize(mat3(modelMatrix) * aBitangent);

    vTBN = mat3(T, B, N);

    gl_Position = projection * view * modelMatrix * vec4(aPosition, 1.0);
}