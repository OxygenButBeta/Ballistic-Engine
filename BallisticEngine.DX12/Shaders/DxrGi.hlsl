// Ray-traced one-bounce global illumination gather (lib_6_6) — the P1 WORLD-RADIANCE hit shading.
// Per G-buffer pixel, cosine-sample ONE hemisphere ray about the world normal (rotated per frame), trace
// against the scene BVH, and at the hit shade REAL WORLD-SPACE RADIANCE:
//
//     hitRadiance = albedo * ( SunColor * saturate(dot(Ng, SunDir)) * shadowRay  +  IBL.Sample(Ng) )
//
// where Ng is the hit's INTERPOLATED geometric normal and `albedo` is the hit's textured base color.
// This is the published DDGI/RTXGI 1-bounce estimator (NOT the recursive probe term). It REPLACES the old
// screen-color hit sample, so off-screen bounce + color bleed are now CORRECT (a red wall lights the floor
// it can't see on screen). Lumen's default samples a pre-lit surface cache instead of re-shading per hit;
// per-hit re-shading is Lumen's "Hit Lighting" mode — the correct interim until the P2 surface cache lands,
// and the bindless-geometry-at-hit plumbing here is REUSED verbatim by the surface cache + RT reflections.
//
// Bound (global root sig): TLAS t0, depth t1, world-normal t2, irradiance cube t3, lit scene t4 (unused now),
// output UAV u0, GiConstants b0, RtGiSun b1, GpuMaterials t5 (root SRV), RtInstance[] t6 (root SRV),
// + bindless heap (ResourceDescriptorHeap[] for the per-instance index/normal/uv buffers + albedo textures),
// + static clamp sampler s0 + wrap sampler s1.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth     : register(t1);
Texture2D<float4> Normal    : register(t2);   // primary-surface world normal packed [0,1]
TextureCube Irradiance      : register(t3);   // sky/IBL irradiance — the hit's ambient term
Texture2D<float4> SceneColor : register(t4);  // (kept bound; unused now the hit re-shades world-space)
RWTexture2D<float4> Output  : register(u0);

cbuffer GiConstants : register(b0) {
    float4x4 InvViewProj;          // screen+depth → world (JITTERED, transposed)
    float4x4 ViewProj;             // world → clip (JITTERED, transposed)
    float4 Params;                 // x=PreExposure y=RayLength z=(unused) w=FrameIndex
};
cbuffer RtGiSun : register(b1) {
    float3 SunDir;     float NormalBias;   // TO the sun (normalized), world; bias = shadow-ray origin offset
    float3 SunColor;   float LightCount;   // sun radiance, RAW HDR (NOT pre-exposed); # punctual lights
};

// Punctual lights (point/spot) — byte-identical to Dx12ClusteredLights.GpuLight. The hit shader loops ALL
// of them (no froxel grid: that's view-space, but GI hits are off-screen). Counts are small in practice.
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; };
StructuredBuffer<GpuLight> Lights : register(t7);
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

// --- Bindless geometry + material (byte-identical decode to GBufferBindless.hlsl) ---
struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; };   // bindless heap indices per TLAS instance
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor;
    float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
StructuredBuffer<GpuMaterial> GpuMaterials : register(t5);
StructuredBuffer<RtInstance>  RtInstances  : register(t6);

struct GiPayload { float3 Color; };

float3 Sanitize(float3 v) {   // ternary component-select — never mix(v,0,flag) (NaN*0==NaN; the proven AMD bug)
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

float2 Hash2(uint2 p, uint f) {
    uint n = p.x * 1973u + p.y * 9277u + f * 26699u;
    n = (n << 13) ^ n;
    n = n * (n * n * 15731u + 789221u) + 1376312589u;
    float a = float(n & 0x7fffffffu) / float(0x7fffffff);
    n = n * 1664525u + 1013904223u;
    float b = float(n & 0x7fffffffu) / float(0x7fffffff);
    return float2(a, b);
}

float3 CosineSampleHemisphere(float3 n, float2 xi) {
    float r = sqrt(xi.x);
    float phi = 6.2831853 * xi.y;
    float3 t = normalize(abs(n.x) > 0.9 ? cross(n, float3(0, 1, 0)) : cross(n, float3(1, 0, 0)));
    float3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - xi.x)));
}

// Inline shadow/visibility ray (RayQuery — no recursion, so the PSO stays MaxTraceRecursionDepth=1).
// Returns 1 lit / 0 occluded. Origin offset along the hit normal kills self-shadow acne. maxDist bounds it
// to the light distance for punctual (a wall PAST the light shouldn't shadow it).
float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray;
    ray.Origin = origin + N * max(NormalBias, 0.001);
    ray.Direction = dir;
    ray.TMin = 0.02;
    ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray);
    q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// Diffuse irradiance from all punctual lights at a hit (Lambert N·L × radiance × range-window × spot cone,
// shadow-rayed). No specular (this is a DIFFUSE GI bounce). Mirrors DeferredLighting's punctual diffuse.
float3 PunctualDiffuse(float3 hit, float3 N) {
    float3 sum = 0.0.xxx;
    int n = min((int)LightCount, 64);
    [loop] for (int i = 0; i < n; i++) {
        GpuLight L = Lights[i];
        float3 toL = L.PosRange.xyz - hit;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;          // range cull
        float3 Ld = toL / dist;
        float ndl = saturate(dot(N, Ld));
        if (ndl <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));    // smooth range window (GL parity)
        float atten = t * t / max(dist * dist, 1e-4);
        float3 radiance = L.Color.rgb * atten;
        if (L.Color.w >= 0.5) {                                      // spot cone
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            radiance *= cone * cone;
        }
        sum += radiance * ndl * Visibility(hit, N, Ld, dist);       // diffuse Lambert (albedo applied by caller)
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
    if (depth >= 1.0) return;
    float3 worldN = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(worldN, worldN) < 0.1) return;

    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    float3 worldPos = w.xyz / w.w;
    float3 N = normalize(worldN);

    float2 xi = Hash2(idx, (uint)Params.w);
    float3 dir = CosineSampleHemisphere(N, xi);

    GiPayload p;
    p.Color = 0.0.xxx;
    RayDesc ray;
    ray.Origin = worldPos + N * 0.05;
    ray.Direction = dir;
    ray.TMin = 0.02;
    ray.TMax = max(Params.y, 0.1);
    TraceRay(Scene, RAY_FLAG_FORCE_OPAQUE, 0xFF, 0, 1, 0, ray, p);

    // Cosine-weighted 1-sample estimator: the temporal pass averages the per-frame rotated samples.
    Output[idx] = float4(Sanitize(p.Color), 1.0);
}

[shader("miss")]
void Miss(inout GiPayload p) { p.Color = 0.0.xxx; }   // sky = no bounce (IBL ambient already counts it)

[shader("closesthit")]
void ClosestHit(inout GiPayload p, in BuiltInTriangleIntersectionAttributes attr) {
    // --- Fetch the hit triangle's interpolated normal + UV from the bindless per-instance geometry buffers ---
    RtInstance inst = RtInstances[InstanceID()];
    Buffer<uint>            indices = ResourceDescriptorHeap[inst.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[inst.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[inst.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[inst.TriMatIdx];

    uint prim = PrimitiveIndex();
    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float3 bary = float3(1.0 - attr.barycentrics.x - attr.barycentrics.y, attr.barycentrics.x, attr.barycentrics.y);

    // Object-space interpolated normal → world (ObjectToWorld3x4 for the instance transform).
    float3 nObj = normalize(normals[i0] * bary.x + normals[i1] * bary.y + normals[i2] * bary.z);
    float3 Ng = normalize(mul((float3x3)ObjectToWorld3x4(), nObj));
    // Two-sided: face the bounce ray's incoming side so a backface hit still lights correctly.
    if (dot(Ng, WorldRayDirection()) > 0.0) Ng = -Ng;

    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;

    // --- Albedo from the bindless material table (matches GBufferBindless decode) ---
    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb;
    albedo = min(albedo, 0.9.xxx);   // energy clamp (DDGI maxAlbedo) — no runaway in the feedback loop

    float3 hit = WorldRayOrigin() + RayTCurrent() * WorldRayDirection();

    // --- Direct light at the hit (sun + punctual, each shadow-ray-occluded) + sky IBL ambient. SunColor +
    //     light radiance are RAW HDR; preExp is applied to the WHOLE GI output below, so don't pre-scale. ---
    float ndl = saturate(dot(Ng, normalize(SunDir)));
    float3 sun = SunColor * ndl * (ndl > 0.0 ? Visibility(hit, Ng, normalize(SunDir), 1e4) : 0.0);
    float3 punctual = PunctualDiffuse(hit, Ng);                        // point/spot lights (e.g. the Bistro lamp)
    float3 ambient = Irradiance.SampleLevel(LinearClamp, Ng, 0).rgb;   // diffuse sky irradiance at the hit
    float3 radiance = albedo * (sun + punctual + ambient);

    // Soft luminance clamp (NOT saturate — that would crush the ~1e5 HDR). Tame fireflies before the shared
    // temporal+OIDN feedback chain. Then pre-expose like the old hit shading (Params.x).
    float luma = dot(radiance, float3(0.2126, 0.7152, 0.0722));
    float maxLuma = 1.0e5;
    if (luma > maxLuma) radiance *= maxLuma / max(luma, 1e-4);
    p.Color = Sanitize(radiance) * Params.x;
}
