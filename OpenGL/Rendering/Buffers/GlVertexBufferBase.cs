using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.OpenGL;

public abstract class GlVertexBufferBase<TData>(RenderContext renderContext) : GLBufferBase<TData>(renderContext)
    where TData : struct
{
    protected abstract (int Size, int Stride,bool Normalized) GetVertexAttributes();
    protected virtual int AttributeLocation => 0;
    public override void Create()
    {
        RenderContext.Activate();
        var (Size, Stride,Normalized) = GetVertexAttributes();
        UID = GL.GenBuffer();
        GL.BindBuffer(Target, UID);
        GL.VertexAttribPointer(AttributeLocation, Size, VertexAttribPointerType.Float, Normalized, stride: Stride,
            IntPtr.Zero);
        GL.EnableVertexAttribArray(AttributeLocation);
    }
}