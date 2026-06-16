// Ray-traced reflections (lib_6_6) — GI plan PHASE 8: reflections via the WORLD CACHE.
// One reflection ray per pixel from the G-buffer surface: reconstruct world pos + view dir, reflect about the
// normal, trace against the scene BVH. Miss = the prefiltered sky/IBL cube along the ray (roughness-mipped);
// CLOSEST-HIT now shades REAL WORLD-SPACE RADIANCE (this is the Phase-8 win) — the SAME estimator the diffuse
// GI uses (DxrGi.hlsl ClosestHit / DdgiTrace.hlsl ShadeHit, byte-identical math):
//
//     hitRadiance = albedo * ( SunColor*saturate(dot(Ng,SunDir))*shadowRay + punctual(shadow-rayed) + ambient )
//
// where `ambient` = the DDGI world-cache IRRADIANCE field at the hit (SampleIrradianceField, sampled along the
// hit normal Ng) when the cache is bound, else the flat IBL irradiance cube (graceful no-DDGI fallback). So a
// reflected wall shows its OWN multi-bounce GI from the SAME world cache the diffuse pass reads — Lumen's "Hit
// Lighting" reflection feeding the world radiance cache, unifying GI + reflections. This REPLACES the old
// ambient-grey placeholder (Irradiance*0.5). NO new rays vs the placeholder: the reflection ray was already
// traced; only the hit shading changed (it now fires sun + punctual shadow rays + one cache read at the hit).
//
// IMPORTANT energy notes (verified against ScreenProbeTrace.hlsl's /PI fix):
//   - The ambient is the hit's DIFFUSE irradiance E and we form albedo*E directly (NOT E/PI) — this matches
//     DdgiTrace.ShadeHit's ambient construction exactly (the field is RAW HDR irradiance; the gather forms
//     albedo*E). The /PI in ScreenProbeTrace's MISS path is for an E->L-along-ONE-ray conversion that the blend
//     then cosine-re-integrates; that does NOT apply here (we use E as the receiver's hemisphere irradiance).
//   - The DDGI field ALREADY folds in the sky (open-sky probes sample the cube), so we do NOT add the IBL cube
//     on top when DDGI is bound — that would double-count the sky.
//
// Mirror rays only (R = reflect(-V,N), no jitter) → deterministic → NO denoiser needed (byte-identical capture).
// Writes (rgb reflected color, a strength) into the half-res SSR reflection target — the SAME contract as
// Ssr.hlsl's march — so the existing SSR combine (depth-aware upsample + Fresnel lerp) mixes it into the scene.
// roughFade still tapers reflections to 0 at MAX_ROUGHNESS; the diffuse GI carries near-diffuse roughness (no
// rough-tail field-along-R term — that path needs /PI AND double-counts where diffuse GI already lights).
//
// Bound (global root sig, HeapDirectlyIndexed): TLAS t0, depth t1, world-normal t2, material t3, irradiance
// cube t4, prefilter cube t5, DDGI irradiance atlas t6, output UAV u0, ReflConstants b0, RtGiSun b1,
// DdgiGrid b2; root SRVs GpuMaterials t7 / RtInstance[] t8 / Lights t9 / ProbeState t10; bindless heap
// (ResourceDescriptorHeap[] for per-instance index/normal/uv buffers + albedo textures); static clamp s0 + wrap s1.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth    : register(t1);
Texture2D<float4> Normal   : register(t2);   // world normal packed [0,1]
Texture2D<float4> Material : register(t3);   // r metallic, g roughness
TextureCube Irradiance     : register(t4);   // sky/IBL irradiance (miss = open-sky ambient; hit fallback w/o DDGI)
TextureCube Prefilter      : register(t5);   // roughness-mipped sky/IBL radiance (the reflection-ray MISS color)
Texture2D<float4> DdgiIrr   : register(t6);  // PHASE 8: the DDGI world-cache irradiance atlas (hit ambient)
RWTexture2D<float4> Output  : register(u0);

cbuffer ReflConstants : register(b0) {
    float4x4 InvViewProj;    // screen+depth → world (JITTERED, transposed)
    float3 CameraPos; float Intensity;
    float PrefilterMaxMip; float NormalBias; float UseDdgi; float Pad0;  // UseDdgi: 1=sample cache at hits; Pad0=emissiveEnable (1=add emissive L_e at hits)
};
cbuffer RtGiSun : register(b1) {
    float3 SunDir;     float SunNormalBias;   // TO the sun (normalized), world; bias = shadow-ray origin offset
    float3 SunColor;   float LightCount;      // sun radiance, RAW HDR (NOT pre-exposed); # punctual lights
};
// DDGI grid description (byte-identical to Dx12Ddgi.DdgiConstants / the gather's b0) — for SampleIrradianceField.
cbuffer DdgiGrid : register(b2) {
    float4 OriginSpacingX;   // xyz grid origin (world), w spacing.x
    float4 SpacingYZ;        // x spacing.y, y spacing.z
    float4 ProbeDims;        // xyz (ProbesX,ProbesY,ProbesZ), w ProbeCount
    float4 GParams0;         // x irrTexels, y depthTexels, z hysteresis, w frameIndex
    float4 GParams1;         // x maxRayDist, y normalBias, z feedbackEnable, w intensity
    float4 GParams2;         // round-robin (unused here)
};
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

// --- Bindless geometry + material (byte-identical decode to GBufferBindless.hlsl / DxrGi.hlsl) ---
struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; };
StructuredBuffer<GpuMaterial> GpuMaterials : register(t7);
StructuredBuffer<RtInstance>  RtInstances  : register(t8);
StructuredBuffer<GpuLight>    Lights       : register(t9);
StructuredBuffer<float4>      ProbeState   : register(t10);   // P2.4: per-probe (relocation offset.xyz, active)

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

// Diffuse irradiance from all punctual lights at a hit (mirrors DxrGi.hlsl / DdgiTrace.hlsl PunctualDiffuse).
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

// --- DDGI world-cache sample (byte-identical to DdgiTrace.hlsl SampleIrradianceField / DdgiGather tile layout).
// Trilinear over the 8 enclosing probes × cosine front-facing wrap; sampled along the hit normal N (the diffuse
// receiver hemisphere). Returns RAW HDR irradiance E at the world point (the hit's ambient = its multi-bounce GI).
float2 OctEncode(float3 dir) {
    dir /= (abs(dir.x) + abs(dir.y) + abs(dir.z));
    float2 uv = dir.xy;
    if (dir.z < 0.0)
        uv = (1.0 - abs(uv.yx)) * float2(uv.x >= 0.0 ? 1.0 : -1.0, uv.y >= 0.0 ? 1.0 : -1.0);
    return uv * 0.5 + 0.5;
}
float3 ProbePos(uint px, uint py, uint pz) {
    float3 basePos = OriginSpacingX.xyz + float3(px * OriginSpacingX.w, py * SpacingYZ.x, pz * SpacingYZ.y);
    uint probe = (pz * (uint)ProbeDims.y + py) * (uint)ProbeDims.x + px;   // matches DdgiTrace ProbeWorldPos flatten
    return basePos + ProbeState[probe].xyz;
}
float ProbeActive(uint px, uint py, uint pz) {
    uint probe = (pz * (uint)ProbeDims.y + py) * (uint)ProbeDims.x + px;
    return ProbeState[probe].w;
}
float3 SampleDdgiField(float3 worldPos, float3 N) {
    float3 spacing = float3(OriginSpacingX.w, SpacingYZ.x, SpacingYZ.y);
    float3 biasPos = worldPos + N * GParams1.y;
    float3 rel = (biasPos - OriginSpacingX.xyz) / spacing;
    int3 baseC = (int3)floor(rel);
    float3 f = rel - (float3)baseC;
    int3 dims = int3((int)ProbeDims.x, (int)ProbeDims.y, (int)ProbeDims.z);
    uint irrTexels = (uint)GParams0.x;
    uint tile = irrTexels + 2u;          // +2*BORDER (BORDER=1, must match DdgiBlend/DdgiGather)
    float2 atlasSize = float2((uint)ProbeDims.x * (uint)ProbeDims.z, (uint)ProbeDims.y) * float(tile);
    float2 octI = OctEncode(N);          // sample along the surface normal (diffuse receiver)

    float3 sum = 0.0.xxx; float wsum = 0.0;
    [unroll] for (int i = 0; i < 8; i++) {
        int3 off = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int3 c = baseC + off;
        if (any(c < 0) || any(c >= dims)) continue;
        uint cx = (uint)c.x, cy = (uint)c.y, cz = (uint)c.z;
        if (ProbeActive(cx, cy, cz) < 0.5) continue;   // skip buried/inactive probes (P2.4)
        float3 toProbe = ProbePos(cx, cy, cz) - biasPos;
        float3 dirToProbe = dot(toProbe, toProbe) > 1e-10 ? normalize(toProbe) : N;
        float3 triv = lerp(1.0 - f, f, (float3)off);
        float trilinear = triv.x * triv.y * triv.z;
        float wrap = saturate(dot(dirToProbe, N) * 0.5 + 0.5); wrap = wrap * wrap + 0.2;
        float w = trilinear * wrap;
        if (w < 1e-6) continue;
        uint col = cz * (uint)ProbeDims.x + cx, row = cy;
        float2 texelXY = float2(col * tile, row * tile) + 1.0 + octI * float(irrTexels);
        float3 irr = DdgiIrr.SampleLevel(LinearClamp, texelXY / atlasSize, 0).rgb;
        sum += Sanitize(irr) * w; wsum += w;
    }
    return wsum > 1e-5 ? sum / wsum : 0.0.xxx;
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
    // PHASE 8 — full world-space radiance at the reflection hit (sun + punctual + DDGI-field ambient), the same
    // estimator the diffuse GI uses. Fetch the hit triangle's interpolated normal + UV from the bindless
    // per-instance geometry buffers (byte-identical to DxrGi.hlsl ClosestHit / GBufferBindless decode).
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

    // Emissive self-emission L_e (emissive-as-GI-source): an emissive surface seen IN a reflection lights up
    // (neon in a mirror). Byte-identical decode to GBufferBindless (emissiveMap*EmissiveFactor, gated on
    // HasEmissive); added OUTSIDE the albedo product (no /PI, no albedo multiply). Gated by Pad0 (emissiveEnable).
    float3 emissive = 0.0.xxx;
    if (Pad0 > 0.5 && m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    float3 hit = WorldRayOrigin() + RayTCurrent() * WorldRayDirection();

    // Direct light at the hit (sun + punctual, each shadow-rayed). RAW HDR — the SSR combine does NOT pre-expose
    // the reflection color, so unlike the GI gather this stays in scene-radiance units (the prefilter-cube MISS
    // color is raw HDR too, so hit + miss share the same scale into the depth-aware Fresnel-lerp combine).
    float ndl = saturate(dot(Ng, normalize(SunDir)));
    float3 sun = SunColor * ndl * (ndl > 0.0 ? Visibility(hit, Ng, normalize(SunDir), 1e4) : 0.0);
    float3 punctual = PunctualDiffuse(hit, Ng);

    // Ambient at the hit = the DDGI world cache (its OWN multi-bounce GI; the field already folds in sky, so do
    // NOT add the IBL cube on top when DDGI is bound). Falls back to the flat IBL irradiance cube without DDGI.
    float3 ambient = UseDdgi > 0.5 ? SampleDdgiField(hit, Ng) : Irradiance.SampleLevel(LinearClamp, Ng, 0).rgb;
    float3 radiance = albedo * (sun + punctual + ambient) + emissive;

    // Soft luminance clamp (NOT saturate — that crushes the ~1e5 HDR). Tame fireflies, then ternary Sanitize.
    // Per-channel cap below the fp16 ceiling (~65504) BEFORE Sanitize: the +emissive term is UNBOUNDED
    // (EmissiveFactor is raw HDR, added outside the albedo<=0.9 product), and the luma clamp alone can leave a
    // single channel > fp16 max → a finite +Inf store into the half-res RGBA16F ssrTarget that the SSR combine
    // would spread (no read-side scrub there). The min() caps at the source. Matches DdgiTrace/ScreenProbeTrace.
    float luma = dot(radiance, float3(0.2126, 0.7152, 0.0722));
    if (luma > 1.0e5) radiance *= 1.0e5 / max(luma, 1e-4);
    p.Color = Sanitize(min(radiance, 60000.0.xxx));
}
