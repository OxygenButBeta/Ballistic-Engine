using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

// Per-vertex bone INDICES (location 8), 4 indices into the skeleton. Sent as FLOATS (not an integer
// attribute) so it reuses the float vertex-buffer path; the shader rounds them back to ints. float32
// is exact for integers up to 2^24, far beyond any bone count, so no precision is lost. Only skinned
// meshes create this buffer — a static mesh never enables location 8.
public class GLBoneIndexBuffer(RenderContext renderContext) : GlVertexBufferBase<Vector4>(renderContext) {
    const int Size = 4;
    protected override int AttributeLocation => 8;
    protected override (int Size, int Stride, bool Normalized) GetVertexAttributes() => (Size, sizeof(float) * 4, false);
}
