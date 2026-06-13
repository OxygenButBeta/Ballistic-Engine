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
    vec4 planes[6];     // frustum planes (left,right,bottom,top,near,far), same as CPU ExtractFrustumPlanes
    uint submeshCount;
    uint pass;          // 0 = opaque, 1 = transparent (kept for future; opaque-only for now)
    uint cutoutFilter;  // 0 = SOLID only (cutout flag clear), 1 = CUTOUT only — two batches: cutout
                        // cards draw with backface culling OFF (single-sided foliage), like the CPU.
    uint pad1;
};

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
