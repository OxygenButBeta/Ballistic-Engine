using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

// Per-vertex bone WEIGHTS (location 9), 4 floats summing to 1. Only skinned meshes create this; a
// static mesh never enables location 9, so its VAO is unchanged.
public class GLBoneWeightBuffer(RenderContext renderContext) : GlVertexBufferBase<Vector4>(renderContext) {
    const int Size = 4;
    protected override int AttributeLocation => 9;
    protected override (int Size, int Stride, bool Normalized) GetVertexAttributes() => (Size, sizeof(float) * 4, false);
}
