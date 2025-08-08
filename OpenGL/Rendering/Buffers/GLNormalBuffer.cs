using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

public class GLNormalBuffer(RenderContext renderContext) : GlVertexBufferBase<Vector3>(renderContext)
{
    const int Size = 3;
    protected override int AttributeLocation => 2;
    protected override (int Size, int Stride,bool Normalized) GetVertexAttributes() => (Size, sizeof(float) * 3,false);
}