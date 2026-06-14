#version 460 core

// LUMEN octahedral screen-probe INTEGRATE (Phase 4b, 3/3). The probe trace wrote, per screen probe, an
// OctRes x OctRes octahedral map of INCOMING radiance over the probe's hemisphere (in the probe-surface
// tangent frame) into the probe atlas. This pass reconstructs the half-res GI: for each half-res pixel
// it finds the 2x2 surrounding probes and, for each, INTEGRATES that probe's octmap against the pixel's
// cosine (diffuse BRDF) lobe — sum(radiance(dir) * max(0,dot(N,dir))) over the oct texels — then
// bilateral-weights the probes by depth + normal similarity. Directional (real Lumen), not a flat
// irradiance value: a probe on a different surface is rejected, so GI keeps silhouettes. The existing
// temporal + a-trous after this finish the denoise.

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D probeAtlas;     // RGBA16F octahedral radiance atlas (ProbeGrid*OctRes)
uniform sampler2D depthTexture;   // full-res window depth
uniform sampler2D normalTexture;  // full-res world normal (rgb)*0.5+0.5
uniform mat4  InvProjection;      // for linear view-Z
uniform mat4  InvView;            // view->world (probe surfaces share the same tangent-frame rule)
uniform int   ProbeGridX;         // probe grid resolution (separate ints — the shader API has no ivec2 setter)
uniform int   ProbeGridY;
uniform int   HalfDimsX;          // half-res output resolution
uniform int   HalfDimsY;
uniform int   ProbeStep;          // half-res px per probe edge
uniform int   OctRes;             // octahedral tile edge
#define ProbeGrid ivec2(ProbeGridX, ProbeGridY)
#define HalfDims  ivec2(HalfDimsX, HalfDimsY)

float LinearZ(float d) { vec4 v = InvProjection * vec4(0.0, 0.0, d * 2.0 - 1.0, 1.0); return v.z / v.w; }

// Equal-area octahedral hemisphere ENCODE (inverse of OctDecodeHemi in SdfTrace_Comp): a hemisphere
// direction (in the probe tangent frame, +Z = normal) -> [0,1]^2 tile UV.
vec2 OctEncodeHemi(vec3 d) {
    d = normalize(d);
    d.z = max(d.z, 1e-3);
    vec2 p = d.xy / (abs(d.x) + abs(d.y) + d.z); // project to the octahedron's z>=0 face
    return p * 0.5 + 0.5;
}

// Integrate one probe's octmap against the cosine lobe around worldN. The probe's own tangent frame is
// rebuilt from its normal (same Frisvad branch as the trace) so the stored directions decode correctly.
vec3 IntegrateProbe(ivec2 probe, vec3 worldN) {
    // Probe tangent frame (must match SdfTrace_Comp.ProbeOctMain).
    vec3 up = abs(worldN.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 T = normalize(cross(up, worldN));
    vec3 B = cross(worldN, T);

    ivec2 atlasBase = probe * OctRes;
    vec3 sum = vec3(0.0);
    float wSum = 0.0;
    // Walk every oct texel = a hemisphere direction; weight by the cosine of THIS pixel's normal vs the
    // texel direction. (The probe shares the pixel's normal closely — same block — so the probe-frame
    // direction is ~the pixel-frame direction; the cosine lobe gives the diffuse BRDF integral.)
    for (int oy = 0; oy < OctRes; ++oy)
    for (int ox = 0; ox < OctRes; ++ox) {
        vec2 octUV = (vec2(ox, oy) + 0.5) / float(OctRes);
        // Decode the texel's hemisphere direction (mirror of OctDecodeHemi).
        vec2 f = octUV * 2.0 - 1.0;
        vec3 nd = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
        float t = max(-nd.z, 0.0);
        nd.x += nd.x >= 0.0 ? -t : t;
        nd.y += nd.y >= 0.0 ? -t : t;
        nd.z = abs(nd.z);
        nd = normalize(nd);
        vec3 dir = T * nd.x + B * nd.y + worldN * nd.z; // world-space incoming direction
        float cosT = max(dot(worldN, dir), 0.0);
        if (cosT <= 0.0) continue;
        vec3 rad = texelFetch(probeAtlas, atlasBase + ivec2(ox, oy), 0).rgb;
        sum += rad * cosT;
        wSum += cosT;
    }
    // Normalize by the cosine weight => cosine-weighted average incoming radiance = the diffuse irradiance
    // estimate (the /PI of the Lambert BRDF is folded into the additive-composite scale downstream).
    return wSum > 1e-4 ? sum / wSum : vec3(0.0);
}

void main() {
    float depth = texture(depthTexture, TexCoords).r;
    if (depth >= 1.0) { FragColor = vec4(0.0); return; }
    vec3 n = normalize(texture(normalTexture, TexCoords).rgb * 2.0 - 1.0);
    float z = LinearZ(depth);

    // Fractional probe coordinate of this half-res pixel.
    vec2 halfPx = TexCoords * vec2(HalfDims);
    vec2 probeF = halfPx / float(ProbeStep) - 0.5;
    vec2 baseF = floor(probeF);
    vec2 frac = probeF - baseF;

    vec3 sum = vec3(0.0);
    float wSum = 0.0;
    for (int dy = 0; dy <= 1; ++dy)
    for (int dx = 0; dx <= 1; ++dx) {
        ivec2 pi = ivec2(baseF) + ivec2(dx, dy);
        if (pi.x < 0 || pi.y < 0 || pi.x >= ProbeGrid.x || pi.y >= ProbeGrid.y) continue;
        float wBil = (dx == 0 ? 1.0 - frac.x : frac.x) * (dy == 0 ? 1.0 - frac.y : frac.y);

        // The probe's representative-pixel surface for the bilateral compare (block centre).
        vec2 probeUv = (vec2(pi) + 0.5) * float(ProbeStep) / vec2(HalfDims);
        float pd = texture(depthTexture, probeUv).r;
        if (pd >= 1.0) continue;
        vec3 pn = normalize(texture(normalTexture, probeUv).rgb * 2.0 - 1.0);
        float pz = LinearZ(pd);
        float wN = pow(max(dot(n, pn), 0.0), 8.0);            // reject different-surface probes
        float wZ = 1.0 / (1.0 + abs(z - pz) * 4.0);           // reject depth discontinuities
        float w = wBil * wN * wZ;
        if (w <= 1e-5) continue;
        // Integrate the probe's octmap against THIS pixel's cosine lobe (directional, not flat).
        sum += IntegrateProbe(pi, n) * w;
        wSum += w;
    }
    FragColor = wSum > 1e-4 ? vec4(sum / wSum, 1.0) : vec4(0.0);
}
