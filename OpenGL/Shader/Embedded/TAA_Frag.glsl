#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// Temporal anti-aliasing: reproject last frame's accumulated image with the camera
// matrices (depth-based, camera motion only) and blend it with the jittered current frame.
//
// Quality features (vs a naive box-clamp TAA):
//  - YCoCg VARIANCE clipping: history is clipped to mean +/- gamma*sigma of the 3x3
//    neighborhood in YCoCg, which rejects ghosts much more precisely than an RGB min/max
//    box (the box passes any color inside the neighborhood's bounding volume).
//  - Catmull-Rom history resampling: bilinear history fetches blur the accumulation a
//    little every frame until texture detail is mush; the 9-tap Catmull-Rom kernel keeps
//    the history sharp under sub-pixel reprojection.
//  - Luma-adaptive feedback: where current and history luminance disagree strongly
//    (disocclusion, fast shading change), the history weight drops so the image converges
//    instead of smearing.

uniform sampler2D currentTexture;
uniform sampler2D historyTexture;
uniform sampler2D depthTexture;

uniform mat4 CurrInvViewProj; // current frame, unjittered
uniform mat4 PrevViewProj;    // previous frame, unjittered
uniform float Feedback;       // history weight (0..0.97)
uniform bool ValidHistory;

vec3 RGBToYCoCg(vec3 c) {
    return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b,
                0.5  * c.r            - 0.5  * c.b,
               -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}

vec3 YCoCgToRGB(vec3 c) {
    return vec3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

// 9-tap Catmull-Rom (Karis/Jimenez): sharp history resampling without ringing blowups.
vec3 SampleHistoryCatmullRom(vec2 uv, vec2 texSize) {
    vec2 samplePos = uv * texSize;
    vec2 texPos1 = floor(samplePos - 0.5) + 0.5;
    vec2 f = samplePos - texPos1;

    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;
    vec2 offset12 = w2 / max(w12, vec2(1e-5));

    vec2 texPos0 = (texPos1 - 1.0) / texSize;
    vec2 texPos3 = (texPos1 + 2.0) / texSize;
    vec2 texPos12 = (texPos1 + offset12) / texSize;

    vec3 result =
        texture(historyTexture, vec2(texPos0.x,  texPos0.y)).rgb  * (w0.x  * w0.y) +
        texture(historyTexture, vec2(texPos12.x, texPos0.y)).rgb  * (w12.x * w0.y) +
        texture(historyTexture, vec2(texPos3.x,  texPos0.y)).rgb  * (w3.x  * w0.y) +
        texture(historyTexture, vec2(texPos0.x,  texPos12.y)).rgb * (w0.x  * w12.y) +
        texture(historyTexture, vec2(texPos12.x, texPos12.y)).rgb * (w12.x * w12.y) +
        texture(historyTexture, vec2(texPos3.x,  texPos12.y)).rgb * (w3.x  * w12.y) +
        texture(historyTexture, vec2(texPos0.x,  texPos3.y)).rgb  * (w0.x  * w3.y) +
        texture(historyTexture, vec2(texPos12.x, texPos3.y)).rgb  * (w12.x * w3.y) +
        texture(historyTexture, vec2(texPos3.x,  texPos3.y)).rgb  * (w3.x  * w3.y);
    return max(result, vec3(0.0));
}

void main() {
    vec3 current = texture(currentTexture, TexCoords).rgb;
    if (!ValidHistory) {
        FragColor = vec4(current, 1.0);
        return;
    }

    // Reproject this pixel into last frame's screen space.
    float depth = texture(depthTexture, TexCoords).r;
    vec4 ndc = vec4(TexCoords * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 world = CurrInvViewProj * ndc;
    world /= world.w;
    vec4 prevClip = PrevViewProj * world;
    vec2 prevUV = prevClip.xy / prevClip.w * 0.5 + 0.5;

    if (prevUV.x < 0.0 || prevUV.x > 1.0 || prevUV.y < 0.0 || prevUV.y > 1.0 || prevClip.w <= 0.0) {
        FragColor = vec4(current, 1.0);
        return;
    }

    vec2 texSize = vec2(textureSize(currentTexture, 0));
    vec3 history = RGBToYCoCg(SampleHistoryCatmullRom(prevUV, texSize));

    // First/second moments of the 3x3 neighborhood in YCoCg.
    vec2 texel = 1.0 / texSize;
    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);
    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            vec3 c = RGBToYCoCg(texture(currentTexture, TexCoords + vec2(x, y) * texel).rgb);
            m1 += c;
            m2 += c * c;
        }
    }
    vec3 mean = m1 / 9.0;
    vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));

    // Clip (not clamp) the history toward the neighborhood mean: clipping preserves the
    // direction of the history color, so partially-valid history degrades gracefully.
    const float Gamma = 1.0;
    vec3 extents = Gamma * sigma + 1e-5;
    vec3 delta = history - mean;
    float maxUnit = max(abs(delta.x / extents.x), max(abs(delta.y / extents.y), abs(delta.z / extents.z)));
    if (maxUnit > 1.0)
        history = mean + delta / maxUnit;

    // Luma-adaptive feedback: agreement keeps the full history weight; strong disagreement
    // (disocclusion, shading pop) drops it so the pixel re-converges fast.
    vec3 currYCoCg = RGBToYCoCg(current);
    float lumaDiff = abs(currYCoCg.x - history.x) / max(max(currYCoCg.x, history.x), 0.2);
    float agreement = 1.0 - lumaDiff;
    float feedback = mix(0.5, clamp(Feedback, 0.0, 0.97), clamp(agreement * agreement, 0.0, 1.0));

    vec3 blended = YCoCgToRGB(mix(currYCoCg, history, feedback));
    FragColor = vec4(max(blended, vec3(0.0)), 1.0);
}
