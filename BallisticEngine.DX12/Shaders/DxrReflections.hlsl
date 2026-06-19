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
    float PrefilterMaxMip; float NormalBias; float UseCards; float Unused1;   // UseCards: P5 — sample the Lumen card cache at hits
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
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; };
struct LumenInstanceMeta { uint TriOffset, TriCount, ClusterOffset, ClusterCount; float4x4 World; };
StructuredBuffer<GpuMaterial>       GpuMaterials : register(t7);
StructuredBuffer<RtInstance>        RtInstances  : register(t8);
StructuredBuffer<GpuLight>          Lights       : register(t9);
StructuredBuffer<float4>            CardRadiance : register(t11);   // #2A: Lumen per-CLUSTER lit+multibounce radiance
StructuredBuffer<LumenInstanceMeta> InstanceMeta : register(t12);   // per-instance {triOffset, clusterOffset, world}
StructuredBuffer<uint>              TriToCluster : register(t13);   // #2A: global tri index → LOCAL cluster index

static const float MAX_ROUGHNESS = 0.6;
struct ReflPayload { float3 Color; float Roughness; };

float3 Sanitize(float3 v) {   // ternary component-select — never mix(v,0,flag) (NaN*0==NaN; the proven AMD bug)
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
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
    float3 R = reflect(-V, N);

    // Fresnel strength (matches Ssr.hlsl so the shared combine lerps consistently).
    float F0 = metallic >= 0.5 ? 0.6 : 0.04;
    float fres = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
    float grazeKeep = 1.0 - smoothstep(0.05, 0.45, roughness);
    fres = F0 + (fres - F0) * grazeKeep;
    float roughFade = 1.0 - smoothstep(0.3, MAX_ROUGHNESS, roughness);
    float strength = saturate(fres * Intensity) * roughFade;
    if (strength <= 0.001) return;

    ReflPayload p;
    p.Color = 0.0.xxx;
    p.Roughness = roughness;
    RayDesc ray;
    ray.Origin = worldPos + N * NormalBias;
    ray.Direction = R;
    ray.TMin = 0.02;
    ray.TMax = 1e4;
    TraceRay(Scene, RAY_FLAG_FORCE_OPAQUE, 0xFF, 0, 1, 0, ray, p);

    Output[idx] = float4(Sanitize(p.Color), strength);
}

[shader("miss")]
void Miss(inout ReflPayload p) {
    // Reflection ray escaped → the sky/IBL in that direction (roughness-blurred via the prefilter mips).
    float mip = clamp(p.Roughness * PrefilterMaxMip, 0.0, PrefilterMaxMip);
    p.Color = Prefilter.SampleLevel(LinearClamp, WorldRayDirection(), mip).rgb;
}

[shader("closesthit")]
void ClosestHit(inout ReflPayload p, in BuiltInTriangleIntersectionAttributes attr) {
    // P5: when the Lumen card cache is live, the reflection hit SAMPLES the card (the lit + multi-bounce
    // radiance leaving that surface — albedo*(direct+indirect)+emissive) instead of re-shading direct+IBL, so
    // a reflection sees the SAME GI the diffuse does. This is the "rough reflections sample the radiance cache"
    // path; the mirror ray (sharp) lands on one triangle whose card is its outgoing radiance — correct for both.
    if (UseCards > 0.5) {
        LumenInstanceMeta meta = InstanceMeta[InstanceID()];
        uint record = meta.ClusterOffset + TriToCluster[meta.TriOffset + PrimitiveIndex()];   // #2A cluster record
        p.Color = Sanitize(min(CardRadiance[record].rgb, 60000.0.xxx));
        return;
    }

    // Full world-space radiance at the reflection hit. Fetch the hit triangle's interpolated normal + UV from
    // the bindless per-instance geometry buffers.
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

    float3 ambient = Irradiance.SampleLevel(LinearClamp, Ng, 0).rgb;
    float3 radiance = albedo * (sun + punctual + ambient);

    // Soft luminance clamp (NOT saturate — that crushes the ~1e5 HDR). Tame fireflies, then ternary Sanitize.
    // Per-channel cap below the fp16 ceiling (~65504) BEFORE Sanitize: the +emissive term is UNBOUNDED
    // (EmissiveFactor is raw HDR, added outside the albedo<=0.9 product), and the luma clamp alone can leave a
    // single channel > fp16 max → a finite +Inf store into the half-res RGBA16F ssrTarget that the SSR combine
    // would spread (no read-side scrub there). The min() caps at the source.
    float luma = dot(radiance, float3(0.2126, 0.7152, 0.0722));
    if (luma > 1.0e5) radiance *= 1.0e5 / max(luma, 1e-4);
    p.Color = Sanitize(min(radiance, 60000.0.xxx));
}
