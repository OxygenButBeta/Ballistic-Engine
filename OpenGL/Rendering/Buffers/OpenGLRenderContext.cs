using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.OpenGL;

//VERTEX ARRAY (VAO)
public sealed class OpenGLRenderContext : RenderContext
{
    public override int UID { get; protected set; } = GL.GenVertexArray();


    public override void Dispose()
    {
        if (UID == 0)
            return;

        GL.DeleteVertexArray(UID);
        UID = 0;
    }

    public override void Activate()
    {
        if (UID == 0)
            throw new InvalidOperationException("VertexArrayObject has not been created.");

        activeRenderContext = this;
        GL.BindVertexArray(UID);
    }

    public override void Deactivate()
    {
        if (UID == 0)
            throw new InvalidOperationException("VertexArrayObject has not been created.");
        
        GL.BindVertexArray(0);
    }
}