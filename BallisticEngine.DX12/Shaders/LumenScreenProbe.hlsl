// Lumen V2 — Sıra 1: SCREEN-SPACE RADIANCE PROBES (the published Lumen final-gather front end).
//
// WHY: the baseline CSTrace (LumenGi.hlsl) shoots a few cosine rays from EVERY full-res G-buffer pixel — ~2M
// trace points at 1080p, the dominant Lumen cost (~3.2ms measured on Bistro interior). Lumen instead places a
// SPARSE grid of radiance probes (one per 16x16 screen tile, ~8K probes), traces MANY rays per probe into an
// octahedral radiance atlas, then INTERPOLATES that atlas at full-res per pixel (bilateral, depth+normal aware).
// Far fewer trace points + more rays each = lower variance AND lower cost. This file is that probe front end;
// it writes the SAME `indirect` irradiance E buffer the per-pixel trace used to, so the downstream denoise +
// combine + probe-temporal chain is untouched (byte-identical contract).
//
// THREE passes (compute):
//   CSPlace    — 1 thread / probe. Pick a representative pixel in the probe's screen tile (the one closest to the
//                tile center with valid geometry), store its world pos + normal + depth into the probe header.
//   CSTrace    — 1 thread / (probe, ray). Trace one octahedral-distributed hemisphere ray from the probe using
//                the EXACT LumenGi hierarchy (screen-trace → HW RT → card sample → sky → distance falloff), write
//                the incoming radiance into the probe's octahedral radiance tile.
//   CSIntegrate— 1 thread / full-res pixel. Reconstruct world pos+normal, gather the 4 nearest probes (bilateral:
//                screen-bilinear × depth × normal × validity), sample each probe's octahedral tile along the
//                pixel normal hemisphere (cosine-weighted), write cosine-weighted irradiance E into `indirect`.
//
// Bindings mirror LumenGi.CSTrace so the ray hierarchy is shared code. Octahedral mapping: standard
// equal-area-ish octahedron unwrap of the unit sphere; we only store the upper hemisphere implicitly by
// sampling cosine-weighted directions around the probe normal, but the tile stores a FULL-sphere octahedron so
// a pixel whose normal differs from the probe normal can still read a plausible direction (Lumen stores the full
// sphere per screen probe for exactly this reason).

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth     : register(t1);
Texture2D<float4> Normal    : register(t2);
Texture2D<float4> Material  : register(t3);
Texture2D<float4> SceneColor: register(t4);
TextureCube SkyIrradiance   : register(t5);
TextureCube SkyPrefilter    : register(t6);

cbuffer ProbeConstants : register(b0) {
    float4x4 InvViewProj;
    float4x4 ViewProj;
    float3 CameraPos;   float Intensity;
    float2 FullTexel;   float RayCount;   float FrameIndex;     // FullTexel = 1/full-res
    float NormalBias;   float MaxRayDist; float UseCards;       float ScreenSteps;
    float SkyIntensity; float UseSky;     float UseScreenTrace; float ScreenRange;
    float FalloffDist;  float UseSH;      float ProbeStride;    float OctSize;       // UseSH=1 → CSIntegrate evaluates the SH cache (was ProbeTile, unused)
    uint  ProbesX;      uint ProbesY;     uint FullW;           uint FullH;
    float HistoryValid; float ProbeEma;   float TexelDim;       float SpPad1;        // Sıra 3 EMA; Sıra 5 mesh-card grid edge (1=legacy)
    float AdaptiveRays; float AdaptiveStride; float AdaptiveVar; float SpPad2;        // variance-guided adaptive ray (0=off, byte-identical)
};
cbuffer ProbeSun : register(b1) {
    float3 SunDir;   float SunBias;
    float3 SunColor; float LightCount;
};

// Probe header: world pos (xyz) + valid flag (w), normal (xyz) + linear depth (w). One per probe.
struct ProbeHeader { float4 PosValid; float4 NormalDepth; };
RWStructuredBuffer<ProbeHeader> ProbeHeaders : register(u0);   // CSPlace writes, CSTrace/CSIntegrate read
RWTexture2D<float4> ProbeAtlas : register(u1);                 // octahedral radiance atlas (ProbesX*OctSize wide)
RWTexture2D<float4> Indirect   : register(u2);                 // OUT (CSIntegrate): incoming irradiance E
// SH IRRADIANCE CACHE (the integrate-cost fix): CSProbeSH projects each probe's filtered oct tile into 9 RGB
// SH coefficients ONCE per probe (oct² taps amortised over the probe, not per pixel). CSIntegrate then only
// EVALUATES the SH in the pixel normal direction for its 4..16 neighbour probes — O(probes·9) instead of the
// old O(pixels·16·oct²). 9 coeffs × RGB = 27 floats → 7 float4 per probe (last .yzw padding). Index: probe p
// occupies [p*7 .. p*7+6]. The SH already bakes the cosine (Lambert) convolution, so the evaluate is a plain
// dot — no per-pixel hemisphere integral.
RWStructuredBuffer<float4> ProbeSH : register(u4);             // 7 float4 / probe (9 RGB irradiance-SH coeffs)
Texture2D<float4>   ProbeAtlasHistory : register(t13);        // Sıra 3: previous frame's accumulated atlas (EMA)
StructuredBuffer<ProbeHeader> ProbeHeadersPrev : register(t16);// Sıra 3: previous frame's probe headers (reproject reject)

SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; uint PositionIdx, TriCount, Pad0, Pad1; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; };
struct LumenInstanceMeta { uint TriOffset, TriCount, ClusterOffset, ClusterCount; float4x4 World; };
StructuredBuffer<GpuMaterial>       GpuMaterials : register(t7);
StructuredBuffer<RtInstance>        RtInstances  : register(t8);
StructuredBuffer<GpuLight>          Lights       : register(t9);
StructuredBuffer<float4>            CardRadiance : register(t10);
StructuredBuffer<LumenInstanceMeta> InstanceMeta : register(t11);
StructuredBuffer<uint>              TriToCluster : register(t12);
struct ClusterCard { float3 Origin; float InvExtentU; float3 U; float InvExtentV; float3 V; float Pad0; float3 Normal; float Pad1; };
StructuredBuffer<ClusterCard>       ClusterCards : register(t17);   // Sıra 5: per-record world card plane (texel lookup)

static const float PI = 3.14159265359;

// Sıra 5: hit world point → texel index within a record's card grid (TexelDim from the CB; 1 → texel 0).
uint CardTexelIndex(uint record, float3 hitPos) {
    uint td = (uint)max(TexelDim, 1.0);
    if (td == 1u) return 0u;
    ClusterCard c = ClusterCards[record];
    float3 rel = hitPos - c.Origin;
    float u = saturate(dot(rel, c.U) * c.InvExtentU);
    float v = saturate(dot(rel, c.V) * c.InvExtentV);
    return min((uint)(v * td), td - 1u) * td + min((uint)(u * td), td - 1u);
}
uint CardBaseIndex(uint record) { uint td = (uint)max(TexelDim, 1.0); return record * td * td; }

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
// FIREFLY CLAMP: a single probe ray that happens to hit a bright surface/light returns a radiance far above the
// hemisphere mean. With only oct² rays per probe, that one sample blows up the average → a bright speck the EMA
// can't fully smooth, and it's WORST in dark areas (the sample dwarfs the near-zero true signal → the "düşük ışıkta
// patlıyor" blobs). Clamp each ray's luminance to a ceiling while preserving its hue (scale RGB, don't desaturate).
// This is the standard MC-GI firefly fix and adds ZERO rays — pure post-trace ALU. maxLum is generous so genuine
// bright bounces still read bright; it only shaves the outliers that the sparse sampling can't resolve.
float3 FireflyClamp(float3 c, float maxLum) {
    float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
    return (lum > maxLum) ? c * (maxLum / max(lum, 1e-5)) : c;
}
float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}
float3 WorldFromUvDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

// --- Octahedral map: unit direction (full sphere) <-> [0,1]^2 ---
float2 OctEncode(float3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(e.yx)) * float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}
float3 OctDecode(float2 f) {
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

// --- Order-2 (9-coefficient) real spherical harmonics, evaluated for a direction. Standard basis. ---
void ShBasis(float3 d, out float sh[9]) {
    sh[0] = 0.282095;                       // Y00
    sh[1] = 0.488603 * d.y;                 // Y1-1
    sh[2] = 0.488603 * d.z;                 // Y10
    sh[3] = 0.488603 * d.x;                 // Y11
    sh[4] = 1.092548 * d.x * d.y;           // Y2-2
    sh[5] = 1.092548 * d.y * d.z;           // Y2-1
    sh[6] = 0.315392 * (3.0 * d.z * d.z - 1.0); // Y20
    sh[7] = 1.092548 * d.x * d.z;           // Y21
    sh[8] = 0.546274 * (d.x * d.x - d.y * d.y); // Y22
}

// Cosine-lobe (Lambert) convolution weights per band — bakes the clamped-cosine hemisphere integral into the
// coefficients so the evaluate is a plain SH dot (Ramamoorthi & Hanrahan 2001). A_l: l=0 π, l=1 2π/3, l=2 π/4.
static const float ShCosA0 = 3.141593;
static const float ShCosA1 = 2.094395;
static const float ShCosA2 = 0.785398;

// Pack/unpack the 9 RGB coeffs to/from the 7-float4 ProbeSH slab (last float4 holds coeff 8 in .xyz).
void StoreProbeSH(uint p, float3 c[9]) {
    uint b = p * 7u;
    ProbeSH[b + 0] = float4(c[0], c[1].x);
    ProbeSH[b + 1] = float4(c[1].yz, c[2].xy);
    ProbeSH[b + 2] = float4(c[2].z, c[3]);
    ProbeSH[b + 3] = float4(c[4], c[5].x);
    ProbeSH[b + 4] = float4(c[5].yz, c[6].xy);
    ProbeSH[b + 5] = float4(c[6].z, c[7]);
    ProbeSH[b + 6] = float4(c[8], 0.0);
}
void LoadProbeSH(uint p, out float3 c[9]) {
    uint b = p * 7u;
    float4 a0 = ProbeSH[b + 0], a1 = ProbeSH[b + 1], a2 = ProbeSH[b + 2], a3 = ProbeSH[b + 3];
    float4 a4 = ProbeSH[b + 4], a5 = ProbeSH[b + 5], a6 = ProbeSH[b + 6];
    c[0] = a0.xyz; c[1] = float3(a0.w, a1.xy); c[2] = float3(a1.zw, a2.x);
    c[3] = a2.yzw; c[4] = a3.xyz; c[5] = float3(a3.w, a4.xy);
    c[6] = float3(a4.zw, a5.x); c[7] = a5.yzw; c[8] = a6.xyz;
}

// Evaluate the cosine-convolved irradiance SH in direction N → irradiance E (already /π-normalised so the
// downstream combine multiplies albedo/π exactly as before — same E units the oct-integral produced).
float3 EvalProbeSH(float3 c[9], float3 N) {
    float sh[9]; ShBasis(N, sh);
    float3 E = c[0] * (ShCosA0 * sh[0]);
    E += (c[1] * sh[1] + c[2] * sh[2] + c[3] * sh[3]) * ShCosA1;
    E += (c[4] * sh[4] + c[5] * sh[5] + c[6] * sh[6] + c[7] * sh[7] + c[8] * sh[8]) * ShCosA2;
    return max(E / PI, 0.0.xxx);   // /π: convolved SH gives radiance·π integral; divide back to match oct-path E
}

float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray; ray.Origin = origin + N * max(SunBias, 0.002); ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

float3 ShadeHit(uint instId, uint prim, float2 bary2, float3x4 o2w, float3 rayDir, float3 hitPos) {
    RtInstance inst = RtInstances[instId];
    Buffer<uint>             indices = ResourceDescriptorHeap[inst.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[inst.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[inst.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[inst.TriMatIdx];
    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float3 bary = float3(1.0 - bary2.x - bary2.y, bary2.x, bary2.y);
    float3 nObj = normalize(normals[i0] * bary.x + normals[i1] * bary.y + normals[i2] * bary.z);
    float3 Ng = normalize(mul((float3x3)o2w, nObj));
    if (dot(Ng, rayDir) > 0.0) Ng = -Ng;
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;
    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.95.xxx);
    float3 emissive = 0.0.xxx;
    if (m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }
    float3 sunDir = normalize(SunDir);
    float ndl = saturate(dot(Ng, sunDir));
    float3 sun = (ndl > 0.0) ? SunColor * ndl * Visibility(hitPos, Ng, sunDir, MaxRayDist) : 0.0.xxx;
    float3 punctual = 0.0.xxx;
    int n = min((int)LightCount, 32);
    [loop] for (int i = 0; i < n; i++) {
        GpuLight L = Lights[i];
        float3 toL = L.PosRange.xyz - hitPos;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float nl = saturate(dot(Ng, Ld));
        if (nl <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 rad = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            rad *= cone * cone;
        }
        punctual += rad * nl * Visibility(hitPos, Ng, Ld, dist);
    }
    return albedo * (sun + punctual) + emissive;
}

bool ScreenTrace(float3 origin, float3 dir, out float3 radiance) {
    radiance = 0.0.xxx;
    float range = min(ScreenRange, MaxRayDist);
    int steps = max((int)ScreenSteps, 1);
    float stepLen = range / (float)steps;
    float3 p = origin + dir * stepLen;
    [loop] for (int i = 0; i < steps; i++, p += dir * stepLen) {
        float4 clip = mul(float4(p, 1.0), ViewProj);
        if (clip.w <= 0.0) return false;
        float3 ndc = clip.xyz / clip.w;
        float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0)) return false;
        float sceneDepth = Depth.SampleLevel(LinearClamp, uv, 0).r;
        if (sceneDepth >= 1.0) continue;
        float3 rayWorld = WorldFromUvDepth(uv, ndc.z);
        float3 sceneWorld = WorldFromUvDepth(uv, sceneDepth);
        float rayZ = length(rayWorld - CameraPos);
        float sceneZ = length(sceneWorld - CameraPos);
        float diff = rayZ - sceneZ;
        if (diff > 0.01 * rayZ && diff < stepLen * 2.0) {
            if (length(sceneWorld - origin) > range) return false;
            radiance = SceneColor.SampleLevel(LinearClamp, uv, 0).rgb;
            return true;
        }
        if (diff >= stepLen * 2.0) return false;
    }
    return false;
}

// Resolve one ray's incoming radiance with the shared LumenGi hierarchy. `falloffApply` lets the integrate path
// skip falloff (it's baked at trace time here).
float3 TraceRay(float3 origin, float3 dir) {
    float3 rad;
    if (UseScreenTrace > 0.5 && ScreenTrace(origin, dir, rad)) return rad;
    RayDesc ray; ray.Origin = origin; ray.Direction = dir; ray.TMin = 0.02; ray.TMax = MaxRayDist;
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
        float hitT = q.CommittedRayT();
        float falloff = (FalloffDist > 0.01) ? exp2(-hitT / FalloffDist) : 1.0;
        if (UseCards > 0.5) {
            uint inst = q.CommittedInstanceID();
            LumenInstanceMeta meta = InstanceMeta[inst];
            uint record = meta.ClusterOffset + TriToCluster[meta.TriOffset + q.CommittedPrimitiveIndex()];
            float3 hitP = origin + dir * hitT;   // Sıra 5: pick the card texel from the hit point (TexelDim 1 → texel 0)
            return CardRadiance[CardBaseIndex(record) + CardTexelIndex(record, hitP)].rgb * falloff;
        } else {
            float3 hitPos = origin + dir * hitT;
            return ShadeHit(q.CommittedInstanceID(), q.CommittedPrimitiveIndex(),
                            q.CommittedTriangleBarycentrics(), q.CommittedObjectToWorld3x4(), dir, hitPos) * falloff;
        }
    } else if (UseSky > 0.5) {
        return SkyIrradiance.SampleLevel(LinearClamp, dir, 0).rgb * SkyIntensity;
    }
    return 0.0.xxx;
}

// ===== CSPlace: choose a representative pixel per probe tile =====
[numthreads(8, 8, 1)]
void CSPlace(uint3 dtid : SV_DispatchThreadID) {
    uint2 probe = dtid.xy;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;

    uint stride = (uint)ProbeStride;
    // Scan the tile for the valid pixel nearest the tile center (Lumen places probes on real geometry, not a
    // fixed grid pixel that might land on a depth discontinuity / sky).
    int2 tileBase = int2(probe) * (int)stride;
    int2 center = tileBase + (int)stride / 2;
    float bestDist = 1e9; bool found = false;
    float3 bestPos = 0; float3 bestN = 0; float bestDepth = 1.0;
    [loop] for (uint sy = 0; sy < stride; sy += 2)
    [loop] for (uint sx = 0; sx < stride; sx += 2) {
        int2 px = tileBase + int2(sx, sy);
        if (px.x >= (int)FullW || px.y >= (int)FullH) continue;
        float2 uv = (float2(px) + 0.5) * FullTexel;
        float d = Depth.SampleLevel(LinearClamp, uv, 0).r;
        if (d >= 1.0) continue;
        float3 nW = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
        if (dot(nW, nW) < 0.1) continue;
        float dist = dot(float2(px - center), float2(px - center));
        if (dist < bestDist) {
            bestDist = dist; found = true;
            bestPos = WorldFromUvDepth(uv, d);
            bestN = normalize(nW);
            bestDepth = d;
        }
    }
    ProbeHeaders[pidx].PosValid    = float4(bestPos, found ? 1.0 : 0.0);
    ProbeHeaders[pidx].NormalDepth = float4(bestN, bestDepth);
}

// ===== CSTrace: one ray per (probe, octahedral cell) =====
[numthreads(8, 8, 1)]
void CSProbeTrace(uint3 dtid : SV_DispatchThreadID) {
    uint oct = (uint)OctSize;
    uint2 cell = uint2(dtid.x % oct, dtid.y);            // dtid.x = probeX*oct + cellX ; dtid.y handled below
    // Layout: dispatch X = ProbesX*OctSize, dispatch Y = ProbesY*OctSize.
    uint2 atlasPx = dtid.xy;
    uint2 probe = atlasPx / oct;
    uint2 lcell = atlasPx % oct;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;

    ProbeHeader h = ProbeHeaders[pidx];
    if (h.PosValid.w < 0.5) { ProbeAtlas[atlasPx] = float4(0, 0, 0, 0); return; }
    float3 P = h.PosValid.xyz;
    float3 N = h.NormalDepth.xyz;

    // Octahedral cell center → a full-sphere direction. Jitter inside the cell for AA across frames. The tile
    // stores the FULL SPHERE of incoming radiance (Lumen screen probes are full-sphere): the integrate then picks
    // the pixel's own hemisphere by cosine-weighting against the PIXEL normal, so a pixel whose normal differs
    // from the probe normal (a silhouette / curved surface inside the tile) still reads valid directions. Tracing
    // only the probe-N hemisphere (the previous approach) left the pixel-hemisphere cells that fall in the probe's
    // BACK hemisphere empty → energy loss + the measured darkening/grain.
    float jitter = Hash(pidx * 2654435761u ^ (lcell.x * 73856093u) ^ (lcell.y * 19349663u) ^ (uint)FrameIndex);
    float2 octUv = (float2(lcell) + float2(frac(jitter * 1.61803), frac(jitter * 2.41421))) / float(oct);
    float3 dir = OctDecode(octUv);

    // Sıra 3 — TEMPORAL ACCUMULATION. The single-frame few-ray probe is noisy/blobby; EMA it over the previous
    // frame's atlas → many effective rays per probe at no extra trace cost (the published Lumen screen-probe
    // temporal filter). Cells are screen-tile-anchored; on a static camera the same probe maps to the same atlas
    // cell across frames, so a straight EMA per cell is correct. On a moving camera the probe at this grid cell may
    // now cover DIFFERENT geometry — reject (take fresh) when the previous probe at the same cell sat on a surface
    // far from this one (disocclusion), so we accumulate instead of smearing a trail.
    //
    // The reproject test is computed ONCE here and shared by both the EMA and the adaptive-ray gate below.
    bool sameSurface = false;
    float3 prev = 0.0.xxx;
    [branch] if (HistoryValid > 0.5) {
        ProbeHeader hp = ProbeHeadersPrev[pidx];
        float posDiff = distance(hp.PosValid.xyz, P);
        sameSurface = hp.PosValid.w > 0.5 && posDiff < max(0.5, length(P - CameraPos) * 0.03);
        if (sameSurface) prev = Sanitize(ProbeAtlasHistory[atlasPx].rgb);
    }

    // VARIANCE-GUIDED ADAPTIVE RAY (AdaptiveRays>0.5; OFF in deterministic capture → byte-identical golden).
    // On a reprojected (sameSurface) probe cell, measure the local DIRECTIONAL variance of the already-converged
    // previous atlas over this probe's own oct block. A flat (low coefficient-of-variation) cell carries no new
    // information frame-to-frame, so it traces only 1-in-AdaptiveStride frames (round-robin phase so every flat
    // cell still fully refreshes over the stride window) and inherits the converged history on the off-frames.
    // High-variance cells (silhouettes, sharp lighting, color-bleed boundaries) and disoccluded cells always
    // trace. The skipped path writes `prev` verbatim, so the atlas tile stays fully populated — Filter/SH/
    // Integrate downstream are byte-unaffected. This drops TraceRay calls on flat probes with no visual change.
    bool traceThisFrame = true;
    [branch] if (AdaptiveRays > 0.5 && sameSurface) {
        uint2 tileMin = probe * oct;
        uint2 tileMax = tileMin + (oct - 1u);
        const float3 LUM = float3(0.2126, 0.7152, 0.0722);
        float3 cC = prev;
        float3 cL = Sanitize(ProbeAtlasHistory[uint2(max(atlasPx.x, tileMin.x + 1u) - 1u, atlasPx.y)].rgb);
        float3 cR = Sanitize(ProbeAtlasHistory[uint2(min(atlasPx.x + 1u, tileMax.x), atlasPx.y)].rgb);
        float3 cD = Sanitize(ProbeAtlasHistory[uint2(atlasPx.x, max(atlasPx.y, tileMin.y + 1u) - 1u)].rgb);
        float3 cU = Sanitize(ProbeAtlasHistory[uint2(atlasPx.x, min(atlasPx.y + 1u, tileMax.y))].rgb);
        float m = dot((cC + cL + cR + cD + cU) / 5.0, LUM);
        float v = abs(dot(cC, LUM) - m) + abs(dot(cL, LUM) - m) + abs(dot(cR, LUM) - m)
                + abs(dot(cD, LUM) - m) + abs(dot(cU, LUM) - m);
        v /= 5.0;
        float cov = v / max(m, 1e-4);   // coefficient of variation → scale-invariant (bright & dark gate alike)
        if (cov < AdaptiveVar) {
            uint stride = (uint)max(AdaptiveStride, 1.0);
            uint phase = (lcell.x * 7u + lcell.y * 13u + pidx) % stride;
            traceThisFrame = (((uint)FrameIndex) % stride) == phase;
        }
    }

    float3 rad;
    [branch] if (!traceThisFrame) {
        rad = prev;   // INHERIT — skip the TraceRay this frame (the cost saving on flat probes)
    } else {
        float3 origin = P + N * NormalBias;
        rad = Sanitize(TraceRay(origin, dir));
        // Firefly clamp the fresh sample BEFORE it enters the EMA, so an outlier ray can't poison the accumulated
        // history. SpPad2 carries the ceiling (BALLISTIC_DX12_LUMEN_FIREFLY, 0 = off → byte-identical legacy).
        if (SpPad2 > 0.0) rad = FireflyClamp(rad, SpPad2);
        if (sameSurface) rad = lerp(prev, rad, saturate(ProbeEma));   // low alpha → strong accumulation
    }
    ProbeAtlas[atlasPx] = float4(rad, 1.0);
}

// ===== CSProbeFilter: SPATIAL filter of the probe atlas in PROBE space (the proper blob fix) =====
// The few-ray probes vary probe-to-probe on a flat wall → ~tile-size BLOBS. Filtering in probe space (each
// atlas cell blended with the SAME oct cell of NEIGHBOURING probes, depth+normal weighted) removes that variance
// at its SOURCE — cheap (only ~ProbesX*ProbesY*oct² texels, far fewer than full-res), and far more effective than
// a wide gather at integrate time. Joint-bilateral: wide on a flat surface, sharp across a silhouette/plane edge.
// Reads ProbeAtlas (t-bound as a UAV is fine to read), writes ProbeAtlasFiltered (u2 reused — see C# binding).
RWTexture2D<float4> ProbeAtlasFiltered : register(u3);   // Sıra: filtered atlas the integrate reads

[numthreads(8, 8, 1)]
void CSProbeFilter(uint3 dtid : SV_DispatchThreadID) {
    uint oct = (uint)OctSize;
    uint2 atlasPx = dtid.xy;
    uint2 probe = atlasPx / oct;
    uint2 lcell = atlasPx % oct;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;
    ProbeHeader hc = ProbeHeaders[pidx];
    if (hc.PosValid.w < 0.5) { ProbeAtlasFiltered[atlasPx] = ProbeAtlas[atlasPx]; return; }
    float3 Pc = hc.PosValid.xyz; float3 Nc = hc.NormalDepth.xyz; float dc = hc.NormalDepth.w;

    // Blend the SAME oct cell across a neighbourhood of probes (radius from the CB, default 2 → 5x5 probes).
    int r = (int)clamp(SpPad1, 1.0, 3.0);   // SpPad1 repurposed as the probe-filter radius
    float3 acc = 0.0.xxx; float wsum = 0.0;
    [loop] for (int dy = -r; dy <= r; dy++)
    [loop] for (int dx = -r; dx <= r; dx++) {
        int2 np = int2(probe) + int2(dx, dy);
        if (np.x < 0 || np.y < 0 || np.x >= (int)ProbesX || np.y >= (int)ProbesY) continue;
        uint nidx = np.y * ProbesX + np.x;
        ProbeHeader hn = ProbeHeaders[nidx];
        if (hn.PosValid.w < 0.5) continue;
        // Joint-bilateral: gaussian on probe distance × world-position proximity × normal similarity. The world +
        // normal terms keep the filter from blending probes across a wall corner / onto a different plane.
        float wS = exp(-float(dx*dx + dy*dy) * 0.4);
        float posD = distance(hn.PosValid.xyz, Pc);
        float wP = exp(-posD * posD * 0.5);   // ~1.4m falloff — same flat surface only
        float wN = pow(saturate(dot(hn.NormalDepth.xyz, Nc)), 8.0);
        float w = wS * wP * wN + 1e-5;
        acc += ProbeAtlas[uint2(np.x * oct + lcell.x, np.y * oct + lcell.y)].rgb * w;
        wsum += w;
    }
    ProbeAtlasFiltered[atlasPx] = float4(wsum > 1e-4 ? acc / wsum : ProbeAtlas[atlasPx].rgb, 1.0);
}

// ===== CSProbeSH: project each probe's FILTERED octahedral tile into 9 RGB SH coefficients (1 thread / probe) =====
// This is the integrate-cost fix: the oct² tile scan happens ONCE per probe here (a few thousand probes) instead
// of per full-res pixel ×16 in CSIntegrate. The result is the directional radiance distribution as SH; the pixel
// pass then evaluates it in the surface-normal direction (cosine-convolved) for a Lambertian irradiance with no
// per-pixel hemisphere loop. The octahedral map is equal-area-ISH, so we weight each cell by its solid angle via
// the standard 1/(|x|+|y|+|z|)³ octahedral Jacobian to keep the projection unbiased.
[numthreads(64, 1, 1)]
void CSProbeSH(uint3 dtid : SV_DispatchThreadID) {
    uint pidx = dtid.x;
    if (pidx >= ProbesX * ProbesY) return;
    uint2 probe = uint2(pidx % ProbesX, pidx / ProbesX);
    uint oct = (uint)OctSize;

    float3 c[9];
    [unroll] for (uint k = 0; k < 9; k++) c[k] = 0.0.xxx;
    ProbeHeader h = ProbeHeaders[pidx];
    if (h.PosValid.w < 0.5) { StoreProbeSH(pidx, c); return; }

    float wsum = 0.0;
    [loop] for (uint cy = 0; cy < oct; cy++)
    [loop] for (uint cx = 0; cx < oct; cx++) {
        float2 octUv = (float2(cx, cy) + 0.5) / float(oct);
        float3 dir = OctDecode(octUv);
        float3 rad = ProbeAtlasFiltered[uint2(probe.x * oct + cx, probe.y * oct + cy)].rgb;
        // Octahedral solid-angle weight (Jacobian of the equal-area-ish unwrap). Constant cell area in oct space
        // maps to a varying sphere solid angle ~ 1/(|x|+|y|+|z|)³ in the pre-normalised direction.
        float3 un = float3(octUv.x * 2.0 - 1.0, octUv.y * 2.0 - 1.0, 0.0);
        float l1 = abs(un.x) + abs(un.y); un.z = 1.0 - l1;
        float dw = 1.0 / pow(max(abs(un.x) + abs(un.y) + abs(un.z), 1e-3), 3.0);
        float sh[9]; ShBasis(dir, sh);
        [unroll] for (uint k = 0; k < 9; k++) c[k] += rad * (sh[k] * dw);
        wsum += dw;
    }
    // Normalise to the unit sphere integral (4π) so the convolved evaluate yields physical irradiance.
    float norm = wsum > 1e-4 ? (4.0 * PI) / wsum : 0.0;
    [unroll] for (uint k = 0; k < 9; k++) c[k] = Sanitize(c[k] * norm);
    StoreProbeSH(pidx, c);
}

// ===== CSIntegrate: per full-res pixel, gather 4 nearest probes' octahedral radiance, cosine-weighted =====
// Sample a probe's octahedral tile for the irradiance arriving at a pixel with normal Npix: integrate the tile's
// full sphere weighted by cos(theta) over the pixel hemisphere. We approximate with the cosine-importance the
// tile was traced under (the tile already only has hemisphere-around-probe-N radiance), reading along Npix.
float3 SampleProbeTile(uint2 probe, float3 Npix) {
    uint oct = (uint)OctSize;
    // Cosine-weighted integral over the hemisphere around Npix, sampled from the octahedral tile. Few-tap: walk
    // the tile cells, weight each stored direction by max(0, dot(dir, Npix)).
    float3 acc = 0.0.xxx; float wsum = 0.0;
    [loop] for (uint cy = 0; cy < oct; cy++)
    [loop] for (uint cx = 0; cx < oct; cx++) {
        float2 octUv = (float2(cx, cy) + 0.5) / float(oct);
        float3 dir = OctDecode(octUv);
        float w = max(dot(dir, Npix), 0.0);
        if (w <= 0.0) continue;
        float4 t = ProbeAtlasFiltered[uint2(probe.x * oct + cx, probe.y * oct + cy)];   // filtered (blob removed at source)
        acc += t.rgb * w; wsum += w;
    }
    return wsum > 1e-4 ? acc / wsum : 0.0.xxx;
}

[numthreads(8, 8, 1)]
void CSIntegrate(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    if (px.x >= FullW || px.y >= FullH) return;
    float2 uv = (float2(px) + 0.5) * FullTexel;
    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) { Indirect[px] = float4(0, 0, 0, 1); return; }
    float3 nW = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(nW, nW) < 0.1) { Indirect[px] = float4(0, 0, 0, 1); return; }
    float3 Npix = normalize(nW);
    float3 worldPos = WorldFromUvDepth(uv, depth);

    // 4 nearest probes (the 2x2 enclosing the pixel in probe space). A probe `p` represents the screen position
    // tileCenter = p*stride + stride/2, so the pixel's coordinate in probe-grid space is (px - stride/2)/stride;
    // the enclosing 2x2 is floor()..+1. Bilateral weight: screen bilinear × depth similarity × normal similarity ×
    // probe validity — rejects a probe on a different surface (silhouette) but with a guaranteed fallback so a
    // pixel whose 2x2 all reject still gets the BEST available probe instead of a black hole (the measured holes).
    float stride = ProbeStride;
    float2 probeF = (float2(px) - stride * 0.5) / stride;
    float2 base = floor(probeF);
    float2 f = frac(probeF);

    float3 acc = 0.0.xxx; float wsum = 0.0;
    // Fallback bookkeeping: track the single highest-validity probe so a fully-rejected neighbourhood still
    // resolves to the best nearby probe (no black hole — the measured failure).
    float3 bestRad = 0.0.xxx; float bestW = -1.0; bool anyValid = false;
    int2 ibase = int2(base);
    // 4x4 neighbourhood — the blob is now removed at its SOURCE by the probe-space CSProbeFilter pass, so the
    // integrate only needs a modest bilinear-ish gather (cheaper than the 6x6 it briefly used → FPS back). The
    // depth/normal bilateral still keeps edges sharp.
    [unroll] for (int dy = -1; dy <= 2; dy++)
    [unroll] for (int dx = -1; dx <= 2; dx++) {
        int2 pc = ibase + int2(dx, dy);
        if (pc.x < 0 || pc.y < 0 || pc.x >= (int)ProbesX || pc.y >= (int)ProbesY) continue;
        uint pidx = pc.y * ProbesX + pc.x;
        ProbeHeader h = ProbeHeaders[pidx];
        if (h.PosValid.w < 0.5) continue;
        anyValid = true;
        float2 d2 = (float2(pc) - probeF);
        float wSpatial = exp(-dot(d2, d2) * 0.5);
        float wDepth = 1.0 / (1.0 + abs(h.NormalDepth.w - depth) * 1500.0);
        float wNormal = pow(saturate(dot(h.NormalDepth.xyz, Npix) * 0.5 + 0.5), 2.0);
        float w = wSpatial * wDepth * wNormal + 1e-5;
        // SH evaluate (integrate-cost fix): the per-probe directional radiance is precomputed as cosine-convolved
        // irradiance SH by CSProbeSH, so this is a plain 9-term evaluate in the pixel normal direction — no oct²
        // tile scan per probe. Identical irradiance to the old SampleProbeTile cosine integral, O(1) per probe.
        // UseSH=0 (env BALLISTIC_DX12_LUMEN_PROBE_NOSH=1) falls back to the per-pixel oct integral for A/B.
        float3 rad;
        if (UseSH > 0.5) { float3 prc[9]; LoadProbeSH(pidx, prc); rad = EvalProbeSH(prc, Npix); }
        else             { rad = SampleProbeTile((uint2)pc, Npix); }
        acc += rad * w; wsum += w;
        float fw = wSpatial * wDepth * (saturate(dot(h.NormalDepth.xyz, Npix)) + 0.1);
        if (fw > bestW) { bestW = fw; bestRad = rad; }
    }
    float3 E;
    if (wsum > 1e-3) E = acc / wsum;
    else if (anyValid) E = bestRad;          // all weights collapsed → take the best single probe (no black hole)
    else E = 0.0.xxx;                        // genuinely no valid probe nearby (rare: tile fully sky/invalid)
    Indirect[px] = float4(Sanitize(E * Intensity), depth);   // depth in .a for the downstream probe-temporal + history copy
}
