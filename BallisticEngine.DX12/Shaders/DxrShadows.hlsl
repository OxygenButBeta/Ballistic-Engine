// Ray-traced sun shadows (lib_6_x). For each G-buffer pixel: reconstruct world position from depth, offset
// along the surface normal (acne bias), and trace one OR MANY shadow rays toward the sun. Closest-hit =
// occluded; miss = lit. Writes a soft shadow mask (1 = lit, 0 = shadowed) the deferred lighting multiplies
// into the sun term — sharp contact-accurate shadows with no cascade peter-panning.
//
// SOFT PENUMBRA: the sun is not a point — it subtends an angular radius (SunAngularRadius, from the
// DirectionalLight's AngularDiameter). We cone-sample RayCount rays in a disk of that angular radius around
// SunDir, each rotated per-pixel by an interleaved-gradient rotation + the frame index (temporal variation),
// and average the hit fraction → a soft mask in [0,1]. A spatial bilateral denoise (Dx12RtShadowDenoise.hlsl)
// cleans the few-ray noise afterwards. The HARD path (RayCount<=1 OR SunAngularRadius~0) is the EXACT old
// single-ray result — bit-identical fast path, no jitter, no basis math.
//
// Bound: TLAS t0, depth t1, world-normal t2, mask UAV u0, ShadowConstants b0 (global root sig).

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float> Depth   : register(t1);
Texture2D<float4> Normal : register(t2);   // world normal packed [0,1]
RWTexture2D<float> ShadowMask : register(u0);

cbuffer ShadowConstants : register(b0) {
    float4x4 InvViewProj;     // screen+depth → world (transposed on upload)
    float3 SunDir;            // TO the sun, world space, normalized
    float NormalBias;         // world-space ray-origin offset along the normal (acne)
    float SunAngularRadius;   // radians; 0.5 * AngularDiameter. 0 (or RayCount<=1) → hard fast path.
    int   RayCount;           // shadow rays per pixel (1 = hard; soft cone uses up to 32)
    int   FrameIndex;         // temporal jitter rotation (frozen to 0 under deterministic capture)
    float Pad0;
};

struct ShadowPayload { uint Occluded; };

#define MAX_SHADOW_RAYS 32

// Hammersley low-discrepancy 2D point (van der Corput radical inverse on the y axis).
float2 Hammersley(uint i, uint n) {
    uint bits = i;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    float rdi = float(bits) * 2.3283064365386963e-10; // / 0x100000000
    return float2(float(i) / float(n), rdi);
}

// Interleaved-gradient noise → a per-pixel rotation angle, animated by the frame index. Magic constants from
// Jimenez "Next Generation Post Processing in Call of Duty: Advanced Warfare".
float InterleavedGradientNoise(float2 pix, int frame) {
    pix += float2(frame * 5.588238, frame * 5.588238) * 0.7548776662; // decorrelate per frame
    return frac(52.9829189 * frac(0.06711056 * pix.x + 0.00583715 * pix.y));
}

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
    float3 sun = normalize(SunDir);

    RayDesc ray;
    ray.Origin = worldPos + N * NormalBias;
    ray.TMin = 0.01;
    ray.TMax = 1e4;

    // HARD fast path — bit-identical to the original single-ray shadow (no jitter, no basis math).
    if (RayCount <= 1 || SunAngularRadius <= 1e-5) {
        ray.Direction = sun;
        ShadowPayload p; p.Occluded = 0;
        TraceRay(Scene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE,
                 0xFF, 0, 1, 0, ray, p);
        ShadowMask[idx] = p.Occluded != 0 ? 0.0 : 1.0;
        return;
    }

    // SOFT path: build an orthonormal basis around the sun direction, cone-sample a disk of angular radius
    // SunAngularRadius, jitter the sample set by a per-pixel rotation (IGN) for spatial decorrelation.
    float3 up = abs(sun.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, sun));
    float3 bitangent = cross(sun, tangent);

    float rot = InterleavedGradientNoise(float2(idx), FrameIndex) * 6.2831853; // [0, 2π)
    float cs = cos(rot), sn = sin(rot);
    float tanR = tan(SunAngularRadius);

    int rays = min(RayCount, MAX_SHADOW_RAYS);
    float litSum = 0.0;
    [loop] for (int s = 0; s < rays; ++s) {
        float2 h = Hammersley((uint)s, (uint)rays);
        // concentric-ish disk sample (sqrt for area-uniform radius), rotated per pixel
        float r = sqrt(saturate(h.x));
        float ang = h.y * 6.2831853;
        float2 disk = float2(cos(ang), sin(ang)) * r;
        float2 d = float2(disk.x * cs - disk.y * sn, disk.x * sn + disk.y * cs); // rotate
        // perturb the sun direction inside the angular cone
        float3 dir = normalize(sun + (tangent * d.x + bitangent * d.y) * tanR);

        ray.Direction = dir;
        ShadowPayload p; p.Occluded = 0;
        TraceRay(Scene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE,
                 0xFF, 0, 1, 0, ray, p);
        litSum += (p.Occluded != 0) ? 0.0 : 1.0;
    }

    ShadowMask[idx] = litSum / float(rays);
}

[shader("miss")]
void Miss(inout ShadowPayload p) { p.Occluded = 0; }   // reached the sun → lit

[shader("closesthit")]
void ClosestHit(inout ShadowPayload p, in BuiltInTriangleIntersectionAttributes attr) { p.Occluded = 1; }
