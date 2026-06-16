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
    float    Pad4, Pad5, Pad6;
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
cbuffer FrameConstants : register(b1) {
    float4x4 Cascade0, Cascade1, Cascade2, Cascade3;
    float4   CascadeBias;
    float    CascadeCountF; float ShadowsEnabled; float ShadowMapTexel; float CascadeBlend;
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

// Clustered punctual lights (faithful to the GL clustered path).
struct GpuLight {
    float4 PosRange;     // xyz world pos, w range
    float4 Color;        // xyz radiance (HDR), w type (0 point / 1 spot)
    float4 DirCosOuter;  // xyz spot dir, w cosOuter
    float4 Extra;        // x cosInner, y shadowSlot, z sourceRadius, w pad
};
StructuredBuffer<GpuLight> ClusterLights : register(t9);
Buffer<int2>               ClusterGrid   : register(t10);  // per-cluster {offset, count}
Buffer<uint>               ClusterIndex  : register(t11);  // flat light-index list
Texture2D RtShadowMask     : register(t12);                // ray-traced sun shadow (1 lit / 0 shadowed)
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

float CascadeMatrixApply(int c, float3 worldPos, out float3 proj) {
    float4x4 m = c == 0 ? Cascade0 : (c == 1 ? Cascade1 : (c == 2 ? Cascade2 : Cascade3));
    float4 clip = mul(float4(worldPos, 1.0), m);
    proj = clip.xyz;
    proj.xy = proj.xy * float2(0.5, -0.5) + 0.5;
    return max(abs(clip.x), abs(clip.y));
}

float SunShadow(float3 N, float3 L, float3 worldPos) {
    if (ShadowsEnabled < 0.5) return 1.0;
    float ndl = saturate(dot(N, L));
    int count = (int)CascadeCountF;
    for (int c = 0; c < count; c++) {
        float3 proj;
        float edge = CascadeMatrixApply(c, worldPos, proj);
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0) continue;
        float bias = max(CascadeBias[c] * (1.0 - ndl), CascadeBias[c] * 0.1);
        float lit = 0.0;
        [unroll] for (int dy = -1; dy <= 1; dy++)
        [unroll] for (int dx = -1; dx <= 1; dx++) {
            float2 uv = proj.xy + float2(dx, dy) * ShadowMapTexel;
            float d = ShadowCascades.SampleLevel(LinearClamp, float3(uv, (float)c), 0).r;
            lit += (proj.z - bias) <= d ? 1.0 : 0.0;
        }
        return lit / 9.0;
    }
    return 1.0;
}

// Unproject a screen UV + depth into world space (DX NDC: xy [-1,1] with y flipped, z = depth [0,1]).
float3 WorldPosFromDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
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
    float3 kD = (1.0 - F) * (1.0 - metallic);
    float3 diffuseTerm = kD * albedo / PI * radiance * NdotL;
    float3 specTerm = ClampSpecular(spec * radiance * NdotL, SpecClamp);   // V2: bound punctual specular fireflies
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
    float ao = g2.b;

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
                                            : SunShadow(N, D, worldPos);
        float3 radiance = LightColor * shadow;
        float3 H = normalize(V + D);
        float NDF = DistributionGGX(N, H, roughness);
        float G = GeometrySmith(N, V, D, roughness);
        float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
        float NdotV = max(dot(N, V), 0.0);
        float3 spec = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);
        float3 kD = (1.0 - F) * (1.0 - metallic);
        diffuse = kD * albedo / PI * radiance * NdotL;
        specular = ClampSpecular(spec * radiance * NdotL, SpecClamp);   // V2: bound sun specular fireflies
    }

    // --- Clustered punctual lights (point/spot) ---
    float3 punctual = 0.0.xxx;
    if (PunctualCount > 0.5) {
        int cluster = ClusterIndexFor(i.Position.xy, worldPos);
        int2 range = ClusterGrid[cluster];   // {offset, count}
        for (int k = 0; k < range.y; k++) {
            uint li = ClusterIndex[range.x + k];
            punctual += ShadePunctual(ClusterLights[li], N, V, worldPos, albedo, metallic, roughness, F0);
        }
    }

    // Ambient: split-sum IBL when baked, flat fill otherwise.
    float NdotVamb = max(dot(N, V), 0.0);
    float3 ambient;
    if (UseIBL > 0.5) {
        float3 Famb = FresnelSchlickRoughness(NdotVamb, F0, roughness);
        float3 kD = (1.0 - Famb) * (1.0 - metallic);
        float3 irradiance = IrradianceMap.SampleLevel(LinearClamp, N, 0).rgb;
        float3 ambientDiffuse = kD * irradiance * albedo * ao;
        float3 R = reflect(-V, N);
        float mip = clamp(roughness * PrefilterMaxMip, 0.0, PrefilterMaxMip);
        float3 prefiltered = PrefilterMap.SampleLevel(LinearClamp, R, mip).rgb;
        float2 brdf = BrdfLut.SampleLevel(LinearClamp, float2(NdotVamb, roughness), 0).rg;
        float3 ambientSpecular = prefiltered * (Famb * brdf.x + brdf.y) * ao;
        ambient = ambientDiffuse + ambientSpecular;
    }
    else {
        ambient = Ambient * albedo * ao;
    }

    float3 litHdr = diffuse + specular + punctual + ambient + emissive;
    return float4(litHdr, 1.0);
}
