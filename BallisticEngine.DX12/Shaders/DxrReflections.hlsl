// Ray-traced reflections (lib_6_x). One reflection ray per pixel from the G-buffer surface: reconstruct
// world pos + view dir, reflect about the normal, trace against the scene BVH. Miss = the prefiltered sky/
// IBL cube along the ray (roughness-mipped); closest-hit = a simple ambient-grey shade of the off-screen
// geometry (full per-instance material shade via bindless geometry is the quality follow-up). Writes (rgb
// reflected color, a strength) into the half-res SSR reflection target — the SAME contract as Ssr.hlsl's
// march — so the existing SSR combine (depth-aware upsample + Fresnel lerp) mixes it into the scene. Mirror
// rays are deterministic (no noise → no denoise; jittered rough reflections + OIDN are a follow-up).
//
// Bound (global root sig): TLAS t0, depth t1, world-normal t2, material t3, irradiance cube t4, prefilter
// cube t5, output UAV u0, ReflConstants b0, static linear-clamp sampler s0.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth    : register(t1);
Texture2D<float4> Normal   : register(t2);   // world normal packed [0,1]
Texture2D<float4> Material : register(t3);   // r metallic, g roughness
TextureCube Irradiance     : register(t4);
TextureCube Prefilter      : register(t5);
RWTexture2D<float4> Output  : register(u0);

cbuffer ReflConstants : register(b0) {
    float4x4 InvViewProj;    // screen+depth → world (JITTERED, transposed)
    float3 CameraPos; float Intensity;
    float PrefilterMaxMip; float NormalBias; float2 Pad;
};
SamplerState LinearClamp : register(s0);

static const float MAX_ROUGHNESS = 0.6;
struct ReflPayload { float3 Color; float Roughness; };

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

    Output[idx] = float4(p.Color, strength);
}

[shader("miss")]
void Miss(inout ReflPayload p) {
    // Reflection ray escaped → the sky/IBL in that direction (roughness-blurred via the prefilter mips).
    float mip = clamp(p.Roughness * PrefilterMaxMip, 0.0, PrefilterMaxMip);
    p.Color = Prefilter.SampleLevel(LinearClamp, WorldRayDirection(), mip).rgb;
}

[shader("closesthit")]
void ClosestHit(inout ReflPayload p, in BuiltInTriangleIntersectionAttributes attr) {
    // Simple shade: off-screen geometry reflects as ambient-lit grey (env irradiance along the ray × a
    // mid albedo). The RT win here is correct occlusion/visibility the screen-space march can't give;
    // full per-instance material shading (bindless geometry + lights at the hit) is the quality follow-up.
    p.Color = Irradiance.SampleLevel(LinearClamp, WorldRayDirection(), 0).rgb * 0.5;
}
