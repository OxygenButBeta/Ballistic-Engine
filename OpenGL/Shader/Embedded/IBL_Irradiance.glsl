#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// Cosine-convolved diffuse irradiance, one cube face per pass.

uniform samplerCube EnvironmentMap;
uniform int Face;

const float PI = 3.14159265359;

// Standard GL cubemap face direction from face index + face UV (s right, t down).
vec3 FaceDir(int face, vec2 uv) {
    vec2 st = uv * 2.0 - 1.0;
    if (face == 0) return vec3( 1.0, -st.y, -st.x);
    if (face == 1) return vec3(-1.0, -st.y,  st.x);
    if (face == 2) return vec3( st.x,  1.0,  st.y);
    if (face == 3) return vec3( st.x, -1.0, -st.y);
    if (face == 4) return vec3( st.x, -st.y,  1.0);
    return vec3(-st.x, -st.y, -1.0);
}

void main() {
    vec3 N = normalize(FaceDir(Face, TexCoords));

    vec3 up = abs(N.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 right = normalize(cross(up, N));
    up = normalize(cross(N, right));

    vec3 irradiance = vec3(0.0);
    float sampleDelta = 0.025;
    float sampleCount = 0.0;
    for (float phi = 0.0; phi < 2.0 * PI; phi += sampleDelta) {
        for (float theta = 0.0; theta < 0.5 * PI; theta += sampleDelta) {
            vec3 tangentDir = vec3(sin(theta) * cos(phi), sin(theta) * sin(phi), cos(theta));
            vec3 sampleDir = tangentDir.x * right + tangentDir.y * up + tangentDir.z * N;
            // Clamp guards against infinities from EXR suns blowing up the integral.
            vec3 radiance = min(texture(EnvironmentMap, sampleDir).rgb, vec3(500.0));
            irradiance += radiance * cos(theta) * sin(theta);
            sampleCount += 1.0;
        }
    }

    irradiance = PI * irradiance / sampleCount;
    FragColor = vec4(irradiance, 1.0);
}
