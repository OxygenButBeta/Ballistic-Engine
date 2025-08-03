using OpenTK.Windowing.Desktop;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Graphics.OpenGL;

namespace BallisticEngine.Rendering;

public class GLGameWindow : GameWindow
{
    Shader defaultShader;
    Camera m_RenderCamera;
    int width, height;
    readonly bool m_init;


    public GLGameWindow(int width, int height) : base(GameWindowSettings.Default,
        NativeWindowSettings.Default)
    {
        this.width = width;
        this.height = height;
        Title = "Oxygen Engine | Graphic API: OpenGL " + APIVersion;
        CenterWindow(new Vector2i(width, height));
        m_init = true;
    }


    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        this.width = e.Width;
        this.height = e.Height;
    }

    protected override void OnLoad()
    {
        defaultShader = new();

        GL.Enable(EnableCap.DepthTest);

        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);

        m_RenderCamera = new Camera(width, height, Vector3.Zero);
    }

    bool fistFrame = true;

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        if (fistFrame)
        {
            Entity entity = new Entity("", true);
            entity.AddComponent<MeshRenderer>();
            fistFrame = false;
        }

        base.OnRenderFrame(args);
        GL.ClearColor(0.0f, 0.4f, 0.7f, 1f);

        // Clear the color buffer
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                 ClearBufferMask.StencilBufferBit);


        Matrix4 view = m_RenderCamera.GetViewMatrix();
        Matrix4 projection = m_RenderCamera.GetProjectionMatrix();


        var viewLocation = GL.GetUniformLocation(defaultShader.ID, "view");
        var projectionLocation = GL.GetUniformLocation(defaultShader.ID, "projection");

        GL.UniformMatrix4(viewLocation, true, ref view);
        GL.UniformMatrix4(projectionLocation, true, ref projection);

        foreach (IRenderTarget target in MeshRenderer.RenderTargets)
        {
            DispatchAndRender(target);
        }

        m_RenderCamera.FrameUpdate();
        Context.SwapBuffers();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        m_RenderCamera.Update(args, this);
    }

    void DispatchAndRender(IRenderTarget renderTarget)
    {
        Material material = renderTarget.Material;
        Mesh mesh = renderTarget.Mesh;

        if (!mesh.IsUploaded)
            return;

        material.Shader.Bind();
        GL.BindVertexArray(mesh.VAO);
        GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        Console.WriteLine(m_RenderCamera.GetProjectionMatrix());
    }
}