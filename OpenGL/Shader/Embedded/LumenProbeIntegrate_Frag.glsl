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

// Integrate one probe's octmap against the cosine lobe around the PIXEL normal `pixelN`. CRITICAL: the
// octmap was TRACED in the PROBE's own tangent frame (from `probeN`), so it MUST be decoded in THAT
// frame — decoding in the pixel's frame (the prior bug) made adjacent probes with slightly different
// normals decode inconsistently => the visible probe-lattice GRID. We decode each texel's direction in
// the probe frame (matching SdfTrace_Comp.ProbeOctMain), then weight by the PIXEL's cosine lobe so the
// result is this pixel's diffuse irradiance estimate from that probe.
vec3 IntegrateProbe(ivec2 probe, vec3 probeN, vec3 pixelN) {
    // PROBE tangent frame (must match SdfTrace_Comp.ProbeOctMain — built from the PROBE normal).
    vec3 up = abs(probeN.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 T = normalize(cross(up, probeN));
    vec3 B = cross(probeN, T);

    ivec2 atlasBase = probe * OctRes;
    vec3 sum = vec3(0.0);
    float wSum = 0.0;
    for (int oy = 0; oy < OctRes; ++oy)
    for (int ox = 0; ox < OctRes; ++ox) {
        vec2 octUV = (vec2(ox, oy) + 0.5) / float(OctRes);
        // Decode the texel's hemisphere direction (mirror of OctDecodeHemi), in the PROBE frame.
        vec2 f = octUV * 2.0 - 1.0;
        vec3 nd = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
        float t = max(-nd.z, 0.0);
        nd.x += nd.x >= 0.0 ? -t : t;
        nd.y += nd.y >= 0.0 ? -t : t;
        nd.z = abs(nd.z);
        nd = normalize(nd);
        vec3 dir = T * nd.x + B * nd.y + probeN * nd.z; // world-space incoming direction (probe frame)
        float cosT = max(dot(pixelN, dir), 0.0);        // weight by the PIXEL's BRDF lobe
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
        // The probe's OWN normal (at its representative pixel = the block centre ProbeOctMain used to
        // build the probe frame). Decode the octmap in this frame; weight against the PIXEL normal `n`.
        vec3 pn = normalize(texture(normalTexture, probeUv).rgb * 2.0 - 1.0);
        float pz = LinearZ(pd);
        // Softer edge stops than before (pow 8 -> 3, depth slope 4 -> 2): too-sharp weights left only
        // one probe contributing inside a block -> the lattice grid. Softer = neighbour probes blend
        // smoothly across block boundaries while still rejecting genuinely different surfaces/depths.
        float wN = pow(max(dot(n, pn), 0.0), 3.0);
        float wZ = 1.0 / (1.0 + abs(z - pz) * 2.0);
        float w = wBil * wN * wZ + 1e-4;        // tiny floor so a fully-rejected 2x2 still picks nearest
        sum += IntegrateProbe(pi, pn, n) * w;   // decode in the PROBE frame (pn), weight by PIXEL lobe (n)
        wSum += w;
    }
    FragColor = wSum > 1e-4 ? vec4(sum / wSum, 1.0) : vec4(0.0);
}
