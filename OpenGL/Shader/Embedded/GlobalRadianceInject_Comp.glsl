#version 460 core

// LUMEN PHASE 2 — GLOBAL VOXEL LIGHTING (the surface cache for the Global Distance Field).
//
// One dispatch per cascade. For each NEAR-SURFACE voxel of the cascade's distance field, compute the
// surface's lit radiance and temporally accumulate it into the parallel RGBA16F radiance clipmap. The
// SDF march (SdfTrace_Comp, GDF path) then READS this cached radiance at a hit instead of the neutral
// direct estimate — giving STABLE, COLORED, multi-bounce off-screen GI (the Lumen surface cache idea,
// in global voxel form for the global field; per-mesh cards are a separate screen-probe near-field path).
//
// Radiance(voxel) = albedo/PI * directSun(shadowed) + bounceAlbedo * gatheredBounce  (+ emissive later).
// Bounce reads LAST frame's radiance through the GDF (ping-pong), so each frame adds one converged
// bounce. Energy-bounded (clamp bounce albedo + hard cap) exactly like the per-mesh RadianceInject —
// the white-wall feedback runaway (R = direct*a/(1-a)) is the same hazard here.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

// This cascade's distance field (read: occupancy + gradient normal) and the radiance WRITE target.
layout(binding = 0) uniform sampler3D DistanceField;                 // signed world-metre distance
layout(rgba16f, binding = 1) uniform writeonly image3D RadianceOut;  // rgb = radiance, a = occupancy
layout(binding = 14) uniform sampler3D AlbedoField;                  // per-voxel surface albedo (RGB)

// Sky IBL (ambient/miss) + cascaded sun shadow (same convention as the march).
layout(binding = 3) uniform samplerCube IrradianceMap;
layout(binding = 5) uniform sampler2DArrayShadow ShadowMap;

// The full GDF clipmap (all cascades) for the one-bounce gather — read LAST frame's radiance there.
#define GDF_CASCADES 4
layout(binding = 6) uniform sampler3D GdfDistance[GDF_CASCADES];     // distance, all cascades
layout(binding = 10) uniform sampler3D GdfRadiance[GDF_CASCADES];    // LAST frame radiance, all cascades

uniform int   Cascade;            // which cascade this dispatch lights
uniform vec3  CascadeMin;         // world min corner of THIS cascade
uniform float CascadeCell;        // world cell size of THIS cascade
uniform int   Res;                // voxels per axis
uniform vec3  GdfMin[GDF_CASCADES];
uniform float GdfCell[GDF_CASCADES];
uniform float SkyExposure;
uniform float Feedback;           // EMA weight for the OLD value (~0.9 sticky)

const int MAX_CASCADES = 4;
uniform mat4  CascadeMatrices[MAX_CASCADES];
uniform vec4  CascadeBias;
uniform int   CascadeCountSun;    // # of SUN shadow cascades (distinct from the GDF cascades)
uniform vec3  SunDirectionWorld;  // toward the sun
uniform vec3  SunColor;
uniform vec3  Albedo;             // FALLBACK albedo when the per-voxel albedo field is empty/black.

const float PI = 3.14159265359;

vec3 Sanitize(vec3 v) {
    return vec3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

float SampleSunVisibility(vec3 worldPos) {
    for (int c = 0; c < CascadeCountSun && c < MAX_CASCADES; c++) {
        vec4 clip = CascadeMatrices[c] * vec4(worldPos, 1.0);
        float edge = max(abs(clip.x), abs(clip.y));
        vec3 proj = clip.xyz * 0.5 + 0.5;
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0)
            continue;
        return texture(ShadowMap, vec4(proj.xy, float(c), proj.z - CascadeBias[c]));
    }
    return 1.0;
}

// Distance of THIS cascade at a voxel (texelFetch on the R channel).
float DistAt(ivec3 v) { return texelFetch(DistanceField, v, 0).r; }

// Sample the GDF (finest cascade containing worldP) distance — for the bounce ray's sphere trace.
float GdfDist(vec3 worldP, out bool inside) {
    inside = false;
    for (int c = 0; c < GDF_CASCADES; ++c) {
        float extent = GdfCell[c] * float(Res);
        vec3 rel = (worldP - GdfMin[c]) / extent;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThanEqual(rel, vec3(1.0)))) {
            inside = true;
            return texture(GdfDistance[c], rel).r;
        }
    }
    return 1e9;
}

// Read LAST frame's radiance at a world point (finest GDF cascade containing it).
vec3 GdfRadianceAt(vec3 worldP) {
    for (int c = 0; c < GDF_CASCADES; ++c) {
        float extent = GdfCell[c] * float(Res);
        vec3 rel = (worldP - GdfMin[c]) / extent;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThanEqual(rel, vec3(1.0))))
            return texture(GdfRadiance[c], rel).rgb;
    }
    return vec3(0.0);
}

// One bounce: a few cosine rays through the GDF; hit => last frame's radiance there, miss => sky.
vec3 GatherBounce(vec3 worldP, vec3 n, float cell) {
    const int RAYS = 4;
    const int STEPS = 24;
    const float MAXD = 24.0;
    vec3 up = abs(n.z) < 0.999 ? vec3(0,0,1) : vec3(1,0,0);
    vec3 T = normalize(cross(up, n));
    vec3 B = cross(n, T);
    vec3 sum = vec3(0.0);
    for (int r = 0; r < RAYS; ++r) {
        float a = (float(r) + 0.5) / float(RAYS);
        float phi = 6.2831853 * fract(a * 2.61803);
        float cosT = sqrt(1.0 - a * 0.85 - 0.1);
        float sinT = sqrt(max(0.0, 1.0 - cosT*cosT));
        vec3 dir = normalize(T*cos(phi)*sinT + B*sin(phi)*sinT + n*cosT);
        vec3 p = worldP + n * (1.0 * cell);
        float traveled = 0.0; bool hit = false; vec3 hp = vec3(0.0);
        for (int s = 0; s < STEPS; ++s) {
            bool inside; float dist = GdfDist(p, inside);
            if (inside && dist < 0.5*cell && traveled > 1.5*cell) { hit = true; hp = p; break; }
            float adv = inside ? max(dist, 0.5*cell) : 0.75;
            p += dir * adv; traveled += adv;
            if (traveled >= MAXD) break;
        }
        sum += hit ? GdfRadianceAt(hp)
                   : Sanitize(textureLod(IrradianceMap, dir, 0.0).rgb) * SkyExposure;
    }
    return sum / float(RAYS);
}

void main() {
    ivec3 v = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(v, ivec3(Res))))
        return;

    float d = DistAt(v);
    // Only near-surface voxels carry radiance (|d| within ~1.5 cells). Far/empty voxels decay toward 0.
    if (abs(d) > 1.5 * CascadeCell) {
        vec4 old = texelFetch(GdfRadiance[Cascade], v, 0);
        imageStore(RadianceOut, v, vec4(old.rgb * 0.5, old.a * 0.5));
        return;
    }

    vec3 worldP = CascadeMin + (vec3(v) + 0.5) * CascadeCell;

    // Surface normal = normalized distance-field gradient (central differences).
    float dx = DistAt(v + ivec3(1,0,0)) - DistAt(v - ivec3(1,0,0));
    float dy = DistAt(v + ivec3(0,1,0)) - DistAt(v - ivec3(0,1,0));
    float dz = DistAt(v + ivec3(0,0,1)) - DistAt(v - ivec3(0,0,1));
    vec3 n = vec3(dx, dy, dz);
    float nl = length(n);
    n = nl > 1e-5 ? n / nl : vec3(0.0, 1.0, 0.0);

    vec3 litPos = worldP + n * (1.0 * CascadeCell);
    float ndl = max(dot(n, normalize(SunDirectionWorld)), 0.0);
    float vis = SampleSunVisibility(litPos);
    vec3 direct = SunColor * (ndl * vis);
    vec3 bounce = GatherBounce(worldP, n, CascadeCell);

    // PER-VOXEL ALBEDO (Lumen surface-cache albedo): the nearest surface's material colour, so this
    // voxel bounces ITS OWN colour — a red wall bounces red. Falls back to the uniform grey where the
    // albedo field is empty (un-baked / black material).
    vec3 albedo = texelFetch(AlbedoField, v, 0).rgb;
    if (dot(albedo, albedo) < 1e-4) albedo = Albedo;

    // Energy-bounded multi-bounce (same reasoning as the per-mesh inject): clamp bounce albedo so the
    // R = direct*a + a*R_prev recurrence converges (a->1 white walls would otherwise explode), and a
    // hard cap on the stored value as a final safety. Sky enters via the gather's missed rays.
    vec3 bounceAlbedo = min(albedo, vec3(0.9));
    vec3 radiance = (albedo / PI) * direct + bounceAlbedo * bounce;
    radiance = Sanitize(radiance);

    vec4 old = texelFetch(GdfRadiance[Cascade], v, 0);
    vec3 blended = old.a > 0.0 ? mix(radiance, old.rgb, Feedback) : radiance;
    blended = min(Sanitize(blended), vec3(32.0));
    imageStore(RadianceOut, v, vec4(blended, 1.0));
}
