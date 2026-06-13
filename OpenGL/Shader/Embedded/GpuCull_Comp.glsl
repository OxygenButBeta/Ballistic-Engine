#version 460 core
// GPU-driven frustum culling + draw-command compaction.
// One invocation per submesh: test its world AABB against the frustum, and if visible emit a
// glDrawElementsIndirect command + a per-draw data record into dense, atomically-allocated slots.
// The result is consumed by glMultiDrawElementsIndirectCount in ONE draw call.
//
// Correctness contract: this MUST select the exact same submeshes the CPU path culled, using the
// SAME positive-vertex AABB-vs-plane test, so the rendered image is unchanged. Opaque draw order
// is irrelevant to the final color (z-prepass => each pixel shaded once, no blending), so dense
// compaction in arbitrary order is safe.

layout(local_size_x = 64) in;

struct SubmeshMeta {
    mat4 model;        // per-submesh model matrix (InverseNodeTransform * world)
    vec4 localAabbMin; // baked model-space AABB, w unused
    vec4 localAabbMax;
    uint firstIndex;
    uint indexCount;
    uint materialId;
    uint flags;        // bit0 cutout, bit1 transparent
};

struct DrawCmd {
    uint count;
    uint instanceCount;
    uint firstIndex;
    uint baseVertex;
    uint baseInstance;
};

struct PerDraw {
    mat4 model;
    uint materialId;
    uint pad0; uint pad1; uint pad2;
};

layout(std430, binding = 2) readonly buffer MetaBuf   { SubmeshMeta meta[]; };
layout(std430, binding = 3) writeonly buffer CmdBuf   { DrawCmd cmds[]; };
layout(std430, binding = 4) buffer CountBuf           { uint drawCount; };
layout(std430, binding = 5) writeonly buffer DrawBuf  { PerDraw perDraw[]; };

layout(std140, binding = 7) uniform CullParams {
    mat4 viewProj;      // for Hi-Z screen-space projection (un-jittered, matches the pyramid)
    mat4 view;          // world -> view, to get the AABB's LINEAR view Z (window depth is too
                        // non-linear to compare directly — Sun Temple depth sits in [0.96,1.0])
    vec4 planes[6];     // frustum planes (left,right,bottom,top,near,far), same as CPU ExtractFrustumPlanes
    vec4 hizParams;     // x = pyramid width, y = height, z = mip count, w = Hi-Z enabled (>0.5)
    vec4 linearize;     // x = proj[2][2], y = proj[3][2] — window-depth -> linear view Z reconstruction
    uint submeshCount;
    uint pass;          // 0 = opaque, 1 = transparent (kept for future; opaque-only for now)
    uint cutoutFilter;  // 0 = SOLID only (cutout flag clear), 1 = CUTOUT only — two batches: cutout
                        // cards draw with backface culling OFF (single-sided foliage), like the CPU.
    uint pad1;
};

// Window depth [0,1] -> linear distance in front of the camera (positive metres). Standard GL
// perspective: zndc = M33 + M43/z_view (z_view negative in front), so z_view = M43/(zndc - M33).
// abs() gives a robust positive distance regardless of the OpenTK sign conventions (verified:
// depth 0.9586 with near=0.1/far=1000 -> ~2.4 m).
float linearViewDist(float windowDepth) {
    float zndc = windowDepth * 2.0 - 1.0;
    return abs(linearize.y / (zndc + linearize.x)); // |M43 / (z_ndc + M33)| = view distance (m)
}

// Hi-Z pyramid: each mip texel = MAX (farthest) window-depth of its footprint (built last frame).
layout(binding = 0) uniform sampler2D HiZPyramid;

// Positive-vertex AABB test - identical to GLHDRenderer.AabbInFrustum.
bool aabbInFrustum(vec3 mn, vec3 mx) {
    for (int i = 0; i < 6; ++i) {
        vec4 pl = planes[i];
        vec3 p = vec3(pl.x >= 0.0 ? mx.x : mn.x,
                      pl.y >= 0.0 ? mx.y : mn.y,
                      pl.z >= 0.0 ? mx.z : mn.z);
        if (dot(pl.xyz, p) + pl.w < 0.0)
            return false;
    }
    return true;
}

// The AABB stored in localAabbMin/Max is ALREADY in world space (the C# builder transformed it
// with the exact 8-corner loop the CPU cull uses), so the cull tests it directly. No in-shader
// transform => bit-identical to the CPU AabbInFrustum for BOTH the camera and the light frustum.

// Conservative Hi-Z occlusion test. Projects the world AABB's 8 corners to screen space, takes the
// screen-space rectangle + the AABB's NEAREST window-depth, samples the pyramid mip whose texel
// covers the whole rectangle, and reports occluded ONLY when the nearest corner is strictly behind
// the farthest occluder in that footprint. MAX-pyramid + nearest-corner test => can never false-cull
// (it only culls when the ENTIRE AABB is provably behind a closer surface). Returns false (visible)
// for any AABB that crosses the near plane or the screen edge — those are never safely occluded.
bool occludedByHiZ(vec3 mn, vec3 mx) {
    if (hizParams.w < 0.5)
        return false; // Hi-Z disabled this frame (e.g. fast camera motion)

    vec2 uvMin = vec2(1e9), uvMax = vec2(-1e9);
    float nearDist = 1e9;   // nearest LINEAR view distance among the corners (metres)
    for (int c = 0; c < 8; ++c) {
        vec3 corner = vec3((c & 1) == 0 ? mn.x : mx.x,
                           (c & 2) == 0 ? mn.y : mx.y,
                           (c & 4) == 0 ? mn.z : mx.z);
        vec4 clip = viewProj * vec4(corner, 1.0);
        if (clip.w <= 1e-5)
            return false;            // crosses/behind the near plane — don't risk it
        vec3 ndc = clip.xyz / clip.w;
        if (any(lessThan(ndc.xy, vec2(-1.0))) || any(greaterThan(ndc.xy, vec2(1.0))))
            return false;            // touches the screen edge — keep it
        vec2 uv = ndc.xy * 0.5 + 0.5;
        uvMin = min(uvMin, uv);
        uvMax = max(uvMax, uv);
        nearDist = min(nearDist, -(view * vec4(corner, 1.0)).z); // linear distance in front of camera
    }

    // Pick the mip where the rectangle spans at most ~2 texels, so a small set of fetches covers it.
    vec2 sizePx = (uvMax - uvMin) * hizParams.xy;
    float level = ceil(log2(max(max(sizePx.x, sizePx.y), 1.0)));
    level = clamp(level, 0.0, hizParams.z - 1.0);

    // Farthest (MAX) occluder window-depth over the footprint.
    float o0 = textureLod(HiZPyramid, vec2(uvMin.x, uvMin.y), level).r;
    float o1 = textureLod(HiZPyramid, vec2(uvMax.x, uvMin.y), level).r;
    float o2 = textureLod(HiZPyramid, vec2(uvMin.x, uvMax.y), level).r;
    float o3 = textureLod(HiZPyramid, vec2(uvMax.x, uvMax.y), level).r;
    float maxOccluderDepth = max(max(o0, o1), max(o2, o3));
    if (maxOccluderDepth >= 1.0)
        return false; // footprint includes sky (far clear) — never safely occluded

    // Compare in LINEAR distance with a metric bias (robust where window depth is bunched near 1).
    float occluderDist = linearViewDist(maxOccluderDepth);
    const float BIAS_METRES = 0.25;
    return nearDist > occluderDist + BIAS_METRES;
}

void main() {
    uint id = gl_GlobalInvocationID.x;
    if (id >= submeshCount)
        return;

    SubmeshMeta m = meta[id];

    // Skip empty ranges and (for the opaque pass) transparent submeshes.
    if (m.indexCount == 0u)
        return;
    bool transparent = (m.flags & 2u) != 0u;
    if (pass == 0u && transparent)
        return;
    if (pass == 1u && !transparent)
        return;

    // Solid/cutout partition: cutout cards draw in a separate batch with backface culling off, so
    // the two batches never mix. cutoutFilter 0 keeps SOLID (cutout bit clear), 1 keeps CUTOUT.
    bool cutout = (m.flags & 1u) != 0u;
    if (cutoutFilter == 0u && cutout)
        return;
    if (cutoutFilter == 1u && !cutout)
        return;

    if (!aabbInFrustum(m.localAabbMin.xyz, m.localAabbMax.xyz))
        return;

    // Hi-Z occlusion: drop submeshes whose whole AABB is provably behind a closer occluder.
    // Conservative (never false-culls); only active for the camera pass (planes match the pyramid).
    if (occludedByHiZ(m.localAabbMin.xyz, m.localAabbMax.xyz))
        return;

    // Visible: allocate a dense slot and emit the command + per-draw record.
    uint slot = atomicAdd(drawCount, 1u);

    cmds[slot].count         = m.indexCount;
    cmds[slot].instanceCount = 1u;
    cmds[slot].firstIndex    = m.firstIndex;
    cmds[slot].baseVertex    = 0u;
    cmds[slot].baseInstance  = slot;   // also exposed as gl_BaseInstance if needed

    perDraw[slot].model      = m.model;
    perDraw[slot].materialId = m.materialId;
}
