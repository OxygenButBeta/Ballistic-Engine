using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

public class GlUVBuffer2D(RenderContext renderContext) : GlVertexBufferBase<Vector2>(renderContext)
{
    const int Size = 2;
    protected override int AttributeLocation => 1;
    protected override (int Size, int Stride,bool Normalized) GetVertexAttributes() => (Size, sizeof(float) * 2,true);
    public override void Create() {
        base.Create();
        GL.VertexAttribDivisor(AttributeLocation,0);
    }
}