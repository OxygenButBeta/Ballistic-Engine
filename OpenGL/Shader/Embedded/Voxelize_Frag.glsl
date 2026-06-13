#version 460 core
// Voxelization fragment stage: scatter each surface fragment's DIRECT-lit radiance into the 3D
// voxel texture. radiance = albedo * (sun*NdotL*shadow + skyAmbient). That injected radiance is
// the first bounce the cone tracer then gathers as indirect light. Written with imageStore using a
// moving-average-ish max-blend (atomic via a packed RGBA8 would be ideal; we use a simple store
// since each voxel is dominated by its nearest surface and the mip filter smooths the rest).

#extension GL_ARB_bindless_texture : require

in GsOut {
    vec3 worldPos;
    vec3 normal;
    vec2 uv;
    flat uint materialId;
} gs;

// DIRECT pass binds an r32ui VIEW of the grid for atomic moving-average inject (see below).
// BOUNCE passes bind the same storage as rgba8 for plain read-modify-write.
layout(binding = 0, r32ui) uniform uimage3D VoxelRadianceU;  // direct pass: atomic moving-average
layout(binding = 0, rgba8) uniform image3D  VoxelRadiance;   // bounce pass: rgba8 RMW

// Pack/unpack RGBA8 <-> uint. RGB = running-average radiance (8-bit, the grid is rgba8 anyway so no
// precision lost vs the old store), A = saturating sample count used to weight the moving average.
uint packRGBA8(vec4 c) {
    uvec4 q = uvec4(clamp(c, 0.0, 1.0) * 255.0 + 0.5);
    return q.r | (q.g << 8) | (q.b << 16) | (q.a << 24);
}
vec4 unpackRGBA8(uint u) {
    return vec4(float(u & 0xFFu), float((u >> 8) & 0xFFu),
                float((u >> 16) & 0xFFu), float((u >> 24) & 0xFFu)) / 255.0;
}

// Accumulate `newColor` into voxel `vc` as a moving average. Many fragments land in one voxel; a
// plain imageStore kept only the last (random) writer and the grid collapsed to a flat average
// color. compSwap loops until our blended value wins, so the voxel converges to the true mean of
// every surface that touched it - preserving per-surface color (red columns stay red, etc.).
void atomicAverage(ivec3 vc, vec3 newColor) {
    uint prev = imageLoad(VoxelRadianceU, vc).r;
    uint cur = prev;
    for (int i = 0; i < 16; ++i) {
        vec4 dec = unpackRGBA8(cur);
        float n = dec.a * 255.0;                 // prior sample count (0..255)
        vec3 avg = (dec.rgb * n + newColor) / (n + 1.0);
        float nc = min((n + 1.0) / 255.0, 1.0);  // saturating count back into alpha
        uint packed = packRGBA8(vec4(avg, nc));
        uint got = imageAtomicCompSwap(VoxelRadianceU, vc, cur, packed);
        if (got == cur) break;                   // our write landed
        cur = got;                               // contended: retry with the newer value
    }
}
// Bounce pass: read the grid's already-injected radiance (coarse mips, from the previous iteration's
// GenerateMipmap) along the surface normal and ADD it. Each iteration = one more bounce -> deep
// interiors fill with light. BouncePass=0 is the direct pass (overwrite); >0 adds bounce (RMW).
uniform sampler3D VoxelRadianceSampler;
uniform int BouncePass;

struct GpuMaterial { uvec2 dH;uvec2 nH;uvec2 mH;uvec2 rH;uvec2 aH;uvec2 eH;
    vec4 bcf;vec4 ef;float mm;float rm;float ns;float op;uint fl;uint a;uint b;uint c; };
layout(std430, binding = 6) readonly buffer GpuMaterialBuf { GpuMaterial gpuMats[]; };

uniform vec3 VolumeMin;
uniform vec3 VolumeInvSize;
uniform int  VoxelRes;

uniform vec3 SunDir;        // toward the sun (world)
uniform vec3 SunColor;      // pre-exposed radiance
uniform vec3 SkyAmbient;    // flat sky fill (pre-exposed)

// Sun cascade shadow (reuse the renderer's cascades — same uniforms as the lit pass).
uniform sampler2DArrayShadow ShadowCascades;
uniform mat4 CascadeMatrices[4];
uniform vec4 CascadeBias;
uniform int  CascadeCount;

float sunShadow(vec3 wp, vec3 N) {
    // Pick the first cascade whose projection contains the point.
    for (int c = 0; c < CascadeCount && c < 4; ++c) {
        vec4 p = CascadeMatrices[c] * vec4(wp, 1.0);
        vec3 proj = p.xyz / p.w * 0.5 + 0.5;
        if (all(greaterThan(proj.xy, vec2(0.0))) && all(lessThan(proj.xy, vec2(1.0))) && proj.z < 1.0) {
            float bias = CascadeBias[c];
            return texture(ShadowCascades, vec4(proj.xy, float(c), proj.z - bias));
        }
    }
    return 1.0; // outside all cascades: lit
}

void main() {
    vec3 g = (gs.worldPos - VolumeMin) * VolumeInvSize;
    if (any(lessThan(g, vec3(0.0))) || any(greaterThan(g, vec3(1.0))))
        return;
    ivec3 vc = ivec3(g * float(VoxelRes));
    vc = clamp(vc, ivec3(0), ivec3(VoxelRes - 1));

    GpuMaterial m = gpuMats[gs.materialId];
    vec3 albedo = texture(sampler2D(m.dH), gs.uv).rgb * m.bcf.rgb;

    vec3 N = normalize(gs.normal);
    float NdotL = max(dot(N, SunDir), 0.0);
    float sh = NdotL > 0.0 ? sunShadow(gs.worldPos, N) : 1.0;

    if (BouncePass == 0) {
        // DIRECT pass: the radiance this surface REFLECTS, injected as the first bounce the cone
        // tracer gathers. Inject ONLY the directly-lit term (sun*NdotL*shadow) + emissive - NOT the
        // sky ambient. The sky ambient is the sky itself, already supplied by the IBL in the lit
        // shader; injecting it here made every voxel = albedo*sky (a flat warm fill that just
        // re-added what IBL already had -> GI on/off looked identical and the grid read flat orange).
        // True colored bounce = sunlight reflecting off the RED column reaching a shadowed wall, which
        // only the directional term carries. A small ambient floor keeps fully-shadowed pockets from
        // contributing literally zero (so multi-bounce has a seed) without re-flattening the grid.
        vec3 direct = SunColor * NdotL * sh + SkyAmbient * 0.15;
        vec3 radiance = albedo * direct;
        if ((m.fl & 16u) != 0u)   // emissive-as-area-light (flag bit4 = HasEmissive)
            radiance += texture(sampler2D(m.eH), gs.uv).rgb * m.ef.rgb;
        atomicAverage(vc, radiance);
    } else {
        // BOUNCE pass: gather the already-injected radiance arriving over the HEMISPHERE around N
        // (5 taps: along N + 4 tilted ~60deg, each a coarse-mip average at a couple of distances),
        // then ADD this surface's reflected share. The wider gather lets light wrap around corners
        // and propagate deeper into interiors instead of only straight out along N. RMW preserves
        // the direct light from pass 0 so each pass compounds another bounce.
        vec3 up = abs(N.y) < 0.95 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
        vec3 T = normalize(cross(up, N));
        vec3 B = cross(N, T);
        const float COS60 = 0.5, SIN60 = 0.866;

        vec3 incoming = vec3(0.0);
        float wsum = 0.0;
        // Sample directions: N (weight 1) + 4 tilted (weight 0.6).
        for (int d = 0; d < 5; ++d) {
            vec3 dir; float w;
            if (d == 0) { dir = N; w = 1.0; }
            else {
                float a = float(d - 1) * 1.5707963; // 90deg apart
                dir = normalize(N * COS60 + (T * cos(a) + B * sin(a)) * SIN60);
                w = 0.6;
            }
            // Two distances along the direction, at coarse mips (the hemisphere average there).
            vec3 p1 = (gs.worldPos + dir * 1.5 - VolumeMin) * VolumeInvSize;
            vec3 p2 = (gs.worldPos + dir * 4.0 - VolumeMin) * VolumeInvSize;
            if (all(greaterThan(p1, vec3(0.0))) && all(lessThan(p1, vec3(1.0)))) {
                incoming += w * textureLod(VoxelRadianceSampler, p1, 1.5).rgb;
                wsum += w;
            }
            if (all(greaterThan(p2, vec3(0.0))) && all(lessThan(p2, vec3(1.0)))) {
                incoming += w * 0.7 * textureLod(VoxelRadianceSampler, p2, 2.5).rgb;
                wsum += w * 0.7;
            }
        }
        if (wsum > 0.0)
            incoming /= wsum;

        vec3 bounce = albedo * incoming * 0.9; // 0.9 = bounce energy retained per hop
        vec4 cur = imageLoad(VoxelRadiance, vc);
        imageStore(VoxelRadiance, vc, vec4(cur.rgb + bounce, 1.0));
    }
}
