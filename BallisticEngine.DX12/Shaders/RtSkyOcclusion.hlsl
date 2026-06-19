// RT sky-occlusion (inline RayQuery, SM 6.6 compute) — the "lightless closed interior glows from IBL" fix.
//
// The IBL ambient term lights every surface with the sky irradiance regardless of whether that surface can
// actually SEE the sky. In a closed interior (e.g. SunTemple) this floods the room with skylight even though
// no light reaches it — screen-space GTAO can't fix it (its ~2 m radius can't tell a sealed hall from an open
// arcade). This pass casts a few cosine-hemisphere rays per pixel against the scene TLAS and measures the
// fraction that ESCAPE to the sky (miss = open, hit = occluded). That sky-visibility is multiplied INTO the
// existing AO target, so the deferred pass's IBL-ambient * AO term is automatically gated by real openness —
// a sealed room goes dark, an open arcade stays lit, with no change to the deferred shader.
//
// Bound: TLAS t0, depth t1, world-normal t2; AO UAV u0 (read-modify-write the GTAO result); constants b0.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth   : register(t1);
Texture2D<float4> Normal  : register(t2);   // world normal packed [0,1]
Texture2D<float>  AoIn    : register(t3);   // GTAO result (SRV read; 1 = unoccluded)
RWTexture2D<float> AoOut  : register(u0);   // own target: AoIn * sky-vis (copied back into GTAO's AO afterwards)

cbuffer RtaoConstants : register(b0) {
    float4x4 InvViewProj;   // screen+depth -> world (transposed on upload)
    float2 TexelSize;       // 1/width, 1/height of the AO target
    float  RayLength;       // world-space max occluder distance (beyond this = "open to sky")
    float  NormalBias;      // ray-origin offset along the normal (acne)
    float  RayCount;        // hemisphere rays per pixel (clamped 1..16)
    float  Intensity;       // 0 = no effect (AO unchanged), 1 = full sky-vis gate
    float  FrameIndex;      // per-pixel rotation seed (frozen under deterministic capture)
    float  Pad;
};

float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

// Cosine-weighted hemisphere sample around +Z (local). The cosine weight is folded into the average by
// construction (more rays near the normal), so a uniform mean of escape/occlude already approximates the
// cosine-weighted sky visibility — exactly the term that gates Lambertian IBL diffuse.
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
    return float3x3(t, b, n);   // rows; mul(localDir, basis) maps +Z -> n
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    uint W = (uint)round(1.0 / TexelSize.x), H = (uint)round(1.0 / TexelSize.y);
    if (px.x >= W || px.y >= H) return;

    float depth = Depth[px];
    if (depth >= 1.0) return;                 // sky pixel: leave AO unchanged (no ambient surface here)

    float3 nWorld = Normal[px].rgb * 2.0 - 1.0;
    if (dot(nWorld, nWorld) < 0.1) return;    // unshaded: leave AO unchanged
    float3 N = normalize(nWorld);

    float2 uv = (float2(px) + 0.5) * TexelSize;
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 wp = mul(ndc, InvViewProj);
    float3 worldPos = wp.xyz / wp.w;

    uint rays = (uint)clamp(RayCount, 1.0, 16.0);
    float jitter = Hash(px.x * 73856093u ^ px.y * 19349663u ^ (uint)FrameIndex * 2654435761u);
    float3x3 basis = BuildBasis(N);
    float3 origin = worldPos + N * NormalBias;

    float open = 0.0;
    [loop] for (uint i = 0; i < rays; i++) {
        float3 local = CosineHemisphere(i, rays, jitter);
        float3 dir = normalize(mul(local, basis));

        RayDesc rd;
        rd.Origin = origin; rd.Direction = dir; rd.TMin = 0.0; rd.TMax = max(RayLength, 0.1);
        RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> q;
        q.TraceRayInline(Scene, 0, 0xFF, rd);
        q.Proceed();
        if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT) open += 1.0;   // escaped within RayLength -> sees sky
    }
    float skyVis = open / float(rays);

    // Multiply sky-visibility INTO the existing AO (read-modify-write). Intensity lerps from "AO unchanged"
    // (0) to "full sky-vis gate" (1) so it's a tunable, opt-in dial. A sealed receiver (skyVis~0) drops its
    // IBL ambient to ~0; an open one (skyVis~1) is untouched.
    float ao = AoIn[px];
    AoOut[px] = ao * lerp(1.0, skyVis, saturate(Intensity));
}
