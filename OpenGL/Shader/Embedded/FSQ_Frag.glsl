#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D hdrTexture;
uniform sampler2D bloomTexture;
uniform sampler2D aoTexture;

uniform float Exposure;
uniform float BloomIntensity;
uniform bool ApplyAO;
uniform float Contrast;
uniform float Saturation;
uniform float VignetteStrength;
uniform float FilmGrain;
uniform float Sharpen;

// ACES Tonemap
vec3 ACESFilm(vec3 x) {
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x*(a*x+b)) / (x*(c*x+d)+e), 0.0, 1.0);
}

float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898,78.233))) * 43758.5453);
}

// HDR scene value at uv: AO-attenuated color plus bloom, pre-tonemap.
vec3 SceneHDR(vec2 uv) {
    vec3 hdr = texture(hdrTexture, uv).rgb;
    if (ApplyAO)
        hdr *= texture(aoTexture, uv).r;
    hdr += texture(bloomTexture, uv).rgb * BloomIntensity;
    return hdr;
}

// HDR -> display: exposure + ACES. Every sample that feeds later math MUST go
// through this first; mixing raw HDR values with tonemapped ones explodes around
// very bright pixels (sun in an EXR sky) and produces NaN holes.
vec3 Tonemap(vec3 hdr) {
    return ACESFilm(hdr * Exposure);
}

void main()
{
    vec3 color = Tonemap(SceneHDR(TexCoords));

    // Optional unsharp mask (tonemapped samples; raw HDR here would create negatives/NaN).
    if (Sharpen > 0.0) {
        vec2 texel = 1.0 / vec2(textureSize(hdrTexture, 0));
        vec3 blur =
            Tonemap(SceneHDR(TexCoords + vec2(-texel.x, 0.0))) +
            Tonemap(SceneHDR(TexCoords + vec2( texel.x, 0.0))) +
            Tonemap(SceneHDR(TexCoords + vec2(0.0, -texel.y))) +
            Tonemap(SceneHDR(TexCoords + vec2(0.0,  texel.y)));
        blur *= 0.25;
        color = clamp(mix(blur, color, 1.0 + Sharpen), 0.0, 1.0);
    }

    if (Contrast != 1.0)
        color = pow(max(color, 0.0), vec3(Contrast));

    if (Saturation != 1.0) {
        float gray = dot(color, vec3(0.299, 0.587, 0.114));
        color = mix(vec3(gray), color, Saturation);
    }

    if (VignetteStrength > 0.0) {
        float dist = length(TexCoords - 0.5);
        color *= mix(1.0, smoothstep(0.8, 0.5, dist), VignetteStrength);
    }

    if (FilmGrain > 0.0)
        color += (rand(TexCoords * 1280.0) - 0.5) * FilmGrain;

    color = pow(max(color, 0.0), vec3(1.0/2.2));

    FragColor = vec4(color, 1.0);
}
