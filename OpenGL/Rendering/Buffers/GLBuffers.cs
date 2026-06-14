using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// GL-side mapping of the backend-agnostic BufferUsage to OpenTK's BufferUsageHint. Keeps the GL enum
// out of the abstraction (layering rules) while the GL buffer backend translates at the boundary.
internal static class GLBuffers {
    public static BufferUsageHint Hint(BufferUsage usage) => usage switch {
        BufferUsage.StaticDraw => BufferUsageHint.StaticDraw,
        BufferUsage.DynamicDraw => BufferUsageHint.DynamicDraw,
        BufferUsage.StreamDraw => BufferUsageHint.StreamDraw,
        _ => BufferUsageHint.StaticDraw,
    };
}
