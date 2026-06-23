// Deferred lighting pass for the DX12 clustered-deferred renderer. A single fullscreen triangle reads the
// fat G-buffer (albedo+F0 / world-normal / metallic-roughness-ao-flags / emissive) + scene depth,
// reconstructs world position from depth, and shades exactly like the old forward StandardOpaque: Cook-
// Torrance GGX direct sun + split-sum IBL ambient + cascaded PCF sun shadows, writing RAW HDR into the
// scene color target (the composite tonemaps later). The shading math is byte-for-byte the forward path's
// — only the inputs move from interpolated vertex data to G-buffer fetches.
//
// CONVENTIONS (locked): System.Numerics row-major, HLSL column-major, CPU transposes on upload.

cbuffer LightConstants : register(b0) {
    float4x4 InvViewProj;    // unproject screen+depth → world (transposed on upload)
    float4x4 View;           // world → view (transposed) — to find a pixel's froxel from its view depth
    float3   LightDir;       float Pad0;          // TO the light, normalized, world space
    float3   LightColor;     float Pad1;          // sun radiance (HDR)
    float3   Ambient;        float Pad2;          // flat ambient fill (IBL stand-in)
    float3   CameraPos;      float UseIBL;        // world camera pos; >0.5 = sample baked IBL
    float    PrefilterMaxMip;
    // clustered punctual lights:
    float    PunctualCount;                       // active punctual lights (0 = skip the clustered path)
    float2   ScreenSize;                          // render-target pixel size (for the froxel tile lookup)
    float2   ClusterNearFar;                      // near/far the froxel log-Z grid was built with
    float    UseRtShadows;                        // >0.5 = sample the RT shadow mask instead of cascade PCF
    float    SpecClamp;                            // V2: max per-light specular LUMA (0 = off); caps NDF fireflies
    float    SpecAaStrength;                       // V2: geometric specular AA strength (0 = off); roughens noisy normals
    float    UseSsao;                              // >0.5 = multiply the GTAO term into the IBL ambient (ambient-only)
    float    UseIBLDiffuse;                         // >0.5 = add the IBL diffuse-irradiance ambient; 0 when Lumen owns diffuse GI
    float    UseIBLSpecular;                        // >0.5 = add the IBL prefiltered-specular ambient; 0 when RT/SSR own reflections
    float    UseCapsuleShadows; float3 CapPad;      // >0.5 = multiply the capsule-shadow mask (t16) into the sun term
    float4x4 ViewProjFwd;                          // world → clip (transposed on upload); contact-shadow march reprojection
    float    UseVsm; float VsmLevels; float VsmTexel; float VsmLevel0Extent;  // VSM (virtual shadow map) clipmap params; UseVsm>0.5 selects the VSM path
    float3   VsmCamPos; float MsBrdfEnabled;        // camera world pos; MsBrdfEnabled>0.5 = multi-scatter energy-preserving BRDF (was VsmPad0)
};

// V2 specular firefly clamp (fixes D3): a normal-mapped surface lit by a sharp light produces single-pixel
// GGX NDF spikes (the half-vector momentarily aligns with a texel normal) → crawling specular sparkles, which
// V1's correct exposure made stark on the Bistro brick. Bound each light's specular contribution by luma so a
// lone texel can't blow up, WITHOUT dimming a broad highlight (the clamp only bites the outliers). SpecClamp=0
// disables it (byte-identical). Applied per light (sun + each punctual) so the cap is on the per-source spike.
float3 ClampSpecular(float3 spec, float maxLuma) {
    if (maxLuma <= 0.0) return spec;
    float luma = dot(spec, float3(0.2126, 0.7152, 0.0722));
    return (luma > maxLuma) ? spec * (maxLuma / luma) : spec;
}

// Froxel grid dims — must match Dx12ClusteredLights (16x9x24, log-Z).
static const int ClusterDimX = 16;
static const int ClusterDimY = 9;
static const int ClusterDimZ = 24;

// Per-frame cascade matrices + shadow params (shared layout with the forward FrameConstants, b1).
// The Shadows-volume tail (Filtering..ContactThickness) must match DX12HDRenderer.FrameConstants exactly.
cbuffer FrameConstants : register(b1) {
    float4x4 Cascade0, Cascade1, Cascade2, Cascade3;
    float4   CascadeBias;
    float    CascadeCountF; float ShadowsEnabled; float ShadowMapTexel; float CascadeBlend;
    float    ShadowFiltering;     // 0 = hard, 1 = soft PCF, 2 = PCSS
    float    ShadowSoftness;      // PCSS penumbra scale
    float    ContactShadowsOn;    // >0.5 = march a screen-space contact shadow
    float    ContactShadowLength; // world metres marched
    float    ContactShadowSteps;
    float    ContactShadowThickness;
    float    FramePad0, FramePad1;
};

// VIRTUAL SHADOW MAP clipmap matrices (b2) — a SEPARATE cbuffer so the default cascade path's b1 layout is
// untouched (byte-identical when VSM is off; b2 is simply never bound/read). Up to 16 camera-anchored,
// texel-snapped clipmap-level light-space matrices (world → light clip, transposed on upload). Sampled by
// VsmSunShadow when UseVsm > 0.5. Mirrors Dx12VirtualShadowMap.MaxLevels.
cbuffer VsmConstants : register(b2) {
    float4x4 VsmMatrix[16];
};

Texture2D GAlbedo   : register(t0);   // rgb albedo, a = specularReflectance
Texture2D GNormal   : register(t1);   // rgb world normal packed [0,1]
Texture2D GMaterial : register(t2);   // r metallic, g roughness, b ao, a = flags
Texture2D GEmissive : register(t3);   // rgb emissive radiance (HDR)
Texture2D DepthTex  : register(t4);   // scene depth (R32_Float)
TextureCube IrradianceMap   : register(t5);
TextureCube PrefilterMap    : register(t6);
Texture2D   BrdfLut         : register(t7);
Texture2DArray ShadowCascades : register(t8);   // sun cascade depth (R32_Float), manual PCF

// Clustered lights (faithful to the GL clustered path). Now 80 bytes — grew one float4 (RightAxisHalfW) for
// RECT (area / LTC) lights. Point/spot leave the new field 0 and never read it → their bits are unchanged.
struct GpuLight {
    float4 PosRange;        // xyz world pos/center, w range (rect: w = cull radius)
    float4 Color;           // xyz radiance (HDR), w type (0 point / 1 spot / 2 rect)
    float4 DirCosOuter;     // point/spot: xyz dir, w cosOuter. rect: xyz forward(normal), w halfWidth
    float4 Extra;           // point/spot: x cosInner, y shadowSlot, z sourceRadius. rect: x twoSided, z range, w halfHeight
    float4 RightAxisHalfW;  // RECT ONLY: xyz right axis (unit), w halfWidth. 0 for point/spot.
};
StructuredBuffer<GpuLight> ClusterLights : register(t9);
Buffer<int2>               ClusterGrid   : register(t10);  // per-cluster {offset, count}
Buffer<uint>               ClusterIndex  : register(t11);  // flat light-index list
Texture2D RtShadowMask     : register(t12);                // ray-traced sun shadow (1 lit / 0 shadowed)
Texture2D SsaoTex          : register(t13);                // GTAO (1 = unoccluded); multiplied into ambient only
Texture2D LtcMatTex        : register(t14);                // LTC inverse-matrix coeffs (area lights); 64x64 RGBA32F
Texture2D LtcAmpTex        : register(t15);                // LTC amplitude/Fresnel split-sum (area lights)
Texture2D CapsuleShadowTex : register(t16);                // analytic capsule sun-shadow occlusion (1 lit / 0 occluded)
Texture2DArray VsmClipmap  : register(t17);                // VSM clipmap depth (R32_Float), one layer per level; manual PCF
SamplerState LinearClamp : register(s0);

static const float PI = 3.14159265359;
static const float EPS = 1e-6;

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float DistributionGGX(float3 N, float3 H, float rough) {
    float a = rough * rough; float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + EPS);
}
float GeometrySchlickGGX(float NdotV, float rough) {
    float k = (rough + 1.0); k = (k * k) / 8.0;
    return NdotV / max(NdotV * (1.0 - k) + k, EPS);
}
float GeometrySmith(float3 N, float3 V, float3 L, float rough) {
    return GeometrySchlickGGX(max(dot(N, V), 0.0), rough) *
           GeometrySchlickGGX(max(dot(N, L), 0.0), rough);
}
float3 FresnelSchlick(float cosT, float3 F0) {
    return F0 + (1.0 - F0) * pow(1.0 - cosT, 5.0);
}
float3 FresnelSchlickRoughness(float cosT, float3 F0, float rough) {
    float3 Fr = max((1.0 - rough).xxx, F0);
    return F0 + (Fr - F0) * pow(1.0 - cosT, 5.0);
}

// ===================== MULTI-SCATTER ENERGY-PRESERVING BRDF (kajiya/Belcour 2018) =====================
// Plain Cook-Torrance GGX (single-scatter) throws away the energy that bounces more than once between
// microfacets — rough metals/dielectrics come out visibly too dark and desaturated. kajiya's fix (a special
// case of Belcour's "Atomic Decomposition" §5.1, matched to Heitz's Smith multi-scatter reference): use the
// preintegrated split-sum FG-LUT (fg.x scale, fg.y bias — IDENTICAL to our existing BrdfLut) to get the
// single-scatter directional albedo e_ss = fg.x+fg.y, then add back the lost (1-e_ss) energy as the closed
// form of the infinite inter-reflection geometric series, with an ad-hoc shift toward F90 for grazing tail
// bounces (desaturates successive bounces — counters metal over-saturation). Returns a MULTIPLICATIVE factor
// on the specular term + the diffuse transmission fraction (the (1-F)-equivalent that survives the spec layer).
// MsBrdfEnabled gates it (0 = byte-identical legacy). MULTIPLICATIVE only → zero temporal-feedback risk.
struct MsEnergy {
    float3 reflMult;        // multiply the single-scatter specular by this (>= 1) to restore multi-scatter energy
    float3 transFraction;   // energy transmitted through the spec layer to the diffuse below (= 1 - preReflection)
};
MsEnergy MultiScatterEnergy(float3 specAlbedo, float roughness, float ndotv) {
    // Preintegrated FG at (ndotv, roughness). Our BrdfLut is the same split-sum integral kajiya bakes.
    float2 fg = BrdfLut.SampleLevel(LinearClamp, float2(saturate(ndotv), roughness), 0).rg;
    float3 singleScatter = specAlbedo * fg.x + fg.y.xxx;
    float  e_ss = fg.x + fg.y;
    MsEnergy res;
    // e_ss can be ~0 at extreme grazing+rough; guard the divisions (degenerate → no boost, no leak).
    if (e_ss <= 1e-4) { res.reflMult = 1.0.xxx; res.transFraction = max(0.0.xxx, 1.0.xxx - singleScatter); return res; }
    float3 f_ss = singleScatter / e_ss;
    float3 f_ss_tail = lerp(f_ss, 1.0.xxx, 0.4);                 // grazing-tail desaturation (Belcour ad-hoc)
    float3 bounce = (1.0 - e_ss) * f_ss_tail;
    // Closed-form infinite geometric series of per-bounce lost energy. bounce < 1 by construction (e_ss in
    // (0,1], f_ss_tail in [.,1]); clamp denom defensively so a pathological LUT texel can't divide by ~0.
    float3 mult = 1.0.xxx + bounce / max(1.0.xxx - bounce, 1e-4.xxx);
    float3 preReflection = singleScatter * mult;
    res.reflMult = mult;
    res.transFraction = max(0.0.xxx, 1.0.xxx - preReflection);
    return res;
}

// Metalness in (0,1) loses energy because albedo is split between the spec and diffuse layers. kajiya's
// polynomial fit (RMSE 0.0007691) scales both layers' albedo back up to recover it. Returns the boost factor.
float3 MetalnessAlbedoBoost(float metalness, float3 albedo) {
    const float a0 = 1.749, a1 = -1.61, e1 = 0.5555, e3 = 0.8244;
    float x = metalness;
    float3 y = albedo, y3 = y * y * y;
    return 1.0.xxx + (0.25 - (x - 0.5) * (x - 0.5)) * (a0 + a1 * abs(x - 0.5)) * (e1 * y + e3 * y3);
}

float CascadeMatrixApply(int c, float3 worldPos, out float3 proj) {
    float4x4 m = c == 0 ? Cascade0 : (c == 1 ? Cascade1 : (c == 2 ? Cascade2 : Cascade3));
    float4 clip = mul(float4(worldPos, 1.0), m);
    proj = clip.xyz;
    proj.xy = proj.xy * float2(0.5, -0.5) + 0.5;
    return max(abs(clip.x), abs(clip.y));
}

// One hard depth-compare tap (Filtering == 0): razor-sharp, aliased.
float ShadowTapHard(int c, float2 uv, float z, float bias) {
    float d = ShadowCascades.SampleLevel(LinearClamp, float3(uv, (float)c), 0).r;
    return (z - bias) <= d ? 1.0 : 0.0;
}

// Fixed 5x5 PCF (Filtering == 1): the default soft edge. radiusTexels widens with ShadowSoftness.
float ShadowPcf(int c, float2 base, float z, float bias, float radiusTexels) {
    float lit = 0.0;
    [unroll] for (int dy = -2; dy <= 2; dy++)
    [unroll] for (int dx = -2; dx <= 2; dx++) {
        float2 uv = base + float2(dx, dy) * ShadowMapTexel * radiusTexels;
        lit += ShadowTapHard(c, uv, z, bias);
    }
    return lit / 25.0;
}

// PCSS (Filtering == 2): contact-hardening. A blocker search estimates the average occluder depth; the
// penumbra grows with the receiver↔blocker gap × ShadowSoftness, then a variable-radius PCF fills it.
float ShadowPcss(int c, float2 base, float z, float bias) {
    // 1) Blocker search over a softness-scaled kernel.
    float searchTexels = 2.0 + ShadowSoftness * 2.0;
    float blockerSum = 0.0; float blockerCount = 0.0;
    [unroll] for (int sy = -2; sy <= 2; sy++)
    [unroll] for (int sx = -2; sx <= 2; sx++) {
        float2 uv = base + float2(sx, sy) * ShadowMapTexel * searchTexels;
        float d = ShadowCascades.SampleLevel(LinearClamp, float3(uv, (float)c), 0).r;
        if (d < z - bias) { blockerSum += d; blockerCount += 1.0; }
    }
    if (blockerCount < 0.5) return 1.0;                  // fully lit — no blockers
    float avgBlocker = blockerSum / blockerCount;
    // 2) Penumbra ∝ (receiver - blocker) / blocker, scaled by the artist softness (1 = physical-ish sharp).
    float penumbra = max(z - avgBlocker, 0.0) / max(avgBlocker, 1e-4);
    float radiusTexels = clamp(penumbra * ShadowSoftness * 64.0, 0.75, 12.0);
    // 3) Variable-radius PCF (reuse the 5x5 kernel at the computed radius).
    return ShadowPcf(c, base, z, bias, radiusTexels);
}

// ===================================== VIRTUAL SHADOW MAP sampling =====================================
// Clipmap-array VSM (the camera-anchored, log2-extent, per-level-cached form — see Dx12VirtualShadowMap.cs).
// Select the FINEST clipmap level whose world half-extent covers the receiver's distance from the camera,
// project into that level's light clip, and PCF-sample its array layer. Level i covers VsmLevel0Extent·2^i
// world half-extent; picking by distance gives near geometry the densest texels (the VSM resolution win).
//
// One hard depth-compare tap into a VSM clipmap level layer.
float VsmTapHard(int level, float2 uv, float z, float bias) {
    float d = VsmClipmap.SampleLevel(LinearClamp, float3(uv, (float)level), 0).r;
    return (z - bias) <= d ? 1.0 : 0.0;
}

// Fixed 3x3 PCF in one VSM level (texel size = VsmTexel). Small soft kernel — the clipmap level already
// adapts the texel density, so a wide kernel isn't needed for a soft edge.
float VsmPcf(int level, float2 base, float z, float bias, float radiusTexels) {
    float lit = 0.0;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++) {
        float2 uv = base + float2(dx, dy) * VsmTexel * radiusTexels;
        lit += VsmTapHard(level, uv, z, bias);
    }
    return lit / 9.0;
}

float VsmSunShadow(float3 N, float3 L, float3 worldPos) {
    if (ShadowsEnabled < 0.5) return 1.0;
    float ndl = saturate(dot(N, L));
    int levels = (int)(VsmLevels + 0.5);

    // Pick the finest level whose half-extent covers the receiver. Camera-relative distance (Chebyshev /
    // max-axis) matches the square ortho footprint of a level better than Euclidean radius.
    float3 rel = worldPos - VsmCamPos;
    float dist = max(max(abs(rel.x), abs(rel.y)), abs(rel.z));
    for (int level = 0; level < levels; level++) {
        float extent = VsmLevel0Extent * exp2((float)level);
        if (dist > extent * 0.95) continue;          // not covered by this (or any finer) level — try coarser
        float4 clip = mul(float4(worldPos, 1.0), VsmMatrix[level]);
        float3 proj = clip.xyz;
        proj.xy = proj.xy * float2(0.5, -0.5) + 0.5;
        // Guard against the rare snap-edge sliver where the receiver sits just outside this level's clip.
        float edge = max(abs(clip.x), abs(clip.y));
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0) continue;
        // Bias scales with the level's texel size (coarser level = larger world texel = more bias needed).
        float bias = max(0.0006 * (1.0 + (float)level), 0.0006) * (2.0 - ndl);
        int mode = (int)(ShadowFiltering + 0.5);
        if (mode == 0) return VsmTapHard(level, proj.xy, proj.z, bias);
        float radiusTexels = clamp(ShadowSoftness * 0.75, 0.5, 4.0);
        return VsmPcf(level, proj.xy, proj.z, bias, radiusTexels);
    }
    return 1.0;   // beyond the coarsest level — treat as lit (the CSM far-fade equivalent)
}

float SunShadow(float3 N, float3 L, float3 worldPos) {
    if (ShadowsEnabled < 0.5) return 1.0;
    float ndl = saturate(dot(N, L));
    int count = (int)CascadeCountF;
    int mode = (int)(ShadowFiltering + 0.5);
    for (int c = 0; c < count; c++) {
        float3 proj;
        float edge = CascadeMatrixApply(c, worldPos, proj);
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0) continue;
        float bias = max(CascadeBias[c] * (1.0 - ndl), CascadeBias[c] * 0.1);
        if (mode == 0) return ShadowTapHard(c, proj.xy, proj.z, bias);
        if (mode == 2) return ShadowPcss(c, proj.xy, proj.z, bias);
        // mode 1 (default soft PCF): radius scales gently with softness, matching the old GL path.
        float radiusTexels = clamp(ShadowSoftness * 0.75, 0.5, 4.0);
        return ShadowPcf(c, proj.xy, proj.z, bias, radiusTexels);
    }
    return 1.0;
}

// Unproject a screen UV + depth into world space (DX NDC: xy [-1,1] with y flipped, z = depth [0,1]).
float3 WorldPosFromDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

// Screen-space contact shadows (Shadows volume): a short ray-march toward the sun in screen space that
// grounds small props + fine geometry the cascades miss. It only DARKENS (never lifts) — a multiplier on the
// cascade/RT shadow. Marches ContactShadowLength world-metres in ContactShadowSteps samples; a hit is when
// the scene depth at a sample sits in front of the ray by more than the threshold but within ContactShadow
// Thickness (a thin-occluder window, so a far background doesn't spuriously occlude). Returns 1 = unshadowed.
float ContactShadow(float3 worldPos, float3 L) {
    if (ContactShadowsOn < 0.5 || ContactShadowLength <= 0.0) return 1.0;
    int steps = max((int)(ContactShadowSteps + 0.5), 1);
    float stepLen = ContactShadowLength / (float)steps;
    float3 p = worldPos;
    [loop] for (int i = 1; i <= steps; i++) {
        p += L * stepLen;
        // Reproject the marched world point to this frame's screen UV + clip depth.
        float4 clip = mul(float4(p, 1.0), ViewProjFwd);
        if (clip.w <= 0.0) continue;
        float3 ndc = clip.xyz / clip.w;
        float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0)) continue;          // marched off-screen — no info
        float sceneDepth = DepthTex.SampleLevel(LinearClamp, uv, 0).r;
        // Both depths are DX clip-z [0,1]; reconstruct each sample's VIEW-space z so the thickness window is
        // in metres, not nonlinear depth. We only need the delta, so compare reconstructed view positions.
        float3 rayWorld = WorldPosFromDepth(uv, ndc.z);
        float3 sceneWorld = WorldPosFromDepth(uv, sceneDepth);
        float rayVz   = -mul(float4(rayWorld, 1.0), View).z;   // positive view distance
        float sceneVz = -mul(float4(sceneWorld, 1.0), View).z;
        float diff = rayVz - sceneVz;                          // >0 = scene surface is in front of the ray
        if (diff > 0.01 && diff < ContactShadowThickness)
            return 0.0;                                        // occluded by a thin foreground surface
    }
    return 1.0;
}

// Inverse-square distance attenuation with a smooth range cutoff (windowing). range = light.w.
// V2 (fixes D3 — fireflies clustered AT light fixtures): the old `1/max(d², 1e-4)` floor let a surface
// ~1 cm from a light receive a ~10000× radiance pop (1e-4 m² = (1 cm)²) — the lamp-shade interior in the
// Bistro point lights blew up into a crawling speckle field, which V1's correct exposure made stark. The
// physical fix is the spherical-source ("representative point" / Karis) window `1/(d² + r²)`: finite at
// d=0 (max 1/r²), smooth, and IDENTICAL to `1/d²` once d ≫ r (so anything past ~5·r is unchanged — lights
// at normal stand-off, e.g. LightTest, stay byte-identical). r = the light's SourceRadius, floored at
// rMin so a delta light (SourceRadius=0, the common authored case) still can't singularly spike up close.
// sourceRadius arrives in GpuLight.Extra.z; rMin keeps the bound even when it's 0.
float DistanceAttenuation(float dist, float range, float sourceRadius) {
    const float rMin = 0.05;                                  // 5 cm: caps near-field atten at 1/0.0025 = 400
    float r = max(sourceRadius, rMin);
    float inv = 1.0 / (dist * dist + r * r);                  // spherical-source window (no singularity)
    float t = saturate(1.0 - pow(dist / range, 4.0));
    return inv * t * t;
}

// ================================ AREA / RECT LIGHTS (LTC, Heitz et al. 2016) =========================
// Linearly-Transformed Cosines: shade a rectangular emitter by transforming the GGX specular lobe into a
// clamped-cosine distribution (via the per-(NdotV,roughness) inverse matrix in LtcMatTex), then analytically
// integrating that cosine over the rect polygon (the edge-integral below). Diffuse uses the identity matrix
// (the cosine lobe directly). Ported from selfshadow/ltc_code (Hill/Heitz), the canonical reference impl.

static const float LTC_LUT_SIZE = 64.0;
static const float LTC_LUT_SCALE = (LTC_LUT_SIZE - 1.0) / LTC_LUT_SIZE;
static const float LTC_LUT_BIAS  = 0.5 / LTC_LUT_SIZE;

// Integrate one polygon edge of the (already cosine-space) clamped-cosine distribution. Returns the vector
// form (Baum's formula) — the z component is the form factor, accumulated per edge.
float3 LtcIntegrateEdgeVec(float3 v1, float3 v2) {
    float x = dot(v1, v2);
    float y = abs(x);
    float a = 0.8543985 + (0.4965155 + 0.0145206 * y) * y;
    float b = 3.4175940 + (4.1616724 + y) * y;
    float v = a / b;
    float theta_sintheta = (x > 0.0) ? v : 0.5 * rsqrt(max(1.0 - x * x, 1e-7)) - v;
    return cross(v1, v2) * theta_sintheta;
}

// Evaluate the LTC over the rect with corners p0..p3 (world), from shading point P with normal N and view V.
// Minv = the inverse LTC matrix (identity for diffuse). twoSided: light both faces. Returns the (scalar)
// irradiance the rect contributes through this lobe. (Horizon-clipping the simple way: clamp the form factor.)
float LtcEvaluate(float3 N, float3 V, float3 P, float3x3 Minv,
                  float3 p0, float3 p1, float3 p2, float3 p3, bool twoSided) {
    // Build an orthonormal basis around N (tangent T1 in the view-tangent plane).
    float3 T1 = normalize(V - N * dot(V, N));
    float3 T2 = cross(N, T1);
    // Rotate the rect into the tangent frame, then apply Minv.
    float3x3 R = transpose(float3x3(T1, T2, N));
    Minv = mul(Minv, R);

    float3 L0 = mul(Minv, p0 - P);
    float3 L1 = mul(Minv, p1 - P);
    float3 L2 = mul(Minv, p2 - P);
    float3 L3 = mul(Minv, p3 - P);

    // Approximate horizon clipping: if all corners are below the local horizon, no contribution.
    float3 dir = p0 - P;
    float3 lightNormal = cross(p1 - p0, p3 - p0);
    bool behind = dot(dir, lightNormal) < 0.0;
    if (behind && !twoSided) return 0.0;

    L0 = normalize(L0); L1 = normalize(L1); L2 = normalize(L2); L3 = normalize(L3);

    float3 vsum = 0.0.xxx;
    vsum += LtcIntegrateEdgeVec(L0, L1);
    vsum += LtcIntegrateEdgeVec(L1, L2);
    vsum += LtcIntegrateEdgeVec(L2, L3);
    vsum += LtcIntegrateEdgeVec(L3, L0);

    // The form factor is the length of the accumulated edge-vector sum (the irradiance of the clamped-cosine
    // polygon integral). Two-sided takes the absolute contribution; one-sided drops the back face.
    float sum = length(vsum);
    if (!twoSided && behind) sum = 0.0;
    return max(sum, 0.0);
}

// One AREA / RECT light via LTC. radiance is the rect's HDR radiance (already power/(area*pi)-normalized on
// the CPU). Diffuse uses the identity LTC (cosine); specular uses the matrix at (NdotV, roughness). NO shadows.
float3 ShadeRect(GpuLight L, float3 N, float3 V, float3 worldPos, float3 albedo,
                 float metallic, float roughness, float3 F0) {
    float3 center = L.PosRange.xyz;
    float range = L.Extra.z;                                  // rect influence range (Extra.z), separate from cull radius
    float3 toCenter = center - worldPos;
    float dist = length(toCenter);
    if (dist > range + max(L.DirCosOuter.w, L.Extra.w)) return 0.0.xxx;  // range cull (range + extent)

    float3 fwd  = normalize(L.DirCosOuter.xyz);               // rect normal (emitting face = +fwd)
    float3 right = normalize(L.RightAxisHalfW.xyz);
    float3 up   = normalize(cross(fwd, right));
    float halfW = L.DirCosOuter.w, halfH = L.Extra.w;
    bool twoSided = L.Extra.x > 0.5;

    // The four rect corners in world space.
    float3 ex = right * halfW, ey = up * halfH;
    float3 p0 = center - ex - ey;
    float3 p1 = center + ex - ey;
    float3 p2 = center + ex + ey;
    float3 p3 = center - ex + ey;

    // Smooth range window (parity with DistanceAttenuation's windowing, distance to the rect center).
    float win = saturate(1.0 - pow(saturate(dist / max(range, 1e-3)), 4.0));
    win *= win;
    if (win <= 0.0) return 0.0.xxx;

    float NdotV = saturate(dot(N, V));
    // LUT fetch at (NdotV, roughness).
    float2 uv = float2(NdotV, roughness);
    uv = uv * LTC_LUT_SCALE + LTC_LUT_BIAS;
    float4 t1 = LtcMatTex.SampleLevel(LinearClamp, uv, 0);    // r=m00, g=m20, b=m02, a=m11 (Heitz packing)
    float4 t2 = LtcAmpTex.SampleLevel(LinearClamp, uv, 0);    // r=magnitude, g=fresnel

    // Rebuild the inverse LTC matrix: Minv = {{ a, 0, b },{ 0, 1, 0 },{ c, 0, 1 }} with the 4 stored coeffs.
    float3x3 Minv = float3x3(
        t1.x, 0.0,  t1.z,
        0.0,  t1.w, 0.0,
        t1.y, 0.0,  1.0);
    float3x3 identity = float3x3(1,0,0, 0,1,0, 0,0,1);

    // Diffuse (cosine lobe = identity LTC) and specular (the fitted matrix).
    float diff = LtcEvaluate(N, V, worldPos, identity, p0, p1, p2, p3, twoSided);
    float spec = LtcEvaluate(N, V, worldPos, Minv,     p0, p1, p2, p3, twoSided);

    // Split-sum GGX scale/bias on the specular (Karis): F0 * scale + bias-from-Fresnel.
    float3 specColor = F0 * t2.x + (1.0.xxx - F0) * t2.y;
    float3 kD = (1.0 - metallic);                             // metals have no diffuse
    float3 radiance = L.Color.rgb * win;

    // 1/(2*PI) normalization is folded into the table fit (the reference applies it on the result).
    float3 diffuseTerm = kD * albedo * diff;
    float3 specTerm = specColor * spec;
    float3 outv = (diffuseTerm + specTerm) * radiance * (1.0 / (2.0 * PI));
    return ClampSpecular(outv, SpecClamp);
}

// One punctual light (point or spot) via the SAME Cook-Torrance BRDF as the sun. radiance already folds
// attenuation × cone. No punctual shadows yet (shadowSlot is -1 for now).
float3 ShadePunctual(GpuLight L, float3 N, float3 V, float3 worldPos, float3 albedo,
                     float metallic, float roughness, float3 F0) {
    float3 toLight = L.PosRange.xyz - worldPos;
    float dist = length(toLight);
    if (dist > L.PosRange.w) return 0.0.xxx;          // range cull
    float3 Ld = toLight / max(dist, 1e-4);
    float atten = DistanceAttenuation(dist, L.PosRange.w, L.Extra.z);   // Extra.z = SourceRadius (V2 near-field window)
    if (atten <= 0.0) return 0.0.xxx;

    float3 radiance = L.Color.rgb * atten;
    if (L.Color.w >= 0.5) {                            // spot: cone falloff
        float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
        float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
        if (cone <= 0.0) return 0.0.xxx;
        radiance *= cone * cone;
    }

    float NdotL = max(dot(N, Ld), 0.0);
    if (NdotL <= 0.0) return 0.0.xxx;
    float3 H = normalize(V + Ld);
    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, Ld, roughness);
    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
    float NdotV = max(dot(N, V), 0.0);
    float3 spec = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);
    float3 specGain = 1.0.xxx, diffAlbedo = albedo, kD;
    if (MsBrdfEnabled > 0.5) {
        MsEnergy ms = MultiScatterEnergy(F0, roughness, NdotV);
        float3 boost = MetalnessAlbedoBoost(metallic, albedo);
        specGain = ms.reflMult;
        diffAlbedo = albedo * (1.0 - metallic) * boost;
        kD = ms.transFraction;
    } else {
        kD = (1.0 - F) * (1.0 - metallic);
    }
    float3 diffuseTerm = kD * diffAlbedo / PI * radiance * NdotL;
    float3 specTerm = ClampSpecular(spec * specGain * radiance * NdotL, SpecClamp);   // V2: bound punctual specular fireflies
    return diffuseTerm + specTerm;
}

// This pixel's froxel index from screen pixel + view-space depth (log-Z), matching Dx12ClusteredLights.
int ClusterIndexFor(float2 pixel, float3 worldPos) {
    float viewZ = -mul(float4(worldPos, 1.0), View).z;   // positive view distance
    float near = ClusterNearFar.x, far = ClusterNearFar.y;
    int zSlice = (int)(log(max(viewZ, near) / near) / log(far / near) * (float)ClusterDimZ);
    zSlice = clamp(zSlice, 0, ClusterDimZ - 1);
    int2 tile = (int2)(pixel / (ScreenSize / float2(ClusterDimX, ClusterDimY)));
    tile = clamp(tile, int2(0, 0), int2(ClusterDimX - 1, ClusterDimY - 1));
    return tile.x + ClusterDimX * (tile.y + ClusterDimY * zSlice);
}

float4 PSMain(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(LinearClamp, i.Uv, 0).r;
    if (depth >= 1.0) discard;   // sky / unwritten: leave the cleared target for the sky pass

    float4 g0 = GAlbedo.SampleLevel(LinearClamp, i.Uv, 0);
    float4 g1 = GNormal.SampleLevel(LinearClamp, i.Uv, 0);
    float4 g2 = GMaterial.SampleLevel(LinearClamp, i.Uv, 0);
    float3 emissive = GEmissive.SampleLevel(LinearClamp, i.Uv, 0).rgb;

    float3 albedo = g0.rgb;
    float specularReflectance = g0.a;
    float3 N = normalize(g1.rgb * 2.0 - 1.0);
    float metallic = g2.r;
    float roughness = clamp(g2.g, 0.045, 1.0);
    // Material AO (baked into the texture) times the screen-space GTAO. AO modulates the AMBIENT/indirect term
    // ONLY (used in the IBL + fallback ambient below) — direct sun/punctual light is left untouched, which is
    // the physically-correct layer. GTAO is rendered at the AO resolution; LinearClamp upsamples it here.
    float ao = g2.b;
    if (UseSsao > 0.5) ao *= SsaoTex.SampleLevel(LinearClamp, i.Uv, 0).r;

    // GEOMETRIC SPECULAR ANTI-ALIASING (V2, fixes D3 — the crawling sparkle on normal-mapped surfaces). The
    // high-frequency tiled normal maps (Bistro brick/stone) alias under-sampled: adjacent screen pixels get
    // wildly different G-buffer normals (measured std ~0.14 on a flat wall), so the GGX lobe peaks on lone
    // texels → fireflies that TAA can't fully flush. Kaplanyan/Tokuyoshi fix: estimate the normal's screen-
    // space variance from its derivatives and fold it into the roughness (in α=roughness² space), widening the
    // specular lobe exactly where the normal is noisy and leaving smooth surfaces untouched. SpecAaStrength=0
    // disables it (byte-identical). The deferred pass reads the G-buffer normal, so ddx/ddy here = the on-screen
    // normal variation directly. This is a SHADING-quality fix; it does NOT alter the z-prepass (depth-only).
    if (SpecAaStrength > 0.0) {
        float3 dNdx = ddx(N), dNdy = ddy(N);
        float variance = SpecAaStrength * (dot(dNdx, dNdx) + dot(dNdy, dNdy));
        float kernelRough2 = min(variance, 0.25);            // clamp the added α² so a silhouette edge can't over-roughen
        float alpha = roughness * roughness;
        roughness = clamp(sqrt(saturate(alpha + kernelRough2)), 0.045, 1.0);
    }

    float3 worldPos = WorldPosFromDepth(i.Uv, depth);
    float3 V = normalize(CameraPos - worldPos);

    // Cook-Torrance direct sun (mirrors the forward ShadeSun path).
    float3 F0 = lerp(0.08 * specularReflectance.xxx, albedo, metallic);
    float3 D = normalize(LightDir);
    float NdotL = max(dot(N, D), 0.0);
    float3 diffuse = 0, specular = 0;
    if (NdotL > 0.0) {
        float shadow = (UseRtShadows > 0.5) ? RtShadowMask.SampleLevel(LinearClamp, i.Uv, 0).r
                     : (UseVsm > 0.5)       ? VsmSunShadow(N, D, worldPos)
                                            : SunShadow(N, D, worldPos);
        // Contact shadows refine the cascade path (RT shadows already capture contact). Only darkens.
        if (UseRtShadows <= 0.5) shadow *= ContactShadow(worldPos, D);
        // Capsule shadows (character proxy capsules) — combine via product (independent occluder). The mask is
        // 1 (lit) when no caster covers this pixel, so multiplying is min-like and only darkens. Gated off
        // (UseCapsuleShadows=0) when no caster ran → byte-identical default.
        if (UseCapsuleShadows > 0.5)
            shadow *= CapsuleShadowTex.SampleLevel(LinearClamp, i.Uv, 0).r;
        float3 radiance = LightColor * shadow;
        float3 H = normalize(V + D);
        float NDF = DistributionGGX(N, H, roughness);
        float G = GeometrySmith(N, V, D, roughness);
        float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
        float NdotV = max(dot(N, V), 0.0);
        float3 spec = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);
        float3 specGain = 1.0.xxx, diffAlbedo = albedo;
        float3 kD;
        if (MsBrdfEnabled > 0.5) {
            // Layered multi-scatter: boost spec by the inter-reflection energy, transmit the COMPLEMENT to the
            // diffuse layer (transFraction replaces the crude (1-F)), and recover the split-energy via the
            // metalness albedo boost. F0 for the spec layer = lerp(0.04*specRefl, albedo, metallic) (= our F0).
            MsEnergy ms = MultiScatterEnergy(F0, roughness, NdotV);
            float3 boost = MetalnessAlbedoBoost(metallic, albedo);
            specGain = ms.reflMult;
            diffAlbedo = albedo * (1.0 - metallic) * boost;            // metals lose diffuse; boost recovers mid-metalness
            kD = ms.transFraction;                                     // diffuse masked by what passes the spec layer
        } else {
            kD = (1.0 - F) * (1.0 - metallic);
        }
        diffuse = kD * diffAlbedo / PI * radiance * NdotL;
        specular = ClampSpecular(spec * specGain * radiance * NdotL, SpecClamp);   // V2: bound sun specular fireflies
    }

    // --- Clustered punctual lights (point/spot) ---
    float3 punctual = 0.0.xxx;
    if (PunctualCount > 0.5) {
        int cluster = ClusterIndexFor(i.Position.xy, worldPos);
        int2 range = ClusterGrid[cluster];   // {offset, count}
        for (int k = 0; k < range.y; k++) {
            uint li = ClusterIndex[range.x + k];
            GpuLight gl = ClusterLights[li];
            // Type branch: <0.5 point, <1.5 spot (both Cook-Torrance), <2.5 rect/area (LTC).
            if (gl.Color.w < 1.5)
                punctual += ShadePunctual(gl, N, V, worldPos, albedo, metallic, roughness, F0);
            else
                punctual += ShadeRect(gl, N, V, worldPos, albedo, metallic, roughness, F0);
        }
    }

    // Ambient: split-sum IBL when baked, flat fill otherwise.
    float NdotVamb = max(dot(N, V), 0.0);
    float3 ambient;
    if (UseIBL > 0.5) {
        float3 Famb = FresnelSchlickRoughness(NdotVamb, F0, roughness);
        // Multi-scatter on the ambient lobe: boost the prefiltered specular by the inter-reflection energy and
        // transmit the complement to the diffuse irradiance (matches the analytic-light layering above).
        MsEnergy msAmb;
        float3 ambDiffBoost = albedo;
        float3 kD;
        if (MsBrdfEnabled > 0.5) {
            msAmb = MultiScatterEnergy(F0, roughness, NdotVamb);
            ambDiffBoost = albedo * MetalnessAlbedoBoost(metallic, albedo);
            kD = msAmb.transFraction * (1.0 - metallic);
        } else {
            msAmb.reflMult = 1.0.xxx;
            kD = (1.0 - Famb) * (1.0 - metallic);
        }
        float3 irradiance = IrradianceMap.SampleLevel(LinearClamp, N, 0).rgb;
        // When a GI pass owns diffuse GI (UseIBLDiffuse=0 — Aurora OR Lumen's FAZ 6 screen-probe gather), suppress
        // the IBL diffuse-irradiance ambient here so the GI combine (which ADDS its own diffuse indirect) does not
        // double-count. Specular IBL below is untouched — the GI here is diffuse-only; reflections stay IBL/RT.
        float3 ambDiffAlbedo = (MsBrdfEnabled > 0.5) ? ambDiffBoost : albedo;
        float3 ambientDiffuse = (UseIBLDiffuse > 0.5) ? kD * irradiance * ambDiffAlbedo * ao : 0.0.xxx;
        float3 R = reflect(-V, N);
        float mip = clamp(roughness * PrefilterMaxMip, 0.0, PrefilterMaxMip);
        float3 prefiltered = PrefilterMap.SampleLevel(LinearClamp, R, mip).rgb;
        float2 brdf = BrdfLut.SampleLevel(LinearClamp, float2(NdotVamb, roughness), 0).rg;
        // SPECULAR OCCLUSION (Lagarde "GetSpecularOcclusion"): a rough/occluded surface's ambient-specular lobe
        // integrates the average prefiltered sky, so against a bright sky it can wash into a broad untextured veil.
        // Derive a specular AO from AO, NdotV and roughness so occluded / grazing / rough surfaces drop the excess
        // sky reflection (restoring material contrast) while smooth, open, face-on surfaces (water, glass, metal,
        // polished floor) keep their full, sharp reflection. A no-op (≈1) for the common smooth-and-open case.
        float specOcc = saturate(pow(max(NdotVamb + ao, 0.0), exp2(-16.0 * roughness - 1.0)) - 1.0 + ao);
        // Sky-IBL specular is gated the SAME way as the diffuse above. The prefiltered cube is the procedural
        // sky's average radiance with NO sky-visibility term (only short-range GTAO), so a CLOSED interior whose
        // walls never see the sky still ate the sky's bright, sun-tinted average as a broad untextured veil — the
        // exact diffuse leak fixed earlier, just on the specular lobe (user: orange tent on a roofed Bistro hall).
        // Reflections come from Lumen RT reflections / SSR (both sky-visibility-aware); IBL is the miss fallback.
        float3 ambientSpecular = (UseIBLSpecular > 0.5)
            ? prefiltered * (Famb * brdf.x + brdf.y) * specOcc * msAmb.reflMult : 0.0.xxx;
        ambient = ambientDiffuse + ambientSpecular;
    }
    else {
        ambient = Ambient * albedo * ao;
    }

    float3 litHdr = diffuse + specular + punctual + ambient + emissive;
    return float4(litHdr, 1.0);
}
