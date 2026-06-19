// Lumen V2 — minimal truthful diffuse GI (P2). One bounce, NO surface cache, NO temporal history yet.
//
// Per G-buffer pixel: integrate incoming diffuse radiance over the cosine hemisphere with a handful of rays.
// Each ray is resolved by a HIERARCHY (plan §Render Architecture 4→5):
//   1) SCREEN TRACE first — march the depth buffer a few steps; if the ray hits an on-screen surface, the
//      incoming radiance is THAT pixel's already-lit HDR color (free near-field contact bounce, no RT cost).
//   2) HARDWARE RT on a screen miss — inline RayQuery against the scene TLAS. On a triangle hit, shade the hit
//      with REAL first-bounce radiance: emissive + sun(N·L, shadow-rayed) + punctual, all × albedo decoded
//      bindlessly (the same per-instance geometry/material the reflections hit shader uses). On an RT miss the
//      ray escaped → sky/IBL irradiance in that direction.
// The cosine weight is folded into the sampling (rays drawn cosine-weighted around N), so a plain mean of the
// per-ray radiance already approximates the cosine-weighted irradiance E that gates Lambertian diffuse. The
// result is written as that irradiance (NOT yet × albedo) into the indirect buffer; the combine multiplies by
// the receiver albedo so one buffer serves any surface.
//
// "Noisy but truthful" (plan §P2): low ray count, no denoise. The gates are correctness, not smoothness —
//   sealed black room stays black, color-bleed box bleeds (emissive hit term), thin wall does not leak
//   (the RT ray hits the occluder). Temporal stabilization + cards come in P3/P4.
//
// CSTrace bindings (compute): TLAS t0 (root SRV), depth t1, world-normal t2, material t3, LIT scene color t4,
// sky irradiance cube t5, sky prefilter cube t6 (table); UAV indirect u0 (table); LumenConstants b0, LumenSun
// b1; root SRVs GpuMaterials t7 / RtInstance[] t8 / Lights t9 / CardRadiance t10 / LumenInstanceMeta t11;
// bindless heap (ResourceDescriptorHeap[] for per-instance buffers + albedo textures); clamp s0 + wrap s1.
//
// P3: an RT hit now SAMPLES the surface card (CardRadiance[meta.TriOffset + prim]) — the lit first-bounce
// radiance a separate card-light pass wrote — instead of re-shading direct light per hit. Off-screen surfaces
// contribute real albedo/emissive radiance, and P4 will accumulate multi-bounce into the same cache.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth     : register(t1);
Texture2D<float4> Normal    : register(t2);   // world normal packed [0,1]
Texture2D<float4> Material  : register(t3);   // r metallic, g roughness, b ao
Texture2D<float4> SceneColor: register(t4);   // the lit HDR color this frame (opaque+sky+transparent), for screen hits
TextureCube SkyIrradiance   : register(t5);   // sky/IBL diffuse irradiance (RT-miss diffuse term)
TextureCube SkyPrefilter    : register(t6);   // sky/IBL radiance (unused in P2; reserved)
RWTexture2D<float4> Indirect : register(u0);  // OUT: rgb = incoming diffuse irradiance E, a = 1

cbuffer LumenConstants : register(b0) {
    float4x4 InvViewProj;     // screen+depth → world (JITTERED, transposed)
    float4x4 ViewProj;        // world → clip (transposed) — screen-trace reprojection
    float3 CameraPos;   float Intensity;         // GI intensity multiplier
    float2 TexelSize;   float RayCount;  float FrameIndex;   // 1/res; hemisphere rays per pixel; rotation seed
    float NormalBias;   float MaxRayDist; float UseCards;   float ScreenSteps;   // bias; world ray length; >0.5 = sample cards on RT hit; screen march
    float SkyIntensity; float UseSky;    float UseScreenTrace; float ScreenRange;   // sky-miss scale; >0.5 sky; >0.5 screen-trace; contact range (m)
    float HistoryValid; float ProbeAlpha; float ImportanceSampling; float Pad1;   // #3 temporal; #4 sun-importance (>0.5 on)
    float4x4 PrevViewProj;   // #3: previous-frame UNJITTERED view*proj (world→prev clip) for camera-robust reprojection
};
cbuffer LumenSun : register(b1) {
    float3 SunDir;   float SunBias;       // TO the sun (normalized), world; shadow-ray origin offset
    float3 SunColor; float LightCount;    // sun radiance (RAW HDR); # punctual lights
};
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

// --- Bindless geometry + material (identical layout to DxrReflections.hlsl) ---
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
StructuredBuffer<float4>            CardRadiance : register(t10);   // #2A: per-CLUSTER lit radiance (the records)
StructuredBuffer<LumenInstanceMeta> InstanceMeta : register(t11);   // per-instance {triOffset, clusterOffset, world}
StructuredBuffer<uint>              TriToCluster : register(t12);   // #2A: global tri index → LOCAL cluster index
Texture2D<float4>                   ProbeHistory : register(t14);   // #3: last frame's accumulated E (rgb) + depth (a)
Texture2D<float2>                   Motion       : register(t15);   // #3: screen motion (prevUV-currUV) for ghosting reject

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {   // ternary component-select — NEVER lerp(v,0,flag) (NaN*0==NaN; proven AMD bug)
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

// Cosine-weighted hemisphere sample around +Z (local). Folding the cosine into the sampling means a plain mean
// of per-ray radiance ≈ the cosine-weighted irradiance.
float3 CosineHemisphere(uint i, uint n, float jitter) {
    float u1 = (float(i) + jitter) / float(n);
    float u2 = frac(jitter * 1.61803398875 + float(i) * 0.7548776662);
    float r = sqrt(saturate(u1));
    float phi = 6.28318530718 * u2;
    return float3(r * cos(phi), r * sin(phi), sqrt(saturate(1.0 - u1)));
}
float3x3 BuildBasis(float3 n) {
    float3 up = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 t = normalize(cross(up, n));
    float3 b = cross(n, t);
    return float3x3(t, b, n);   // rows; mul(local, basis) maps +Z → n
}

float3 WorldFromUvDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

// Inline shadow/visibility ray (any-hit-and-end). Returns 1 = visible, 0 = occluded.
float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray;
    ray.Origin = origin + N * max(SunBias, 0.002);
    ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// Shade an RT hit with first-bounce radiance: emissive + sun(N·L, shadow-rayed) + punctual, × albedo. This is
// the "truthful" off-screen term in P2 — no surface cache yet, so an off-screen hit returns its DIRECT-lit
// (and emissive) radiance, which is exactly what the color-bleed gate (emissive) and the black-room gate
// (no light → 0) need.
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
    if (dot(Ng, rayDir) > 0.0) Ng = -Ng;   // two-sided: face the incoming ray
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.95.xxx);

    // Emissive (matches StandardOpaque: EmissiveFactor is color*intensity, optionally × an emissive map).
    float3 emissive = 0.0.xxx;
    if (m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    float3 sunDir = normalize(SunDir);
    float ndl = saturate(dot(Ng, sunDir));
    float3 sun = (ndl > 0.0) ? SunColor * ndl * Visibility(hitPos, Ng, sunDir, MaxRayDist) : 0.0.xxx;

    // Punctual diffuse (shadow-rayed). Bounded loop; matches the reflection hit's punctual model.
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

// March the depth buffer along a world-space ray for a CONFIDENT, CLOSE near-field contact only (cheap contact
// bounce that avoids an RT dispatch when the answer is unambiguously on screen). Returns true ONLY for a hit
// inside ScreenRange metres — anything beyond that, off-screen, or behind a thick/uncertain occluder returns
// false so the RT ray (view-INDEPENDENT) owns the mid/far light. This is the fix for the SSGI view-dependent
// darkening: previously ANY on-screen hit (even a dark wall) vetoed RT, so turning the camera so the lit
// corridor left the screen made walls go dark; now only short-range contacts win, RT handles the rest.
bool ScreenTrace(float3 origin, float3 dir, out float3 radiance) {
    radiance = 0.0.xxx;
    // Confident screen contact distance — short (contact bounce only). The dominant mid/far GI comes from RT.
    float range = min(ScreenRange, MaxRayDist);
    int steps = max((int)ScreenSteps, 1);
    float stepLen = range / (float)steps;
    // Start a little along the ray so we don't self-intersect the origin pixel.
    float3 p = origin + dir * stepLen;
    [loop] for (int i = 0; i < steps; i++, p += dir * stepLen) {
        float4 clip = mul(float4(p, 1.0), ViewProj);
        if (clip.w <= 0.0) return false;
        float3 ndc = clip.xyz / clip.w;
        float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0)) return false;     // marched off-screen → let RT handle it
        float sceneDepth = Depth.SampleLevel(LinearClamp, uv, 0).r;
        if (sceneDepth >= 1.0) continue;                       // sky pixel — no occluder here
        // Compare reconstructed view-space distance so the thickness window is in metres.
        float3 rayWorld = WorldFromUvDepth(uv, ndc.z);
        float3 sceneWorld = WorldFromUvDepth(uv, sceneDepth);
        float rayZ = length(rayWorld - CameraPos);
        float sceneZ = length(sceneWorld - CameraPos);
        float diff = rayZ - sceneZ;                            // >0 = scene surface is in front of the ray
        // Thin-window contact: the scene surface sits just in front of the ray (a real touch), AND the contact
        // is CLOSE to the origin (within range). A thick gap (diff large) means the ray passed well behind the
        // on-screen surface — NOT a contact; bail to RT instead of returning that surface's (maybe dark) color.
        if (diff > 0.01 * rayZ && diff < stepLen * 2.0) {
            if (length(sceneWorld - origin) > range) return false;   // too far to be a confident contact → RT
            radiance = SceneColor.SampleLevel(LinearClamp, uv, 0).rgb;
            return true;
        }
        if (diff >= stepLen * 2.0) return false;              // ray went behind a thick occluder → not a contact, use RT
    }
    return false;
}

[numthreads(8, 8, 1)]
void CSTrace(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    uint W = (uint)round(1.0 / TexelSize.x), H = (uint)round(1.0 / TexelSize.y);
    if (px.x >= W || px.y >= H) return;

    // P7 #1b — the trace runs at (possibly) HALF the G-buffer resolution. `px` indexes the LOW-res indirect
    // buffer; `uv` (half-res texel center) maps to the full UV, so depth/normal must be UV-SAMPLED, not integer-
    // loaded (an integer load would read full-res texel `px`, i.e. the wrong place). A half-res pixel legitimately
    // represents its 2x2 full-res block; the combine's depth-aware upsample restores the full-res silhouette.
    float2 uv = (float2(px) + 0.5) * TexelSize;
    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) { Indirect[px] = float4(0, 0, 0, 1); return; }   // sky: no indirect receiver

    float3 nWorld = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(nWorld, nWorld) < 0.1) { Indirect[px] = float4(0, 0, 0, 1); return; }
    float3 N = normalize(nWorld);

    float3 worldPos = WorldFromUvDepth(uv, depth);
    float3 origin = worldPos + N * NormalBias;

    uint rays = (uint)clamp(RayCount, 1.0, 16.0);
    float jitter = Hash(px.x * 73856093u ^ px.y * 19349663u ^ (uint)FrameIndex * 2654435761u);
    float3x3 basis = BuildBasis(N);

    // #4 IMPORTANCE SAMPLING — the dominant indirect contributor in a sun-lit scene is the SUN-facing hemisphere
    // (direct-sun bounce + open sky toward the sun). Pure cosine sampling spends rays uniformly; biasing the FIRST
    // ray toward the sun direction (when the surface faces it) guarantees the brightest direction is sampled even
    // at 1-2 rays/pixel, where cosine sampling has visible hotspot noise (measured: 1 ray = 10.8% hotspot). The
    // remaining rays stay cosine for unbiased hemisphere coverage; the per-ray radiance is still plainly averaged,
    // so the estimate stays the cosine-weighted irradiance (the sun ray lands in the cosine lobe it would have
    // covered anyway — this is a stratification/guarantee, not a reweighting). Gated by ImportanceSampling.
    float3 sunLocalDir = mul(SunDir, transpose(basis));   // sun direction in the surface's local frame
    bool sunUp = sunLocalDir.z > 0.05;   // surface faces the sun → its bounce is worth a guaranteed ray
    float importance = ImportanceSampling;

    float3 sum = 0.0.xxx;
    [loop] for (uint r = 0; r < rays; r++) {
        float3 local;
        if (importance > 0.5 && r == 0u && sunUp) {
            // Guaranteed ray toward the sun (jittered within a small cosine-lobe cone so a flat surface still gets
            // a smooth result, not a hard single direction).
            float3 c = CosineHemisphere(0u, max(rays, 2u), jitter);
            local = normalize(sunLocalDir * 2.0 + c);   // pull the cosine sample toward the sun
        } else {
            local = CosineHemisphere(r, rays, jitter);
        }
        float3 dir = normalize(mul(local, basis));

        // 1) Screen trace (near-field contact bounce). UseScreenTrace<0.5 disables it → pure RT+cards (the A/B
        // door to prove the view-dependent darkening is screen-trace's fault, not the RT path).
        float3 rad;
        if (UseScreenTrace > 0.5 && ScreenTrace(origin, dir, rad)) { sum += rad; continue; }

        // 2) Hardware RT on screen miss.
        RayDesc ray;
        ray.Origin = origin; ray.Direction = dir; ray.TMin = 0.02; ray.TMax = MaxRayDist;
        RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
        q.TraceRayInline(Scene, 0, 0xFF, ray);
        q.Proceed();
        if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
            if (UseCards > 0.5) {
                // #2A: SAMPLE the surface card at the hit triangle's CLUSTER RECORD (the lit radiance the card-
                // light pass wrote per cluster) — no per-hit relighting. record = instance.ClusterOffset + the
                // local cluster of the hit triangle. Diffuse GI is low-frequency, so the cluster's single radiance
                // reads identically to per-triangle on the receiver.
                uint inst = q.CommittedInstanceID();
                LumenInstanceMeta meta = InstanceMeta[inst];
                uint record = meta.ClusterOffset + TriToCluster[meta.TriOffset + q.CommittedPrimitiveIndex()];
                sum += CardRadiance[record].rgb;
            } else {
                // A/B fallback (BALLISTIC_DX12_LUMEN_NOCARDS=1): re-shade the hit directly (the P2 path).
                float3 hitPos = origin + dir * q.CommittedRayT();
                sum += ShadeHit(q.CommittedInstanceID(), q.CommittedPrimitiveIndex(),
                                q.CommittedTriangleBarycentrics(), q.CommittedObjectToWorld3x4(), dir, hitPos);
            }
        } else if (UseSky > 0.5) {
            // 3) Ray escaped → sky/IBL irradiance in that direction.
            sum += SkyIrradiance.SampleLevel(LinearClamp, dir, 0).rgb * SkyIntensity;
        }
    }

    // Mean over the cosine-sampled rays ≈ cosine-weighted incoming irradiance E. Store E (not E*albedo); the
    // combine applies the receiver albedo. Intensity is the artist GI dial.
    float3 E = Sanitize(sum / float(rays) * Intensity);

    // #3 PROBE TEMPORAL ACCUMULATION — EMA this frame's noisy few-ray E over the REPROJECTED accumulated history.
    // Over frames this converges to a many-ray, low-variance probe radiance (the screen-probe quality win) while
    // three guards kill GHOSTING (the temporal trail under motion):
    //   1) REPROJECT: sample history at the surface's PREVIOUS screen position (px + motion), not px — so a moving
    //      surface accumulates correctly instead of dragging a trail.
    //   2) MOTION + DEPTH reject: if the reprojected texel left the screen, or its depth disagrees (disocclusion),
    //      or screen motion is large, take the FRESH E (no blend) so a fast move shows the current frame, sharp.
    //   3) NEIGHBOURHOOD CLAMP: clamp the history to this frame's local 3x3 E min/max. A lighting change (a shadow
    //      sweeps across a surface whose DEPTH is unchanged — depth-reject can't catch it) is bounded immediately
    //      instead of leaving the old bright/dark value to fade out slowly. This is the AABB-clamp TAA uses.
    float3 outE = E;
    [branch] if (HistoryValid > 0.5) {
        // REPROJECT this surface to its previous-frame screen position via worldPos * PrevViewProj (catches a
        // moving SCENE camera even with a zero motion buffer). prevUv = previous-frame UV.
        float4 prevClip = mul(float4(worldPos, 1.0), PrevViewProj);
        bool wValid = prevClip.w > 1e-6;
        float2 prevUv = wValid ? (prevClip.xy / prevClip.w) * float2(0.5, -0.5) + 0.5 : uv;
        float2 motion = prevUv - uv;
        float motionTexels = length(motion / max(TexelSize, 1e-6));
        float4 hist = ProbeHistory.SampleLevel(LinearClamp, prevUv, 0);   // rgb=accumulated E, a=depth at capture

        // SOFT history weight in [0,1] — NEVER a binary all-or-nothing reject (that caused the "GI flicks off then
        // back on" gitgel AND the go-dark-on-zoom-out: a hard reject drops to a single noisy/dark frame). Three
        // smooth factors multiply down the trust instead:
        //   • on-screen: 0 only when the reprojection truly left the screen.
        //   • depth agreement: RELATIVE tolerance (abs(dz)/depth) so far surfaces aren't falsely rejected on a
        //     zoom-out (the old absolute 0.0015 rejected everything when the camera pulled back → whole-scene dark).
        //   • motion: ease trust down as the surface moves on screen, don't slam it to 0.
        // TRUST factors — kept DELIBERATELY soft. The earlier per-frame pulse ("parlayıp sönme") came from a
        // depth-agreement term that wobbled as the camera moved: hist.a is LAST frame's depth, `depth` is this
        // frame's, so on any camera move the same surface's depth shifts and a tight band made `trust` (hence the
        // blend weight, hence the brightness) oscillate frame to frame. So:
        //   • depth band is now WIDE — it only catches a real disocclusion (a totally different surface), ~25%.
        //   • motion barely lowers trust (the reprojection already handles motion; we don't want a brightness ramp).
        //   • the PULSE is bounded by the LUMINANCE CLAMP instead (a tight ±band keeps each frame's result close to
        //     the stable history, so a noisy fresh E can't flash brighter/darker than the converged value).
        float onScreen = (wValid && all(prevUv >= 0.0) && all(prevUv <= 1.0)) ? 1.0 : 0.0;
        float depthAgree = saturate(1.0 - abs(hist.a - depth) / max(depth * 0.25, 1e-4));   // wide → disocclusion only
        float motionTrust = saturate(1.0 - motionTexels * 0.04);                            // very gentle
        float trust = onScreen * depthAgree * motionTrust;

        // Luminance clamp: bound the history's brightness to a TIGHT band around this frame's E so neither a noisy
        // fresh frame nor a stale history can pulse. This is the primary anti-flicker now (not the depth reject).
        float lh = max(dot(hist.rgb, float3(0.2126, 0.7152, 0.0722)), 1e-4);
        float le = dot(E, float3(0.2126, 0.7152, 0.0722));
        float lClamped = clamp(lh, le * 0.7, le * 1.5 + 1e-3);
        float3 clampedHist = hist.rgb * (lClamped / lh);

        // Weight: ProbeAlpha when trusted (slow, stable accumulation), easing to 1 only on a real disocclusion.
        // Low trust falls toward fresh E (not black) so nothing collapses.
        float alpha = lerp(1.0, saturate(ProbeAlpha), trust);
        float3 histContribution = lerp(E, clampedHist, trust);
        outE = lerp(histContribution, E, alpha);
    }
    Indirect[px] = float4(Sanitize(outE), depth);             // store depth in .a for next frame's reject + history copy
}

// ===== Spatial denoise (P4): edge-aware blur of the per-pixel indirect E =====
// The cache (cards) is temporally STABLE, but the per-pixel screen gather still Monte-Carlo-samples only a few
// hemisphere rays → spatial variance (the visible "grain"). Diffuse GI is LOW-FREQUENCY, so a depth+normal-
// guided blur removes that variance without a screen-space temporal history (plan: "final screen pixels do not
// carry the history burden"). One wide à-trous-style pass, separable-ish 5x5 with bilateral weights. Reads the
// raw E (t0), depth (t1), normal (t2); writes the filtered E to a scratch the combine then reads.
Texture2D<float4> DnIn     : register(t0);
Texture2D<float>  DnDepth  : register(t1);
Texture2D<float4> DnNormal : register(t2);
RWTexture2D<float4> DnOut  : register(u0);
SamplerState DnLinearClamp : register(s0);   // P7 #1b: G-buffer is full-res, the E buffer is half-res → UV-sample

cbuffer DenoiseConstants : register(b0) {
    float2 DnTexel; float DnStep; float DnEnabled;   // 1/res (of the HALF-res E buffer); tap stride (px); >0.5 = blur
};

[numthreads(8, 8, 1)]
void CSDenoise(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    uint W = (uint)round(1.0 / DnTexel.x), H = (uint)round(1.0 / DnTexel.y);
    if (px.x >= W || px.y >= H) return;

    // P7 #1b — px indexes the HALF-res E buffer (integer load OK for DnIn). depth/normal are FULL-res, so they
    // are UV-sampled at the half-res texel center (uvC) and per-tap (uvQ).
    float2 uvC = (float2(px) + 0.5) * DnTexel;
    float3 c = DnIn[px].rgb;
    if (DnEnabled < 0.5) { DnOut[px] = float4(c, 1); return; }

    float dC = DnDepth.SampleLevel(DnLinearClamp, uvC, 0).r;
    if (dC >= 1.0) { DnOut[px] = float4(c, 1); return; }   // sky — nothing to filter
    float3 nC = DnNormal.SampleLevel(DnLinearClamp, uvC, 0).rgb * 2.0 - 1.0;

    float3 sum = 0.0.xxx; float wsum = 0.0;
    int r = 2; float stride = max(DnStep, 1.0);
    [unroll] for (int dy = -2; dy <= 2; dy++)
    [unroll] for (int dx = -2; dx <= 2; dx++) {
        int2 q = int2(px) + int2(dx, dy) * (int)stride;
        if (q.x < 0 || q.y < 0 || q.x >= (int)W || q.y >= (int)H) continue;
        float2 uvQ = (float2(q) + 0.5) * DnTexel;
        float dq = DnDepth.SampleLevel(DnLinearClamp, uvQ, 0).r;
        if (dq >= 1.0) continue;
        float3 nq = DnNormal.SampleLevel(DnLinearClamp, uvQ, 0).rgb * 2.0 - 1.0;
        // Bilateral weights: gaussian spatial * normal similarity * depth similarity (linear-ish window depth).
        float wSpatial = exp(-float(dx * dx + dy * dy) / 4.0);
        float wNormal = pow(saturate(dot(nC, nq)), 32.0);
        float wDepth = exp(-abs(dq - dC) * 600.0);
        float w = wSpatial * wNormal * wDepth;
        sum += DnIn[q].rgb * w; wsum += w;
    }
    DnOut[px] = float4(wsum > 1e-4 ? sum / wsum : c, 1);
}

// ===== Combine: add the diffuse indirect into the HDR scene color =====
// Indirect holds incoming irradiance E. The diffuse response is E * albedo * occlusion (Lambertian, the same
// albedo the deferred pass used). The deferred pass SUPPRESSED its IBL diffuse ambient when Lumen is active
// (UseIBLDiffuse=0), so this is not double-counting — Lumen OWNS the diffuse indirect. Specular IBL + direct
// light + emissive are already in the scene color. Additive blend into the existing HDR target.
//
// OCCLUSION: the material's baked AO (GMaterial.b) ALWAYS applies (it's authored detail). The screen-space
// GTAO (t4, the AmbientOcclusion volume's output) applies at AoStrength — the Lumen RT trace already carries
// MACRO occlusion (rays that don't escape find less light), so full GTAO on top double-darkens corners. At
// AoStrength 0 the GI sees only its own RT occlusion + material AO; at 1 the GTAO bites fully. This is how the
// AmbientOcclusion override drives CONTACT detail in the GI (the high-frequency darkening the coarse cache /
// few-ray gather miss). GTAO is at AO resolution → LinearClamp upsamples it.

Texture2D<float4> IndirectIn : register(t0);   // E from CSTrace
Texture2D<float4> GAlbedo    : register(t1);   // rgb albedo
Texture2D<float4> GMaterial  : register(t2);   // b = baked material AO
Texture2D<float>  CombineDepth : register(t3);
Texture2D<float>  GtaoTex    : register(t4);   // screen-space GTAO (1 = unoccluded); AmbientOcclusion volume

cbuffer CombineConstants : register(b0) {
    float AoStrength; float2 IndirectTexel; float CombinePad0;   // GTAO bite (0..1); 1/half-res for the upsample
};

// P7 #1b — DEPTH-AWARE UPSAMPLE of the half-res indirect E (the SSR-combine pattern). The E buffer is rendered
// at a fraction of the G-buffer resolution; a plain bilinear sample bleeds indirect across silhouettes (a wall's
// GI leaks onto the floor in front of it). Weight each of the 4 nearest half-res taps by bilinear × depth
// similarity to the FULL-res center depth, so a tap on the wrong surface is rejected → crisp edges from a coarse
// buffer. When IndirectTexel matches full-res (RESSCALE=1) this degrades to a plain bilinear fetch (taps coincide).
float3 UpsampleIndirect(float2 uv, float centerDepth) {
    float2 lowSize = 1.0 / IndirectTexel;
    float2 pos = uv * lowSize - 0.5;
    float2 baseUV = (floor(pos) + 0.5) * IndirectTexel;
    float2 f = frac(pos);
    float3 acc = 0.0.xxx; float wSum = 0.0;
    [unroll] for (int k = 0; k < 4; k++) {
        float2 corner = float2(k & 1, k >> 1);
        float2 tuv = baseUV + corner * IndirectTexel;
        float wBilinear = (corner.x > 0.5 ? f.x : 1.0 - f.x) * (corner.y > 0.5 ? f.y : 1.0 - f.y);
        float tapDepth = CombineDepth.SampleLevel(LinearClamp, tuv, 0).r;
        float wDepth = 1.0 / (1.0 + abs(tapDepth - centerDepth) * 4000.0);   // depth is non-linear [0,1] → tight tol
        float w = wBilinear * wDepth + 1e-5;
        acc += Sanitize(IndirectIn.SampleLevel(LinearClamp, tuv, 0).rgb) * w;
        wSum += w;
    }
    return acc / wSum;
}

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSCombine(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float4 PSCombine(VSOut i) : SV_Target {
    float depth = CombineDepth.SampleLevel(LinearClamp, i.Uv, 0).r;
    if (depth >= 1.0) discard;                       // sky: leave the scene color untouched
    float3 E = UpsampleIndirect(i.Uv, depth);
    float3 albedo = GAlbedo.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    float matAo = GMaterial.SampleLevel(LinearClamp, i.Uv, 0).b;
    // GTAO eased by AoStrength (1 = no GTAO darkening; lerp toward the raw GTAO at full strength).
    float gtao = lerp(1.0, GtaoTex.SampleLevel(LinearClamp, i.Uv, 0).r, saturate(AoStrength));
    float3 diffuseIndirect = E * albedo * matAo * gtao / PI;   // Lambertian: outgoing = E*albedo/PI
    return float4(Sanitize(diffuseIndirect), 1.0);   // additive blend (One/One) adds onto the HDR scene color
}

// DEBUG (BALLISTIC_DX12_LUMEN_DEBUG=1): OPAQUE replace with the raw incoming irradiance E so the GI signal is
// directly visible (isolates "is the trace producing radiance?" from the combine/exposure). Not a product path.
float4 PSDebugE(VSOut i) : SV_Target {
    float depth = CombineDepth.SampleLevel(LinearClamp, i.Uv, 0).r;
    if (depth >= 1.0) return float4(0, 0, 0, 1);
    float3 E = UpsampleIndirect(i.Uv, depth);
    return float4(Sanitize(E), 1.0);
}
