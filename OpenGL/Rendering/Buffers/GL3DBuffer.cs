using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

public class GL3DBuffer(RenderContext renderContext) : GLVertexBuffer<Vector3>(renderContext)
{
    const int Size = 3;
    protected override (int Size, int Stride) GetVertexAttributes() => (Size, sizeof(float) * 3);
}