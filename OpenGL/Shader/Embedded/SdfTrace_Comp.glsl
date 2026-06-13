#version 460 core

// SDF World-Space GI (P6.3, DESIGN.md component 3) — HALF-RES off-screen indirect gather.
//
// This is the Lumen differentiator the baked IBL probes (static) and SSGI (screen-space,
// blind to off-screen geometry) cannot provide: dynamic OFF-SCREEN bounce. For each
// half-res pixel we reconstruct the world-space surface point + normal from the full-res
// G-buffer, build a tangent frame, and trace N cosine-hemisphere rays through a sparse set
// of per-mesh signed-distance fields packed into one atlas 3D texture. Sphere-tracing finds
// the nearest off-screen surface; on hit we approximate its lit radiance with the baked IBL
// irradiance map (the surface cache, P7, replaces this later); on miss the ray sees sky.
//
// Correctness is the hard part here. The atlas packs each distinct mesh's MeshSdf grid as an
// axis-aligned sub-volume; mapping a WORLD point through that packing to the right atlas
// texel (with manual trilinear) is the load-bearing math, commented carefully below.
//
// Default-OFF subsystem (BALLISTIC_SDFGI=1). With the flag off this compute never runs, so
// the renderer is byte-identical to the committed baseline.

layout(local_size_x = 8, local_size_y = 8) in;

// ---------------------------------------------------------------------------------------
// G-buffer (full-res; we sample at half-res UVs — the half pixel center maps to a full-res
// texel via texture() bilinear, fine for an indirect gather).
// ---------------------------------------------------------------------------------------
layout(binding = 1) uniform sampler2D DepthTex;    // window depth [0,1]
layout(binding = 2) uniform sampler2D NormalTex;   // world normal*0.5+0.5 in rgb, rough/metal in a

// Baked IBL diffuse irradiance (the SKY term + the miss radiance).
layout(binding = 3) uniform samplerCube IrradianceMap;

// Cascaded directional shadow map (hardware PCF compare) — same texture/convention the lit pass
// and the volumetric march use. Lets the SDF hit evaluate DIRECT SUN visibility, so the gather
// returns BRIGHT lit bounce (the sun is the source the dim IBL irradiance lacks) instead of dim sky.
layout(binding = 5) uniform sampler2DArrayShadow ShadowMap;

// The packed SDF atlas: one R16F 3D texture. We DON'T rely on its hardware linear filtering
// for the sub-volume sample (a slot's neighbour bleeds across the packing boundary), so we do
// manual trilinear with texelFetch on integer atlas coords.
layout(binding = 4) uniform sampler3D SdfAtlas;

// Output: half-res RGBA16F. rgb = gathered off-screen indirect radiance, a = validity
// (0 = sky/invalid pixel, 1 = a real surface that got a gather).
layout(rgba16f, binding = 0) uniform writeonly image2D OutGi;

// ---------------------------------------------------------------------------------------
// SDF scene SSBOs. Bindings 8/9 (2..7 are GpuDriven, UBO 0 is PassData — do NOT reuse).
//
// SsdfInstance: one record per visible/opaque renderer that has a bakeable mesh.
//   worldToLocal — inverse of the renderer's world matrix; maps a world point into the
//                  mesh-local space the SDF was baked in.
//   slot         — index into SdfSlot[] (which atlas sub-volume / bounds this instance uses).
//   p0,p1,p2     — padding so the struct is a multiple of 16 bytes (std430 mat4 is 64,
//                  +uint*4 = 80, a clean 16-byte multiple; the integrator must match this).
// ---------------------------------------------------------------------------------------
struct SsdfInstance {
    mat4 worldToLocal;
    vec4 worldAabbMin;   // xyz = instance world-space AABB min (cheap pre-reject before the transform)
    vec4 worldAabbMax;   // xyz = instance world-space AABB max
    uint slot;
    uint p0;
    uint p1;
    uint p2;
};

// SsdfSlot: one record per distinct baked mesh, describing where its grid lives in the atlas
// and what mesh-local region it covers. All vec4 (16-byte aligned) for a tight std430 layout.
//   offsetRes0 — .xyz = integer atlas offset in TEXELS of this slot's (0,0,0) corner. .w unused.
//   res        — .xyz = cell counts (MeshSdf.Res); the grid is res.xyz samples along each axis.
//                A grid of N cells spans N samples at indices 0..N-1; cell size = (boundsMax -
//                boundsMin) / (res - 1)  (samples sit ON the bounds, MeshSdf.Sample convention).
//                .w unused.
//   boundsMin  — .xyz = mesh-local AABB min (where atlas index 0 sits). .w unused.
//   boundsMax  — .xyz = mesh-local AABB max (where atlas index res-1 sits). .w unused.
struct SsdfSlot {
    vec4 offsetRes0;
    vec4 res;
    vec4 boundsMin;
    vec4 boundsMax;
};

layout(std430, binding = 8) readonly buffer InstanceBuf { SsdfInstance instances[]; };
layout(std430, binding = 9) readonly buffer SlotBuf     { SsdfSlot     slots[]; };

// ---------------------------------------------------------------------------------------
// Uniforms (plain uniforms — set per-dispatch by the C# pass, NOT a UBO).
// ---------------------------------------------------------------------------------------
uniform mat4 InvProjection;  // clip -> view (same reconstruction SSGI/SSR use)
uniform mat4 InvView;        // view -> world
uniform uint InstanceCount;  // number of valid SsdfInstance records
uniform ivec2 HalfSize;      // output (half-res) dimensions in pixels
uniform float SkyExposure;   // irradiance luminance scale x camera pre-exposure
uniform int  FrameIndex;     // rotates the ray hash each frame (temporal resolves the noise)

// Direct-sun lighting at the hit (the bright-bounce source). Same data the volumetric march uses.
const int MAX_CASCADES = 4;
uniform mat4  CascadeMatrices[MAX_CASCADES]; // world -> light clip per cascade
uniform vec4  CascadeBias;                   // compare-space bias per cascade
uniform int   CascadeCount;
uniform vec3  SunDirectionWorld;             // normalized, points TOWARD the light
uniform vec3  SunColor;                      // pre-exposed sun radiance
// Neutral diffuse albedo for the hit surface (the SDF carries no per-hit material in v1). A mid
// grey reflects the room's character without over-saturating; the surface cache (next) replaces
// this with the real per-surface lit radiance.
uniform float HitAlbedo;
// Diagnostic: 0 = normal radiance gather; 1 = output the HIT FRACTION as grayscale (white = every
// ray hit SDF geometry, black = every ray escaped to sky). Disambiguates "rays miss" (granularity)
// from "radiance too dim" (needs the surface cache) when the gather looks empty. Set by the pass
// from BALLISTIC_SDFGI_DIAG; never on in shipping.
uniform int  DiagMode;

// ---------------------------------------------------------------------------------------
// March tuning. World-space metres.
// ---------------------------------------------------------------------------------------
const int   RAY_COUNT   = 4;      // cosine-hemisphere rays per pixel
const int   MAX_STEPS   = 48;     // sphere-trace steps per ray (raised: empty space marches coarsely)
const float MAX_DIST    = 30.0;   // max world march distance (metres)
const float HIT_EPS     = 0.02;   // |dist| below this = surface hit (metres)
const float MIN_STEP    = 0.05;   // floor on the advance so we never stall in a flat region
const float EMPTY_STEP  = 0.75;   // coarse fixed step through space NO brick covers (sparse scenes)
const float NORMAL_EPS  = 0.05;   // finite-difference step for the SDF gradient (metres)
const float PI = 3.14159265359;

// MUST be a true component SELECT, never arithmetic on the bad value: mix(v, 0, flag) expands
// to v*(1-flag) + 0*flag, and NaN*0 == NaN / Inf*0 == NaN in IEEE, so that form passes the
// poison straight through (proven on AMD RX 9070 XT — it grew into a screen-eating field).
vec3 Sanitize(vec3 v) {
    return vec3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
float SanitizeF(float v) {
    return (isnan(v) || isinf(v)) ? 0.0 : v;
}

float Hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

vec3 ViewPosFromDepth(vec2 uv, float depth) {
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    return view.xyz / view.w;
}

// ---------------------------------------------------------------------------------------
// Sample one slot's SDF at a MESH-LOCAL point with manual trilinear in atlas texel space.
//
// MUST mirror MeshSdf.Sample EXACTLY (the authoritative CPU field this atlas was baked from) or
// every fetch is spatially misregistered — the prior "all-teal" class of bug. CPU convention
// (MeshSdf.cs): samples are CELL-CENTERED, so cell (i,j,k) holds the value at
//     center = BoundsMin + (i+0.5) * CellSize,   CellSize = (BoundsMax - BoundsMin) / Res   (÷ Res, NOT res-1)
// The continuous index of a local point is therefore
//     cell = (local - BoundsMin) / CellSize - 0.5
// clamped to [0, Res - 1.0001]. floor(cell) and floor(cell)+1 are the bracketing centered samples;
// the fractional part is the trilinear weight. We add the slot's atlas texel offset and texelFetch .r.
//
// `inside` reports whether `local` is within this slot's bounds (half-cell margin so the boundary
// samples stay usable). Out-of-bounds returns a large positive distance so the caller's min() skips
// this slot.
// ---------------------------------------------------------------------------------------
float SampleSlot(uint slotIdx, vec3 local, out bool inside) {
    SsdfSlot sl = slots[slotIdx];
    vec3 bmin = sl.boundsMin.xyz;
    vec3 bmax = sl.boundsMax.xyz;

    vec3 span = max(bmax - bmin, vec3(1e-6));
    vec3 res  = max(sl.res.xyz, vec3(1.0));
    // CellSize = extent / Res (NOT res-1) — cell-centered grid, MeshSdf.cs convention.
    vec3 cellSize = span / res;
    vec3 margin = 0.5 * cellSize; // half a cell of slack at the boundary

    inside = all(greaterThanEqual(local, bmin - margin)) &&
             all(lessThanEqual(local, bmax + margin));
    if (!inside)
        return 1e9;

    // Continuous CELL-CENTER index: subtract 0.5 so integer indices land on cell centers, exactly
    // like MeshSdf.Sample. Clamp to the valid sample range so the trilinear stencil never reaches a
    // neighbouring slot's texels across the packing boundary.
    vec3 cell = (local - bmin) / cellSize - vec3(0.5);
    cell = clamp(cell, vec3(0.0), res - vec3(1.0001));

    // floor(cell) is the low corner; base+1 must stay <= res-1, so clamp base to res-2.
    vec3 base = floor(cell);
    base = clamp(base, vec3(0.0), max(res - vec3(2.0), vec3(0.0)));
    vec3 f = cell - base; // trilinear weights in [0,1]

    ivec3 off = ivec3(sl.offsetRes0.xyz + 0.5); // atlas texel offset of this slot's (0,0,0)
    ivec3 b   = ivec3(base) + off;

    // Fetch the 8 corner samples directly (texelFetch = no hardware filtering / no slot bleed).
    float c000 = texelFetch(SdfAtlas, b + ivec3(0, 0, 0), 0).r;
    float c100 = texelFetch(SdfAtlas, b + ivec3(1, 0, 0), 0).r;
    float c010 = texelFetch(SdfAtlas, b + ivec3(0, 1, 0), 0).r;
    float c110 = texelFetch(SdfAtlas, b + ivec3(1, 1, 0), 0).r;
    float c001 = texelFetch(SdfAtlas, b + ivec3(0, 0, 1), 0).r;
    float c101 = texelFetch(SdfAtlas, b + ivec3(1, 0, 1), 0).r;
    float c011 = texelFetch(SdfAtlas, b + ivec3(0, 1, 1), 0).r;
    float c111 = texelFetch(SdfAtlas, b + ivec3(1, 1, 1), 0).r;

    // Standard trilinear: lerp along x, then y, then z.
    float x00 = mix(c000, c100, f.x);
    float x10 = mix(c010, c110, f.x);
    float x01 = mix(c001, c101, f.x);
    float x11 = mix(c011, c111, f.x);
    float y0  = mix(x00, x10, f.y);
    float y1  = mix(x01, x11, f.y);
    return mix(y0, y1, f.z);
}

// Scene SDF at a WORLD point: min over all instances of (point transformed to local, sampled).
// `nearestSlot` and `nearestLocal` report which instance was closest, for the gradient on hit.
float SceneSdf(vec3 worldP, out uint nearestSlot, out vec3 nearestLocal, out bool anyInside) {
    float d = 1e9;
    nearestSlot = 0u;
    nearestLocal = vec3(0.0);
    anyInside = false;
    for (uint i = 0u; i < InstanceCount; ++i) {
        SsdfInstance inst = instances[i];
        // Cheap world-AABB pre-reject BEFORE the matrix transform + 8 texelFetches: skip any
        // instance the march point isn't inside (a small margin covers the padded brick shell).
        // This is the perf win for hundreds of per-submesh instances — most are far from any point.
        if (any(lessThan(worldP, inst.worldAabbMin.xyz)) ||
            any(greaterThan(worldP, inst.worldAabbMax.xyz)))
            continue;
        vec3 local = (inst.worldToLocal * vec4(worldP, 1.0)).xyz;
        bool inside;
        float sd = SampleSlot(inst.slot, local, inside);
        if (inside && sd < d) {
            d = sd;
            nearestSlot = inst.slot;
            nearestLocal = local;
            anyInside = true;
        }
    }
    return d;
}

// SDF gradient (surface normal) at a world point, via central differences of SceneSdf. World
// space — the per-instance worldToLocal rotation is absorbed because we difference in world.
vec3 SceneGradient(vec3 worldP) {
    uint s; vec3 l; bool ins;
    float dx = SceneSdf(worldP + vec3(NORMAL_EPS, 0.0, 0.0), s, l, ins)
             - SceneSdf(worldP - vec3(NORMAL_EPS, 0.0, 0.0), s, l, ins);
    float dy = SceneSdf(worldP + vec3(0.0, NORMAL_EPS, 0.0), s, l, ins)
             - SceneSdf(worldP - vec3(0.0, NORMAL_EPS, 0.0), s, l, ins);
    float dz = SceneSdf(worldP + vec3(0.0, 0.0, NORMAL_EPS), s, l, ins)
             - SceneSdf(worldP - vec3(0.0, 0.0, NORMAL_EPS), s, l, ins);
    vec3 g = vec3(dx, dy, dz);
    float len = length(g);
    return len > 1e-5 ? g / len : vec3(0.0, 1.0, 0.0);
}

// Sun visibility at a world point via the cascade that covers it (0 = shadowed, 1 = lit). Copied
// verbatim from Volumetric_Frag.SampleSunVisibility so the SDF hit's shadowing matches the lit pass.
float SampleSunVisibility(vec3 worldPos) {
    for (int c = 0; c < CascadeCount && c < MAX_CASCADES; c++) {
        vec4 clip = CascadeMatrices[c] * vec4(worldPos, 1.0);
        float edge = max(abs(clip.x), abs(clip.y));
        vec3 proj = clip.xyz * 0.5 + 0.5; // ortho: w == 1
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0)
            continue;
        return texture(ShadowMap, vec4(proj.xy, float(c), proj.z - CascadeBias[c]));
    }
    return 1.0; // outside every cascade: lit (matches the lit shader)
}

// Lit radiance of an off-screen surface at `worldHit` with geometric normal `hitN`: direct sun
// (cosine, shadowed) + sky irradiance, times a neutral albedo. This is single-bounce GI evaluated
// at the hit — the bright source the dim IBL-only v1 lacked. The surface cache (next phase) will
// replace this with real per-surface multi-bounce radiance.
vec3 HitRadiance(vec3 worldHit, vec3 hitN) {
    vec3 toLight = normalize(SunDirectionWorld);   // points toward the sun
    float ndl = max(dot(hitN, toLight), 0.0);
    float vis = SampleSunVisibility(worldHit);
    vec3 direct = SunColor * (ndl * vis);
    vec3 sky = Sanitize(textureLod(IrradianceMap, hitN, 0.0).rgb) * SkyExposure;
    return HitAlbedo * (direct + sky);
}

void main() {
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    if (px.x >= HalfSize.x || px.y >= HalfSize.y)
        return;

    // Half-res pixel center -> [0,1] UV (full-res G-buffer sampled bilinearly here).
    vec2 uv = (vec2(px) + 0.5) / vec2(HalfSize);

    float depth = texture(DepthTex, uv).r;
    vec4 nr = texture(NormalTex, uv);
    vec3 worldN = nr.rgb * 2.0 - 1.0;

    // Sky or un-shaded pixels: nothing to gather here.
    if (depth >= 1.0 || dot(worldN, worldN) < 0.1) {
        imageStore(OutGi, px, vec4(0.0));
        return;
    }

    worldN = normalize(worldN);

    // Reconstruct world-space surface point: depth -> view -> world.
    vec3 viewP = ViewPosFromDepth(uv, depth);
    vec3 worldP = (InvView * vec4(viewP, 1.0)).xyz;

    // Tangent frame around the world normal (Frisvad-style branch to avoid the degenerate axis).
    vec3 up = abs(worldN.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 T = normalize(cross(up, worldN));
    vec3 B = cross(worldN, T);

    // Start the march just off the surface so we don't immediately self-hit our own SDF.
    vec3 origin = worldP + worldN * max(HIT_EPS * 2.0, MIN_STEP);

    vec3 gathered = vec3(0.0);
    int hitCount = 0;

    for (int r = 0; r < RAY_COUNT; ++r) {
        // Cosine-weighted hemisphere sample. Hash by pixel + ray index + frame so the noise
        // rotates every frame and the (future) temporal pass can resolve it.
        vec2 h = vec2(
            Hash(vec2(px) + vec2(float(r) * 1.7, float(FrameIndex) * 1.618)),
            Hash(vec2(px) * 1.31 + vec2(float(r) * 2.3 + float(FrameIndex) * 0.911, 7.0)));
        float phi = 2.0 * PI * h.x;
        float cosT = sqrt(1.0 - h.y);   // cosine-weighted: cosT = sqrt(1-xi)
        float sinT = sqrt(h.y);
        vec3 localDir = vec3(cos(phi) * sinT, sin(phi) * sinT, cosT);
        vec3 dir = normalize(T * localDir.x + B * localDir.y + worldN * localDir.z);

        // Sphere-trace the world-space SDF.
        vec3 p = origin;
        float traveled = 0.0;
        bool hit = false;
        vec3 hitPoint = p;

        for (int s = 0; s < MAX_STEPS; ++s) {
            uint nearSlot; vec3 nearLocal; bool anyInside;
            float dist = SceneSdf(p, nearSlot, nearLocal, anyInside);
            dist = SanitizeF(dist);

            // Hit when we're at (or just inside) a surface AND we were actually inside some
            // instance's volume (anyInside guards against the 1e9 "no slot covers this point").
            if (anyInside && dist < HIT_EPS) {
                hit = true;
                hitPoint = p;
                break;
            }

            // Advance. INSIDE a brick: true sphere-trace step (max(dist, MIN_STEP)). In EMPTY space
            // (no brick covers p, dist == 1e9) we MUST NOT teleport to infinity — march a coarse
            // fixed step so the ray can cross the gap and enter a DIFFERENT mesh's brick (the whole
            // point of off-screen cross-mesh bounce in sparse multi-mesh scenes).
            float advance = anyInside ? max(dist, MIN_STEP) : EMPTY_STEP;
            p += dir * advance;
            traveled += advance;
            if (traveled >= MAX_DIST)
                break;
        }

        vec3 radiance = vec3(0.0);
        if (hit) {
            hitCount++;
            // Lit radiance at the hit = direct sun (shadowed) + sky, x neutral albedo. The SDF
            // gradient is the hit normal; if it degenerates, face the gather point (reversed ray).
            vec3 hitN = SceneGradient(hitPoint);
            if (dot(hitN, hitN) < 1e-5)
                hitN = -dir;
            radiance = Sanitize(HitRadiance(hitPoint, hitN));
        }
        // MISS = the ray escaped to open sky. Contribute ZERO: the sky's contribution to this
        // surface is ALREADY in the baked IBL ambient that lit the scene color we composite onto.
        // Re-adding sky irradiance here DOUBLE-COUNTS it — that washed the bright exterior milky
        // (mean +21, contrast lost). This GI term is purely the OFF-SCREEN BOUNCE the IBL/SSGI
        // miss: only real surface HITS contribute. Open scenes (mostly-miss) correctly get ~0 GI;
        // enclosed scenes (mostly-hit) get the full colored bounce.

        gathered += radiance;
    }

    // Diagnostic: emit the hit fraction (white = all rays hit SDF geometry) instead of radiance.
    if (DiagMode == 1) {
        float frac = float(hitCount) / float(RAY_COUNT);
        imageStore(OutGi, px, vec4(frac, frac, frac, 1.0));
        return;
    }

    gathered /= float(RAY_COUNT);
    gathered = Sanitize(gathered);

    imageStore(OutGi, px, vec4(gathered, 1.0));
}
