using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

public class GLTangentBuffer(RenderContext renderContext) : GlVertexBufferBase<Vector4>(renderContext)
{
    const int Size = 4; // xyz tangent + w handedness
    protected override int AttributeLocation => 3;
    protected override (int Size, int Stride,bool Normalized) GetVertexAttributes() => (Size, sizeof(float) * 4,false);
}