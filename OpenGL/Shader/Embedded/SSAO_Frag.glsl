#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D depthTexture;
uniform mat4 Projection;
uniform mat4 InvProjection;
uniform float Radius;
uniform float Intensity;

const int KERNEL_SIZE = 16;
// Hemisphere kernel (z > 0), denser near the origin. Pre-generated offline.
const vec3 kernel[KERNEL_SIZE] = vec3[](
    vec3( 0.0530,  0.0247,  0.0273), vec3(-0.0344,  0.0560,  0.0394),
    vec3( 0.0631, -0.0716,  0.0379), vec3(-0.0993, -0.0273,  0.0820),
    vec3( 0.1115,  0.0899,  0.1018), vec3(-0.0588,  0.1715,  0.1117),
    vec3(-0.1958, -0.1316,  0.0925), vec3( 0.2317, -0.1409,  0.1450),
    vec3( 0.1611,  0.2693,  0.1675), vec3(-0.3214,  0.1196,  0.2024),
    vec3(-0.1393, -0.3704,  0.1873), vec3( 0.4034,  0.1751,  0.2543),
    vec3( 0.1305, -0.4501,  0.3170), vec3(-0.4641,  0.2638,  0.3149),
    vec3(-0.2828, -0.4516,  0.4554), vec3( 0.5610,  0.3565,  0.3823)
);

float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

vec3 ViewPos(vec2 uv) {
    float depth = texture(depthTexture, uv).r;
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    return view.xyz / view.w;
}

void main() {
    float depth = texture(depthTexture, TexCoords).r;
    if (depth >= 1.0) { // sky
        FragColor = vec4(1.0);
        return;
    }

    vec3 P = ViewPos(TexCoords);
    vec3 N = normalize(cross(dFdx(P), dFdy(P)));

    // Random per-pixel rotation of the kernel hides banding.
    float angle = rand(TexCoords * 911.0) * 6.2831853;
    vec3 randomVec = vec3(cos(angle), sin(angle), 0.0);
    vec3 T = normalize(randomVec - N * dot(randomVec, N) + vec3(1e-5));
    vec3 B = cross(N, T);
    mat3 TBN = mat3(T, B, N);

    float occlusion = 0.0;
    float bias = 0.02;
    for (int i = 0; i < KERNEL_SIZE; i++) {
        vec3 samplePos = P + TBN * kernel[i] * (Radius / 0.7);

        vec4 clip = Projection * vec4(samplePos, 1.0);
        vec2 sampleUV = clip.xy / clip.w * 0.5 + 0.5;
        if (sampleUV.x < 0.0 || sampleUV.x > 1.0 || sampleUV.y < 0.0 || sampleUV.y > 1.0)
            continue;

        float sceneZ = ViewPos(sampleUV).z;
        float rangeCheck = smoothstep(0.0, 1.0, Radius / max(abs(P.z - sceneZ), 1e-4));
        occlusion += (sceneZ >= samplePos.z + bias ? 1.0 : 0.0) * rangeCheck;
    }

    float ao = clamp(1.0 - Intensity * occlusion / float(KERNEL_SIZE), 0.0, 1.0);
    FragColor = vec4(vec3(ao), 1.0);
}
