// Minimal DXR self-test library (lib_6_3). Proves the whole ray-tracing pipeline end-to-end: a ray-gen
// shoots one ray per output pixel along +Z into a single-triangle BLAS/TLAS; the closest-hit writes RED,
// the miss writes BLUE. The C# probe (Dx12DxrProbe) reads the UAV back and checks both colors appear —
// confirming AS build + RT PSO + shader binding table + DispatchRays all work before real RT effects.

RaytracingAccelerationStructure Scene : register(t0);
RWTexture2D<float4> Output : register(u0);

struct Payload { float3 Color; };

[shader("raygeneration")]
void RayGen() {
    uint2 idx = DispatchRaysIndex().xy;
    uint2 dim = DispatchRaysDimensions().xy;
    float2 uv = (float2(idx) + 0.5) / float2(dim);

    RayDesc ray;
    ray.Origin = float3(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0);  // [-1,1] plane at z=0
    ray.Direction = float3(0.0, 0.0, 1.0);                          // toward the triangle at z=1
    ray.TMin = 0.001;
    ray.TMax = 100.0;

    Payload p;
    p.Color = float3(0, 0, 0);
    TraceRay(Scene, RAY_FLAG_NONE, 0xFF, 0, 1, 0, ray, p);
    Output[idx] = float4(p.Color, 1.0);
}

[shader("miss")]
void Miss(inout Payload p) { p.Color = float3(0.0, 0.0, 1.0); }   // blue = ray missed

[shader("closesthit")]
void ClosestHit(inout Payload p, in BuiltInTriangleIntersectionAttributes attr) {
    p.Color = float3(1.0, 0.0, 0.0);   // red = ray hit the triangle
}
