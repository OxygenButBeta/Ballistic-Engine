#version 330 core
out vec4 FragColor;
in vec3 TexCoords;

uniform samplerCube skybox;
uniform float exposure;

void main()
{
    vec4 sky = texture(skybox, TexCoords);
    FragColor = vec4(sky.rgb * exposure, sky.a);
}
