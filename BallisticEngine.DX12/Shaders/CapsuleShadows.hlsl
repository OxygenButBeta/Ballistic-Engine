// Capsule shadows: cheap analytic soft sun shadows from character proxy capsules (the Unreal capsule-shadow
// feature). For each screen pixel we reconstruct world position from depth and march the shadow ray TOWARD
// the sun, testing each capsule for soft occlusion: find the closest approach between the shadow ray and the
// capsule's segment, treat the nearest point as an occluding sphere of the capsule radius, and compute its
// soft cone occlusion using the sun's angular radius (a wider sun = softer edge). No ray tracing — pure
// analytic per-capsule math, output an R8 occlusion mask (1 = lit, 0 = fully occluded). The deferred sun term
// multiplies this with the cascade / RT shadow.
//
// Bound: depth t0, world-normal t1, capsule buffer t2 (StructuredBuffer<Capsule>), occlusion UAV u0, CB b0.

cbuffer CapsuleConstants : register(b0) {
    float4x4 InvViewProj;   // screen+depth → world (transposed on upload)
    float3 SunDir;          // TO the sun, world space, normalized
    float SunAngularRadius; // radians (0.5 * AngularDiameter); penumbra width
    int   CapsuleCount;
    float NormalBias;       // world offset along N for the ray origin (self-shadow acne)
    float2 ScreenSize;
};

struct Capsule {
    float3 A; float Radius;   // segment endpoint A + capsule radius
    float3 B; float Pad;      // segment endpoint B
};

Texture2D<float>           Depth     : register(t0);
Texture2D<float4>          Normal    : register(t1);
StructuredBuffer<Capsule>  Capsules  : register(t2);
RWTexture2D<float>         Occlusion : register(u0);

// Closest points between two segments / ray-vs-segment. We treat the shadow as a RAY from the surface toward
// the sun (semi-infinite). Returns the squared distance between the ray and the capsule segment, and the
// parameter `tCap` of the closest point along the capsule segment [0,1].
float RaySegmentClosest(float3 ro, float3 rd, float3 sa, float3 sb, out float3 closestOnSeg, out float tRay) {
    float3 u = rd;            // ray dir (unit)
    float3 v = sb - sa;       // segment dir
    float3 w = ro - sa;
    float a = dot(u, u);      // = 1 (rd unit)
    float b = dot(u, v);
    float c = dot(v, v);
    float d = dot(u, w);
    float e = dot(v, w);
    float denom = a * c - b * b;

    float sc, tc;
    if (denom < 1e-6) {       // near-parallel
        sc = 0.0;
        tc = (c > 1e-6) ? saturate(e / c) : 0.0;
    } else {
        sc = (b * e - c * d) / denom;       // ray param (can be < 0 → clamp to the origin side)
        tc = (a * e - b * d) / denom;       // segment param
        sc = max(sc, 0.0);                  // ray is semi-infinite forward only
        tc = saturate(tc);
    }
    tRay = sc;
    closestOnSeg = sa + v * tc;
    float3 pRay = ro + u * sc;
    float3 diff = pRay - closestOnSeg;
    return dot(diff, diff);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dt : SV_DispatchThreadID) {
    uint2 idx = dt.xy;
    if (idx.x >= (uint)ScreenSize.x || idx.y >= (uint)ScreenSize.y) return;

    float depth = Depth[idx];
    if (depth >= 1.0) { Occlusion[idx] = 1.0; return; }   // sky: unoccluded

    float2 uv = (float2(idx) + 0.5) / ScreenSize;
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    float3 worldPos = w.xyz / max(w.w, 1e-6);
    float3 N = normalize(Normal[idx].rgb * 2.0 - 1.0);
    float3 sun = normalize(SunDir);

    float3 ro = worldPos + N * NormalBias;

    // Multiply soft occlusion from each capsule (independent occluders → product). 1 = lit.
    float vis = 1.0;
    int count = min(CapsuleCount, 64);
    [loop] for (int ci = 0; ci < count; ++ci) {
        Capsule cap = Capsules[ci];
        float3 onSeg; float tRay;
        float dist2 = RaySegmentClosest(ro, sun, cap.A, cap.B, onSeg, tRay);
        if (tRay <= 0.0) continue;             // occluder behind the surface along the sun ray

        float dist = sqrt(max(dist2, 1e-8));
        // Angular size of the occluding sphere (capsule radius at the closest distance along the ray) vs the
        // sun's angular radius. occluderAngle = atan(radius / tRay); the penumbra spans ±SunAngularRadius.
        float occluderAngle = atan(cap.Radius / max(tRay, 1e-4));
        // Angular offset of the occluder centre from the sun direction (how far off-axis the sphere sits).
        float centerAngle = atan(dist / max(tRay, 1e-4));
        // Soft transition: fully occluded when the occluder fully covers the sun (centerAngle + sunR <
        // occluderAngle); fully lit when they don't touch (centerAngle - sunR > occluderAngle). smoothstep
        // the penumbra band between. (Approximate disk-overlap, the standard capsule-shadow model.)
        float lo = occluderAngle - SunAngularRadius;
        float hi = occluderAngle + SunAngularRadius;
        // capVis = 1 at/above hi (centre far enough out → lit), 0 at/below lo (centre inside → occluded).
        float capVis = smoothstep(lo, hi, centerAngle);
        vis *= capVis;
    }

    Occlusion[idx] = saturate(vis);
}
