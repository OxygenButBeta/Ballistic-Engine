#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// SDF World-Space GI composite (P6.5). Depth-aware upsamples the half-res off-screen-bounce
// gather (SdfTrace_Comp output: rgb = gathered indirect radiance, a = validity/confidence) and
// adds it PURELY ADDITIVELY onto the already-lit scene colour. This is the hard-won GI lesson:
// indirect light only ever LIFTS a surface, it never darkens below the no-GI baseline. So unlike
// SSR_Combine (which lerps, replacing the baked sky reflection) this shader does scene + gi.
//
// Runs as a post pass right before SSGI, mirroring how GLSSGIPass returns a modified colour
// texture — so no Frag.glsl change is needed (the opaque pass already shaded before the SDF march).

uniform sampler2D sceneTexture;   // full-res lit scene
uniform sampler2D giTexture;      // HALF-RES off-screen GI: rgb = indirect, a = validity
uniform sampler2D depthTexture;   // full-res depth (the same buffer the march reconstructed from)
uniform sampler2D albedoTexture;  // full-res diffuse-albedo G-buffer (rgb), for receiver reflectance
uniform mat4 InvProjection;

uniform float SdfGiIntensity;     // master strength of the additive off-screen bounce
uniform bool DebugView;           // BALLISTIC_SDFGI_DEBUG: output ONLY the gathered GI
uniform bool HasAlbedo;           // 1 = multiply GI by the per-pixel albedo G-buffer; 0 = flat fallback

float LinearDepth(float d) {
    vec4 v = InvProjection * vec4(0.0, 0.0, d * 2.0 - 1.0, 1.0);
    return v.z / v.w;
}

void main() {
    vec3 scene = texture(sceneTexture, TexCoords).rgb;

    // SKY pixels receive ZERO GI — they are not a surface. Without this, the depth-aware upsample below
    // can pull a foreground-geometry GI texel into a sky pixel near a silhouette (all 4 taps depth-
    // rejected -> the 1e-5 weight floors cancel in acc/wSum and leave a foreground value), painting a
    // bright fringe into the sky hugging rooflines/edges (the review's silhouette-halo defect). A hard
    // sky gate is unambiguously correct and kills the halo at the source.
    if (texture(depthTexture, TexCoords).r >= 1.0) {
        FragColor = vec4(DebugView ? vec3(0.0) : scene, 1.0);
        return;
    }

    // Depth-aware upsample of the half-res GI buffer: each of the 4 nearest half-res texels is
    // weighted by its bilinear factor x depth similarity, so the off-screen bounce doesn't bleed
    // across silhouettes (the halo a plain bilinear upsample smears around edges).
    vec2 giSize = vec2(textureSize(giTexture, 0));
    vec2 texel = 1.0 / giSize;
    vec2 pos = TexCoords * giSize - 0.5;
    vec2 base = (floor(pos) + 0.5) * texel;
    vec2 f = fract(pos);

    float centerZ = LinearDepth(texture(depthTexture, TexCoords).r);

    vec4 acc = vec4(0.0);
    float wSum = 0.0;
    for (int i = 0; i < 4; i++) {
        vec2 corner = vec2(float(i & 1), float(i >> 1));
        vec2 uv = base + corner * texel;
        float wBilinear = (corner.x > 0.5 ? f.x : 1.0 - f.x) * (corner.y > 0.5 ? f.y : 1.0 - f.y);
        float tapZ = LinearDepth(texture(depthTexture, uv).r);
        float wDepth = 1.0 / (1.0 + abs(tapZ - centerZ) * 2.0);
        float w = wBilinear * wDepth + 1e-5;
        acc += texture(giTexture, uv) * w;
        wSum += w;
    }
    vec4 giSample = acc / wSum;

    // NaN/Inf scrub via a true component SELECT (ternary) — NEVER mix(v, 0, flag): float mix is
    // arithmetic (v*(1-flag) + 0*flag) and NaN*0 == NaN / Inf*0 == NaN, so it never scrubs. One
    // bad texel from the march would otherwise paint a black/white speckle that scene+add carries.
    vec3 gi = giSample.rgb;
    gi = vec3(isnan(gi.x) || isinf(gi.x) ? 0.0 : gi.x,
              isnan(gi.y) || isinf(gi.y) ? 0.0 : gi.y,
              isnan(gi.z) || isinf(gi.z) ? 0.0 : gi.z);
    gi = max(gi, 0.0);

    // RECEIVER REFLECTANCE (the energy fix). The gather returns INCOMING radiance (irradiance); the
    // diffuse GI a surface actually reflects is rho * irradiance, where rho is the RECEIVER's diffuse
    // albedo. With rho implicitly = 1 the bounce was physically ~3-5x too much — tolerable on a dim
    // interior but it SATURATED bright/enclosed scenes (the BistroExterior red wash, the BistroInterior
    // pure-red rail). Now multiply by the PER-PIXEL diffuse albedo from the G-buffer (HasAlbedo): a
    // dark/low-albedo surface bounces little, a red wall reflects only its red, and crucially the
    // bounce can never exceed what the surface can physically reflect — bounding the enclosed case at
    // the source. Falls back to the flat rho=0.3 (radiosity avg-albedo convention) when no G-buffer.
    // SdfGiIntensity stays as the artistic strength on top.
    vec3 rho = HasAlbedo ? max(texture(albedoTexture, TexCoords).rgb, vec3(0.0)) : vec3(0.3);

    // Confidence-weight by the gather's validity (a) so missed/invalid pixels add nothing.
    float valid = isnan(giSample.a) || isinf(giSample.a) ? 0.0 : clamp(giSample.a, 0.0, 1.0);
    vec3 add = gi * valid * max(SdfGiIntensity, 0.0) * rho;

    // Debug: the gathered off-screen bounce ALONE, so an enclosed-scene screenshot shows the raw
    // off-screen GI the SDF march produced. Black means the rays hit nothing lit / missed entirely.
    if (DebugView) {
        FragColor = vec4(add, 1.0);
        return;
    }

    // Purely additive — never darken below the no-GI baseline.
    FragColor = vec4(scene + add, 1.0);
}
