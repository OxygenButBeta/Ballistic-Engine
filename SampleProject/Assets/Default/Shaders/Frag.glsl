#version 460 core
// 460: SSBOs in the fragment stage (clustered Forward+ light lists) need GLSL 430+; the engine runs a
// GL 4.6 core context. samplerCubeArray (local reflection probes) is core since 4.0.

// --- Inputs from vertex shader ---
in vec2 texCoord;
in vec3 fragNormal;
in vec3 fragPos;
in mat3 fragTBN;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 NormalRough; // world normal (xyz, 0..1) + roughness, for SSR/TAA
layout(location = 2) out vec4 AlbedoGBuf;  // linear DIFFUSE albedo (rgb) for deferred GI receiver reflectance

// --- Texture maps ---
uniform sampler2D Diffuse;
uniform sampler2D Normal;
uniform sampler2D Metallic;
uniform sampler2D Roughness;
uniform sampler2D AO;
uniform sampler2D Emissive;
uniform samplerCube Skybox;
uniform samplerCube IrradianceMap;
uniform samplerCube PrefilteredEnvMap;
uniform sampler2D BRDF_LUT;
uniform sampler2DArrayShadow ShadowCascades;
uniform sampler2DArray ShadowCascadesRaw;     // same texture, non-compare sampler (PCSS blockers)
uniform sampler2DArrayShadow PunctualShadows; // 512x512 array: spots layers 0..3, point faces after
uniform sampler2D ScreenAO;
uniform sampler3D ProbeSH0;   // baked irradiance probe volume: L1 SH coefficient grids
uniform sampler3D ProbeSH1;
uniform sampler3D ProbeSH2;
uniform sampler3D ProbeSH3;
uniform samplerCubeArray ReflectionProbes;    // baked local reflection cubes, one layer per cell
uniform isampler3D ReflectionCellToLayer;     // cell -> layer index, or -1 = fall back to skybox

// --- Pass constants (std140, binding 0 via the renderer) ---
// One block shared by every lit program and uploaded once per pass. The declaration MUST be
// textually identical in Vert.glsl and Frag.glsl (GLSL link rule). Member names match the old
// plain uniforms so shading code is unchanged.
const int MAX_POINT_LIGHTS = 8;
const int MAX_SPOT_LIGHTS = 4;
const int MAX_CASCADES = 4;
const int MAX_SHADOWED_SPOTS = 4;
const int MAX_SHADOWED_POINTS = 2;

layout(std140) uniform PassData {
    mat4 view;
    mat4 projection;
    mat4 SkyRotation;
    mat4 CascadeMatrices[MAX_CASCADES];
    mat4 SpotShadowMatrix[MAX_SHADOWED_SPOTS];
    mat4 PointShadowMatrix[MAX_SHADOWED_POINTS * 6];

    vec4 CascadeBias;
    vec4 CascadeTexelWorld;
    vec4 CascadeDepthRangeW;

    vec3 CameraPos;               float ShadowStrength;
    vec3 LightDirection;          float SunAngularRadius;
    vec3 LightColor;              float CascadeBlend;
    vec3 AmbientLight;            float ShadowSoftness;
    vec3 ShadowColor;             float minRoughness;
    vec3 AmbientTint;             float ReflectionIntensity;
    vec3 FogColor;                float FogDensity;
    vec3 ProbeVolumeMin;          float ProbeExposure;
    vec3 ProbeVolumeInvSize;      float SkyExposure;
    vec3 ReflectionVolumeMin;     float MaxPrefilterMips;
    vec3 ReflectionVolumeInvSize; float ReflectionMaxMips;

    vec3 PointLightPosition[MAX_POINT_LIGHTS];
    vec3 PointLightColor[MAX_POINT_LIGHTS];
    float PointLightRange[MAX_POINT_LIGHTS];
    vec3 SpotLightPosition[MAX_SPOT_LIGHTS];
    vec3 SpotLightDirection[MAX_SPOT_LIGHTS];
    vec3 SpotLightColor[MAX_SPOT_LIGHTS];
    float SpotLightRange[MAX_SPOT_LIGHTS];
    float SpotLightCosInner[MAX_SPOT_LIGHTS];
    float SpotLightCosOuter[MAX_SPOT_LIGHTS];
    int SpotShadowSlot[MAX_SPOT_LIGHTS];
    float SpotShadowBias[MAX_SHADOWED_SPOTS];
    int PointShadowSlot[MAX_POINT_LIGHTS];
    float PointShadowBias[MAX_SHADOWED_POINTS];

    vec2 ScreenSize;
    int PointLightCount;
    int SpotLightCount;
    int CascadeCount;
    int ShadowFiltering;
    int renderMode;
    int ReflectionGridX;
    int ReflectionGridY;
    int ReflectionGridZ;
    bool UseIBL;
    bool UseProbeVolume;
    bool UseReflectionVolume;
    bool HasScreenAO;
    bool ReflectionBlendWithSky;
    bool EnableAtmosphericScattering;
    float ReflectionIntensityLocal;
    float ProbeIntensity;            // GlobalIllumination volume: diffuse-probe ambient strength (1 = unchanged)
    float AmbientFloor;              // tiny shadow-fill so interiors never crush to pure black (default ~0.03)
};

// --- Material controls (plain uniforms: these change per draw) ---
uniform vec4 BaseColorFactor;  // glTF baseColorFactor: tints the albedo map (and its alpha)
uniform float MetallicMultiplier;   // material metallicFactor x debug global
uniform float RoughnessMultiplier;  // material roughnessFactor x debug global
uniform float SpecularReflectance;  // glTF KHR_materials_specular: dielectric F0 = 0.08*this (0.5 = 4%)
uniform float Clearcoat;            // glTF KHR_materials_clearcoat: thin lacquer layer strength (0 = none)
uniform float ClearcoatRoughness;   // the coat's own roughness (low = sharp varnish reflection)
uniform bool PackedOrm;        // Metallic tex = (occlusion, roughness, metallic) RGB
uniform bool HasMetallicMap;   // metallic texture assigned (otherwise the factor stands alone)
uniform bool HasRoughnessMap;  // separate roughness texture assigned
uniform float NormalStrength;
uniform bool NormalFlipY;
uniform vec3 EmissiveFactor;   // color * intensity
uniform bool HasEmissive;
uniform bool AlphaBlend;
uniform float Opacity;
uniform bool AlphaCutout;      // masked materials (foliage): discard below 0.5 alpha

// --- CLUSTERED FORWARD (Forward+) light lists ---
// When UseClustered is true the fragment loops only the lights in ITS cluster (froxel) instead of
// the capped MAX_POINT/SPOT arrays in PassData. Lights live in an SSBO (no 8/4 cap); a compute pass
// (ClusterCull_Comp) filled the per-cluster (offset,count) grid + the flat index list. The legacy
// PassData arrays remain for the fallback path (UseClustered=false / no SSBO support).
struct GpuLight {
    vec4 posRange;    // xyz world pos, w range
    vec4 lcolor;      // xyz pre-exposed radiance, w type (0 = point, 1 = spot)
    vec4 dirCosOuter; // xyz spot dir (world), w cosOuter
    vec4 extra;       // x cosInner, y shadowSlot (-1 none), zw pad
};
layout(std430, binding = 12) readonly buffer ClusterLightBuf  { GpuLight clusterLights[]; };
layout(std430, binding = 14) readonly buffer ClusterGridBuf   { ivec2 clusterGrid[]; };   // (offset,count)
layout(std430, binding = 15) readonly buffer ClusterIndexBuf  { int clusterLightIndex[]; };
uniform bool  UseClustered;
uniform int   ClusterDimX;
uniform int   ClusterDimY;
uniform int   ClusterDimZ;
uniform vec2  ClusterNearFar;   // x near, y far

const float PI = 3.14159265359;
const float EPS = 1e-6;

// ---------------- PBR helpers ----------------
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom + EPS);
}

float GeometrySchlickGGX(float NdotV, float roughness)
{
    float k = (roughness + 1.0);
    k = (k * k) / 8.0;
    return NdotV / max(NdotV * (1.0 - k) + k, EPS);
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    return GeometrySchlickGGX(max(dot(N, V), 0.0), roughness) *
           GeometrySchlickGGX(max(dot(N, L), 0.0), roughness);
}

vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

vec3 FresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness)
{
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(1.0 - cosTheta, 5.0);
}

// Cook-Torrance contribution of one light, accumulated into separate diffuse/specular
// terms so blended glass can fade transmission without dimming its reflections.
void ShadeLight(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo, float metallic, float roughness, vec3 F0,
                inout vec3 diffuseAcc, inout vec3 specularAcc)
{
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0)
        return;

    vec3 H = normalize(V + L);
    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    float NdotV = max(dot(N, V), 0.0);
    vec3 specular = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);

    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    diffuseAcc += kD * albedo / PI * radiance * NdotL;
    specularAcc += specular * radiance * NdotL;
}

// AREA (sphere) light shading — Karis representative point. A punctual light with a physical
// SourceRadius is a small sphere, not a delta point: its specular highlight should have real
// angular SIZE (a soft disc), not a pinpoint. Find the point on the light sphere closest to the
// reflection ray and shade from there, with the Karis energy normalization so the brighter-but-
// -wider highlight conserves energy. sourceRadius 0 -> degenerates EXACTLY to ShadeLight (the
// representative point collapses to the light centre, the normalization -> 1), so the default
// (radius 0) is byte-identical. `unforced` toLight = lightPos - fragPos (NOT normalized), dist.
void ShadeLightArea(vec3 N, vec3 V, vec3 Lc, float dist, float sourceRadius, vec3 radiance,
                    vec3 albedo, float metallic, float roughness, vec3 F0,
                    inout vec3 diffuseAcc, inout vec3 specularAcc) {
    float NdotL = max(dot(N, Lc), 0.0);
    if (NdotL <= 0.0)
        return;

    // Diffuse uses the light-centre direction (area diffuse is ~the same as point diffuse here).
    vec3 H0 = normalize(V + Lc);
    vec3 F = FresnelSchlick(max(dot(H0, V), 0.0), F0);
    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    diffuseAcc += kD * albedo / PI * radiance * NdotL;

    // Specular: representative point on the sphere of angular radius alpha = sourceRadius/dist.
    vec3 R = reflect(-V, N);
    vec3 toCentre = Lc;                                  // already normalized centre direction
    vec3 centreToRay = dot(toCentre, R) * R - toCentre;  // perpendicular from centre to the ray
    float radFrac = clamp(sourceRadius / max(dist, 1e-3), 0.0, 1.0);
    vec3 Ls = normalize(toCentre + centreToRay * clamp(radFrac / max(length(centreToRay), 1e-4), 0.0, 1.0));

    // Karis energy normalization: a wider highlight must not also be brighter. Widen the effective
    // roughness by the light's angular size and rescale by (a/a')^2.
    float a = max(roughness * roughness, 1e-3);
    float aPrime = clamp(a + radFrac * 0.5, 0.0, 1.0);
    float sphereNorm = (a / aPrime); sphereNorm *= sphereNorm;

    float NdotLs = max(dot(N, Ls), 0.0);
    vec3 H = normalize(V + Ls);
    float NDF = DistributionGGX(N, H, roughness);
    float Gs = GeometrySmith(N, V, Ls, roughness);
    vec3 Fs = FresnelSchlick(max(dot(H, V), 0.0), F0);
    float NdotV = max(dot(N, V), 0.0);
    vec3 specular = (NDF * Gs * Fs) / max(4.0 * NdotV * NdotLs, EPS) * sphereNorm;
    specularAcc += specular * radiance * NdotLs;
}

// Sun shading: diffuse from the disk-center direction, specular from the representative
// point on the sun cone closest to the reflection ray (Karis), so the highlight has the
// sun's physical angular size instead of being a dimensionless delta spike.
void ShadeSun(vec3 N, vec3 V, vec3 D, vec3 radiance, vec3 albedo, float metallic, float roughness, vec3 F0,
              inout vec3 diffuseAcc, inout vec3 specularAcc)
{
    float NdotD = max(dot(N, D), 0.0);
    if (NdotD <= 0.0)
        return;

    vec3 R = reflect(-V, N);
    float cosR = cos(SunAngularRadius);
    float DdotR = dot(D, R);
    vec3 L;
    if (DdotR >= cosR) {
        L = R; // reflection ray hits the disk: peak of the highlight
    }
    else {
        vec3 S = R - DdotR * D;
        L = normalize(D * cosR + normalize(S + vec3(EPS)) * sin(SunAngularRadius));
    }

    float NdotL = max(dot(N, L), 0.0);
    vec3 H = normalize(V + L);
    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
    float NdotV = max(dot(N, V), 0.0);
    vec3 specular = (NDF * G * F) / max(4.0 * NdotV * NdotL, EPS);

    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    diffuseAcc += kD * albedo / PI * radiance * NdotD;
    specularAcc += specular * radiance * NdotL;
}

// UE-style windowed inverse-square falloff.
float DistanceAttenuation(float dist, float range)
{
    float window = clamp(1.0 - pow(dist / range, 4.0), 0.0, 1.0);
    return window * window / max(dist * dist, 1e-4);
}

// ---------------- Shadows (cascaded) ----------------
// 5x5 PCF inside one cascade layer; each tap is already a hardware 2x2 compare.
float CascadePCF(int cascade, vec3 proj, float bias)
{
    vec2 texel = 1.0 / vec2(textureSize(ShadowCascades, 0).xy);
    float lit = 0.0;
    for (int x = -2; x <= 2; x++)
        for (int y = -2; y <= 2; y++)
            lit += texture(ShadowCascades,
                vec4(proj.xy + vec2(x, y) * texel, float(cascade), proj.z - bias));
    return lit / 25.0;
}

// Golden-angle spiral disk: well-distributed taps from very few samples.
vec2 VogelDisk(int i, int count, float phi)
{
    float r = sqrt((float(i) + 0.5) / float(count));
    float theta = float(i) * 2.39996323 + phi;
    return vec2(r * cos(theta), r * sin(theta));
}

float InterleavedNoise(vec2 pix)
{
    return fract(52.9829189 * fract(dot(pix, vec2(0.06711056, 0.00583715))));
}

// PCSS (contact-hardening soft shadows): search the raw depths for the average blocker, size
// the penumbra from the receiver-blocker gap and the sun's angular radius, then Vogel-disk
// PCF at that radius. Shadows are razor sharp at contact and soften with distance, exactly
// like the real sun; the per-pixel rotated disk dithers the penumbra and TAA resolves it.
float CascadePCSS(int cascade, vec3 proj, float bias)
{
    vec2 texel = 1.0 / vec2(textureSize(ShadowCascades, 0).xy);
    float phi = InterleavedNoise(gl_FragCoord.xy) * 6.2831853;

    // 1) Blocker search over a fixed window.
    const float SearchTexels = 6.0;
    float blockerSum = 0.0;
    float blockerCount = 0.0;
    for (int i = 0; i < 12; i++) {
        vec2 offset = VogelDisk(i, 12, phi) * SearchTexels * texel;
        float d = texture(ShadowCascadesRaw, vec3(proj.xy + offset, float(cascade))).r;
        if (d < proj.z - bias) {
            blockerSum += d;
            blockerCount += 1.0;
        }
    }
    if (blockerCount < 0.5)
        return 1.0; // nothing between us and the sun

    // 2) Penumbra radius: world gap to the average blocker x tan(sun angular radius).
    float avgBlocker = blockerSum / blockerCount;
    float gapWorld = max(proj.z - avgBlocker, 0.0) * CascadeDepthRangeW[cascade];
    float penumbraWorld = gapWorld * tan(SunAngularRadius) * ShadowSoftness;
    float radiusTexels = clamp(penumbraWorld / max(CascadeTexelWorld[cascade], 1e-5), 0.75, 16.0);

    // 3) Vogel-disk PCF at the penumbra radius (hardware-compare taps).
    float lit = 0.0;
    for (int i = 0; i < 16; i++) {
        vec2 offset = VogelDisk(i, 16, phi) * radiusTexels * texel;
        lit += texture(ShadowCascades, vec4(proj.xy + offset, float(cascade), proj.z - bias));
    }
    return lit / 16.0;
}

// Filtering dispatcher: 0 = hard single tap, 1 = fixed 5x5 PCF, 2 = PCSS.
float SampleCascade(int cascade, vec3 proj, float bias)
{
    if (ShadowFiltering == 0)
        return texture(ShadowCascades, vec4(proj.xy, float(cascade), proj.z - bias));
    if (ShadowFiltering == 2)
        return CascadePCSS(cascade, proj, bias);
    return CascadePCF(cascade, proj, bias);
}

// ---------------- Contact shadows ----------------
// Short screen-space ray march toward the sun against the prepass depth buffer. Catches the
// tiny, high-frequency occlusions the cascade shadow maps miss at their texel resolution
// (object-to-ground contact, small props, fine geometry) so things look grounded instead of
// floating. Returns 1.0 (lit) when disabled.
uniform sampler2D SceneDepth;          // prepass depth (complete before the opaque pass)
uniform bool ContactShadowsOn;
uniform float ContactShadowLength;     // world-space march distance (metres)
uniform int ContactShadowSteps;
uniform float ContactShadowThickness;  // depth-difference window that counts as a hit (metres)

// View-space Z from a window-depth sample, given a PRECOMPUTED inverse projection. The old form
// called inverse(projection) INSIDE this function, i.e. a full 4x4 matrix inversion per contact-
// shadow step per pixel (ContactShadowSteps inversions/pixel) — a serious pointless cost (review).
// inverse(projection) is loop-invariant, so the caller hoists it out and passes it here. Only the
// z,w rows of the inverse matter for view-Z, so this is just two dot products.
float ViewZFromDepth(float d, mat4 invP) {
    vec4 clip = vec4(0.0, 0.0, d * 2.0 - 1.0, 1.0);
    vec4 v = invP * clip;
    return v.z / v.w;
}

float ContactShadow(vec3 worldPos, vec3 L) {
    if (!ContactShadowsOn || ContactShadowLength <= 0.0)
        return 1.0;

    mat4 invP = inverse(projection);   // ONCE per pixel, hoisted out of the march loop
    vec3 viewPos = (view * vec4(worldPos, 1.0)).xyz;
    vec3 viewL = normalize(mat3(view) * L);
    float stepLen = ContactShadowLength / float(ContactShadowSteps);
    float dither = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));

    for (int i = 1; i <= ContactShadowSteps; i++) {
        vec3 sampleView = viewPos + viewL * stepLen * (float(i) - dither);
        vec4 clip = projection * vec4(sampleView, 1.0);
        if (clip.w <= 0.0) break;
        vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;

        float sceneZ = ViewZFromDepth(texture(SceneDepth, uv).r, invP);
        float diff = sceneZ - sampleView.z;
        if (diff > 0.003 && diff < ContactShadowThickness)
            return 0.0;
    }
    return 1.0;
}

// Pick the first cascade whose (snapped, sphere-fit) ortho box contains the fragment, and
// cross-fade into the next cascade near the box edge so the resolution step never pops.
float SunShadow(vec3 N, vec3 L)
{
    float ndl = clamp(dot(N, L), 0.0, 1.0);
    for (int c = 0; c < CascadeCount && c < MAX_CASCADES; c++) {
        vec4 clip = CascadeMatrices[c] * vec4(fragPos, 1.0);
        float edge = max(abs(clip.x), abs(clip.y));
        vec3 proj = clip.xyz * 0.5 + 0.5; // ortho: w == 1
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0)
            continue;

        float bias = max(CascadeBias[c] * (1.0 - ndl), CascadeBias[c] * 0.1);
        float lit = SampleCascade(c, proj, bias);

        // Blend zone: the outer CascadeBlend fraction fades into the next cascade.
        float fade = smoothstep(1.0 - CascadeBlend, 1.0, edge);
        if (fade > 0.0 && c + 1 < CascadeCount) {
            vec4 clipN = CascadeMatrices[c + 1] * vec4(fragPos, 1.0);
            vec3 projN = clipN.xyz * 0.5 + 0.5;
            float biasN = max(CascadeBias[c + 1] * (1.0 - ndl), CascadeBias[c + 1] * 0.1);
            lit = mix(lit, SampleCascade(c + 1, projN, biasN), fade);
        }
        return lit;
    }
    return 1.0; // beyond every cascade: lit
}

// ---------------- Punctual shadows ----------------
float PunctualPCF(int layer, vec3 proj, float bias)
{
    // Rotated Vogel-disk PCF (12 taps) instead of the old fixed 3x3 box: softer, dithered punctual
    // shadows (TAA resolves the per-pixel rotation), the same softness technique the sun cascades
    // use. The wider rotated kernel also spreads/dithers the cube-face boundary so the hard 3x3 seam
    // is far less visible. Radius scales with ShadowSoftness. Hardware-compare taps (sampler2DArrayShadow).
    vec2 texel = 1.0 / vec2(textureSize(PunctualShadows, 0).xy);
    float phi = InterleavedNoise(gl_FragCoord.xy) * 6.2831853;
    float radiusTexels = clamp(ShadowSoftness * 1.5, 0.75, 6.0);
    float lit = 0.0;
    for (int i = 0; i < 12; i++) {
        vec2 offset = VogelDisk(i, 12, phi) * radiusTexels * texel;
        // Clamp keeps taps inside the cube face (proj.xy in [0,1]); the dithered spread hides the seam.
        vec2 uv = clamp(proj.xy + offset, vec2(0.001), vec2(0.999));
        lit += texture(PunctualShadows, vec4(uv, float(layer), proj.z - bias));
    }
    return lit / 12.0;
}

float SpotShadow(int slot, vec3 nrm, float ndl)
{
    // NORMAL-OFFSET BIAS: offset the sample position along the surface normal before projecting,
    // scaled to taper off as the surface faces the light (sin of the angle ~= sqrt(1-ndl^2)).
    // Depth-precision-independent — fixes acne + peter-panning robustly where a constant window-
    // depth bias couldn't (the bias is authored in world units but proj.z is NON-LINEAR depth, so a
    // flat subtract over/under-biased with distance). A small residual depth bias still backs it up.
    float slope = sqrt(clamp(1.0 - ndl * ndl, 0.0, 1.0));
    vec3 offsetPos = fragPos + nrm * (SpotShadowBias[slot] * 12.0 * (0.3 + slope));
    vec4 clip = SpotShadowMatrix[slot] * vec4(offsetPos, 1.0);
    if (clip.w <= 0.0)
        return 1.0;
    vec3 proj = clip.xyz / clip.w * 0.5 + 0.5;
    if (proj.z >= 1.0 || any(lessThan(proj.xy, vec2(0.0))) || any(greaterThan(proj.xy, vec2(1.0))))
        return 1.0;
    float bias = SpotShadowBias[slot] * mix(3.0, 1.0, ndl);
    return PunctualPCF(slot, proj, bias);
}

// Dominant axis of the light->fragment direction picks the cube face.
int CubeFace(vec3 d)
{
    vec3 a = abs(d);
    if (a.x >= a.y && a.x >= a.z) return d.x > 0.0 ? 0 : 1;
    if (a.y >= a.z)               return d.y > 0.0 ? 2 : 3;
    return d.z > 0.0 ? 4 : 5;
}

float PointShadow(int slot, vec3 lightPos, vec3 nrm, float ndl)
{
    // NORMAL-OFFSET BIAS (see SpotShadow): offset along the normal before projecting so the fix is
    // depth-precision-independent (a constant window-depth bias over/under-biased with distance on
    // the non-linear cube-face depth). The face is picked from the OFFSET position too.
    float slope = sqrt(clamp(1.0 - ndl * ndl, 0.0, 1.0));
    vec3 offsetPos = fragPos + nrm * (PointShadowBias[slot] * 12.0 * (0.3 + slope));
    int face = CubeFace(offsetPos - lightPos);
    vec4 clip = PointShadowMatrix[slot * 6 + face] * vec4(offsetPos, 1.0);
    if (clip.w <= 0.0)
        return 1.0;
    vec3 proj = clip.xyz / clip.w * 0.5 + 0.5;
    if (proj.z >= 1.0)
        return 1.0;
    proj.xy = clamp(proj.xy, vec2(0.002), vec2(0.998)); // PCF taps must not cross the face edge
    float bias = PointShadowBias[slot] * mix(3.0, 1.0, ndl);
    return PunctualPCF(MAX_SHADOWED_SPOTS + slot * 6 + face, proj, bias);
}

// ---------------- Normal map helper ----------------
vec3 GetNormalFromMap(vec2 uv, vec3 geomNormal, mat3 TBN, float strength)
{
    vec2 nxy = texture(Normal, uv).rg;
    if (NormalFlipY) nxy.y = 1.0 - nxy.y;
    // Reconstruct Z from XY rather than reading the blue channel: BC5-compressed normal maps store
    // only X and Y (blue reads 0), and a tangent-space normal's Z is always the positive root anyway.
    // This is identical to reading B for an uncompressed map whose Z was authored correctly, so it's
    // safe for both the BC5 and the RGBA8-fallback paths.
    vec2 xy = nxy * 2.0 - 1.0;
    // Scale the tangent-space XY (the bump tilt) by strength, glTF normalScale-style. This reads
    // far stronger than lerping the WORLD normal toward flat and allows strength > 1 to exaggerate
    // subtle maps past their authored amplitude (the old mix() form capped at the authored normal).
    xy *= max(strength, 0.0);
    float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
    vec3 tangentNormal = normalize(vec3(xy, z));
    return normalize(TBN * tangentNormal);
}

// ---------------- IBL helpers ----------------
// The skybox geometry is rotated by SkyRotation, so world directions sample the
// cubemap (and the maps convolved from it) through the inverse rotation.
vec3 SkyDir(vec3 d)
{
    return transpose(mat3(SkyRotation)) * d;
}

// Box-projection parallax-corrected sample of ONE reflection-volume cell's local cube. The cube was
// captured at the cell CENTRE; intersecting R with the cell AABB and re-aiming from the centre makes
// adjacent cells reflect the same world point (kills cell-boundary mis-registration). Returns false
// (no contribution) when the cell has no baked cube (layer < 0). `cell` is clamped by the caller.
bool SampleLocalProbe(ivec3 cell, vec3 R, vec3 fragP, vec3 cellSize, float localMip, out vec3 outRgb)
{
    outRgb = vec3(0.0);
    int layer = texelFetch(ReflectionCellToLayer, cell, 0).r;
    if (layer < 0)
        return false;
    vec3 cellMin = ReflectionVolumeMin + vec3(cell) * cellSize;
    vec3 cellCtr = cellMin + 0.5 * cellSize;
    vec3 invR = 1.0 / R;
    vec3 t1 = (cellMin            - fragP) * invR;
    vec3 t2 = (cellMin + cellSize - fragP) * invR;
    vec3 tmax = max(t1, t2);
    float t = min(min(tmax.x, tmax.y), tmax.z);
    vec3 sampleDir = (fragP + R * max(t, 0.0)) - cellCtr;
    outRgb = textureLod(ReflectionProbes, vec4(sampleDir, float(layer)), localMip).rgb;
    return true;
}

// ---------------- Main ----------------
void main()
{
    NormalRough = vec4(0.0, 0.0, 0.0, 1.0); // overwritten by the lit path; sane for debug exits

    // --- Debug only modes ---
    if (renderMode == 1) { FragColor = vec4(texture(Diffuse, texCoord).rgb, 1.0); return; }
    if (renderMode == 2) {
        vec3 n = GetNormalFromMap(texCoord, normalize(fragNormal), fragTBN, NormalStrength);
        FragColor = vec4(n * 0.5 + 0.5, 1.0);
        return;
    }
    // --- Sample maps ---
    // Metallic slot is either a grayscale mask (read R) or an ORM-packed map
    // (occlusion=R, roughness=G, metallic=B). Without a separate roughness texture the
    // packed G channel (or fully-rough 1.0) is used.
    vec4 albedoSample = texture(Diffuse, texCoord);
    if (AlphaCutout && albedoSample.a < 0.5)
        discard;
    vec3 albedo = albedoSample.rgb * BaseColorFactor.rgb;
    vec3 mrSample = texture(Metallic, texCoord).rgb;
    float metallicSample = HasMetallicMap ? (PackedOrm ? mrSample.b : mrSample.r) : 1.0;
    float metallic = clamp(metallicSample * MetallicMultiplier, 0.0, 1.0);
    float roughSample = HasRoughnessMap ? texture(Roughness, texCoord).r : (PackedOrm ? mrSample.g : 1.0);
    float roughness = clamp(roughSample * RoughnessMultiplier, minRoughness, 1.0);
    // Note: the packed R (occlusion) channel is intentionally ignored — Bistro-style spec
    // maps often leave it dark/unused, which would zero out all ambient light.
    float ao = texture(AO, texCoord).r;
    // Screen-space AO joins the material AO with min() (Lagarde: avoids double-darkening
    // where both agree) and flows into ambient diffuse AND specular occlusion below.
    if (HasScreenAO) {
        // DEPTH-AWARE upsample of the half-res AO: a plain bilinear tap bleeds AO across silhouettes
        // at the half->full boundary (a halo around objects). Weight the 4 nearest AO taps by how
        // close their full-res depth (SceneDepth, already bound) is to this fragment's — so the AO
        // edge stays crisp. Reuses the contact-shadow ViewZFromDepth + the prepass SceneDepth.
        mat4 invP = inverse(projection);
        vec2 aoUV = gl_FragCoord.xy / ScreenSize;
        vec2 aoTexel = 1.0 / (ScreenSize * 0.5);            // AO buffer is half-res
        float centreZ = ViewZFromDepth(texture(SceneDepth, aoUV).r, invP);
        float ssaoSum = 0.0, wSum = 0.0;
        for (int dx = -1; dx <= 1; dx += 2)
            for (int dy = -1; dy <= 1; dy += 2) {
                vec2 t = aoUV + vec2(dx, dy) * 0.5 * aoTexel;
                float tz = ViewZFromDepth(texture(SceneDepth, t).r, invP);
                float w = 1.0 / (1.0 + abs(tz - centreZ) * 4.0);
                ssaoSum += texture(ScreenAO, t).r * w;
                wSum += w;
            }
        float ssao = wSum > 0.0 ? ssaoSum / wSum : texture(ScreenAO, aoUV).r;
        ao = min(ao, ssao);
    }

    if (renderMode == 3) { FragColor = vec4(vec3(ao), 1.0); return; }
    if (renderMode == 4) { FragColor = vec4(vec3(metallic), 1.0); return; }
    if (renderMode == 5) { FragColor = vec4(vec3(roughness), 1.0); return; }

    vec3 N = GetNormalFromMap(texCoord, normalize(fragNormal), fragTBN, NormalStrength);
    // TWO-SIDED FOLIAGE: cutout cards (leaves) draw with backface culling OFF, so a leaf's far
    // face reaches here with a geometric normal pointing AWAY from the camera — shading it as-is
    // makes the leaf go flat/dark (the washed white-green cards). Flip the normal to face the
    // viewer on a backface so both sides shade as a lit leaf. Only for cutout materials; opaque
    // geometry is single-sided (front-culled) and unaffected.
    if (AlphaCutout && !gl_FrontFacing)
        N = -N;
    vec3 V = normalize(CameraPos - fragPos);
    float NdotV = max(dot(N, V), 0.0);

    // Specular anti-aliasing (Kaplanyan-style): widen roughness where the shading normal
    // changes fast across the pixel, so normal-mapped detail can't produce single-pixel
    // fireflies from the sun or a bright HDR sky.
    vec3 nDdx = dFdx(N);
    vec3 nDdy = dFdy(N);
    float normalVariance = 0.25 * (dot(nDdx, nDdx) + dot(nDdy, nDdy));
    float kernelRoughness = min(2.0 * normalVariance, 0.18);
    roughness = min(sqrt(roughness * roughness + kernelRoughness), 1.0);

    // Dielectric F0 = 0.08 * SpecularReflectance (glTF KHR_materials_specular); 0.5 -> 0.04 (the old
    // hardcoded 4%, byte-identical default). Metals use albedo as F0.
    vec3 F0 = mix(vec3(0.08 * SpecularReflectance), albedo, metallic);

    // --- Direct lighting, diffuse and specular kept separate for premultiplied glass ---
    vec3 diffuseLight = vec3(0.0);
    vec3 specularLight = vec3(0.0);

    // Sun with shadows (disk-aware specular; see ShadeSun).
    vec3 D = normalize(LightDirection);
    float shadow = mix(1.0, SunShadow(N, D), clamp(ShadowStrength, 0.0, 1.0));
    // Contact shadows refine the cascade result with fine screen-space occlusion (multiply only).
    shadow *= ContactShadow(fragPos, D);
    vec3 shadowTint = mix(ShadowColor, vec3(1.0), shadow);
    ShadeSun(N, V, D, LightColor * shadowTint, albedo, metallic, roughness, F0, diffuseLight, specularLight);

    // LEAF TRANSLUCENCY (foliage back-light): a thin leaf lit from BEHIND glows — light scatters
    // through it toward the viewer. Without this, foliage with the sun behind it reads as a flat
    // dark/washed card (the #1 "fake foliage" tell). Cheap wrap-style transmission (Frostbite/
    // DICE fast SSS): the term peaks when the view looks toward the sun THROUGH the leaf (-D points
    // away from the sun, dotted with V), gated by the sun shadow so shadowed leaves don't glow. The
    // leaf's own albedo tints it (greens stay green). Cutout-only; opaque materials are unaffected.
    if (AlphaCutout) {
        float backlit = pow(max(dot(V, -D), 0.0), 4.0);     // view toward the sun through the leaf
        float wrap = max(dot(-N, D), 0.0) * 0.5 + 0.5;       // wrapped so the lit side contributes too
        vec3 transmission = LightColor * albedo * (backlit * wrap * shadow * 1.5);
        diffuseLight += transmission;
    }

    if (renderMode == 6) { FragColor = vec4(vec3(shadow), 1.0); return; }

    if (UseClustered) {
        // CLUSTERED FORWARD: find this fragment's cluster (screen tile + LOG depth slice, matching
        // ClusterBuild_Comp) and shade only the lights the cull pass assigned to it. No 8/4 cap.
        float viewZ = -(view * vec4(fragPos, 1.0)).z;        // positive view-space distance
        float near = ClusterNearFar.x, far = ClusterNearFar.y;
        int zSlice = int(log(max(viewZ, near) / near) / log(far / near) * float(ClusterDimZ));
        zSlice = clamp(zSlice, 0, ClusterDimZ - 1);
        ivec2 tile = ivec2(gl_FragCoord.xy / (ScreenSize / vec2(ClusterDimX, ClusterDimY)));
        tile = clamp(tile, ivec2(0), ivec2(ClusterDimX - 1, ClusterDimY - 1));
        int cluster = tile.x + ClusterDimX * (tile.y + ClusterDimY * zSlice);

        ivec2 range = clusterGrid[cluster];                   // (offset, count)
        for (int k = 0; k < range.y; k++) {
            int li = clusterLightIndex[range.x + k];
            GpuLight L = clusterLights[li];
            vec3 toLight = L.posRange.xyz - fragPos;
            float dist = length(toLight);
            if (dist > L.posRange.w)
                continue;
            vec3 Ld = toLight / dist;
            float atten = DistanceAttenuation(dist, L.posRange.w);
            int shadowSlot = int(L.extra.y);

            float srcRadius = L.extra.z;  // area-light emitter radius (0 = delta point)
            if (L.lcolor.w < 0.5) {
                // POINT
                float vis = shadowSlot >= 0
                    ? PointShadow(shadowSlot, L.posRange.xyz, N, clamp(dot(N, Ld), 0.0, 1.0)) : 1.0;
                if (vis <= 0.001) continue;
                ShadeLightArea(N, V, Ld, dist, srcRadius, L.lcolor.rgb * atten * vis, albedo, metallic,
                               roughness, F0, diffuseLight, specularLight);
            } else {
                // SPOT
                float cosAngle = dot(-Ld, normalize(L.dirCosOuter.xyz));
                float cone = clamp((cosAngle - L.dirCosOuter.w) /
                                   max(L.extra.x - L.dirCosOuter.w, 1e-4), 0.0, 1.0);
                if (cone <= 0.0) continue;
                float vis = shadowSlot >= 0
                    ? SpotShadow(shadowSlot, N, clamp(dot(N, Ld), 0.0, 1.0)) : 1.0;
                if (vis <= 0.001) continue;
                ShadeLightArea(N, V, Ld, dist, srcRadius, L.lcolor.rgb * atten * cone * cone * vis,
                               albedo, metallic, roughness, F0, diffuseLight, specularLight);
            }
        }
    } else {
        // LEGACY capped per-fragment loops (fallback: BALLISTIC_CLUSTERED=0 / no SSBO support).
        for (int i = 0; i < PointLightCount; i++) {
            vec3 toLight = PointLightPosition[i] - fragPos;
            float dist = length(toLight);
            if (dist > PointLightRange[i])
                continue;
            vec3 Lp = toLight / dist;
            float vis = PointShadowSlot[i] >= 0
                ? PointShadow(PointShadowSlot[i], PointLightPosition[i], N, clamp(dot(N, Lp), 0.0, 1.0))
                : 1.0;
            if (vis <= 0.001)
                continue;
            vec3 radiance = PointLightColor[i] * DistanceAttenuation(dist, PointLightRange[i]) * vis;
            ShadeLight(N, V, Lp, radiance, albedo, metallic, roughness, F0, diffuseLight, specularLight);
        }
        for (int i = 0; i < SpotLightCount; i++) {
            vec3 toLight = SpotLightPosition[i] - fragPos;
            float dist = length(toLight);
            if (dist > SpotLightRange[i])
                continue;
            vec3 Ls = toLight / dist;
            float cosAngle = dot(-Ls, normalize(SpotLightDirection[i]));
            float cone = clamp((cosAngle - SpotLightCosOuter[i]) /
                               max(SpotLightCosInner[i] - SpotLightCosOuter[i], 1e-4), 0.0, 1.0);
            if (cone <= 0.0)
                continue;
            float vis = SpotShadowSlot[i] >= 0
                ? SpotShadow(SpotShadowSlot[i], N, clamp(dot(N, Ls), 0.0, 1.0))
                : 1.0;
            if (vis <= 0.001)
                continue;
            vec3 radiance = SpotLightColor[i] * DistanceAttenuation(dist, SpotLightRange[i]) * cone * cone * vis;
            ShadeLight(N, V, Ls, radiance, albedo, metallic, roughness, F0, diffuseLight, specularLight);
        }
    }

    // Multi-scatter energy compensation for analytic lights (Filament): single-scatter
    // Smith-GGX loses energy at high roughness; scale the accumulated specular back up by
    // the white-furnace deficit measured in the BRDF LUT. Clamped: at grazing NdotV the LUT
    // energy is legitimately tiny and 1/Ess would explode into rim fireflies.
    {
        vec2 dfg = texture(BRDF_LUT, vec2(NdotV, roughness)).rg;
        vec3 comp = vec3(1.0) + F0 * (1.0 / max(dfg.x + dfg.y, 1e-3) - 1.0);
        specularLight *= min(comp, vec3(2.5));
    }

    // --- Ambient: full split-sum IBL when baked maps exist, flat sky ambient otherwise ---
    vec3 ambientDiffuse;
    vec3 ambientSpecular;
    vec3 F = FresnelSchlickRoughness(NdotV, F0, roughness);

    // Specular occlusion (Lagarde/Frostbite): attenuate ambient specular where the surface is
    // occluded or rough at grazing angles, so dark corners don't catch a flat sky sheen.
    // AO enters ONCE (the old form multiplied it in twice and crushed reflections).
    float specOcclusion = clamp(pow(NdotV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao, 0.0, 1.0);

    if (UseIBL) {
        // Diffuse irradiance first (the multiscatter term below reuses it): the baked probe
        // volume where it covers this point (position-aware ambient - a corridor reads corridor
        // light, the rotunda reads warm bounce), the global sky irradiance outside the bounds.
        vec3 irradiance;
        vec3 probeUVW = (fragPos - ProbeVolumeMin) * ProbeVolumeInvSize;
        if (UseProbeVolume &&
            all(greaterThanEqual(probeUVW, vec3(0.0))) && all(lessThanEqual(probeUVW, vec3(1.0)))) {
            vec3 sh0 = texture(ProbeSH0, probeUVW).rgb;
            vec3 sh1 = texture(ProbeSH1, probeUVW).rgb;   // linear Y
            vec3 sh2 = texture(ProbeSH2, probeUVW).rgb;   // linear Z
            vec3 sh3 = texture(ProbeSH3, probeUVW).rgb;   // linear X
            // L1 SH irradiance reconstruction (cosine-convolved Ramamoorthi band factors). NOTE: this
            // gives FULL irradiance E, whereas the sky IrradianceMap (else branch) stores E/PI — a ~PI
            // unit mismatch that makes the probe ambient brighter than the sky just outside the volume
            // (a boundary seam). The mathematically-correct fix is /PI here, BUT this scene's exposure
            // was calibrated around the brighter probe ambient, so /PI alone crushes the interior. The
            // unit fix + a matching ambient REBALANCE is done together in the GI phase (Phase E) so the
            // default interior stays well-lit; kept as-is here to preserve the user-approved look.
            // L1 SH irradiance E(N) = DC + linear*N. The linear (directional) band can drive a SINGLE
            // channel NEGATIVE when a probe has a strong colour gradient (e.g. a dome oculus that is
            // red-depleted overhead): the surfaces facing away from the brighter direction reconstruct
            // negative red, and a hard per-channel max(.,0) then ZEROS only red -> the bounce light in
            // an enclosed apse collapses to pure green/teal ("the interior is deep teal, red ~0"). This
            // is SH RINGING, not a colour the sky actually has.
            //
            // De-ring instead of clamp: limit the magnitude of the linear band, PER CHANNEL, so the
            // reconstruction stays >= 0 over the whole sphere without killing a channel. The worst-case
            // (most negative) reconstruction is DC*0.886 - |linear|*1.023; requiring that >= 0 gives a
            // per-channel scale that softly shrinks an over-strong gradient toward a valid, still-
            // directional result. Neutral/weak-gradient probes are unaffected (scale clamps to 1).
            vec3 dc = sh0 * 0.886227;
            vec3 linear = (sh1 * N.y + sh2 * N.z + sh3 * N.x) * 1.023327;
            vec3 linAbs = vec3(
                length(vec3(sh1.r, sh2.r, sh3.r)),
                length(vec3(sh1.g, sh2.g, sh3.g)),
                length(vec3(sh1.b, sh2.b, sh3.b))) * 1.023327;
            vec3 deringScale = min(vec3(1.0), dc / max(linAbs, vec3(1e-4)));
            irradiance = dc + linear * deringScale;
            // ProbeIntensity (GlobalIllumination volume) scales the probe ambient; 1 = unchanged.
            irradiance = max(irradiance, 0.0) * ProbeExposure * ProbeIntensity;
        }
        else {
            irradiance = texture(IrradianceMap, SkyDir(N)).rgb * SkyExposure;
        }

        vec3 R = reflect(-V, N);
        float mip = clamp(roughness * MaxPrefilterMips, 0.0, MaxPrefilterMips);
        // Global skybox reflection (default / fallback). Uses SkyDir(R) because the skybox is rotated.
        vec3 prefiltered = textureLod(PrefilteredEnvMap, SkyDir(R), mip).rgb * SkyExposure;

        // Local reflection probe override: if this fragment sits in an OCCUPIED cell of the
        // reflection volume, replace the sky reflection with that cell's local prefiltered cube.
        // Local cubes are WORLD-space (captured via LookAt), so sample raw R - NOT SkyDir(R). Same
        // SkyExposure as the sky path so local and global specular share one EV.
        if (UseReflectionVolume) {
            vec3 ruvw = (fragPos - ReflectionVolumeMin) * ReflectionVolumeInvSize;
            if (all(greaterThanEqual(ruvw, vec3(0.0))) && all(lessThanEqual(ruvw, vec3(1.0)))) {
                ivec3 dims = ivec3(ReflectionGridX, ReflectionGridY, ReflectionGridZ);
                vec3 cellSize = (vec3(1.0) / ReflectionVolumeInvSize) / vec3(dims);
                float localMip = clamp(roughness * ReflectionMaxMips, 0.0, ReflectionMaxMips);

                // INTER-PROBE TRILINEAR BLEND. A single cell's parallax-corrected cube is sharp, but
                // crossing a cell boundary POPS to a different cube (the review's "adjacent cells with
                // different layers pop with no inter-probe blend"). Sample the 2x2x2 cell neighbourhood
                // around the fragment's CELL-CENTRE-relative position and trilinearly weight by how
                // close the fragment is to each cell's centre — so the reflection fades smoothly across
                // boundaries (each tap is itself box-projection parallax-corrected via SampleLocalProbe).
                vec3 gridPos = ruvw * vec3(dims) - 0.5;        // position in CELL-CENTRE space
                ivec3 baseCell = ivec3(floor(gridPos));
                vec3 f = gridPos - vec3(baseCell);             // trilinear weights toward +cell

                vec3 localAcc = vec3(0.0);
                float wAcc = 0.0;
                for (int c = 0; c < 8; c++) {
                    ivec3 off = ivec3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
                    ivec3 cc = clamp(baseCell + off, ivec3(0), dims - 1);
                    float w = (off.x == 1 ? f.x : 1.0 - f.x)
                            * (off.y == 1 ? f.y : 1.0 - f.y)
                            * (off.z == 1 ? f.z : 1.0 - f.z);
                    if (w <= 0.0) continue;
                    vec3 tapRgb;
                    if (SampleLocalProbe(cc, R, fragPos, cellSize, localMip, tapRgb)) {
                        localAcc += tapRgb * w;
                        wAcc += w;
                    }
                }

                if (wAcc > 0.0) {
                    vec3 local = (localAcc / wAcc) * SkyExposure;
                    // BlendWithSky: lerp from the sky reflection toward the local one by intensity
                    // (intensity>1 over-drives past the local cube). Otherwise hard-replace, scaled.
                    prefiltered = ReflectionBlendWithSky
                        ? mix(prefiltered, local, clamp(ReflectionIntensityLocal, 0.0, 1.0))
                          + local * max(ReflectionIntensityLocal - 1.0, 0.0)
                        : local * ReflectionIntensityLocal;
                }
            }
        }
        vec2 brdf = texture(BRDF_LUT, vec2(NdotV, roughness)).rg;

        // Multi-scatter split-sum (Fdez-Aguera): single-scatter GGX loses energy as roughness
        // grows (rough metals go charcoal); re-inject the multiple-bounce energy so white
        // furnace tests hold and rough metal keeps its brightness.
        vec3 FssEss = F * brdf.x + brdf.y;
        float Ess = brdf.x + brdf.y;
        float Ems = 1.0 - Ess;
        vec3 Favg = F0 + (1.0 - F0) / 21.0;
        vec3 Fms = FssEss * Favg / max(vec3(1.0) - Ems * Favg, vec3(1e-4));
        ambientSpecular = (prefiltered * FssEss + Fms * Ems * irradiance)
                        * ReflectionIntensity * specOcclusion;

        // Energy-conserving diffuse weight: what single+multi scatter specular didn't take.
        // AmbientTint scales the diffuse ambient only; reflections stay sky-driven.
        vec3 kD = albedo * (1.0 - metallic) * max(vec3(1.0) - FssEss - Fms * Ems, vec3(0.0));
        ambientDiffuse = kD * irradiance * AmbientTint * ao;
        // AMBIENT FLOOR: enclosed interiors capture little probe ambient, so the shadowed side of
        // geometry crushes to PURE BLACK (UE interiors never do — there's always bounce fill). Lift
        // by a tiny fraction of the surface albedo, AO-modulated so crevices stay dark. Tunable
        // (AmbientFloor uniform, GI volume override). Default small so lit areas are ~unchanged.
        ambientDiffuse += kD * AmbientFloor * ao;
    }
    else {
        vec3 R = reflect(-V, N);
        vec3 envColor = textureLod(Skybox, SkyDir(R), 0.0).rgb * SkyExposure;
        vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
        ambientDiffuse = AmbientLight * AmbientTint * kD * albedo * ao;
        ambientSpecular = F * envColor * (1.0 - roughness) * ReflectionIntensity * specOcclusion;
    }

    // --- Emissive (unlit, unoccluded; bloom picks it up) ---
    vec3 emissive = HasEmissive ? texture(Emissive, texCoord).rgb * EmissiveFactor : vec3(0.0);

    // --- CLEARCOAT (KHR_materials_clearcoat): a thin lacquer layer over the base — car paint,
    // varnish, wet stone. A second GGX specular lobe with fixed F0 0.04 and its OWN low roughness
    // (a sharp coat reflection on top of a rough base), plus it ATTENUATES the base layers by its
    // Fresnel (energy that reflects off the coat doesn't reach the base). Clearcoat 0 = no change. ---
    if (Clearcoat > 0.0) {
        float ccRough = clamp(ClearcoatRoughness, 0.02, 1.0);
        float ccF0 = 0.04;
        float ccFresnel = ccF0 + (1.0 - ccF0) * pow(1.0 - NdotV, 5.0);
        float ccAtten = 1.0 - Clearcoat * ccFresnel;   // base sees less light under the coat

        // Coat IBL specular: prefiltered env at the coat's (low) roughness, no metal tint (the coat
        // is dielectric). Uses the same SkyDir-rotated prefiltered map as the base reflection.
        vec3 ccR = reflect(-V, N);
        float ccMip = clamp(ccRough * MaxPrefilterMips, 0.0, MaxPrefilterMips);
        vec3 ccEnv = textureLod(PrefilteredEnvMap, SkyDir(ccR), ccMip).rgb * SkyExposure;
        vec2 ccBrdf = texture(BRDF_LUT, vec2(NdotV, ccRough)).rg;
        vec3 ccSpecIBL = ccEnv * (ccF0 * ccBrdf.x + ccBrdf.y) * specOcclusion;

        // Coat sun lobe: a sharp GGX highlight from the sun disk direction at the coat roughness.
        vec3 ccSun = vec3(0.0);
        {
            vec3 Dc = normalize(LightDirection);
            float NdotLc = max(dot(N, Dc), 0.0);
            if (NdotLc > 0.0) {
                vec3 Hc = normalize(V + Dc);
                float NDFc = DistributionGGX(N, Hc, ccRough);
                float Gc = GeometrySmith(N, V, Dc, ccRough);
                float Fc = ccF0 + (1.0 - ccF0) * pow(1.0 - max(dot(Hc, V), 0.0), 5.0);
                float specc = (NDFc * Gc * Fc) / max(4.0 * NdotV * NdotLc, EPS);
                ccSun = LightColor * mix(ShadowColor, vec3(1.0), shadow) * (specc * NdotLc);
            }
        }

        // Attenuate the base contributions, then add the coat on top.
        diffuseLight *= ccAtten;  specularLight *= ccAtten;
        ambientDiffuse *= ccAtten; ambientSpecular *= ccAtten;
        ambientSpecular += ccSpecIBL * Clearcoat;
        specularLight += ccSun * Clearcoat;
    }

    // Premultiplied composition: transmission (diffuse + emissive) fades with alpha, while
    // reflections keep full strength — glass stays reflective as it becomes see-through.
    // The transparent pass blends with (ONE, ONE_MINUS_SRC_ALPHA); opaque is unaffected (alpha 1).
    float alpha = AlphaBlend ? clamp(Opacity * albedoSample.a * BaseColorFactor.a, 0.0, 1.0) : 1.0;
    vec3 color = (diffuseLight + ambientDiffuse + emissive) * alpha + specularLight + ambientSpecular;

    // --- Distance fog ---
    if (EnableAtmosphericScattering) {
        float dist = length(CameraPos - fragPos);
        float fogFactor = clamp(1.0 - exp(-dist * FogDensity), 0.0, 1.0);
        color = mix(color, FogColor * alpha, fogFactor);
    }

    FragColor = vec4(color, alpha); // linear HDR out; tonemap happens in the composite pass
    // Alpha packs roughness + a metal flag (+2.0) so SSR can pick a sensible F0 per pixel.
    NormalRough = vec4(N * 0.5 + 0.5, roughness + (metallic > 0.5 ? 2.0 : 0.0));
    // Diffuse albedo G-buffer for deferred GI receiver reflectance: the surface's diffuse base colour
    // (metals reflect ~no diffuse, so fold in (1-metallic)). The GI composite multiplies its gathered
    // irradiance by THIS so the bounce is bounded by what the surface can actually reflect.
    AlbedoGBuf = vec4(albedo * (1.0 - metallic), 1.0);
}
