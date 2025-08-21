using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

public class GLBuffer<TData>(RenderContext renderContext) : GLBufferBase<TData>(renderContext)
    where TData : struct
{
    public override void Create()
    {
        UID = GL.GenBuffer();
    }
    
}
