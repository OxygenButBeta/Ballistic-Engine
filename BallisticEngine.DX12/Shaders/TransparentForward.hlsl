// Forward TRANSPARENT PBR shader for the DX12 clustered-deferred renderer. Deferred lighting can't blend,
// so after the deferred opaque pass + sky, transparent materials (Material.Transparent) are drawn FORWARD,
// back-to-front, alpha-blended, depth-testing the G-buffer depth (LEqual, NO write). The shading is the
// SAME Cook-Torrance sun + split-sum IBL + cascaded PCF shadows as StandardOpaque/DeferredLighting, PLUS
// clustered punctual lights (point/spot) — material textures are sampled DIRECTLY (forward), not the
// G-buffer. Output is RAW HDR with straight alpha (the composite tonemaps; blend = SrcAlpha/InvSrcAlpha).
//
// CONVENTIONS (locked): System.Numerics row-major, HLSL column-major, CPU transposes on upload;
// mul(float4(pos,1), MVP) matches the CPU math. Vertex attrs arrive in 4 separate input slots.

cbuffer TransparentConstants : register(b0) {
    float4x4 Mvp;            // model * view * proj  (transposed on upload)
    float4x4 Model;          // model               (transposed) — world-space normals/tangents
    float4x4 View;           // world -> view       (transposed) — froxel lookup from view depth
    float3   LightDir;       float Exposure;
    float3   LightColor;     float Metallic;
    float3   Ambient;        float Roughness;
    float3   CameraPos;      float SpecularReflectance;
    float4   BaseColorFactor;
    float3   EmissiveFactor; float HasEmissive;
    float    NormalStrength; float NormalFlipY; float HasMetallicMap; float HasRoughnessMap;
    float    PackedOrm;      float Cutout;      float UseIBL; float PrefilterMaxMip;
    float    Opacity;        float PunctualCount; float2 ScreenSize;
    float2   ClusterNearFar; float2 _pad;
};

Texture2D DiffuseMap   : register(t0);
Texture2D NormalMap    : register(t1);
Texture2D MetallicMap  : register(t2);
Texture2D RoughnessMap : register(t3);
Texture2D AOMap        : register(t4);
Texture2D EmissiveMap  : register(t5);
TextureCube IrradianceMap   : register(t6);
TextureCube PrefilterMap    : register(t7);
Texture2D   BrdfLut         : register(t8);
Texture2DArray ShadowCascades : register(t9);   // sun cascade depth (R32_Float), manual PCF

// Clustered punctual lights (faithful to the GL / deferred clustered path).
struct GpuLight {
    float4 PosRange;     // xyz world pos, w range
    float4 Color;        // xyz radiance (HDR), w type (0 point / 1 spot)
    float4 DirCosOuter;  // xyz spot dir, w cosOuter
    float4 Extra;        // x cosInner, y shadowSlot, z sourceRadius, w pad
};
StructuredBuffer<GpuLight> ClusterLights : register(t10);
Buffer<int2>               ClusterGrid   : register(t11);  // per-cluster {offset, count}
Buffer<uint>               ClusterIndex  : register(t12);  // flat light-index list

SamplerState LinearWrap  : register(s0);
SamplerState LinearClamp : register(s1);

// Per-frame cascade matrices + shadow params (b1, shared layout with the forward/deferred FrameConstants).
cbuffer FrameConstants : register(b1) {
    float4x4 Cascade0, Cascade1, Cascade2, Cascade3;
    float4   CascadeBias;
    float    CascadeCountF; float ShadowsEnabled; float ShadowMapTexel; float CascadeBlend;
    // Shadows-volume tail — must match DX12HDRenderer.FrameConstants. Contact-shadow fields unused here.
    float    ShadowFiltering;     // 0 = hard, 1 = soft PCF, 2 = PCSS
    float    ShadowSoftness;      // PCSS / PCF penumbra scale
    float    ContactShadowsOn;
    float    ContactShadowLength;
    float    ContactShadowSteps;
    float    ContactShadowThickness;
    float    FramePad0, FramePad1;
};

// Froxel grid dims — must match Dx12ClusteredLights (16x9x24, log-Z).
static const int ClusterDimX = 16;
static const int ClusterDimY = 9;
static const int ClusterDimZ = 24;

static const float PI = 3.14159265359;
static const float EPS = 1e-6;

struct VSInput {
    float3 Pos     : POSITION;
    float3 Normal  : NORMAL;
    float2 Uv      : TEXCOORD0;
    float4 Tangent : TANGENT;
};
struct VSOutput {
    float4 Position : SV_Position;
    float3 NormalW  : NORMAL;
    float4 TangentW : TANGENT;
    float2 Uv       : TEXCOORD0;
    float3 PosW     : TEXCOORD1;
};

VSOutput VSMain(VSInput v) {
    VSOutput o;
    o.Position = mul(float4(v.Pos, 1.0), Mvp);
    o.PosW = mul(float4(v.Pos, 1.0), Model).xyz;
    o.NormalW = normalize(mul(float4(v.Normal, 0.0), Model).xyz);
    o.TangentW = float4(normalize(mul(float4(v.Tangent.xyz, 0.0), Model).xyz), v.Tangent.w);
    o.Uv = v.Uv;
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

float3 NormalFromMap(float2 uv, float3 Ngeom, float3 T, float bitangentSign) {
    float2 nxy = NormalMap.Sample(LinearWrap, uv).rg;
    if (NormalFlipY > 0.5) nxy.y = 1.0 - nxy.y;
    float2 xy = (nxy * 2.0 - 1.0) * max(NormalStrength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    float3 tn = normalize(float3(xy, z));
    float3 N = normalize(Ngeom);
    float3 Tn = normalize(T - N * dot(N, T));      // Gram-Schmidt
    float3 B = cross(N, Tn) * bitangentSign;
    return normalize(Tn * tn.x + B * tn.y + N * tn.z);
}

float CascadeMatrixApply(int c, float3 worldPos, out float3 proj) {
    float4x4 m = c == 0 ? Cascade0 : (c == 1 ? Cascade1 : (c == 2 ? Cascade2 : Cascade3));
    float4 clip = mul(float4(worldPos, 1.0), m);
    proj = clip.xyz;
    proj.xy = proj.xy * float2(0.5, -0.5) + 0.5;
    return max(abs(clip.x), abs(clip.y));
}

// Volume-driven sun shadow (hard / soft PCF / PCSS), shared logic with the deferred + opaque paths.
float ShadowTapHard(int c, float2 uv, float z, float bias) {
    float d = ShadowCascades.SampleLevel(LinearClamp, float3(uv, (float)c), 0).r;
    return (z - bias) <= d ? 1.0 : 0.0;
}
float ShadowPcf(int c, float2 base, float z, float bias, float radiusTexels) {
    float lit = 0.0;
    [unroll] for (int dy = -2; dy <= 2; dy++)
    [unroll] for (int dx = -2; dx <= 2; dx++)
        lit += ShadowTapHard(c, base + float2(dx, dy) * ShadowMapTexel * radiusTexels, z, bias);
    return lit / 25.0;
}
float ShadowPcss(int c, float2 base, float z, float bias) {
    float searchTexels = 2.0 + ShadowSoftness * 2.0;
    float blockerSum = 0.0; float blockerCount = 0.0;
    [unroll] for (int sy = -2; sy <= 2; sy++)
    [unroll] for (int sx = -2; sx <= 2; sx++) {
        float d = ShadowCascades.SampleLevel(LinearClamp, float3(base + float2(sx, sy) * ShadowMapTexel * searchTexels, (float)c), 0).r;
        if (d < z - bias) { blockerSum += d; blockerCount += 1.0; }
    }
    if (blockerCount < 0.5) return 1.0;
    float avgBlocker = blockerSum / blockerCount;
    float penumbra = max(z - avgBlocker, 0.0) / max(avgBlocker, 1e-4);
    float radiusTexels = clamp(penumbra * ShadowSoftness * 64.0, 0.75, 12.0);
    return ShadowPcf(c, base, z, bias, radiusTexels);
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
        return ShadowPcf(c, proj.xy, proj.z, bias, clamp(ShadowSoftness * 0.75, 0.5, 4.0));
    }
    return 1.0;
}

// V2 (fixes D3): spherical-source window `1/(d² + r²)` — finite near-field, no 1/max(d²,1e-4) firefly spike;
// identical to 1/d² once d ≫ r. Matches DeferredLighting.hlsl so opaque + transparent punctuals shade alike.
float DistanceAttenuation(float dist, float range, float sourceRadius) {
    const float rMin = 0.05;                                  // 5 cm floor so a delta light can't singularly spike
    float r = max(sourceRadius, rMin);
    float inv = 1.0 / (dist * dist + r * r);
    float t = saturate(1.0 - pow(dist / range, 4.0));
    return inv * t * t;
}

float3 ShadePunctual(GpuLight L, float3 N, float3 V, float3 worldPos, float3 albedo,
                     float metallic, float roughness, float3 F0) {
    float3 toLight = L.PosRange.xyz - worldPos;
    float dist = length(toLight);
    if (dist > L.PosRange.w) return 0.0.xxx;
    float3 Ld = toLight / max(dist, 1e-4);
    float atten = DistanceAttenuation(dist, L.PosRange.w, L.Extra.z);   // Extra.z = SourceRadius (V2 near-field window)
    if (atten <= 0.0) return 0.0.xxx;

    float3 radiance = L.Color.rgb * atten;
    if (L.Color.w >= 0.5) {
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
    return (kD * albedo / PI + spec) * radiance * NdotL;
}

int ClusterIndexFor(float2 pixel, float3 worldPos) {
    float viewZ = -mul(float4(worldPos, 1.0), View).z;   // positive view distance
    float near = ClusterNearFar.x, far = ClusterNearFar.y;
    int zSlice = (int)(log(max(viewZ, near) / near) / log(far / near) * (float)ClusterDimZ);
    zSlice = clamp(zSlice, 0, ClusterDimZ - 1);
    int2 tile = (int2)(pixel / (ScreenSize / float2(ClusterDimX, ClusterDimY)));
    tile = clamp(tile, int2(0, 0), int2(ClusterDimX - 1, ClusterDimY - 1));
    return tile.x + ClusterDimX * (tile.y + ClusterDimY * zSlice);
}

float4 PSMain(VSOutput i) : SV_Target {
    float4 albedoSample = DiffuseMap.Sample(LinearWrap, i.Uv);
    float3 albedo = albedoSample.rgb * BaseColorFactor.rgb;
    float alpha = saturate(albedoSample.a * BaseColorFactor.a * Opacity);

    float3 mr = MetallicMap.Sample(LinearWrap, i.Uv).rgb;
    float metallicSample = HasMetallicMap > 0.5 ? (PackedOrm > 0.5 ? mr.b : mr.r) : 1.0;
    float metallic = saturate(metallicSample * Metallic);
    float roughSample = HasRoughnessMap > 0.5 ? RoughnessMap.Sample(LinearWrap, i.Uv).r
                                              : (PackedOrm > 0.5 ? mr.g : 1.0);
    float roughness = clamp(roughSample * Roughness, 0.045, 1.0);
    float ao = AOMap.Sample(LinearWrap, i.Uv).r;

    float3 N = NormalFromMap(i.Uv, i.NormalW, i.TangentW.xyz, i.TangentW.w);
    float3 V = normalize(CameraPos - i.PosW);

    // Cook-Torrance direct sun.
    float3 F0 = lerp(0.08 * SpecularReflectance.xxx, albedo, metallic);
    float3 D = normalize(LightDir);
    float NdotL = max(dot(N, D), 0.0);
    float3 diffuse = 0, specular = 0;
    if (NdotL > 0.0) {
        float shadow = SunShadow(N, D, i.PosW);
        float3 radiance = LightColor * shadow;
        float3 H = normalize(V + D);
        float NDF = DistributionGGX(N, H, roughness);
        float G = GeometrySmith(N, V, D, roughness);
        float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
        float NdotV = max(dot(N, V), 0.0);
        float3 spec = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);
        float3 kD = (1.0 - F) * (1.0 - metallic);
        diffuse = kD * albedo / PI * radiance * NdotL;
        specular = spec * radiance * NdotL;
    }

    // Clustered punctual lights (point/spot) — same froxel lookup as the deferred pass.
    float3 punctual = 0.0.xxx;
    if (PunctualCount > 0.5) {
        int cluster = ClusterIndexFor(i.Position.xy, i.PosW);
        int2 range = ClusterGrid[cluster];   // {offset, count}
        for (int k = 0; k < range.y; k++) {
            uint li = ClusterIndex[range.x + k];
            punctual += ShadePunctual(ClusterLights[li], N, V, i.PosW, albedo, metallic, roughness, F0);
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
    } else {
        ambient = Ambient * albedo * ao;
    }
    float3 emissive = (HasEmissive > 0.5)
        ? EmissiveMap.Sample(LinearWrap, i.Uv).rgb * EmissiveFactor : 0.0.xxx;

    // RAW HDR + straight alpha (blend = SrcAlpha/InvSrcAlpha; composite tonemaps).
    float3 litHdr = diffuse + specular + punctual + ambient + emissive;
    return float4(litHdr, alpha);
}
