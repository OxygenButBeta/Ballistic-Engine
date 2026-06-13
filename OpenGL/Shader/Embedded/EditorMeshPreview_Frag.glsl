#version 330 core
in vec3 n;
out vec4 color;
void main() {
    vec3 l = normalize(vec3(0.5, 0.8, 0.6));
    float d = max(dot(normalize(n), l), 0.0) * 0.75 + 0.3;
    color = vec4(vec3(0.78, 0.80, 0.84) * d, 1.0);
}
