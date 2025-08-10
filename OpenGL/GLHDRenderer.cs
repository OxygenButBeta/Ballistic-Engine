using BallisticEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

public class GLHDRenderer : HDRenderer
{
    IWindow window;
    bool anythingDrawnThisFrame;


    public override void Initialize()
    {
        window = Window.Current;
        GL.Enable(EnableCap.DepthTest);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);
    }


    public override void RenderOpaque(IReadOnlyCollection<IOpaqueDrawable> renderTargets, RendererArgs args)
    {
        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();

        int drawCallCount = 0;
        foreach (IOpaqueDrawable target in renderTargets)
        {
            if (target.RenderedThisFrame)
                continue;

            drawCallCount++;
            Mesh mesh = target.SharedMesh;
            target.Activate();

            var viewLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "view");
            var projectionLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "projection");
            var modelLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "model");

            var LightPositionLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "LightPos");
            var LightColorLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "LightColor");
            var ambientColorLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "AmbientColor");

            double time = Time.TotalTime * 0.5; // Daha yavaş dönüş
            float radius = 15.0f;
            float angle = (float)Math.Sin(time) * MathF.PI / 3f; // -60° ile +60° arası salınım (yaklaşık 120° yay)

            Vector3 lightPos = new Vector3(
                MathF.Cos(angle) * radius,
                3.0f, // Sabit yükseklik 
                MathF.Sin(angle) * radius
            );
            Vector3 lightColor = new Vector3(1.0f, 0.95f, 0.85f) * 10.0f;

            GL.Uniform3(LightPositionLocation, lightPos);
            GL.Uniform3(LightColorLocation, lightColor);
            GL.Uniform3(ambientColorLocation, args.viewProjectionProvider.AmbientColor);

            Matrix4 WorldMatrix = target.Transform.WorldMatrix;
            GL.UniformMatrix4(viewLocation, true, ref view);
            GL.UniformMatrix4(projectionLocation, true, ref projection);
            GL.UniformMatrix4(modelLocation, true, ref WorldMatrix);
            GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);

            target.Deactivate();
            target.RenderedThisFrame = true;
            anythingDrawnThisFrame = true;
        }

        Debugging.SystemLog("Total Draw Call Count: " + drawCallCount, SystemLogPriority.Medium);
    }

    public override void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args) {
        material.LegacyShader.Activate();
        material.Diffuse.Activate();
        mesh.Activate();
    }
    public override void RenderInstancing(BatchGroup<IOpaqueDrawable> batchGroup, RendererArgs args)
    {
        var instanceCount = batchGroup.Matrix4s.Count;
        if (instanceCount == 0)
            return;

        IOpaqueDrawable target = batchGroup.Drawable;
        Mesh mesh = target.SharedMesh;
        LegacyShader legacyShader = target.SharedMaterial.LegacyShader;
        target.Activate();


        var shaderId = legacyShader.UID;
        var LightPositionLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "LightPos");
        var LightColorLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "LightColor");
        var ambientColorLocation = GL.GetUniformLocation(target.SharedMaterial.LegacyShader.UID, "AmbientColor");
        
        double time = Time.TotalTime * 0.5; 
        float radius = 15.0f;
        float angle = (float)Math.Sin(time) * MathF.PI / 3f;

        
        Vector3 lightPos = new(
            MathF.Cos(angle) * radius,
            3.0f, // Sabit yükseklik 
            MathF.Sin(angle) * radius
        );
        Vector3 lightColor = new Vector3(1.0f, 0.95f, 0.85f) * 10.0f;
        
        GL.Uniform3(LightPositionLocation, lightPos);
        GL.Uniform3(LightColorLocation, lightColor);
        GL.Uniform3(ambientColorLocation, args.viewProjectionProvider.AmbientColor);
        
        
        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();
        Matrix4 model = Matrix4.Identity;
        var viewLocation = GL.GetUniformLocation(shaderId, "view");
        var projectionLocation = GL.GetUniformLocation(shaderId, "projection");
        var modelLocation = GL.GetUniformLocation(shaderId, "model");
        var isInstancedLocation = GL.GetUniformLocation(shaderId, "isInstanced");
        GL.Uniform1(isInstancedLocation, 1);

        GL.UniformMatrix4(viewLocation, true, ref view);
        GL.UniformMatrix4(projectionLocation, true, ref projection);
        GL.UniformMatrix4(modelLocation, true, ref model);

        Matrix4[] matrices = batchGroup.Matrix4s.ToArray();
        target.SharedMesh.InstanceBuffer.SetBufferData(matrices, BufferUsageHint.StreamDraw);
        GL.DrawElementsInstanced(
            PrimitiveType.Triangles,
            mesh.Indices.Length,
            DrawElementsType.UnsignedInt,
            IntPtr.Zero,
            instanceCount
        );


        Debugging.SystemLog("Total Batch Draw Calls: " + batchGroup.Matrix4s.Count, SystemLogPriority.Medium);
        GL.Uniform1(isInstancedLocation, 0);
        batchGroup.Dispose();
        anythingDrawnThisFrame = true;
    }

    public override RenderMetrics BeginRender(RendererArgs args)
    {
        ClearColorBuffer();

        if (RenderAsset.Current.InstancedDrawing)
        {
            foreach (BatchGroup<IOpaqueDrawable> batchGroup in RendererHelpers.CreateBatchGroupsForOpaqueDrawables())
                RenderInstancing(batchGroup, args);
        }

        RenderOpaque(RuntimeSet<IOpaqueDrawable>.ReadOnlyCollection, args);

        if (!anythingDrawnThisFrame)
            window.SwapFrameBuffers();


        return new RenderMetrics();
    }

    public override void PostRenderCleanUp()
    {
        foreach (IOpaqueDrawable opaqueDrawable in RuntimeSet<IOpaqueDrawable>.ReadOnlyCollection)
            opaqueDrawable.RenderedThisFrame = false;
    }



    void ClearColorBuffer()
    {
        GL.ClearColor(0.0f, 0.4f, 0.7f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                 ClearBufferMask.StencilBufferBit);
    }
}