namespace BallisticEngine;

public enum TextureFormat : byte {
    RGBA8 = 1,

    // 4 floats per pixel (HDR sources: .hdr/.exr). Pixels holds the raw float bytes.
    RGBA32F = 2,

    // GPU block-compressed (S3TC/RGTC), stored as raw blocks in the .btex artifact and uploaded
    // straight to the GPU with CompressedTexImage2D — never decoded back to RGBA8 at runtime.
    // 4x4 blocks; image dimensions must be a multiple of 4. Pixels holds the concatenated mip
    // chain (largest first), so Width/Height alone do not give the byte length — see BCn.MipChainBytes.
    BC1 = 3, // DXT1: RGB(+1-bit cutout), 8 bytes/block, 8:1 vs RGBA8 — opaque/cutout color maps.
    BC3 = 4, // DXT5: RGBA, 16 bytes/block, 4:1 — color maps with smooth alpha.
    BC5 = 5, // RGTC2: two channels (X,Y), 16 bytes/block, 4:1 — tangent-space normal maps (Z rebuilt in-shader is not needed; the sampler path reconstructs).
}

public static class TextureFormatExtensions {
    public static bool IsBlockCompressed(this TextureFormat format) =>
        format is TextureFormat.BC1 or TextureFormat.BC3 or TextureFormat.BC5;

    // Bytes per 4x4 block. BC1 is 8; BC3/BC5 are 16.
    public static int BlockBytes(this TextureFormat format) => format == TextureFormat.BC1 ? 8 : 16;
}
