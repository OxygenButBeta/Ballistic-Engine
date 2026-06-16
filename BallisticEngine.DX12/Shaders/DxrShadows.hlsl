// Ray-traced sun shadows (lib_6_x). One shadow ray per G-buffer pixel toward the sun: reconstruct world
// position from depth, offset along the surface normal (acne bias), trace toward the light. Closest-hit =
// occluded; miss = lit. Writes a shadow mask (1 = lit, 0 = shadowed) the deferred lighting multiplies into
// the sun term — sharp, contact-accurate shadows with no cascade peter-panning. Hard shadows are
// deterministic (1 ray, no noise → no denoise); soft penumbra (cone-sampled sun angular size + OIDN) is a
// follow-up. Bound: TLAS t0, depth t1, world-normal t2, mask UAV u0, ShadowConstants b0 (global root sig).

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float> Depth   : register(t1);
Texture2D<float4> Normal : register(t2);   // world normal packed [0,1]
RWTexture2D<float> ShadowMask : register(u0);

cbuffer ShadowConstants : register(b0) {
    float4x4 InvViewProj;   // screen+depth → world (transposed on upload)
    float3 SunDir;          // TO the sun, world space, normalized
    float NormalBias;       // world-space ray-origin offset along the normal (acne)
};

struct ShadowPayload { uint Occluded; };

[shader("raygeneration")]
void RayGen() {
    uint2 idx = DispatchRaysIndex().xy;
    uint2 dim = DispatchRaysDimensions().xy;
    float depth = Depth[idx];
    if (depth >= 1.0) { ShadowMask[idx] = 1.0; return; }   // sky: unoccluded

    float2 uv = (float2(idx) + 0.5) / float2(dim);
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    float3 worldPos = w.xyz / w.w;
    float3 N = normalize(Normal[idx].rgb * 2.0 - 1.0);

    RayDesc ray;
    ray.Origin = worldPos + N * NormalBias;
    ray.Direction = normalize(SunDir);
    ray.TMin = 0.01;
    ray.TMax = 1e4;

    ShadowPayload p;
    p.Occluded = 0;
    TraceRay(Scene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE,
             0xFF, 0, 1, 0, ray, p);
    ShadowMask[idx] = p.Occluded != 0 ? 0.0 : 1.0;
}

[shader("miss")]
void Miss(inout ShadowPayload p) { p.Occluded = 0; }   // reached the sun → lit

[shader("closesthit")]
void ClosestHit(inout ShadowPayload p, in BuiltInTriangleIntersectionAttributes attr) { p.Occluded = 1; }
