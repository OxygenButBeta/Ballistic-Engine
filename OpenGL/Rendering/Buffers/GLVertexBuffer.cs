using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.OpenGL;

public abstract class GLVertexBuffer<TData>(RenderContext renderContext) : GLBuffer<TData>(renderContext)
    where TData : struct
{
    protected abstract (int Size, int Stride) GetVertexAttributes();
    protected virtual int AttributeLocation => 0;
    public override void Create()
    {
        RenderContext.Activate();
        var (Size, Stride) = GetVertexAttributes();
        UID = GL.GenBuffer();
        GL.BindBuffer(Target, UID);
        GL.VertexAttribPointer(AttributeLocation, Size, VertexAttribPointerType.Float, false, stride: Stride,
            IntPtr.Zero);
        GL.EnableVertexAttribArray(AttributeLocation);
    }
}