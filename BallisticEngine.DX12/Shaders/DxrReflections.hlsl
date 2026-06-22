// Ray-traced reflections (lib_6_6).
// One reflection ray per pixel from the G-buffer surface: reconstruct world pos + view dir, reflect about the
// normal, trace against the scene BVH. Miss = the prefiltered sky/IBL cube along the ray (roughness-mipped);
// closest-hit shades direct light plus IBL ambient:
//
//     hitRadiance = albedo * ( SunColor*saturate(dot(Ng,SunDir))*shadowRay + punctual(shadow-rayed) + ambient )
//
// Mirror rays only (R = reflect(-V,N), no jitter) → deterministic → NO denoiser needed (byte-identical capture).
// Writes (rgb reflected color, a strength) into the half-res SSR reflection target — the SAME contract as
// Ssr.hlsl's march — so the existing SSR combine (depth-aware upsample + Fresnel lerp) mixes it into the scene.
// roughFade tapers reflections to 0 at MAX_ROUGHNESS.
//
// Bound (global root sig, HeapDirectlyIndexed): TLAS t0, depth t1, world-normal t2, material t3, irradiance
// cube t4, prefilter cube t5, output UAV u0, ReflConstants b0, RtReflectionLights b1;
// root SRVs GpuMaterials t7 / RtInstance[] t8 / Lights t9; bindless heap
// (ResourceDescriptorHeap[] for per-instance index/normal/uv buffers + albedo textures); static clamp s0 + wrap s1.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth    : register(t1);
Texture2D<float4> Normal   : register(t2);   // world normal packed [0,1]
Texture2D<float4> Material : register(t3);   // r metallic, g roughness
TextureCube Irradiance     : register(t4);   // sky/IBL irradiance
TextureCube Prefilter      : register(t5);   // roughness-mipped sky/IBL radiance (the reflection-ray MISS color)
RWTexture2D<float4> Output  : register(u0);

cbuffer ReflConstants : register(b0) {
    float4x4 InvViewProj;    // screen+depth → world (JITTERED, transposed)
    float3 CameraPos; float Intensity;
    float PrefilterMaxMip; float NormalBias; float UseCards; float FrameIndex;   // UseCards: sample the Aurora card cache at hits; FrameIndex: VNDF temporal jitter (<0 = det fixed)
};
// Reserved cbuffer slot (b2) — kept so the root signature layout matches C# (rtReflGridCb). Unused by the
// Aurora card path (card lookup is per-hit InstanceID/PrimitiveIndex, no world-grid params needed).
cbuffer ReflGiReserved : register(b2) {
    float4 _ReflGiReserved0;
    float4 _ReflGiReserved1;
    uint4  _ReflGiReserved2;
};
cbuffer RtReflectionLights : register(b1) {
    float3 SunDir;     float SunNormalBias;   // TO the sun (normalized), world; bias = shadow-ray origin offset
    float3 SunColor;   float LightCount;      // sun radiance, RAW HDR (NOT pre-exposed); # punctual lights
};
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

// --- Bindless geometry + material ---
struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; uint PositionIdx, TriCount, Pad0, Pad1; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; float4 RightAxisHalfW; }; // 80B (RightAxisHalfW = rect right-axis; 0 for point/spot — must match Dx12ClusteredLights.GpuLight stride)
struct AuroraInstanceMeta { uint TriOffset, TriCount, ClusterOffset, ClusterCount; float4x4 World; };
StructuredBuffer<GpuMaterial>        GpuMaterials : register(t7);
StructuredBuffer<RtInstance>         RtInstances  : register(t8);
StructuredBuffer<GpuLight>           Lights       : register(t9);
StructuredBuffer<float4>             CardRadiance : register(t11);   // Aurora per-CLUSTER lit + multibounce radiance
StructuredBuffer<AuroraInstanceMeta> InstanceMeta : register(t12);   // per-instance {triOffset, clusterOffset, world}
StructuredBuffer<uint>               TriToCluster : register(t13);   // global tri index → LOCAL cluster index

// A reflection hit reads the SAME radiance cache the primary GI fills: record = instance.ClusterOffset +
// TriToCluster[instance.TriOffset + prim]. CardRadiance[record] is that surface's lit + multi-bounce radiance,
// so the reflection shares the exact diffuse GI the diffuse view sees (no separate probe gather).
float3 AuroraCardGather(uint instanceId, uint prim) {
    AuroraInstanceMeta meta = InstanceMeta[instanceId];
    uint record = meta.ClusterOffset + TriToCluster[meta.TriOffset + prim];
    return min(CardRadiance[record].rgb, 60000.0.xxx);
}

// B1/B2: MAX_ROUGHNESS raised from 0.6 → 1.0 so ROUGH surfaces also get ray-traced reflections (previously hard-
// cut to a mirror-only band; rough metals/floors fell back to the blurry IBL cube only). VNDF importance sampling
// (below) makes the rough reflection ray properly distributed so the single per-pixel ray + temporal/spatial
// resolve reconstructs a clean glossy reflection instead of mirror noise.
static const float MAX_ROUGHNESS = 1.0;
struct ReflPayload { float3 Color; float Roughness; };

float3 Sanitize(float3 v) {   // ternary component-select — never mix(v,0,flag) (NaN*0==NaN; the proven AMD bug)
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// ---- B1: VNDF (Heitz 2018) GGX visible-normal importance sampling ----
// A pure mirror ray (reflect(-V,N)) is only correct for a perfect mirror; on a rough surface the reflection lobe
// is a CONE, and a mirror ray gives a too-sharp, wrong reflection. VNDF samples a microfacet half-vector H from
// the distribution of VISIBLE normals (the Falcor/Heitz form kajiya uses, inc/brdf.hlsl), so reflect(-V,H) gives
// a ray drawn from the actual GGX lobe — 2-4x lower variance than naive NDF sampling and the right rough shape.
float2 R2(uint i) {   // plastic-constant low-discrepancy pair (blue-noise-like) for the VNDF urand
    return frac(float2(0.7548776662466927, 0.5698402909980532) * (float)i + 0.5);
}
float Hash1(uint s) { s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15; return float(s & 0x7fffffffu) / float(0x7fffffff); }

// Build an orthonormal tangent basis around N (Duff et al. branchless).
void OnbFrame(float3 n, out float3 t, out float3 b) {
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float bb = n.x * n.y * a;
    t = float3(1.0 + s * n.x * n.x * a, s * bb, -s * n.x);
    b = float3(bb, s + n.y * n.y * a, -n.y);
}

// Sample a microfacet half-vector H (world space) from the GGX VNDF, given world view dir V and roughness.
float3 SampleVndfH(float3 N, float3 V, float roughness, float2 urand) {
    float alpha = max(roughness * roughness, 1e-3);
    float3 T, B;
    OnbFrame(N, T, B);
    // View dir into the tangent frame (z = N).
    float3 wo = float3(dot(V, T), dot(V, B), dot(V, N));
    wo = normalize(wo);
    // Heitz VNDF (Falcor port).
    float3 Vh = normalize(float3(alpha * wo.x, alpha * wo.y, wo.z));
    float3 t1 = (Vh.z < 0.9999) ? normalize(cross(float3(0, 0, 1), Vh)) : float3(1, 0, 0);
    float3 t2 = cross(Vh, t1);
    float r = sqrt(urand.x);
    float phi = 6.28318530718 * urand.y;
    float p1 = r * cos(phi);
    float p2 = r * sin(phi);
    float s = 0.5 * (1.0 + Vh.z);
    p2 = (1.0 - s) * sqrt(max(0.0, 1.0 - p1 * p1)) + s * p2;
    float3 Nh = p1 * t1 + p2 * t2 + sqrt(max(0.0, 1.0 - p1 * p1 - p2 * p2)) * Vh;
    float3 hTan = normalize(float3(alpha * Nh.x, alpha * Nh.y, max(0.0, Nh.z)));
    // Back to world space.
    return normalize(hTan.x * T + hTan.y * B + hTan.z * N);
}

// Inline shadow/visibility ray (RayQuery — no recursion, so the reflection PSO stays MaxTraceRecursionDepth=1).
float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray;
    ray.Origin = origin + N * max(SunNormalBias, 0.001);
    ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// Diffuse irradiance from all punctual lights at a hit.
float3 PunctualDiffuse(float3 hit, float3 N) {
    float3 sum = 0.0.xxx;
    int n = min((int)LightCount, 64);
    [loop] for (int i = 0; i < n; i++) {
        GpuLight L = Lights[i];
        if (L.Color.w >= 1.5) continue;   // skip area/rect lights (type 2) — not in RT reflections v1 (deferred LTC only)
        float3 toL = L.PosRange.xyz - hit;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float ndl = saturate(dot(N, Ld));
        if (ndl <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 radiance = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {                                       // spot cone
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            radiance *= cone * cone;
        }
        sum += radiance * ndl * Visibility(hit, N, Ld, dist);
    }
    return sum;
}

[shader("raygeneration")]
void RayGen() {
    uint2 idx = DispatchRaysIndex().xy;
    uint2 dim = DispatchRaysDimensions().xy;
    float2 uv = (float2(idx) + 0.5) / float2(dim);
    Output[idx] = float4(0, 0, 0, 0);

    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) return;                                   // sky: nothing reflects here
    float4 mat = Material.SampleLevel(LinearClamp, uv, 0);
    float metallic = mat.r, roughness = mat.g;
    float3 worldN = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(worldN, worldN) < 0.1 || roughness > MAX_ROUGHNESS) return;

    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    float3 worldPos = w.xyz / w.w;
    float3 N = normalize(worldN);
    float3 V = normalize(CameraPos - worldPos);
    float NdotV = max(dot(N, V), 0.0);

    // Fresnel strength (matches Ssr.hlsl so the shared combine lerps consistently).
    float F0 = metallic >= 0.5 ? 0.6 : 0.04;
    float fres = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
    float grazeKeep = 1.0 - smoothstep(0.05, 0.45, roughness);
    fres = F0 + (fres - F0) * grazeKeep;
    // B2: roughness no longer hard-fades to 0 at 0.6 — rough surfaces keep a (lower) reflection strength all the
    // way to fully rough, where it blends with the IBL. Taper gently so very rough stays subtle, not absent.
    float roughFade = 1.0 - smoothstep(0.5, 1.0, roughness) * 0.85;
    float strength = saturate(fres * Intensity) * roughFade;
    if (strength <= 0.001) return;

    // B1: VNDF-sampled reflection ray(s). A near-mirror surface (roughness→0) collapses to reflect(-V,N), while a
    // rough surface draws rays from the GGX visible-normal lobe — the correct glossy cone. The bug-hunt flagged
    // that a SINGLE VNDF ray over a wide cone is noisy under camera motion (the temporal denoise hard-disables on
    // motion), so MULTI-SAMPLE the rough band: 1 ray for near-mirror, up to SPP rays for fully rough, each with a
    // decorrelated urand (per-sample R2 stride), averaged. This trades a few rays on the (rare, small-area) rough-
    // reflective pixels for clean glossy reflections — the single-ray sparkle B2 would otherwise introduce.
    // Per-pixel blue-noise base; live path advances by the canonical frame counter so the temporal/spatial resolve
    // still integrates across frames; deterministic capture uses a FIXED per-pixel offset (byte-stable).
    float2 baseRand = float2(Hash1(idx.x * 1973u + idx.y * 9277u + 1u),
                             Hash1(idx.x * 26699u + idx.y * 8537u + 7u));
    float2 frameRand = (FrameIndex < 0.0) ? 0.0.xx : R2((uint)FrameIndex);
    // Sample count scales with roughness: smooth → 1 (mirror, no benefit from more), rough → SPP.
    const uint SPP_MAX = 4u;
    uint spp = (uint)clamp(1.0 + smoothstep(0.15, 0.8, roughness) * (float)(SPP_MAX - 1u) + 0.5, 1.0, (float)SPP_MAX);

    float3 colSum = 0.0.xxx; float wSum = 0.0;
    [loop] for (uint si = 0u; si < SPP_MAX; ++si) {
        if (si >= spp) break;
        float2 urand = frac(baseRand + frameRand + R2(si * 977u + 13u));   // per-sample decorrelated
        float3 H = SampleVndfH(N, V, roughness, urand);
        float3 R = reflect(-V, H);
        if (dot(R, N) <= 0.0) R = reflect(-V, N);   // grazing VNDF tail → mirror about N

        ReflPayload p;
        p.Color = 0.0.xxx;
        p.Roughness = roughness;
        RayDesc ray;
        ray.Origin = worldPos + N * NormalBias;
        ray.Direction = R;
        ray.TMin = 0.02;
        ray.TMax = 1e4;
        TraceRay(Scene, RAY_FLAG_FORCE_OPAQUE, 0xFF, 0, 1, 0, ray, p);
        colSum += Sanitize(p.Color); wSum += 1.0;
    }
    float3 col = (wSum > 0.0) ? colSum / wSum : 0.0.xxx;
    Output[idx] = float4(col, strength);
}

[shader("miss")]
void Miss(inout ReflPayload p) {
    // Reflection ray escaped → the sky/IBL in that direction (roughness-blurred via the prefilter mips).
    float mip = clamp(p.Roughness * PrefilterMaxMip, 0.0, PrefilterMaxMip);
    p.Color = Prefilter.SampleLevel(LinearClamp, WorldRayDirection(), mip).rgb;
}

[shader("closesthit")]
void ClosestHit(inout ReflPayload p, in BuiltInTriangleIntersectionAttributes attr) {
    // A reflection hit is shaded in full world space (sun + punctual + GI), so the reflection sees the SAME
    // diffuse GI the primary view does. When the Aurora radiance cache is live (UseCards) the indirect term is
    // read from the per-triangle card record; otherwise it falls back to the IBL irradiance cube. Fetch the hit
    // triangle's interpolated normal + UV from the bindless per-instance geometry buffers.
    RtInstance inst = RtInstances[InstanceID()];
    Buffer<uint>             indices = ResourceDescriptorHeap[inst.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[inst.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[inst.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[inst.TriMatIdx];

    uint prim = PrimitiveIndex();
    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float3 bary = float3(1.0 - attr.barycentrics.x - attr.barycentrics.y, attr.barycentrics.x, attr.barycentrics.y);

    float3 nObj = normalize(normals[i0] * bary.x + normals[i1] * bary.y + normals[i2] * bary.z);
    float3 Ng = normalize(mul((float3x3)ObjectToWorld3x4(), nObj));
    if (dot(Ng, WorldRayDirection()) > 0.0) Ng = -Ng;   // two-sided: face the reflection ray's incoming side
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.9.xxx);

    float3 hit = WorldRayOrigin() + RayTCurrent() * WorldRayDirection();

    // Direct light at the hit (sun + punctual, each shadow-rayed). RAW HDR — the SSR combine does NOT pre-expose
    // the reflection color, so this stays in scene-radiance units (the prefilter-cube MISS
    // color is raw HDR too, so hit + miss share the same scale into the depth-aware Fresnel-lerp combine).
    float ndl = saturate(dot(Ng, normalize(SunDir)));
    float3 sun = SunColor * ndl * (ndl > 0.0 ? Visibility(hit, Ng, normalize(SunDir), 1e4) : 0.0);
    float3 punctual = PunctualDiffuse(hit, Ng);

    // Indirect/ambient: the Aurora card cache when live (the reflection shares the primary multi-bounce GI),
    // else the IBL cube. The card already holds lit+multibounce radiance for that surface, so it is added
    // directly (not multiplied by albedo again — albedo only scales the direct sun+punctual terms).
    bool useCard = (UseCards > 0.5);
    float3 ambient = useCard ? 0.0.xxx : Irradiance.SampleLevel(LinearClamp, Ng, 0).rgb;
    float3 radiance = albedo * (sun + punctual + ambient)
                    + (useCard ? AuroraCardGather(InstanceID(), prim) : 0.0.xxx);

    // Soft luminance clamp (NOT saturate — that crushes the ~1e5 HDR). Tame fireflies, then ternary Sanitize.
    // Per-channel cap below the fp16 ceiling (~65504) BEFORE Sanitize: the +emissive term is UNBOUNDED
    // (EmissiveFactor is raw HDR, added outside the albedo<=0.9 product), and the luma clamp alone can leave a
    // single channel > fp16 max → a finite +Inf store into the half-res RGBA16F ssrTarget that the SSR combine
    // would spread (no read-side scrub there). The min() caps at the source.
    float luma = dot(radiance, float3(0.2126, 0.7152, 0.0722));
    if (luma > 1.0e5) radiance *= 1.0e5 / max(luma, 1e-4);
    p.Color = Sanitize(min(radiance, 60000.0.xxx));
}
