using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.OpenGL;

public class GLIndexBuffer(RenderContext renderContext) : GLBuffer<uint>(renderContext)
{
    protected override BufferTarget Target => BufferTarget.ElementArrayBuffer;

    public override void Create()
    {
        UID = GL.GenBuffer();
    }
}