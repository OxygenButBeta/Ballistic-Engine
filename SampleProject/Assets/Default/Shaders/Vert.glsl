#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec3 aNormal;
layout(location = 3) in vec3 aTangent;

layout(location = 4) in vec4 instance_matrix_0;
layout(location = 5) in vec4 instance_matrix_1;
layout(location = 6) in vec4 instance_matrix_2;
layout(location = 7) in vec4 instance_matrix_3;

out vec2 texCoord;
out vec3 fragNormal;
out vec3 fragPos;
out mat3 fragTBN;
out vec4 fragPosLightSpace;

uniform bool isInstanced;
uniform mat4 view;
uniform mat4 projection;
uniform mat4 model;
uniform mat4 lightSpaceMatrix;

void main()
{
    mat4 modelMatrix = isInstanced
        ? transpose(mat4(instance_matrix_0, instance_matrix_1, instance_matrix_2, instance_matrix_3))
        : model;

    mat3 normalMatrix = mat3(transpose(inverse(modelMatrix)));
    vec3 N = normalize(normalMatrix * aNormal);
    vec3 T = normalize(mat3(modelMatrix) * aTangent);
    T = normalize(T - dot(T, N) * N); // Gram-Schmidt: keep the TBN orthogonal under non-uniform scale
    vec3 B = cross(N, T);
    fragTBN = mat3(T, B, N);

    texCoord = aTexCoord;
    fragNormal = N;
    fragPos = vec3(modelMatrix * vec4(aPosition, 1.0));
    fragPosLightSpace = lightSpaceMatrix * vec4(fragPos, 1.0);
    gl_Position = projection * view * vec4(fragPos, 1.0);
}
