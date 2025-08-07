using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

public class GLUVBuffer2D(RenderContext renderContext) : GLVertexBuffer<Vector2>(renderContext)
{
    const int Size = 2;
    protected override int AttributeLocation => 1;
    protected override (int Size, int Stride) GetVertexAttributes() => (Size, sizeof(float) * 2);
}