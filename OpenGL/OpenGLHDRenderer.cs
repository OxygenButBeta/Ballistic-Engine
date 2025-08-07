using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

public class OpenGLHDRenderer : HDRenderer
{
    IWindow window;

    public override void Initialize()
    {
        window = Window.Current;
        GL.Enable(EnableCap.DepthTest);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);
    }

    public override void Render(IReadOnlyCollection<IRenderTarget> renderTargets, RenderArgs args)
    {
        ClearColorBuffer();
        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();

        var anythingDrawn = false;
        foreach (IRenderTarget target in renderTargets)
        {
            Mesh mesh = target.CommonMesh;
            Material material = target.Material;

            target.Select();


            var viewLocation = GL.GetUniformLocation(target.Material.Shader.ID, "view");
            var projectionLocation = GL.GetUniformLocation(target.Material.Shader.ID, "projection");
            var modelLocation = GL.GetUniformLocation(target.Material.Shader.ID, "model");
            var textureUniformLocation = GL.GetUniformLocation(target.Material.Shader.ID, "texture0");

            GL.Uniform1(textureUniformLocation, 0);
            Matrix4 WorldMatrix = target.Transform.WorldMatrix;

            GL.UniformMatrix4(viewLocation, true, ref view);
            GL.UniformMatrix4(projectionLocation, true, ref projection);
            GL.UniformMatrix4(modelLocation, true, ref WorldMatrix);


            if (!mesh.IsUploaded)
            {
                Console.WriteLine("Mesh not uploaded, skipping render.");
                continue;
            }

            GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
            anythingDrawn = true;
        }

        if (!anythingDrawn)
            window.SwapFrameBuffers();
    }

    void ClearColorBuffer()
    {
        GL.ClearColor(0.0f, 0.4f, 0.7f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                 ClearBufferMask.StencilBufferBit);
    }
}