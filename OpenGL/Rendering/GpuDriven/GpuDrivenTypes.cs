using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GpuDriven;

// GL's glDrawElementsIndirect command, exactly as the GPU expects it (20 bytes, 5x uint).
// The compute cull shader writes an array of these; glMultiDrawElementsIndirectCount draws them.
//   count         = index count of the submesh
//   instanceCount = 1 (or 0 to skip; cull writes 1 for visible)
//   firstIndex    = IndexStart of the submesh (offset into the shared index buffer)
//   baseVertex    = 0 (the mesh shares one vertex buffer; submeshes are index ranges)
//   baseInstance  = unused here (we index per-draw data by gl_DrawID instead)
[StructLayout(LayoutKind.Sequential)]
public struct DrawElementsIndirectCommand {
    public uint Count;
    public uint InstanceCount;
    public uint FirstIndex;
    public uint BaseVertex;
    public uint BaseInstance;

    public const int SizeBytes = 20;
}

// Per-submesh metadata the cull compute reads (binding 2). std430 layout — mat4 is 64B aligned to
// 16, vec4s aligned to 16. Laid out so the C# struct blits straight to the GPU buffer.
// AABB stored as min.xyz/max.xyz in LOCAL (baked model) space; compute transforms by Model.
[StructLayout(LayoutKind.Sequential)]
public struct SubmeshMeta {
    public Matrix4 Model;        // 64B  — InverseNodeTransform * world (per-submesh model matrix)
    public Vector4 LocalAabbMin; // 16B  — w unused
    public Vector4 LocalAabbMax; // 16B  — w unused
    public uint FirstIndex;      // IndexStart
    public uint IndexCount;
    public uint MaterialId;      // index into the material table (bindless)
    public uint Flags;           // bit0 = cutout (disable backface cull), bit1 = transparent (skip)

    // 64 + 16 + 16 + 16 = 112 bytes, already a multiple of 16.
    public const int SizeBytes = 112;
}

// Per-DRAW data written by the cull compact, indexed by gl_DrawID in the vertex/fragment shader
// (binding 5). One entry per emitted command, dense. std430: mat4 + uint + padding to 16.
[StructLayout(LayoutKind.Sequential)]
public struct PerDrawData {
    public Matrix4 Model;   // 64B
    public uint MaterialId; // 4B
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;       // pad to 80B (multiple of 16)

    public const int SizeBytes = 80;
}

// One material's bindless texture handles + scalar factors (binding 6), indexed by MaterialId.
// uvec2 = a GLuint64 bindless sampler handle. 0 = no map (shader falls back to a default).
// std430: 6 uvec2 (8B each, aligned 8) + vec4 + vec4 + uvec4 flags. Total padded to 16.
[StructLayout(LayoutKind.Sequential)]
public struct GpuMaterial {
    public ulong DiffuseHandle;    // 8B
    public ulong NormalHandle;
    public ulong MetallicHandle;
    public ulong RoughnessHandle;
    public ulong AoHandle;
    public ulong EmissiveHandle;   // 6 * 8 = 48B
    public Vector4 BaseColorFactor;     // 16B
    public Vector4 EmissiveFactor;      // xyz emissive*intensity, w unused — 16B
    public float MetallicMultiplier;    // 4B
    public float RoughnessMultiplier;
    public float NormalStrength;
    public float Opacity;               // 16B
    // Flags packed: bit0 PackedOrm, bit1 HasMetallic, bit2 HasRoughness, bit3 NormalFlipY,
    // bit4 HasEmissive, bit5 AlphaBlend, bit6 AlphaCutout, bit7 HasDiffuse, bit8 HasNormal, bit9 HasAO
    public uint Flags;
    public float SpecularReflectance;   // glTF KHR_materials_specular (dielectric F0 = 0.08*this); was Pad0
    public float Clearcoat;             // KHR_materials_clearcoat strength; was Pad1
    public float ClearcoatRoughness;    // the coat's roughness; was Pad2  (16B)

    // 48 + 16 + 16 + 16 + 16 = 112 bytes.
    public const int SizeBytes = 112;
}

[Flags]
public enum GpuMaterialFlags : uint {
    PackedOrm     = 1 << 0,
    HasMetallic   = 1 << 1,
    HasRoughness  = 1 << 2,
    NormalFlipY   = 1 << 3,
    HasEmissive   = 1 << 4,
    AlphaBlend    = 1 << 5,
    AlphaCutout   = 1 << 6,
    HasDiffuse    = 1 << 7,
    HasNormal     = 1 << 8,
    HasAo         = 1 << 9,
}

[Flags]
public enum SubmeshFlags : uint {
    Cutout      = 1 << 0,
    Transparent = 1 << 1,
}
