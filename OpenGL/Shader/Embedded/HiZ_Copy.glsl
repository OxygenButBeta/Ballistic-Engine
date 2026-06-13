#version 460 core
in vec2 TexCoords;
out float FragDepth;
uniform sampler2D SourceDepth;
void main() { FragDepth = texture(SourceDepth, TexCoords).r; }
