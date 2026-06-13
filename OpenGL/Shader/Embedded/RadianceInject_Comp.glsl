#version 460 core

// SURFACE CACHE — radiance inject (P7.2). For ONE instance per dispatch, walk its SDF brick's
// voxels and, for each NEAR-SURFACE voxel, compute the surface's LIT radiance and temporally
// accumulate it into the parallel RGBA16F radiance atlas (same slot layout as the SDF brick).
//
// The march then READS this cached per-surface radiance at a hit (RadianceInject's output) instead
// of the flickering per-pixel screen reprojection — stable, off-screen-capable, coloured. The cache
// is the proper Lumen surface-cache idea in voxel form: radiance lives on the surface, not on screen.
//
// Lit radiance at a voxel = Albedo * (SunColor * max(0, N.sun) * shadowVis + skyIrradiance) + Emissive
// where N = the SDF gradient (surface normal) at the voxel. Temporal EMA blends frame-to-frame for
// stability and crude multi-bounce (next: add a sample of last frame's radiance along N).

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

// The SDF atlas (read the brick's distance for occupancy + gradient) and the radiance atlas we write.
layout(binding = 4) uniform sampler3D SdfAtlas;
layout(rgba16f, binding = 1) uniform image3D RadianceAtlas;  // rgb = radiance, a = occupancy

// IBL sky (the ambient term) + cascaded shadow map (sun visibility), same as the march.
layout(binding = 3) uniform samplerCube IrradianceMap;
layout(binding = 5) uniform sampler2DArrayShadow ShadowMap;

// ---- Instance + slot SSBOs (same layout as SdfTrace_Comp). ----
struct SsdfInstance {
    mat4 worldToLocal;
    mat4 world;          // local -> world (for the inject's world-space lighting)
    vec4 worldAabbMin;
    vec4 worldAabbMax;
    vec4 albedo;         // xyz
    vec4 emissive;       // xyz
    uint slot;
    uint p0; uint p1; uint p2;
};
struct SsdfSlot {
    vec4 offsetRes0;     // xyz = atlas texel offset
    vec4 res;            // xyz = grid resolution
    vec4 boundsMin;      // xyz = mesh-local min
    vec4 boundsMax;      // xyz = mesh-local max
};
layout(std430, binding = 8) readonly buffer InstanceBuf { SsdfInstance instances[]; };
layout(std430, binding = 9) readonly buffer SlotBuf     { SsdfSlot     slots[]; };

// Instance grid (same as the march) so the bounce-gather can trace world rays cheaply.
layout(std430, binding = 10) readonly buffer GridCellBuf { ivec2 gridCells[]; };
layout(std430, binding = 11) readonly buffer GridListBuf { uint  gridList[]; };

// LAST frame's radiance cache as a SAMPLER (binding 7) — the bounce-gather reads it at ray hits.
// (RadianceAtlas image binding 1 is THIS frame's write target; reading the sampler view gives the
// previous accumulated state, which is exactly what an iterative radiosity bounce wants.)
layout(binding = 7) uniform sampler3D RadianceCache;

uniform int  InstanceIndex;  // which instance this dispatch injects (one dispatch per instance)
uniform uint InstanceCount;  // total instances (for the gather's SceneSdf loop)
uniform float SkyExposure;
uniform float Feedback;      // temporal EMA weight for the OLD value (0 = replace, ~0.9 = sticky)
uniform vec3  GridMin;
uniform vec3  GridInvCell;
uniform int   GridRes;

// Direct sun (same data/convention the march + volumetric use).
const int MAX_CASCADES = 4;
uniform mat4  CascadeMatrices[MAX_CASCADES];
uniform vec4  CascadeBias;
uniform int   CascadeCount;
uniform vec3  SunDirectionWorld;  // toward the sun
uniform vec3  SunColor;

vec3 Sanitize(vec3 v) {
    return vec3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

float SampleSunVisibility(vec3 worldPos) {
    for (int c = 0; c < CascadeCount && c < MAX_CASCADES; c++) {
        vec4 clip = CascadeMatrices[c] * vec4(worldPos, 1.0);
        float edge = max(abs(clip.x), abs(clip.y));
        vec3 proj = clip.xyz * 0.5 + 0.5;
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0)
            continue;
        return texture(ShadowMap, vec4(proj.xy, float(c), proj.z - CascadeBias[c]));
    }
    return 1.0;
}

// SDF value at an atlas texel coordinate (integer), via texelFetch on the SDF atlas R channel.
float SdfAt(ivec3 atlasTexel) {
    return texelFetch(SdfAtlas, atlasTexel, 0).r;
}

const float PI = 3.14159265359;

// ---- Bounce-gather helpers: a trimmed copy of the march's SDF trace + radiance read, so a voxel
// can gather incoming light from OTHER surfaces (multi-bounce). Reads LAST frame's RadianceCache. --

// Trilinear SDF distance for slot `slotIdx` at mesh-local `local` (cell-center convention).
float SampleSlotDist(uint slotIdx, vec3 local, out bool inside) {
    SsdfSlot sl = slots[slotIdx];
    vec3 bmin = sl.boundsMin.xyz, bmax = sl.boundsMax.xyz;
    vec3 res  = max(sl.res.xyz, vec3(1.0));
    vec3 cellSize = (bmax - bmin) / res;
    vec3 margin = 0.5 * cellSize;
    inside = all(greaterThanEqual(local, bmin - margin)) && all(lessThanEqual(local, bmax + margin));
    if (!inside) return 1e9;
    vec3 cell = (local - bmin) / cellSize - vec3(0.5);
    cell = clamp(cell, vec3(0.0), res - vec3(1.0001));
    vec3 base = clamp(floor(cell), vec3(0.0), max(res - vec3(2.0), vec3(0.0)));
    vec3 f = cell - base;
    ivec3 b = ivec3(base) + ivec3(sl.offsetRes0.xyz + 0.5);
    float c000 = SdfAt(b+ivec3(0,0,0)), c100 = SdfAt(b+ivec3(1,0,0));
    float c010 = SdfAt(b+ivec3(0,1,0)), c110 = SdfAt(b+ivec3(1,1,0));
    float c001 = SdfAt(b+ivec3(0,0,1)), c101 = SdfAt(b+ivec3(1,0,1));
    float c011 = SdfAt(b+ivec3(0,1,1)), c111 = SdfAt(b+ivec3(1,1,1));
    float x00=mix(c000,c100,f.x), x10=mix(c010,c110,f.x), x01=mix(c001,c101,f.x), x11=mix(c011,c111,f.x);
    return mix(mix(x00,x10,f.y), mix(x01,x11,f.y), f.z);
}

// Read LAST frame's cached radiance for slot at mesh-local point (normalized-UVW trilinear).
vec3 SampleCacheRadiance(uint slotIdx, vec3 local) {
    SsdfSlot sl = slots[slotIdx];
    vec3 res = max(sl.res.xyz, vec3(1.0));
    vec3 cellSize = (sl.boundsMax.xyz - sl.boundsMin.xyz) / res;
    vec3 cell = (local - sl.boundsMin.xyz) / cellSize - vec3(0.5);
    cell = clamp(cell, vec3(0.0), res - vec3(1.0001));
    vec3 atlasTexel = sl.offsetRes0.xyz + cell + vec3(0.5);
    return texture(RadianceCache, atlasTexel / vec3(textureSize(RadianceCache, 0))).rgb;
}

// Scene SDF at a world point via the instance grid (only the containing cell's instances).
float SceneSdf(vec3 worldP, out uint nearestSlot, out vec3 nearestLocal, out bool anyInside) {
    float dMin = 1e9; nearestSlot = 0u; nearestLocal = vec3(0.0); anyInside = false;
    ivec3 c = ivec3(floor((worldP - GridMin) * GridInvCell));
    if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(GridRes)))) return dMin;
    ivec2 range = gridCells[c.x + GridRes*(c.y + GridRes*c.z)];
    for (int k = 0; k < range.y; ++k) {
        uint i = gridList[range.x + k];
        SsdfInstance inst = instances[i];
        if (any(lessThan(worldP, inst.worldAabbMin.xyz)) || any(greaterThan(worldP, inst.worldAabbMax.xyz)))
            continue;
        vec3 lp = (inst.worldToLocal * vec4(worldP, 1.0)).xyz;
        bool ins; float sd = SampleSlotDist(inst.slot, lp, ins);
        if (ins && sd < dMin) { dMin = sd; nearestSlot = inst.slot; nearestLocal = lp; anyInside = true; }
    }
    return dMin;
}

// Gather ONE bounce of incoming radiance over the hemisphere at (worldP, nWorld): a few cosine rays,
// each sphere-traced through the SDF; on a hit read LAST frame's cached radiance there; on a miss add
// sky. Returns the average incoming radiance (irradiance/PI already folded by the cosine sampling).
vec3 GatherBounce(vec3 worldP, vec3 nWorld, float cell, uint selfSlot) {
    const int RAYS = 4;
    const int STEPS = 24;
    const float MAXD = 20.0;
    vec3 up = abs(nWorld.z) < 0.999 ? vec3(0,0,1) : vec3(1,0,0);
    vec3 T = normalize(cross(up, nWorld));
    vec3 B = cross(nWorld, T);
    vec3 sum = vec3(0.0);
    for (int r = 0; r < RAYS; ++r) {
        // Deterministic low-discrepancy-ish directions (no per-frame jitter needed — the EMA smooths).
        float a = (float(r) + 0.5) / float(RAYS);
        float phi = 6.2831853 * fract(a * 2.61803);
        float cosT = sqrt(1.0 - a * 0.85 - 0.1); // bias up off the surface, cosine-ish
        float sinT = sqrt(max(0.0, 1.0 - cosT*cosT));
        vec3 dir = normalize(T*cos(phi)*sinT + B*sin(phi)*sinT + nWorld*cosT);
        vec3 p = worldP + nWorld * (0.5 * cell);
        float traveled = 0.0; bool hit = false; uint hs = 0u; vec3 hl = vec3(0.0);
        for (int s = 0; s < STEPS; ++s) {
            uint ns; vec3 nl; bool any;
            float dist = SceneSdf(p, ns, nl, any);
            if (any && dist < 0.5*cell && traveled > 1.5*cell) { hit = true; hs = ns; hl = nl; break; }
            float adv = any ? max(dist, 0.5*cell) : 0.75;
            p += dir * adv; traveled += adv;
            if (traveled >= MAXD) break;
        }
        if (hit)
            sum += SampleCacheRadiance(hs, hl);          // light bounced off another surface
        else
            sum += Sanitize(textureLod(IrradianceMap, dir, 0.0).rgb) * SkyExposure; // sky
    }
    return sum / float(RAYS);
}

void main() {
    SsdfInstance inst = instances[InstanceIndex];
    SsdfSlot sl = slots[inst.slot];
    ivec3 res = ivec3(sl.res.xyz + 0.5);

    ivec3 v = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(v, res)))
        return;

    ivec3 atlasOff = ivec3(sl.offsetRes0.xyz + 0.5);
    ivec3 atlasTexel = atlasOff + v;

    // Occupancy: only NEAR-SURFACE voxels carry meaningful radiance. cellSize = extent/res; a voxel
    // is "on the surface" when |sdf| is within ~1 cell. Interior/far-exterior voxels stay empty.
    vec3 extent = sl.boundsMax.xyz - sl.boundsMin.xyz;
    vec3 cellSize = extent / max(sl.res.xyz, vec3(1.0));
    float cell = max(max(cellSize.x, cellSize.y), cellSize.z);
    float d = SdfAt(atlasTexel);
    if (abs(d) > 1.5 * cell) {
        // Empty voxel: decay any stale radiance toward 0 so it doesn't linger.
        vec4 old = imageLoad(RadianceAtlas, atlasTexel);
        imageStore(RadianceAtlas, atlasTexel, vec4(old.rgb * 0.5, old.a * 0.5));
        return;
    }

    // Voxel center in mesh-local space, then to world for lighting.
    vec3 local = sl.boundsMin.xyz + (vec3(v) + 0.5) * cellSize;
    vec3 worldP = (inst.world * vec4(local, 1.0)).xyz;

    // Surface normal = normalized SDF gradient (central differences in atlas texel space, mapped to
    // world via the instance's rotation). For lighting we want the WORLD normal: gradient in local
    // is the same direction (the atlas grid is axis-aligned in local space), then rotate by `world`.
    float dx = SdfAt(atlasTexel + ivec3(1,0,0)) - SdfAt(atlasTexel - ivec3(1,0,0));
    float dy = SdfAt(atlasTexel + ivec3(0,1,0)) - SdfAt(atlasTexel - ivec3(0,1,0));
    float dz = SdfAt(atlasTexel + ivec3(0,0,1)) - SdfAt(atlasTexel - ivec3(0,0,1));
    vec3 gLocal = vec3(dx, dy, dz);
    vec3 nWorld = mat3(inst.world) * gLocal;
    float nl = length(nWorld);
    nWorld = nl > 1e-5 ? nWorld / nl : vec3(0.0, 1.0, 0.0);

    // Push the lighting sample point just off the surface (along the world normal) so the shadow
    // lookup doesn't self-shadow the voxel.
    vec3 litPos = worldP + nWorld * (0.5 * cell);

    // Incoming irradiance at the voxel: direct sun (cosine, shadowed) + ONE BOUNCE gathered from the
    // rest of the scene's cached radiance (the multi-bounce term — this is what carries the emissive
    // panel's light onto the walls, building up over frames via the EMA). The sky comes in through
    // the gather's missed rays, so it isn't added separately (would double-count).
    vec3 toSun = normalize(SunDirectionWorld);
    float ndl = max(dot(nWorld, toSun), 0.0);
    float vis = SampleSunVisibility(litPos);
    vec3 direct = SunColor * (ndl * vis);
    vec3 bounce = GatherBounce(worldP, nWorld, cell, inst.slot);
    // Lambert: outgoing = emissive + (albedo/PI) * incoming irradiance. (GatherBounce already
    // averages over the cosine hemisphere, so it's the incoming radiance estimate; the albedo/PI
    // is the diffuse BRDF. Direct sun gets the same albedo factor.)
    vec3 radiance = inst.emissive.xyz + (inst.albedo.xyz / PI) * (direct + PI * bounce);
    radiance = Sanitize(radiance);

    // Temporal EMA: blend with the existing cached value for stability + crude multi-bounce buildup.
    vec4 old = imageLoad(RadianceAtlas, atlasTexel);
    vec3 blended = old.a > 0.0 ? mix(radiance, old.rgb, Feedback) : radiance;
    imageStore(RadianceAtlas, atlasTexel, vec4(Sanitize(blended), 1.0));
}
