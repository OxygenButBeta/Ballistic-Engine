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

uniform int  InstanceIndex;  // which instance this dispatch injects (one dispatch per instance)
uniform float SkyExposure;
uniform float Feedback;      // temporal EMA weight for the OLD value (0 = replace, ~0.9 = sticky)

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

    vec3 toSun = normalize(SunDirectionWorld);
    float ndl = max(dot(nWorld, toSun), 0.0);
    float vis = SampleSunVisibility(litPos);
    vec3 sky = Sanitize(textureLod(IrradianceMap, nWorld, 0.0).rgb) * SkyExposure;
    vec3 radiance = inst.albedo.xyz * (SunColor * (ndl * vis) + sky) + inst.emissive.xyz;
    radiance = Sanitize(radiance);

    // Temporal EMA: blend with the existing cached value for stability + crude multi-bounce buildup.
    vec4 old = imageLoad(RadianceAtlas, atlasTexel);
    vec3 blended = old.a > 0.0 ? mix(radiance, old.rgb, Feedback) : radiance;
    imageStore(RadianceAtlas, atlasTexel, vec4(Sanitize(blended), 1.0));
}
