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
uniform mat4 InvProjection;

uniform float SdfGiIntensity;     // master strength of the additive off-screen bounce
uniform bool DebugView;           // BALLISTIC_SDFGI_DEBUG: output ONLY the gathered GI

float LinearDepth(float d) {
    vec4 v = InvProjection * vec4(0.0, 0.0, d * 2.0 - 1.0, 1.0);
    return v.z / v.w;
}

void main() {
    vec3 scene = texture(sceneTexture, TexCoords).rgb;

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
    // albedo. This forward renderer has no albedo G-buffer, so the bounce was added with rho implicitly
    // = 1 — physically ~3-5x too much. That stayed tolerable on a DIM interior (SunTemple: small
    // irradiance) but SATURATED a bright daylit exterior red (BistroExterior: the surface-cache radiance
    // is large, so a unit-reflectance bounce blew out the whole street). Apply the standard average
    // scene diffuse albedo rho≈0.3 (the radiosity/ambient convention) so the added GI is energy-bounded.
    // SdfGiIntensity stays as the artistic strength on top (effective ≈ 0.3 * 0.4 default = 0.12 —
    // verified to keep SunTemple's interior bounce while killing the exterior red wash). Proper
    // per-pixel albedo is a later deferred-G-buffer change; 0.3 is the physically-grounded stand-in.
    const float kReceiverAlbedo = 0.3;

    // Confidence-weight by the gather's validity (a) so missed/invalid pixels add nothing.
    float valid = isnan(giSample.a) || isinf(giSample.a) ? 0.0 : clamp(giSample.a, 0.0, 1.0);
    vec3 add = gi * valid * max(SdfGiIntensity, 0.0) * kReceiverAlbedo;

    // Debug: the gathered off-screen bounce ALONE, so an enclosed-scene screenshot shows the raw
    // off-screen GI the SDF march produced. Black means the rays hit nothing lit / missed entirely.
    if (DebugView) {
        FragColor = vec4(add, 1.0);
        return;
    }

    // Purely additive — never darken below the no-GI baseline.
    FragColor = vec4(scene + add, 1.0);
}
