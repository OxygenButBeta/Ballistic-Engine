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
uniform float BounceScale;        // multi-bounce gain (1 = normal; >1 strengthens the indirect bounce)

const int MAX_CASCADES = 4;
uniform mat4  CascadeMatrices[MAX_CASCADES];
uniform vec4  CascadeBias;
uniform int   CascadeCountSun;    // # of SUN shadow cascades (distinct from the GDF cascades)
uniform vec3  SunDirectionWorld;  // toward the sun
uniform vec3  SunColor;
uniform vec3  Albedo;             // FALLBACK albedo when the per-voxel albedo field is empty/black.

// PUNCTUAL (point) lights injected into the surface cache so a point-lit interior (no sun) still gets
// Lumen bounce — real Lumen lights ALL light types into the cache. Pre-exposed colours (same units as
// SunColor). Inverse-square falloff with a smooth range cutout, matching the forward lit pass.
#define MAX_GI_POINTS 8
uniform int   PointCount;
uniform vec3  PointPos[MAX_GI_POINTS];     // world-space
uniform vec3  PointColor[MAX_GI_POINTS];   // pre-exposed radiant intensity
uniform float PointRange[MAX_GI_POINTS];

const float PI = 3.14159265359;

vec3 Sanitize(vec3 v) {
    return vec3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
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

// GDF SUN OCCLUSION (interior phantom-sun fix — see SdfTrace_Comp for the full rationale). March the
// global distance field toward the sun: hit geometry before escaping the clipmap -> shadowed; escape
// -> lit. Used as the OUTSIDE-cascades fallback so enclosed interior voxels are correctly shadowed
// instead of getting phantom full sun (which made BistroInterior's GI pure red). 1 = lit, 0 = occluded.
float SunVisibilityGdf(vec3 worldPos) {
    vec3 dir = normalize(SunDirectionWorld);
    float cell0 = GdfCell[0];
    vec3 p = worldPos + dir * (2.0 * cell0);
    float traveled = 0.0;
    const int SUN_STEPS = 40;
    const float SUN_MAXD = 60.0;
    for (int s = 0; s < SUN_STEPS; ++s) {
        bool inside;
        float d = GdfDist(p, inside);
        if (!inside) return 1.0;                 // escaped the clipmap -> sun visible
        if (d < 0.5 * cell0) return 0.0;         // hit an occluder -> shadowed
        float adv = max(d, 0.5 * cell0);
        p += dir * adv; traveled += adv;
        if (traveled >= SUN_MAXD) return 1.0;
    }
    return 1.0;
}

// Sun visibility: sharp shadow map inside the cascades, GDF sun trace outside them (the phantom-sun
// fix). The inject ALWAYS has the GDF (it's the GDF's own voxel-lighting pass), so the fallback is
// always the trace — never the old unconditional "lit".
float SampleSunVisibility(vec3 worldPos) {
    for (int c = 0; c < CascadeCountSun && c < MAX_CASCADES; c++) {
        vec4 clip = CascadeMatrices[c] * vec4(worldPos, 1.0);
        float edge = max(abs(clip.x), abs(clip.y));
        vec3 proj = clip.xyz * 0.5 + 0.5;
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0)
            continue;
        return texture(ShadowMap, vec4(proj.xy, float(c), proj.z - CascadeBias[c]));
    }
    return SunVisibilityGdf(worldPos);
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

// One bounce: a few cosine rays sphere-traced through the GDF; hit => last frame's radiance there,
// miss => sky. Sphere-tracing the SDF (advance by the distance value) reaches far walls in few steps;
// the previous version was STEP-STARVED (24 steps that, near a surface, advanced only 0.5*cell≈6cm,
// so a ray died after ~1-2m and almost never reached a wall -> the bounce was ~0 and multi-bounce
// contributed nothing). More steps + a hit epsilon tied to the cell (not 0.5*cell) + stepping by the
// real distance fixes the reach.
vec3 GatherBounce(vec3 worldP, vec3 n, float cell) {
    const int RAYS = 6;
    const int STEPS = 64;
    const float MAXD = 40.0;
    float hitEps = 0.5 * cell;        // surface proximity that counts as a hit
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
        vec3 p = worldP + n * (2.0 * cell);   // start clear of the origin voxel's own shell
        float traveled = 2.0 * cell; bool hit = false; vec3 hp = vec3(0.0);
        for (int s = 0; s < STEPS; ++s) {
            bool inside; float dist = GdfDist(p, inside);
            if (!inside) {                     // left the clipmap -> sky for the rest of this ray
                break;
            }
            if (dist < hitEps && traveled > 3.0 * cell) { hit = true; hp = p; break; }
            float adv = max(dist, 0.5 * cell); // sphere-trace step, floored so we never stall
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

    // PUNCTUAL lights -> the surface cache. Without this a point-lit interior (no sun) gets ZERO Lumen
    // bounce (BistroInterior's lamps were absent from the GI). Inverse-square + smooth range cutout +
    // NdotL, matching the forward lit pass. (Point-light SDF shadowing is a later refinement; for a
    // diffuse bounce cache the unshadowed contribution already fills the room far better than nothing.)
    for (int i = 0; i < PointCount && i < MAX_GI_POINTS; ++i) {
        vec3 toL = PointPos[i] - litPos;
        float dist2 = dot(toL, toL);
        float dist = sqrt(max(dist2, 1e-6));
        vec3 L = toL / dist;
        float pndl = max(dot(n, L), 0.0);
        if (pndl <= 0.0) continue;
        float invSq = 1.0 / max(dist2, 0.01);                 // inverse-square
        float rr = clamp(1.0 - dist / max(PointRange[i], 1e-3), 0.0, 1.0);
        float window = rr * rr;                                // smooth range cutout
        direct += PointColor[i] * (pndl * invSq * window);
    }

    vec3 bounce = GatherBounce(worldP, n, CascadeCell);

    // PER-VOXEL ALBEDO (Lumen surface-cache albedo): the nearest surface's material colour, so this
    // voxel bounces ITS OWN colour — a red wall bounces red. Falls back to the uniform grey where the
    // albedo field is empty (un-baked / black material).
    vec3 albedo = texelFetch(AlbedoField, v, 0).rgb;
    if (dot(albedo, albedo) < 1e-4) albedo = Albedo;

    // Energy-bounded multi-bounce. The stored voxel radiance is a geometric series in the bounce
    // albedo: in a FULLY ENCLOSED scene (no sky escape — BistroInterior's closed red room) every
    // bounce ray hits another lit voxel, so R converges to direct*a/(1-a). At a=0.9 that's a 10x
    // amplification — and a red wall's RED channel ran away to full saturation (the isolated bounce
    // was railed pure red) while green/blue stayed low. A real diffuse surface reflects ~0.3-0.5, so
    // a=0.9 was never physical; it was a too-loose explosion guard. Clamp to 0.55 -> series sum ~2.2x
    // (correct multi-bounce: a few real bounces, not ten) so an enclosed coloured room stays bounded.
    // (SunTemple's lower-albedo stone never hit the runaway, so it's unaffected; the exterior escapes
    // to sky so it never summed the series at all.)
    vec3 bounceAlbedo = min(albedo, vec3(0.55));
    vec3 radiance = (albedo / PI) * direct + bounceAlbedo * bounce * BounceScale;
    radiance = Sanitize(radiance);

    vec4 old = texelFetch(GdfRadiance[Cascade], v, 0);
    vec3 blended = old.a > 0.0 ? mix(radiance, old.rgb, Feedback) : radiance;
    blended = min(Sanitize(blended), vec3(32.0));
    imageStore(RadianceOut, v, vec4(blended, 1.0));
}
