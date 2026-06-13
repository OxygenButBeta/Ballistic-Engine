#version 330 core
layout (location = 0) in vec3 aPos;

out vec3 TexCoords;

uniform mat4 projection;
uniform mat4 view;
uniform mat4 rotation;
void main()
{
    mat4 rotView = mat4(mat3(view)) * rotation;
    vec4 pos = projection * rotView * vec4(aPos, 1.0);
    gl_Position = pos.xyww;
    TexCoords = aPos;
}
