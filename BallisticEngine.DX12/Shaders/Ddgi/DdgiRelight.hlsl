// DDGI — per-probe relight (Pass 1). One thread-GROUP per probe (RAYS threads). Each thread traces one ray
// over the full sphere (Fibonacci), shades the hit with first-bounce direct light (sun shadow-ray + punctual +
// emissive) × albedo, or samples the sky on a miss, and stores the ray's radiance+direction in groupshared.
// Then the group's threads split the probe's OctRes×OctRes octahedral texels: each texel integrates all rays
// cosine-weighted by its own direction (the published DDGI integration) and EMA-blends over the previous frame.
//
// View-independent: the radiance cache is world-space, so there is NO reprojection / motion / screen history —
// the entire ghosting/disocclusion class never arises. The hit-shading is the Lumen card-light kernel reduced
// to a per-hit form (no per-triangle cache, no clustering). Bindless geo/material reads use the RtInstance ABI.
//
// Bound: TLAS t0 (root SRV) | Irradiance u0 (root UAV) | PrevIrradiance t1 (root SRV) | RtInstance[] t2 /
//        GpuMaterials t3 / Lights t4 (root SRV) | sky irradiance cube t5 (table) | DdgiRelightConstants b0 |
//        bindless heap (ResourceDescriptorHeap[]) | s0 clamp.

#define RAYS 64           // threads per group == rays per probe (must match the C# dispatch group size)

RaytracingAccelerationStructure Scene : register(t0);
RWStructuredBuffer<float4> Irradiance  : register(u0);   // [probe*OctTexels + texel]  rgb=E, a=1
StructuredBuffer<float4>   PrevIrrad   : register(t1);   // previous frame (EMA source)
RWStructuredBuffer<float2> VisMomentsOut : register(u1); // [probe*VisTexels + texel]  x=mean dist, y=mean dist² (D3 Chebyshev)
StructuredBuffer<float2>   PrevVis     : register(t6);   // previous frame visibility (EMA source)
StructuredBuffer<float4>   ProbeState  : register(t7);   // xyz = relocation offset, w = active (occupancy-aware placement)

struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; uint PositionIdx, TriCount, Pad0, Pad1; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; float4 RightAxisHalfW; };
StructuredBuffer<RtInstance>  RtInstances : register(t2);
StructuredBuffer<GpuMaterial> GpuMaterials: register(t3);
StructuredBuffer<GpuLight>    Lights      : register(t4);
TextureCube SkyRadiance : register(t5);   // env RADIANCE cube (per-ray sky sample; cosine-integrated by the probe)

cbuffer DdgiRelightConstants : register(b0) {
    float3 GridOrigin;   float RayCount;
    float3 ProbeSpacing; float SkyIntensity;
    uint   CountX, CountY, CountZ;  float UseSky;
    float3 SunDir;       float SunBias;       // TO the sun (normalized)
    float3 SunColor;     float LightCount;
    float  EmaAlpha;     float HistoryValid;  float Intensity;  float FrameJitter;  // FrameJitter<0 → fixed (deterministic)
    float  MultiBounce;  float BounceBoost;   float UsePlacement; float ValidateOn;   // D4: 2nd-bounce; UsePlacement: occupancy-aware; A5: ValidateOn = per-texel luma-ratio EMA boost (was Pad1)
};
SamplerState LinearClamp : register(s0);

static const int OctRes = 8;
static const int OctTexels = OctRes * OctRes;   // 64
static const int VisRes = 16;
static const int VisTexels = VisRes * VisRes;   // 256
static const float VisMaxDist = 1e4;            // miss/cap distance for the visibility moments
static const float PI = 3.14159265359;

groupshared float3 gRad[RAYS];
groupshared float3 gDir[RAYS];
groupshared float  gDist[RAYS];   // D3: per-ray hit distance (miss → a large cap) for the visibility moments

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
float3 OctDecode(float2 f) {
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}
float2 OctEncode(float3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(e.yx)) * float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}

// Chebyshev (variance-shadow) probe→point visibility — the SAME test the sample pass uses, so multi-bounce
// honours walls too. Without it, GatherPrevIrradiance pulled a lit probe's radiance through a wall into a probe
// on the other side (probe-to-probe leak): the report's "probes feed each other across a wall". Moments are the
// probe's mean ray distance (+ mean²) in `dir`; if the point is farther than the mean occluder, it's behind a
// wall → low weight.
float2 SampleProbeVisR(uint probe, float3 dir) {
    int2 t = clamp((int2)floor(OctEncode(dir) * float(VisRes)), 0, VisRes - 1);
    return PrevVis[probe * VisTexels + t.y * VisRes + t.x];
}
float ChebyshevWeightR(uint probe, float3 dirProbeToPoint, float dist, float bias) {
    float2 mom = SampleProbeVisR(probe, dirProbeToPoint);
    float mean = mom.x;
    if (dist - bias <= mean) return 1.0;
    float variance = max(mom.y - mean * mean, 1e-4);
    float d = (dist - bias) - mean;
    float p = variance / (variance + d * d);
    return max(p * p * p, 0.0);
}

// D4 multi-bounce: gather the PREVIOUS frame's probe irradiance at a world hit point in direction N (the 8
// bracketing probes, trilinear) WITH Chebyshev occlusion so a wall between probe and point blocks the bounce.
float3 GatherPrevIrradiance(float3 P, float3 N) {
    float3 g = (P - GridOrigin) / max(ProbeSpacing, 1e-4);
    int3 baseC = clamp((int3)floor(g), int3(0,0,0), int3((int)CountX-2, (int)CountY-2, (int)CountZ-2));
    float3 frac = saturate(g - (float3)baseC);
    float2 oct = OctEncode(N);
    int2 ot = clamp((int2)floor(oct * float(OctRes)), 0, OctRes - 1);   // nearest oct texel (cheap)
    float bias = 0.5 * length(ProbeSpacing);
    float3 sum = 0.0.xxx; float wsum = 0.0;
    [unroll] for (int i = 0; i < 8; i++) {
        int3 off = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        uint3 c = (uint3)clamp(baseC + off, int3(0,0,0), int3((int)CountX-1, (int)CountY-1, (int)CountZ-1));
        float3 tw = float3(off.x==0?1.0-frac.x:frac.x, off.y==0?1.0-frac.y:frac.y, off.z==0?1.0-frac.z:frac.z);
        float w = tw.x * tw.y * tw.z;
        uint probe = c.z * (CountX*CountY) + c.y * CountX + c.x;
        // Probe state: a relocated probe traced (and stored its visibility moments) from its MOVED position, so the
        // occlusion test MUST use that same position — using the bare lattice position gave a wrong probe→point
        // distance and the Chebyshev test misfired (occlusion "exploded": probes inside meshes leaking, or valid
        // probes wrongly rejected). An inactive probe is skipped entirely so it can't feed garbage into the bounce.
        float4 pst = (UsePlacement > 0.5) ? ProbeState[probe] : float4(0,0,0,1);
        if (pst.w < 0.5) continue;
        float3 probePos = GridOrigin + (float3)c * ProbeSpacing + pst.xyz;
        float distPP = distance(probePos, P);
        w *= ChebyshevWeightR(probe, normalize(P - probePos), distPP, bias);
        sum += PrevIrrad[probe * OctTexels + ot.y * OctRes + ot.x].rgb * w;
        wsum += w;
    }
    return (wsum > 1e-4) ? sum / wsum : 0.0.xxx;
}
// A2 — blue-noise (R2 low-discrepancy) ray dithering. Plain Fibonacci is a fixed deterministic lattice; over
// frames the per-probe scalar rotation only decorrelated AZIMUTH and used the same formula for every probe, so
// the sphere set stayed grid-aligned → residual banding + temporal crawl in the integrated irradiance. kajiya's
// R2 plastic-constant sequence (Roberts) gives a 2D low-discrepancy (blue-noise-like) offset; we Cranley-
// Patterson-rotate the Fibonacci sphere by BOTH components — jitter.x rotates the golden-angle azimuth, jitter.y
// shifts the z-stratum within one ray's slice — so successive frames cover the hemisphere far more evenly and
// the EMA converges cleaner with the SAME ray count. Deterministic-capture path passes a fixed per-probe offset.
float2 R2Seq(uint i) {
    // 1/plastic-number and its square (Roberts' optimal 2D low-discrepancy additive sequence).
    const float a1 = 0.7548776662466927;   // 1 / 1.32471795724474602596
    const float a2 = 0.5698402909980532;   // 1 / plastic²
    return frac(float2(a1, a2) * (float)i + 0.5);
}
float3 SphereDir(uint i, uint n, float2 jitter) {
    float gold = 2.39996322973;
    // z-stratum jitter (jitter.y in [0,1)) spreads the i-th ray's polar slice — turns the rigid Fibonacci
    // z-lattice into a stratified-jittered set without clumping (each ray keeps its own [i, i+1) stratum).
    float z = 1.0 - (2.0 * (float(i) + jitter.y) ) / float(n);
    z = clamp(z, -1.0, 1.0);
    float r = sqrt(saturate(1.0 - z * z));
    float phi = float(i) * gold + jitter.x * 6.2831853;   // azimuth Cranley-Patterson rotation
    return float3(r * cos(phi), r * sin(phi), z);
}
float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}
float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray; ray.Origin = origin + N * max(SunBias, 0.01); ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// First-bounce shading at an RT hit (radiance leaving the surface), given the known world hit point.
float3 ShadeHit(uint instId, uint prim, float2 bary, float3 Pw) {
    RtInstance geo = RtInstances[instId];
    Buffer<uint>             indices = ResourceDescriptorHeap[geo.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[geo.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[geo.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[geo.TriMatIdx];

    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float w0 = 1.0 - bary.x - bary.y, w1 = bary.x, w2 = bary.y;
    float2 uv = uvs[i0] * w0 + uvs[i1] * w1 + uvs[i2] * w2;
    float3 Nw = normalize(normals[i0] * w0 + normals[i1] * w1 + normals[i2] * w2);

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearClamp, uv, 0).rgb * m.BaseColorFactor.rgb, 0.95.xxx);

    float3 emissive = 0.0.xxx;
    if (m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearClamp, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    float3 sunDir = normalize(SunDir);
    float ndl = saturate(dot(Nw, sunDir));
    float3 sun = (ndl > 0.0) ? SunColor * ndl * Visibility(Pw, Nw, sunDir, 1e4) : 0.0.xxx;

    float3 punctual = 0.0.xxx;
    int nl = min((int)LightCount, 32);
    [loop] for (int i = 0; i < nl; i++) {
        GpuLight L = Lights[i];
        if (L.Color.w >= 1.5) continue;
        float3 toL = L.PosRange.xyz - Pw;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float nd = saturate(dot(Nw, Ld));
        if (nd <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 rad = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            rad *= cone * cone;
        }
        punctual += rad * nd * Visibility(Pw, Nw, Ld, dist);
    }

    // D4 MULTI-BOUNCE: add the previous frame's indirect light arriving at this hit (gathered from the probe
    // grid) × albedo. The cache feeds itself → it converges to full multi-bounce GI over a few frames with NO
    // extra rays. HistoryValid gates it (the first frame has no usable prev cache). BounceBoost (≥1) lets dark
    // multi-bounce-only regions fill faster; the firefly clamp downstream keeps it from running away.
    // ENERGY-CONSISTENT multi-bounce: the value we GATHER (and store) is incident irradiance E; the radiance a
    // Lambert surface re-emits is albedo*E/π — the SAME /π the combine applies. Without the /π each bounce
    // injected π× too much energy, and since the cache feeds itself via the EMA that compounded every frame into
    // a runaway glow (the green flood). albedo is already ≤0.95 (energy-conserving), so albedo*E/π < E and the
    // feedback series converges. BounceBoost stays ≥1 for authoring but is NOT applied to the feedback term
    // anymore (a >1 gain on a self-feeding loop is exactly what diverges).
    float3 bounce = 0.0.xxx;
    if (MultiBounce > 0.5 && HistoryValid > 0.5)
        bounce = albedo * GatherPrevIrradiance(Pw, Nw) * (1.0 / 3.14159265359);

    // Lambert BRDF on the bounce surface: the radiance a diffuse surface re-emits from incident light is
    // albedo/π · (sun + punctual). The /π was MISSING on direct sun+punctual here (deferred's ShadePunctual has
    // it; the multi-bounce term above already uses albedo·E/π) — so the first bounce injected π× too much energy.
    // Combined with PunctualIntensityScale (point radiance ~1e5) and the texel integral's ×π×Intensity, probes
    // pinned at the firefly clamp (32) and the gather went flat → GI carried no contrast/colour, just a DC wash
    // that vanished under exposure (the "GI does nothing / black sphere underside"). emissive is the surface's own
    // emitted radiance (NOT reflected), so it does NOT take the /π.
    return albedo * (sun + punctual) * (1.0 / 3.14159265359) + emissive + bounce;
}

[numthreads(RAYS, 1, 1)]
void CSMain(uint3 gid : SV_GroupID, uint gi : SV_GroupIndex) {
    uint probe = gid.x;
    uint probeCount = CountX * CountY * CountZ;
    if (probe >= probeCount) return;

    // Occupancy-aware placement: skip a probe marked inactive (sits in solid with nowhere to relocate). Leave its
    // irradiance untouched so a stale value can't flash; the sample pass weights it 0 anyway. GATED on UsePlacement:
    // when placement hasn't run (NOPLACEMENT door, or the first frames before the deferred PlaceProbes lands) the
    // ProbeState buffer is all-zero (w=0), so an ungated read would skip EVERY probe → DDGI fully dead. Treat
    // placement-off as all-active, no relocation.
    float4 ps = (UsePlacement > 0.5) ? ProbeState[probe] : float4(0, 0, 0, 1);
    if (ps.w < 0.5) return;

    uint ix = probe % CountX;
    uint iy = (probe / CountX) % CountY;
    uint iz = probe / (CountX * CountY);
    float3 P = GridOrigin + float3(ix, iy, iz) * ProbeSpacing + ps.xyz;   // + relocation offset

    // Per-frame ROTATED ray set + low EMA = true Monte-Carlo convergence (no fixed-set bias) while staying
    // flicker-free on a static scene: each frame aims a different 64-ray Fibonacci rotation, the low-alpha EMA
    // integrates them over time. A2: the rotation is a 2D R2 (plastic, blue-noise-like) offset, decorrelated
    // both spatially (per-probe hash) and temporally (frame advance), so the sphere coverage is far more even
    // than the old scalar-azimuth rotation. FrameJitter<0 → deterministic capture path keeps a FIXED per-probe
    // 2D offset (byte-stable; no frame advance).
    float2 probeBase = float2(Hash(probe * 2654435761u), Hash(probe * 40503u + 1u));   // per-probe spatial decorrelation
    float2 jitter;
    if (FrameJitter < 0.0) {
        jitter = probeBase;                                            // deterministic: fixed per-probe offset
    } else {
        // Temporal advance: FrameJitter carries the running frame ordinal (0..1023, C# `frameCounter & 1023`).
        // Step the R2 sequence by it and add the per-probe base (mod 1) → even, low-discrepancy coverage in time.
        uint frameOrd = (uint)(FrameJitter + 0.5);
        jitter = frac(probeBase + R2Seq(frameOrd));
    }

    // Each thread traces ONE ray (gi in [0,RAYS)).
    float3 d = SphereDir(gi, RAYS, jitter);
    float3 rad;
    RayDesc rd; rd.Origin = P; rd.Direction = d; rd.TMin = 0.0; rd.TMax = 1e4;
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q; q.TraceRayInline(Scene, 0, 0xFF, rd); q.Proceed();
    float dist;
    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
        dist = q.CommittedRayT();
        float3 hitW = P + d * dist;
        rad = ShadeHit(q.CommittedInstanceID(), q.CommittedPrimitiveIndex(), q.CommittedTriangleBarycentrics(), hitW);
    } else {
        dist = VisMaxDist;
        rad = (UseSky > 0.5) ? SkyRadiance.SampleLevel(LinearClamp, d, 0).rgb * SkyIntensity : 0.0.xxx;
    }
    gRad[gi] = rad; gDir[gi] = d; gDist[gi] = dist;
    GroupMemoryBarrierWithGroupSync();

    float alpha = (HistoryValid > 0.5) ? saturate(EmaAlpha) : 1.0;

    // (a) IRRADIANCE: the first OctTexels threads each integrate one octahedral texel cosine-weighted.
    if (gi < (uint)OctTexels) {
        uint texel = gi;
        float2 uv = (float2(texel % OctRes, texel / OctRes) + 0.5) / float(OctRes);
        float3 texelDir = OctDecode(uv);
        float3 sum = 0.0.xxx; float wsum = 0.0;
        [unroll] for (uint r = 0; r < RAYS; r++) {
            float w = max(dot(texelDir, gDir[r]), 0.0);
            sum += gRad[r] * w; wsum += w;
        }
        // IRRADIANCE normalization (the missing π). sum/wsum is the cosine-WEIGHTED AVERAGE radiance L̄ over the
        // texel's hemisphere; the true irradiance there is E = ∫L·cosθ dω = π·L̄ for that average. The old code
        // stored the bare average (π× too dark), and the combine then divides by π again (the Lambert albedo/π) →
        // the indirect came out ~π² too weak, so wall→object bounce was invisible. Multiply by π here so the cache
        // holds real incident irradiance; the combine's /π is the receiver's BRDF and stays. (Matches the D4
        // multi-bounce term, which already re-emits albedo·E/π — consistent units now.)
        // Store PHYSICAL irradiance (π·L̄), NO Intensity. Intensity is a user display gain applied at the sample
        // output only. Baking it here was a RUNAWAY bug: the cache feeds itself (multi-bounce reads PrevIrrad), so an
        // Intensity>1 in the stored value re-multiplied every frame through the EMA → loop gain albedo²·Intensity
        // (>1 at Intensity=4) → bounce-dominated regions (point-light scenes, little direct on a surface) diverged
        // toward white over time ("starts normal, then the unlit areas blow out"). Physical storage → loop gain
        // ≤ albedo² < 1 → converges.
        float3 E = ((wsum > 1e-4) ? sum / wsum : 0.0.xxx) * PI;

        uint idx = probe * OctTexels + texel;
        float3 prev = PrevIrrad[idx].rgb;

        // A5 — cache-space sample validation (kajiya diffuse_validate CONCEPT, adapted to the cache-space EMA;
        // NO screen history, NO temporal feedback → tek-loop felsefe intact). The single global hysteresis can't
        // see a PER-TEXEL staleness: when a light moves or a probe's view of the world changes, that one octahedral
        // direction's new estimate E jumps sharply vs the cached prev, but the low EMA alpha would crawl toward it
        // over ~30 frames (the visible GI lag). Detect the per-texel luminance RATIO between prev and E: a large
        // jump (either direction) BOOSTS alpha toward 1 so the cache re-converges immediately where it went stale,
        // while a small ratio leaves the low alpha untouched (clean, flicker-free convergence on a settled scene).
        // This STRENGTHENS the single loop (faster correct convergence) rather than adding a second one. Also the
        // Lumen-runaway cure: a sudden bright jump that would otherwise compound through the EMA is instead taken
        // in (mostly) one step and then held, instead of ratcheting up frame after frame.
        float adaptAlpha = alpha;
        if (ValidateOn > 0.5 && HistoryValid > 0.5) {
            float lp = dot(prev, float3(0.2126, 0.7152, 0.0722));
            float le = dot(E,    float3(0.2126, 0.7152, 0.0722));
            // Symmetric luminance ratio in [0,1]: 1 = identical, →0 = large jump (brighter OR darker).
            float ratio = min(lp, le) / max(max(lp, le), 1e-5);
            float staleness = 1.0 - ratio;                       // 0 = stable, 1 = total change
            // Map staleness → an alpha boost. A small change (<~15%) keeps the base alpha; a large change ramps
            // alpha up to a fast-converge cap so the stale texel snaps to the new value in a couple of frames.
            float boost = smoothstep(0.15, 0.6, staleness);
            adaptAlpha = lerp(alpha, max(alpha, 0.6), boost);
        }
        float3 blended = lerp(prev, E, adaptAlpha);
        // Firefly / runaway guard — an Inf/NaN + EMA-compounding catch, NOT a brightness cap. The OLD ceiling (32)
        // was sized for skylight irradiance, but a point/area light's first-bounce irradiance is legitimately
        // ~1e3–1e5 HDR (PunctualIntensityScale × inverse-square), the SAME scale the deferred direct lighting feeds
        // into HDR before exposure. Clamping that to 32 pinned every probe near a light to a flat ceiling → the
        // gather lost all contrast/colour → GI became an invisible DC wash (the dead-GI / black-sphere symptom).
        // Keep a HIGH finite ceiling so a real fp16-Inf sun texel can't compound through the EMA, but let physical
        // point/area bounce through untouched.
        blended = min(blended, 65504.0.xxx);   // fp16 max — finite Inf guard, not a brightness cap
        Irradiance[idx] = float4(Sanitize(blended), 1.0);
    }

    // (b) VISIBILITY MOMENTS (D3): VisTexels (256) > RAYS (64), so each thread strides over several texels.
    // Each texel stores the depth-weighted mean distance + mean distance² of rays near its direction (a sharp
    // cosine power focuses the moments) → the sample pass runs the Chebyshev test against them to reject probes
    // occluded from the surface (the leak fix).
    [loop] for (uint vt = gi; vt < (uint)VisTexels; vt += RAYS) {
        float2 vuv = (float2(vt % VisRes, vt / VisRes) + 0.5) / float(VisRes);
        float3 vdir = OctDecode(vuv);
        float m1 = 0.0, m2 = 0.0, wsum = 0.0;
        [unroll] for (uint r = 0; r < RAYS; r++) {
            float w = pow(max(dot(vdir, gDir[r]), 0.0), 50.0);   // sharp lobe → directional depth
            float dd = min(gDist[r], VisMaxDist);
            m1 += dd * w; m2 += dd * dd * w; wsum += w;
        }
        float2 mom = (wsum > 1e-6) ? float2(m1 / wsum, m2 / wsum) : float2(VisMaxDist, VisMaxDist * VisMaxDist);
        uint vidx = probe * VisTexels + vt;
        float2 prev = PrevVis[vidx];
        VisMomentsOut[vidx] = lerp(prev, mom, alpha);
    }
}
