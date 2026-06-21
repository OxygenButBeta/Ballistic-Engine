// DDGI — full-res sample (Pass 2). One thread per screen pixel. Reconstructs the pixel's world position +
// normal from the G-buffer, finds the 8 probes bracketing it, and trilinearly blends their octahedral
// irradiance in the surface-normal direction. Writes the indirect irradiance E into `Indirect` (the combine
// reads it). D1: trilinear + a smooth normal/backface weight. D3 adds the Chebyshev visibility weight (leak fix).
//
// Bound: b0 constants | t0 depth (R32F) | t1 normal (G1, [0,1] packed) | t2 Irradiance (StructuredBuffer) |
//        u0 Indirect (RWTexture2D) | s0 clamp.

cbuffer DdgiSampleConstants : register(b0) {
    float4x4 InvViewProj;
    float3 GridOrigin;   float Pad0;
    float3 ProbeSpacing; float NormalBias;   // push the sample point off the surface along N (self-leak guard)
    uint   CountX, CountY, CountZ;  uint W;
    uint   H;  float Intensity;  float UseVisibility;  float UsePlacement;  // UseVisibility=1 → Chebyshev (D3); UsePlacement=1 → occupancy-aware probe state
};

Texture2D<float>  Depth      : register(t0);
Texture2D<float4> NormalTex  : register(t1);
StructuredBuffer<float4> Irradiance : register(t2);
StructuredBuffer<float2> VisMoments : register(t3);   // D3: per-probe visibility moments (mean dist, mean dist²)
StructuredBuffer<float4> ProbeState : register(t4);   // xyz = relocation offset, w = active (occupancy-aware placement)
Texture2D<float4> Albedo     : register(t5);   // G-buffer base color — folded in HERE (compute) so the combine PS
                                               // never binds the G-buffer (it bound albedo as RENDER_TARGET → 0).
RWTexture2D<float4> Indirect : register(u0);
SamplerState LinearClamp : register(s0);
static const float DDGI_PI = 3.14159265359;

static const int OctRes = 8;   // MUST match Dx12DdgiProbeGrid.OctRes + DdgiRelight.hlsl (irradiance cell edge)
static const int OctTexels = OctRes * OctRes;
static const int VisRes = 16;
static const int VisTexels = VisRes * VisRes;

float2 OctEncode(float3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(e.yx)) * float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}

// Bilinear sample of a probe's octahedral cell in direction `dir`. The cell is OctRes×OctRes float4 texels in
// the Irradiance buffer at [probe*OctTexels ...]. Simple manual bilinear (clamped to cell).
float3 SampleProbeOct(uint probe, float3 dir) {
    float2 uv = OctEncode(dir) * float(OctRes) - 0.5;
    int2 base = (int2)floor(uv);
    float2 f = uv - base;
    float3 c = 0.0.xxx;
    [unroll] for (int dy = 0; dy <= 1; dy++)
    [unroll] for (int dx = 0; dx <= 1; dx++) {
        int2 t = clamp(base + int2(dx, dy), 0, OctRes - 1);
        float w = (dx == 0 ? 1.0 - f.x : f.x) * (dy == 0 ? 1.0 - f.y : f.y);
        c += Irradiance[probe * OctTexels + t.y * OctRes + t.x].rgb * w;
    }
    return c;
}

// Nearest visibility moments for `probe` in direction `dir` (toward the surface). The moments are the depth-
// weighted mean + mean² of the probe's rays near that direction (filled in the relight pass).
float2 SampleProbeVis(uint probe, float3 dir) {
    int2 t = clamp((int2)floor(OctEncode(dir) * float(VisRes)), 0, VisRes - 1);
    return VisMoments[probe * VisTexels + t.y * VisRes + t.x];
}

// Chebyshev (variance-shadow) visibility weight: how likely is the surface point visible from the probe? If the
// surface is FARTHER than the probe's mean occluder distance in that direction, it is probably occluded → low
// weight → the probe's radiance does not leak through the wall. dist = surface-to-probe distance along `dir`.
float ChebyshevWeight(uint probe, float3 dirProbeToSurface, float dist, float bias) {
    float2 mom = SampleProbeVis(probe, dirProbeToSurface);
    float mean = mom.x;
    // Bias the test distance DOWN by a fraction of the probe spacing: a surface within `bias` of the probe's
    // mean occluder depth is still treated as visible. This kills the self-occlusion shimmer where a probe sits
    // just above a floor (its downward rays hit at ~0, so an unbiased test would reject the very floor it lights).
    if (dist - bias <= mean) return 1.0;
    float variance = max(mom.y - mean * mean, 1e-4);
    float d = (dist - bias) - mean;
    float p = variance / (variance + d * d);            // Chebyshev upper bound on P(visible)
    return max(p * p * p, 0.0);                          // cubed → sharper cutoff (standard DDGI)
}

uint ProbeIndex(uint3 c) { return c.z * (CountX * CountY) + c.y * CountX + c.x; }

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= W || tid.y >= H) return;
    int2 px = int2(tid.xy);

    float depth = Depth.Load(int3(px, 0));
    if (depth >= 1.0) { Indirect[px] = float4(0, 0, 0, 1); return; }   // sky → no GI

    // Reconstruct world position from depth.
    float2 uvScreen = (float2(px) + 0.5) / float2(W, H);
    float4 clip = float4(uvScreen.x * 2.0 - 1.0, (1.0 - uvScreen.y) * 2.0 - 1.0, depth, 1.0);
    float4 wp = mul(clip, InvViewProj);
    float3 P = wp.xyz / wp.w;

    float3 N = normalize(NormalTex.Load(int3(px, 0)).xyz * 2.0 - 1.0);
    float3 Pbias = P + N * NormalBias;

    // Grid cell coords (continuous) of the biased point.
    float3 g = (Pbias - GridOrigin) / max(ProbeSpacing, 1e-4);
    int3 baseC = clamp((int3)floor(g), int3(0, 0, 0), int3(CountX - 2, CountY - 2, CountZ - 2));
    float3 frac = saturate(g - (float3)baseC);

    float3 sum = 0.0.xxx; float wsum = 0.0;
    float3 sumNoVis = 0.0.xxx; float wsumNoVis = 0.0;   // parallel visibility-free gather (fallback for sub-cell geometry)
    float3 sumRaw = 0.0.xxx; float wsumRaw = 0.0;       // LAST-resort gather: ignores BOTH visibility AND the active
                                                        // flag, so a surface bracketed entirely by inactive/dead probes
                                                        // (placement marked the whole cell solid) still gets soft GI
                                                        // instead of a pure-black hole — the real cause of the black
                                                        // sphere underside (every bracketing probe was inactive).
    [unroll] for (int i = 0; i < 8; i++) {
        int3 off = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        uint3 c = (uint3)clamp(baseC + off, int3(0, 0, 0), int3(CountX - 1, CountY - 1, CountZ - 1));
        uint pidxC = ProbeIndex(c);

        // Trilinear weight.
        float3 tw = float3(off.x == 0 ? 1.0 - frac.x : frac.x,
                           off.y == 0 ? 1.0 - frac.y : frac.y,
                           off.z == 0 ? 1.0 - frac.z : frac.z);
        float trilinear = tw.x * tw.y * tw.z;

        // Occupancy-aware placement: a probe relocated into free space contributes from its MOVED position
        // (so its visibility/backface tests use where it actually traced); an inactive probe (buried with no
        // relocation) is zero-weighted so it can't leak its garbage irradiance into the gather.
        float probeActive = 1.0;
        float3 probeOffset = 0.0.xxx;
        if (UsePlacement > 0.5) { float4 st = ProbeState[pidxC]; probeOffset = st.xyz; probeActive = st.w; }

        // Backface weight: a probe behind the surface (its direction to P opposes N) contributes less.
        float3 probePos = GridOrigin + (float3)c * ProbeSpacing + probeOffset;
        float3 dirToProbe = normalize(probePos - P);
        float backWeight = saturate(dot(dirToProbe, N) * 0.5 + 0.5);
        backWeight = backWeight * backWeight + 0.05;

        // Chebyshev visibility (D3): reject probes that can't actually see the surface (occluded by a wall) →
        // the leak fix. The moments were measured probe→world, so query in the probe→surface direction.
        float vis = 1.0;
        if (UseVisibility > 0.5) {
            float distPS = distance(probePos, P);
            float bias = 0.5 * length(ProbeSpacing);   // half a cell: tolerates probes sitting near surfaces
            vis = ChebyshevWeight(pidxC, normalize(P - probePos), distPS, bias);
        }

        float w = trilinear * backWeight * vis * probeActive;
        float3 probeE = SampleProbeOct(pidxC, N);
        sum += probeE * w;
        wsum += w;
        // Visibility-free accumulation, kept in parallel as a FALLBACK. On small/thin geometry (a box smaller
        // than the probe spacing) Chebyshev can reject EVERY bracketing probe — the surface self-occludes the
        // nearby lit probes — so wsum collapses to ~0 and the pixel goes pure black even though light is right
        // there. When that happens we fall back to the trilinear+backface gather (no visibility) so the face gets
        // soft GI instead of a black hole. This is the standard DDGI safety net for sub-cell geometry.
        float wNoVis = trilinear * backWeight * probeActive;   // fallback still excludes dead probes
        sumNoVis += probeE * wNoVis;
        wsumNoVis += wNoVis;

        float wRaw = trilinear * backWeight;                   // last-resort: ignore visibility AND active flag
        sumRaw += probeE * wRaw;
        wsumRaw += wRaw;
    }

    float3 E;
    if (wsum > 1e-3) E = sum / wsum;                       // normal: visibility-weighted, active-only gather
    else if (wsumNoVis > 1e-4) E = sumNoVis / wsumNoVis;   // fallback 1: drop Chebyshev (sub-cell geometry)
    else if (wsumRaw > 1e-4) E = sumRaw / wsumRaw;         // fallback 2: drop the active flag too (all-dead bracket)
    else E = 0.0.xxx;
    // Fold the receiver Lambert BRDF (albedo/π) HERE, in the compute pass, instead of in the combine PS. The combine
    // then only does an additive blend of a finished diffuse-indirect color and never binds the G-buffer — which
    // fixed the dead GI: the combine PS was binding G-buffer albedo while its real layout was RENDER_TARGET, so it
    // read 0 and E*albedo=0. Compute reads the G-buffer cleanly (NonPixel SRV).
    float3 albedo = Albedo.Load(int3(px, 0)).rgb;
    Indirect[px] = float4(E * Intensity * albedo * (1.0 / DDGI_PI), 1.0);
}
