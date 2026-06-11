#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D sourceTexture;

void main() {
    vec2 t = 1.0 / vec2(textureSize(sourceTexture, 0));

    // 9-tap tent filter; blended additively onto the destination level.
    vec3 a = texture(sourceTexture, TexCoords + vec2(-t.x,  t.y)).rgb;
    vec3 b = texture(sourceTexture, TexCoords + vec2( 0.0,  t.y)).rgb;
    vec3 c = texture(sourceTexture, TexCoords + vec2( t.x,  t.y)).rgb;
    vec3 d = texture(sourceTexture, TexCoords + vec2(-t.x,  0.0)).rgb;
    vec3 e = texture(sourceTexture, TexCoords).rgb;
    vec3 f = texture(sourceTexture, TexCoords + vec2( t.x,  0.0)).rgb;
    vec3 g = texture(sourceTexture, TexCoords + vec2(-t.x, -t.y)).rgb;
    vec3 h = texture(sourceTexture, TexCoords + vec2( 0.0, -t.y)).rgb;
    vec3 i = texture(sourceTexture, TexCoords + vec2( t.x, -t.y)).rgb;

    vec3 color = e * 4.0 + (b + d + f + h) * 2.0 + (a + c + g + i);
    FragColor = vec4(color / 16.0, 1.0);
}
