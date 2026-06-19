// Lumen V2 — minimal truthful diffuse GI (P2). One bounce, NO surface cache, NO temporal history yet.
//
// Per G-buffer pixel: integrate incoming diffuse radiance over the cosine hemisphere with a handful of rays.
// Each ray is resolved by a HIERARCHY (plan §Render Architecture 4→5):
//   1) SCREEN TRACE first — march the depth buffer a few steps; if the ray hits an on-screen surface, the
//      incoming radiance is THAT pixel's already-lit HDR color (free near-field contact bounce, no RT cost).
//   2) HARDWARE RT on a screen miss — inline RayQuery against the scene TLAS. On a triangle hit, shade the hit
//      with REAL first-bounce radiance: emissive + sun(N·L, shadow-rayed) + punctual, all × albedo decoded
//      bindlessly (the same per-instance geometry/material the reflections hit shader uses). On an RT miss the
//      ray escaped → sky/IBL irradiance in that direction.
// The cosine weight is folded into the sampling (rays drawn cosine-weighted around N), so a plain mean of the
// per-ray radiance already approximates the cosine-weighted irradiance E that gates Lambertian diffuse. The
// result is written as that irradiance (NOT yet × albedo) into the indirect buffer; the combine multiplies by
// the receiver albedo so one buffer serves any surface.
//
// "Noisy but truthful" (plan §P2): low ray count, no denoise. The gates are correctness, not smoothness —
//   sealed black room stays black, color-bleed box bleeds (emissive hit term), thin wall does not leak
//   (the RT ray hits the occluder). Temporal stabilization + cards come in P3/P4.
//
// CSTrace bindings (compute): TLAS t0, depth t1, world-normal t2, material t3, LIT scene color t4, sky
// irradiance cube t5, sky prefilter cube t6; UAV indirect u0; LumenConstants b0, LumenSun b1; root SRVs
// GpuMaterials t7 / RtInstance[] t8 / Lights t9; bindless heap (ResourceDescriptorHeap[] for per-instance
// index/normal/uv buffers + albedo textures); static clamp s0 + wrap s1.

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth     : register(t1);
Texture2D<float4> Normal    : register(t2);   // world normal packed [0,1]
Texture2D<float4> Material  : register(t3);   // r metallic, g roughness, b ao
Texture2D<float4> SceneColor: register(t4);   // the lit HDR color this frame (opaque+sky+transparent), for screen hits
TextureCube SkyIrradiance   : register(t5);   // sky/IBL diffuse irradiance (RT-miss diffuse term)
TextureCube SkyPrefilter    : register(t6);   // sky/IBL radiance (unused in P2; reserved)
RWTexture2D<float4> Indirect : register(u0);  // OUT: rgb = incoming diffuse irradiance E, a = 1

cbuffer LumenConstants : register(b0) {
    float4x4 InvViewProj;     // screen+depth → world (JITTERED, transposed)
    float4x4 ViewProj;        // world → clip (transposed) — screen-trace reprojection
    float3 CameraPos;   float Intensity;         // GI intensity multiplier
    float2 TexelSize;   float RayCount;  float FrameIndex;   // 1/res; hemisphere rays per pixel; rotation seed
    float NormalBias;   float MaxRayDist; float ScreenStepPx; float ScreenSteps;   // bias; world ray length; screen march
    float SkyIntensity; float UseSky;    float Pad0; float Pad1;   // sky-miss scale; >0.5 = sky enters on miss
};
cbuffer LumenSun : register(b1) {
    float3 SunDir;   float SunBias;       // TO the sun (normalized), world; shadow-ray origin offset
    float3 SunColor; float LightCount;    // sun radiance (RAW HDR); # punctual lights
};
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

// --- Bindless geometry + material (identical layout to DxrReflections.hlsl) ---
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

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {   // ternary component-select — NEVER lerp(v,0,flag) (NaN*0==NaN; proven AMD bug)
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

// Cosine-weighted hemisphere sample around +Z (local). Folding the cosine into the sampling means a plain mean
// of per-ray radiance ≈ the cosine-weighted irradiance.
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
    return float3x3(t, b, n);   // rows; mul(local, basis) maps +Z → n
}

float3 WorldFromUvDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

// Inline shadow/visibility ray (any-hit-and-end). Returns 1 = visible, 0 = occluded.
float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray;
    ray.Origin = origin + N * max(SunBias, 0.002);
    ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// Shade an RT hit with first-bounce radiance: emissive + sun(N·L, shadow-rayed) + punctual, × albedo. This is
// the "truthful" off-screen term in P2 — no surface cache yet, so an off-screen hit returns its DIRECT-lit
// (and emissive) radiance, which is exactly what the color-bleed gate (emissive) and the black-room gate
// (no light → 0) need.
float3 ShadeHit(uint instId, uint prim, float2 bary2, float3x4 o2w, float3 rayDir, float3 hitPos) {
    RtInstance inst = RtInstances[instId];
    Buffer<uint>             indices = ResourceDescriptorHeap[inst.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[inst.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[inst.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[inst.TriMatIdx];

    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float3 bary = float3(1.0 - bary2.x - bary2.y, bary2.x, bary2.y);

    float3 nObj = normalize(normals[i0] * bary.x + normals[i1] * bary.y + normals[i2] * bary.z);
    float3 Ng = normalize(mul((float3x3)o2w, nObj));
    if (dot(Ng, rayDir) > 0.0) Ng = -Ng;   // two-sided: face the incoming ray
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.95.xxx);

    // Emissive (matches StandardOpaque: EmissiveFactor is color*intensity, optionally × an emissive map).
    float3 emissive = 0.0.xxx;
    if (m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    float3 sunDir = normalize(SunDir);
    float ndl = saturate(dot(Ng, sunDir));
    float3 sun = (ndl > 0.0) ? SunColor * ndl * Visibility(hitPos, Ng, sunDir, MaxRayDist) : 0.0.xxx;

    // Punctual diffuse (shadow-rayed). Bounded loop; matches the reflection hit's punctual model.
    float3 punctual = 0.0.xxx;
    int n = min((int)LightCount, 32);
    [loop] for (int i = 0; i < n; i++) {
        GpuLight L = Lights[i];
        float3 toL = L.PosRange.xyz - hitPos;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float nl = saturate(dot(Ng, Ld));
        if (nl <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 rad = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            rad *= cone * cone;
        }
        punctual += rad * nl * Visibility(hitPos, Ng, Ld, dist);
    }

    return albedo * (sun + punctual) + emissive;
}

// March the depth buffer along a world-space ray. Returns true + the lit color at the hit pixel when the ray
// passes BEHIND an on-screen surface within a thin thickness window (a real near-field hit). Cheap contact
// bounce that avoids an RT dispatch when the answer is already on screen.
bool ScreenTrace(float3 origin, float3 dir, out float3 radiance) {
    radiance = 0.0.xxx;
    int steps = max((int)ScreenSteps, 1);
    float stepLen = MaxRayDist / (float)steps;
    // Start a little along the ray so we don't self-intersect the origin pixel.
    float3 p = origin + dir * stepLen;
    [loop] for (int i = 0; i < steps; i++, p += dir * stepLen) {
        float4 clip = mul(float4(p, 1.0), ViewProj);
        if (clip.w <= 0.0) return false;
        float3 ndc = clip.xyz / clip.w;
        float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0)) return false;     // marched off-screen → let RT handle it
        float sceneDepth = Depth.SampleLevel(LinearClamp, uv, 0).r;
        if (sceneDepth >= 1.0) continue;                       // sky pixel — no occluder here
        // Compare reconstructed view-space distance so the thickness window is in metres.
        float3 rayWorld = WorldFromUvDepth(uv, ndc.z);
        float3 sceneWorld = WorldFromUvDepth(uv, sceneDepth);
        float rayZ = length(rayWorld - CameraPos);
        float sceneZ = length(sceneWorld - CameraPos);
        float diff = rayZ - sceneZ;                            // >0 = scene surface is in front of the ray
        if (diff > 0.01 * rayZ && diff < stepLen * 2.0) {
            // Hit an on-screen surface: incoming radiance = its lit HDR color. Front-facing check via depth
            // gradient is skipped in P2 (noisy-but-truthful); a back-face would over-count slightly.
            radiance = SceneColor.SampleLevel(LinearClamp, uv, 0).rgb;
            return true;
        }
    }
    return false;
}

[numthreads(8, 8, 1)]
void CSTrace(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    uint W = (uint)round(1.0 / TexelSize.x), H = (uint)round(1.0 / TexelSize.y);
    if (px.x >= W || px.y >= H) return;

    float depth = Depth[px];
    if (depth >= 1.0) { Indirect[px] = float4(0, 0, 0, 1); return; }   // sky: no indirect receiver

    float3 nWorld = Normal[px].rgb * 2.0 - 1.0;
    if (dot(nWorld, nWorld) < 0.1) { Indirect[px] = float4(0, 0, 0, 1); return; }
    float3 N = normalize(nWorld);

    float2 uv = (float2(px) + 0.5) * TexelSize;
    float3 worldPos = WorldFromUvDepth(uv, depth);
    float3 origin = worldPos + N * NormalBias;

    uint rays = (uint)clamp(RayCount, 1.0, 16.0);
    float jitter = Hash(px.x * 73856093u ^ px.y * 19349663u ^ (uint)FrameIndex * 2654435761u);
    float3x3 basis = BuildBasis(N);

    float3 sum = 0.0.xxx;
    [loop] for (uint r = 0; r < rays; r++) {
        float3 local = CosineHemisphere(r, rays, jitter);
        float3 dir = normalize(mul(local, basis));

        // 1) Screen trace.
        float3 rad;
        if (ScreenTrace(origin, dir, rad)) { sum += rad; continue; }

        // 2) Hardware RT on screen miss.
        RayDesc ray;
        ray.Origin = origin; ray.Direction = dir; ray.TMin = 0.02; ray.TMax = MaxRayDist;
        RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
        q.TraceRayInline(Scene, 0, 0xFF, ray);
        q.Proceed();
        if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
            float3 hitPos = origin + dir * q.CommittedRayT();
            sum += ShadeHit(q.CommittedInstanceID(), q.CommittedPrimitiveIndex(),
                            q.CommittedTriangleBarycentrics(), q.CommittedObjectToWorld3x4(), dir, hitPos);
        } else if (UseSky > 0.5) {
            // 3) Ray escaped → sky/IBL irradiance in that direction.
            sum += SkyIrradiance.SampleLevel(LinearClamp, dir, 0).rgb * SkyIntensity;
        }
    }

    // Mean over the cosine-sampled rays ≈ cosine-weighted incoming irradiance E. Store E (not E*albedo); the
    // combine applies the receiver albedo. Intensity is the artist GI dial.
    float3 E = sum / float(rays) * Intensity;
    Indirect[px] = float4(Sanitize(E), 1.0);
}

// ===== Combine: add the diffuse indirect into the HDR scene color =====
// Indirect holds incoming irradiance E. The diffuse response is E * albedo * ao (Lambertian, the same albedo
// the deferred pass used). The deferred pass SUPPRESSED its IBL diffuse ambient when Lumen is active (UseIBL
// Diffuse=0), so this is not double-counting — Lumen OWNS the diffuse indirect. Specular IBL + direct light +
// emissive are already in the scene color. Additive blend into the existing HDR target.

Texture2D<float4> IndirectIn : register(t0);   // E from CSTrace
Texture2D<float4> GAlbedo    : register(t1);   // rgb albedo
Texture2D<float4> GMaterial  : register(t2);   // b = AO
Texture2D<float>  CombineDepth : register(t3);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSCombine(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float4 PSCombine(VSOut i) : SV_Target {
    float depth = CombineDepth.SampleLevel(LinearClamp, i.Uv, 0).r;
    if (depth >= 1.0) discard;                       // sky: leave the scene color untouched
    float3 E = IndirectIn.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    float3 albedo = GAlbedo.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    float ao = GMaterial.SampleLevel(LinearClamp, i.Uv, 0).b;
    float3 diffuseIndirect = E * albedo * ao / PI;   // Lambertian: outgoing = E*albedo/PI
    return float4(Sanitize(diffuseIndirect), 1.0);   // additive blend (One/One) adds onto the HDR scene color
}

// DEBUG (BALLISTIC_DX12_LUMEN_DEBUG=1): OPAQUE replace with the raw incoming irradiance E so the GI signal is
// directly visible (isolates "is the trace producing radiance?" from the combine/exposure). Not a product path.
float4 PSDebugE(VSOut i) : SV_Target {
    float depth = CombineDepth.SampleLevel(LinearClamp, i.Uv, 0).r;
    if (depth >= 1.0) return float4(0, 0, 0, 1);
    float3 E = IndirectIn.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    return float4(Sanitize(E), 1.0);
}
