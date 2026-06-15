// Ray-traced one-bounce global illumination gather (lib_6_x) — "RT-traced SSGI". Per G-buffer pixel,
// cosine-sample ONE hemisphere ray about the world normal (rotated per frame), trace against the scene BVH
// for correct occlusion + off-screen visibility, and shade the hit from the LIT SCENE COLOR (project the
// hit back to screen and sample it) — so the bounce carries the surfaces' real lit colours, exactly like
// SSGI, but with ray-traced visibility SSGI can't have. Off-screen hits fall back to the env irradiance;
// MISS returns 0 (the sky is already the IBL ambient — re-adding it would double-count and wash the scene).
// Output goes into the SSGI raw-GI target → the SHARED SSGI pipeline (motion temporal + OIDN + composite)
// cleans + adds it. 1-spp hemisphere is noisy, so temporal+OIDN are essential.
//
// Pre-exposure: the scene is RAW HDR (~1e5); like SSGI we output PRE-EXPOSED radiance so the shared combine
// (which converts back) is consistent. Bound (global root sig): TLAS t0, depth t1, world-normal t2,
// irradiance cube t3, lit scene color t4, output UAV u0, GiConstants b0, static linear-clamp sampler s0.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth     : register(t1);
Texture2D<float4> Normal    : register(t2);
TextureCube Irradiance      : register(t3);   // off-screen-hit fallback
Texture2D<float4> SceneColor : register(t4);  // lit HDR scene (the bounce source)
RWTexture2D<float4> Output  : register(u0);

cbuffer GiConstants : register(b0) {
    float4x4 InvViewProj;          // screen+depth → world (JITTERED, transposed)
    float4x4 ViewProj;             // world → clip (JITTERED, transposed) — project the hit back to screen
    float4 Params;                 // x=PreExposure y=RayLength z=(unused) w=FrameIndex
};
SamplerState LinearClamp : register(s0);

struct GiPayload { float3 Color; };

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

    // Cosine-weighted 1-sample estimator: the diffuse irradiance/pi is the incoming radiance. The temporal
    // pass averages the per-frame rotated samples into the converged bounce.
    Output[idx] = float4(p.Color, 1.0);
}

[shader("miss")]
void Miss(inout GiPayload p) { p.Color = 0.0.xxx; }   // sky is already the IBL ambient — don't double-count

[shader("closesthit")]
void ClosestHit(inout GiPayload p, in BuiltInTriangleIntersectionAttributes attr) {
    float3 hit = WorldRayOrigin() + RayTCurrent() * WorldRayDirection();
    float preExp = Params.x;
    // Project the hit back to screen; if visible, its lit colour IS the bounce radiance (real colours,
    // RT-occluded). Off-screen → env irradiance fallback so enclosed/edge bounce isn't lost.
    float4 clip = mul(float4(hit, 1.0), ViewProj);
    if (clip.w > 0.0) {
        float2 suv = clip.xy / clip.w;
        suv = float2(suv.x * 0.5 + 0.5, 0.5 - suv.y * 0.5);
        if (suv.x >= 0.0 && suv.x <= 1.0 && suv.y >= 0.0 && suv.y <= 1.0) {
            p.Color = SceneColor.SampleLevel(LinearClamp, suv, 0).rgb * preExp;
            return;
        }
    }
    p.Color = Irradiance.SampleLevel(LinearClamp, WorldRayDirection(), 0).rgb * 0.5 * preExp;
}
