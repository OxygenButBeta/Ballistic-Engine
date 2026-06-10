namespace BallisticEngine;

public enum TextureFormat : byte {
    RGBA8 = 1,

    // 4 floats per pixel (HDR sources: .hdr/.exr). Pixels holds the raw float bytes.
    RGBA32F = 2,
}
