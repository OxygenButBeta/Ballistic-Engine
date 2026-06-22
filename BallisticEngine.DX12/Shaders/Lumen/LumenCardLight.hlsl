// Lumen FAZ 3d — SURFACE-CACHE LIGHTING. This is the payoff of the surface cache: per atlas texel of an allocated
// card page we LIGHT the captured surface (albedo/normal/depth) and write a view-independent FinalLighting atlas.
//
// Dispatch: one thread per ATLAS texel (AtlasSize/8 × AtlasSize/8 groups, 8×8 threads). Each thread maps its atlas
// texel → the page (card) it belongs to (O(pages) scan — a v1 simplification; UE uses a tile list). For an owned
// texel we:
//   1. reconstruct the world position + world normal from the captured card-space normal/depth + the card frame
//      (the INVERSE of LumenCardCapture's ortho mapping — derived to match its conventions exactly),
//   2. DIRECT = sun (N·L, shadow-rayed) + punctual lights (shadow-rayed) + emissive-triangle NEE (shadow-rayed),
//   3. INDIRECT (radiosity / multi-bounce) = trace N cosine hemisphere rays, on a hit map (instance,prim) → a card
//      → its atlas texel → sample LAST frame's FinalLighting (the bounce radiance),
//   4. FinalLighting = Albedo*(Direct+Indirect) + Emissive.
//
// Reads atlases + writes DirectLighting/FinalLighting via BINDLESS (ResourceDescriptorHeap[] by reserved index,
// like GlobalSdfComposite). Bound: CBV b0 | root SRV t0 TLAS | root SRVs t1 cards / t2 pages / t3 lights /
// t4 emissive / t5 instanceMeta | bindless heap (HeapDirectlyIndexed). s0 clamp.
//
// Driver rules (FAZ 3b/3d lessons): NO loop-carried int + branch/lerp color; NO unclamped color sums; NaN scrub is
// a ternary SELECT (never lerp(v,0,flag) — NaN*0=NaN). Guard every divide. Use float math + saturate.

RaytracingAccelerationStructure Scene : register(t0);

struct GpuLumenCard {   // mirrors Dx12LumenCardScene.GpuLumenCard (64 B, world-space)
    float3 Origin; uint  PageId;
    float3 AxisX;  float ExtentX;
    float3 AxisY;  float ExtentY;
    float3 AxisZ;  float ExtentZ;
};
struct GpuLumenPage {   // mirrors Dx12LumenCardScene.GpuLumenPage (32 B)
    uint AtlasOffsetX, AtlasOffsetY;
    uint SizeX, SizeY;
    uint CardId, ResLevel, Pad0, Pad1;
};
// 80B stride — MUST match Dx12ClusteredLights.GpuLight (RightAxisHalfW is the 5th float4). AuroraCardLight.hlsl
// declares only 4 float4 which mis-strides this buffer; we use the correct 5 so punctual lights index correctly.
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; float4 RightAxisHalfW; };
// world-space emissive-triangle area light (v0 + two edges + radiance) — mirrors Dx12EmissiveLights.EmissiveTri.
struct EmissiveTri { float4 V0; float4 E0; float4 E1; float4 Radiance; };
// per-instance card range (offset into the card list + count) — mirrors Dx12LumenCardScene.InstanceCardRange.
struct InstanceCardRange { uint Offset; uint Count; };

StructuredBuffer<GpuLumenCard>      Cards         : register(t1);
StructuredBuffer<GpuLumenPage>      Pages         : register(t2);
StructuredBuffer<GpuLight>          Lights        : register(t3);
StructuredBuffer<EmissiveTri>       EmissiveLights : register(t4);
StructuredBuffer<InstanceCardRange> InstanceRanges : register(t5);

cbuffer LumenLightConstants : register(b0) {
    float3 SunDir;   float SunBias;       // TO the sun (normalized), world; shadow-ray origin offset
    float3 SunColor; float LightCount;    // sun radiance (RAW HDR); # punctual lights
    uint   AtlasSize; uint PageCount; uint CardCount; uint InstanceCount;
    float  EmissiveCount; float NeeIntensity; float IndirectRays; float IndirectIntensity;
    float  FinalValid; uint FrameIndex; float SkyIntensity; float UseSky;
    // bindless reserved-tail indices (ResourceDescriptorHeap[]).
    uint AlbedoSrvIdx; uint NormalSrvIdx; uint EmissiveSrvIdx; uint DepthSrvIdx;
    uint DirectUavIdx; uint FinalReadSrvIdx; uint FinalWriteUavIdx; uint Pad0;
};
SamplerState LinearClamp : register(s0);

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}
float3x3 BuildBasis(float3 n) {
    float3 up = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 t = normalize(cross(up, n)); float3 b = cross(n, t);
    return float3x3(t, b, n);
}
float3 CosineHemisphere(uint i, uint n, float jitter) {
    float u1 = (float(i) + jitter) / float(n);
    float u2 = frac(jitter * 1.61803398875 + float(i) * 0.7548776662);
    float r = sqrt(saturate(u1)); float phi = 6.28318530718 * u2;
    return float3(r * cos(phi), r * sin(phi), sqrt(saturate(1.0 - u1)));
}
float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray; ray.Origin = origin + N * max(SunBias, 0.004); ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// Reconstruct the world position of an atlas texel from its page-local UV (uv in [0,1] across the card) + the
// captured card-space linear depth. INVERTS LumenCardCapture's ortho mapping exactly:
//   capture: eye = Origin + AxisZ*ExtentZ, looks -AxisZ, ortho W=2ExtentX H=2ExtentY near=0 far=2ExtentZ, up=AxisY.
//   ndcX = u*2-1 → plane offset AxisX*ndcX*ExtentX ; viewport v=0 is TOP → ndcY = 1-2v → AxisY*ndcY*ExtentY.
//   depth d = dot(P - frontCenter, -AxisZ)/(2ExtentZ), frontCenter = Origin + AxisZ*ExtentZ.
//   => P = Origin + AxisZ*ExtentZ*(1-2d) + AxisX*ndcX*ExtentX + AxisY*ndcY*ExtentY.
float3 ReconstructWorldPos(GpuLumenCard c, float2 uv, float depth) {
    float ndcX = uv.x * 2.0 - 1.0;
    float ndcY = 1.0 - uv.y * 2.0;        // viewport v=0 (top) → ndcY=+1
    return c.Origin
         + c.AxisZ * (c.ExtentZ * (1.0 - 2.0 * depth))
         + c.AxisX * (ndcX * c.ExtentX)
         + c.AxisY * (ndcY * c.ExtentY);
}
// Reconstruct the world normal from the stored card-space normal.xy (Z reconstructed, outward = +AxisZ).
float3 ReconstructWorldNormal(GpuLumenCard c, float2 nCardXY) {
    float nz = sqrt(saturate(1.0 - dot(nCardXY, nCardXY)));   // outward hemisphere (+AxisZ)
    float3 N = c.AxisX * nCardXY.x + c.AxisY * nCardXY.y + c.AxisZ * nz;
    float len = length(N);
    return len > 1e-6 ? N / len : c.AxisZ;
}

// Map a WORLD point (an indirect-ray hit) to a card's atlas texel and sample LAST frame's FinalLighting. v1 approach:
// for the hit instance, loop its cards, pick the card whose outward normal best matches the hit normal AND whose OBB
// roughly contains the point (projected UV in [0,1] + within the depth slab); project to UV → page rect → sample.
// Returns 0 on no match / no page. (UE formalizes this in the FAZ-5 trace; this is the self-contained radiosity bounce.)
float3 SampleFinalAtHit(uint instance, float3 hitPos, float3 hitNormal,
                        Texture2D<float4> finalRead) {
    if (instance >= InstanceCount) return 0.0.xxx;
    InstanceCardRange range = InstanceRanges[instance];
    float bestScore = -1e9;
    int bestCard = -1;
    float2 bestUv = 0.0.xx;
    [loop] for (uint k = 0; k < range.Count; k++) {
        uint ci = range.Offset + k;
        if (ci >= CardCount) break;
        GpuLumenCard c = Cards[ci];
        if (c.PageId == 0xFFFFFFFFu) continue;
        float3 rel = hitPos - c.Origin;
        float du = dot(rel, c.AxisX) / max(c.ExtentX, 1e-4);   // [-1,1] inside the card plane
        float dv = dot(rel, c.AxisY) / max(c.ExtentY, 1e-4);
        float dd = dot(rel, c.AxisZ) / max(c.ExtentZ, 1e-4);   // [-1,1] inside the depth slab
        // Reject points clearly outside this card's OBB (small slack for capture jitter).
        if (abs(du) > 1.2 || abs(dv) > 1.2 || abs(dd) > 1.5) continue;
        // Score = normal alignment (outward card normal vs hit normal) minus in-plane distance penalty.
        float align = dot(hitNormal, c.AxisZ);
        float score = align - 0.25 * (abs(du) + abs(dv));
        if (score > bestScore) {
            bestScore = score;
            bestCard = (int)ci;
            bestUv = float2(du * 0.5 + 0.5, dv * 0.5 + 0.5);   // card-space [-1,1] → [0,1]
        }
    }
    if (bestCard < 0 || bestScore < -0.5) return 0.0.xxx;      // no plausibly-front-facing card
    GpuLumenCard bc = Cards[bestCard];
    GpuLumenPage pg = Pages[bc.PageId];
    // Page-local UV → atlas texel (clamped to the page rect). viewport v=0 (top) of capture == page row 0.
    float2 luv = saturate(bestUv);
    uint px = pg.AtlasOffsetX + (uint)(luv.x * (float)(pg.SizeX - 1u) + 0.5);
    uint py = pg.AtlasOffsetY + (uint)((1.0 - luv.y) * (float)(pg.SizeY - 1u) + 0.5);   // invert v (capture top-row = uv.y=1)
    return Sanitize(finalRead.Load(int3((int)px, (int)py, 0)).rgb);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint px = dtid.x, py = dtid.y;
    if (px >= AtlasSize || py >= AtlasSize) return;

    // Map this atlas texel → its owning page (O(pages) scan — v1 simplification, fine for ~12 pages).
    int page = -1;
    [loop] for (uint p = 0; p < PageCount; p++) {
        GpuLumenPage pg = Pages[p];
        if (px >= pg.AtlasOffsetX && px < pg.AtlasOffsetX + pg.SizeX &&
            py >= pg.AtlasOffsetY && py < pg.AtlasOffsetY + pg.SizeY) { page = (int)p; break; }
    }
    if (page < 0) return;   // not inside any page — nothing to light

    GpuLumenPage pgo = Pages[page];
    uint cardId = pgo.CardId;
    if (cardId >= CardCount) return;
    GpuLumenCard card = Cards[cardId];

    // Bindless atlases.
    Texture2D<float4> albedoTex   = ResourceDescriptorHeap[AlbedoSrvIdx];
    Texture2D<float4> normalTex   = ResourceDescriptorHeap[NormalSrvIdx];
    Texture2D<float4> emissiveTex = ResourceDescriptorHeap[EmissiveSrvIdx];
    Texture2D<float>  depthTex    = ResourceDescriptorHeap[DepthSrvIdx];
    RWTexture2D<float4> directOut  = ResourceDescriptorHeap[DirectUavIdx];
    Texture2D<float4>   finalRead  = ResourceDescriptorHeap[FinalReadSrvIdx];
    RWTexture2D<float4> finalOut   = ResourceDescriptorHeap[FinalWriteUavIdx];

    int2 tc = int2((int)px, (int)py);
    float depth = depthTex.Load(int3(tc, 0));
    // Empty/cleared texel (capture cleared depth to 1.0 outside geometry) → no surface here. Write 0 + bail.
    if (depth >= 0.999) {
        directOut[tc] = float4(0, 0, 0, 1);
        finalOut[tc]  = float4(0, 0, 0, 1);
        return;
    }
    float4 albedoT = albedoTex.Load(int3(tc, 0));
    float opacity = albedoT.a;
    if (opacity < 0.004) {   // cutout / empty
        directOut[tc] = float4(0, 0, 0, 1);
        finalOut[tc]  = float4(0, 0, 0, 1);
        return;
    }
    float3 albedo = saturate(albedoT.rgb);
    float3 emissive = max(emissiveTex.Load(int3(tc, 0)).rgb, 0.0.xxx);
    float2 nCardXY = normalTex.Load(int3(tc, 0)).rg * 2.0 - 1.0;   // R8G8 *0.5+0.5 → [-1,1]

    // Page-local UV in [0,1] across the card (texel center).
    float2 uv = (float2(px - pgo.AtlasOffsetX, py - pgo.AtlasOffsetY) + 0.5) / float2(pgo.SizeX, pgo.SizeY);
    float3 P = ReconstructWorldPos(card, uv, depth);
    float3 N = ReconstructWorldNormal(card, nCardXY);

    // === DIRECT ===
    // Sun (shadow-rayed).
    float3 sunDir = normalize(SunDir);
    float ndl = saturate(dot(N, sunDir));
    float3 direct = (ndl > 0.0 && dot(SunColor, SunColor) > 0.0)
                  ? SunColor * ndl * Visibility(P, N, sunDir, 1e4) : 0.0.xxx;

    // Punctual lights (shadow-rayed) — same falloff/cone math as AuroraCardLight.
    int nl = min((int)LightCount, 32);
    [loop] for (int i = 0; i < nl; i++) {
        GpuLight L = Lights[i];
        float3 toL = L.PosRange.xyz - P;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float nd = saturate(dot(N, Ld));
        if (nd <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 rad = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            rad *= cone * cone;
        }
        direct += rad * nd * Visibility(P, N, Ld, dist);
    }

    // Emissive-triangle NEE (shadow-rayed, two-sided emitter) — verbatim from AuroraCardLight.
    int ne = (int)min(EmissiveCount, 256.0);
    [loop] for (int li = 0; li < ne; li++) {
        EmissiveTri et = EmissiveLights[li];
        uint eseed = (px * 73856093u) ^ (py * 19349663u) ^ ((uint)li * 83492791u);
        float2 eu = float2(Hash(eseed), Hash(eseed + 1u));
        float su0 = sqrt(eu.x);
        float3 lp = et.V0.xyz + (1.0 - su0) * et.E0.xyz + (eu.y * su0) * et.E1.xyz;
        float3 ln = cross(et.E0.xyz, et.E1.xyz);
        float lnLen = length(ln);
        if (lnLen < 1e-8) continue;
        float area = 0.5 * lnLen;
        float3 toL = lp - P; float d2 = dot(toL, toL);
        if (d2 < 1e-6) continue;
        float d = sqrt(d2); float3 Ld = toL / d;
        float ndl2 = dot(N, Ld);
        float lndl = abs(dot(-Ld, ln / lnLen));   // two-sided
        if (ndl2 <= 0.0 || lndl <= 0.0) continue;
        float psa = (ndl2 * lndl / d2) * area;     // projected-solid-angle metric × area (pdf = 1/area)
        direct += et.Radiance.xyz * (psa * Visibility(P, N, Ld, d - 2e-3) * NeeIntensity);
    }

    // === INDIRECT (radiosity / multi-bounce) === trace cosine hemisphere rays; on a hit sample LAST frame's
    // FinalLighting at the hit's card texel (the bounce radiance). On miss: optional small sky term.
    float3 indirect = 0.0.xxx;
    uint ir = (uint)clamp(IndirectRays, 0.0, 8.0);
    if (FinalValid > 0.5 && ir > 0u) {
        float jit = Hash((px * 2654435761u) ^ (py * 40503u) ^ FrameIndex);
        float3x3 basis = BuildBasis(N);
        float3 acc = 0.0.xxx;
        [loop] for (uint k = 0; k < ir; k++) {
            float3 d = normalize(mul(CosineHemisphere(k, ir, jit), basis));
            RayDesc rd; rd.Origin = P + N * max(SunBias, 0.004); rd.Direction = d; rd.TMin = 0.02; rd.TMax = 1e4;
            RayQuery<RAY_FLAG_FORCE_OPAQUE> q; q.TraceRayInline(Scene, 0, 0xFF, rd); q.Proceed();
            if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
                uint inst = q.CommittedInstanceID();
                float3 hp = q.WorldRayOrigin() + q.WorldRayDirection() * q.CommittedRayT();
                // Hit-surface normal toward the ray origin (we sampled a card facing back at us).
                float3 hn = -d;
                acc += SampleFinalAtHit(inst, hp, hn, finalRead);
            } else if (UseSky > 0.5) {
                acc += 0.0.xxx;   // sky term optional; kept 0 for v1 (no sky cube bound in this pass)
            }
        }
        indirect = acc / float(ir) * IndirectIntensity;
    }

    // === FINAL === Albedo*(Direct+Indirect) + Emissive. Energy: the captured albedo is the surface reflectance;
    // direct/indirect are incoming irradiance-like terms; emissive is added on top (self-emission).
    float3 directRad = Sanitize(max(direct, 0.0.xxx));
    float3 finalLit = albedo * (directRad + indirect) + emissive;
    finalLit = Sanitize(max(finalLit, 0.0.xxx));

    directOut[tc] = float4(directRad, 1.0);
    finalOut[tc]  = float4(finalLit, 1.0);
}
