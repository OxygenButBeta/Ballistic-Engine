using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

public class GL2DBufferBase(RenderContext renderContext) : GlVertexBufferBase<Vector2>(renderContext)
{
    const int Size = 2;
    protected override int AttributeLocation => 0;
    protected override (int Size, int Stride,bool Normalized) GetVertexAttributes() => (Size, sizeof(float) * 2,false);
}