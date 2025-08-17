using System.Buffers;
using BallisticEngine.Rendering;
using BallisticEngine.Sky;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

public class GLHDRenderer : HDRenderer
{
    IWindow window;
    bool anythingDrawnThisFrame;


    SkyboxRenderer skyboxRenderer;

    public override void Initialize()
    {
        window = Window.Current;
        skyboxRenderer = new SkyboxRenderer();
        skyboxRenderer.init();
        GL.Enable(EnableCap.DepthTest);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);
    }


    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args)
    {
        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();
        foreach (IStaticMeshRenderer target in renderTargets)
        {
            if (target.RenderedThisFrame)
                continue;

            Mesh mesh = target.SharedMesh;
            Shader shader = target.SharedMaterial.Shader;
            target.Activate();

            shader.SetFloat3("LightPos", DirectionalLight.Instance.transform.EulerAngles);
            shader.SetFloat3("LightColor", DirectionalLight.Instance.LightColor);
            shader.SetFloat3("AmbientLight",
                DirectionalLight.Instance.ambientIntensity * skyboxRenderer.cubemapTexture.skyAmbient);
            shader.SetBool("EnableAtmosphericScattering", skyboxRenderer.AtmosphereScattering);

            Matrix4 WorldMatrix = target.Transform.WorldMatrix;
            shader.SetInt("Diffuse", 0);
            shader.SetInt("Normal", 1);
            shader.SetMatrix4("view", ref view);
            shader.SetMatrix4("projection", ref projection);
            shader.SetMatrix4("model", ref WorldMatrix);
            shader.SetFloat3("CameraPos", args.viewProjectionProvider.Transform.Position);

            GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);

            target.Deactivate();
            target.RenderedThisFrame = true;
            anythingDrawnThisFrame = true;
        }
    }

    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args)
    {
        throw new NotImplementedException();
    }

    public override void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args)
    {
        throw new NotImplementedException(
            "Instancing is handled in RenderInstancing(BatchGroup<IOpaqueDrawable> batchGroup, RendererArgs args) method.");
    }

    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args)
    {
        var instanceCount = batchGroup.Matrix4s.Count;
        if (instanceCount == 0)
            return;

        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();

        IStaticMeshRenderer target = batchGroup.Drawable;
        Mesh mesh = target.SharedMesh;
        Shader shader = target.SharedMaterial.Shader;
        target.Activate();


        shader.SetBool("isInstanced", true);
        shader.SetMatrix4("view", ref view, true);
        shader.SetMatrix4("projection", ref projection, true);

        Matrix4[] array = ArrayPool<Matrix4>.Shared.Rent(batchGroup.Matrix4s.Count);
        batchGroup.Matrix4s.CopyTo(array, 0);
        target.SharedMesh.InstanceBuffer.SetBufferData(array, BufferUsageHint.StreamDraw);
        GL.DrawElementsInstanced(
            PrimitiveType.Triangles,
            mesh.Indices.Length,
            DrawElementsType.UnsignedInt,
            IntPtr.Zero,
            instanceCount
        );
        ArrayPool<Matrix4>.Shared.Return(array);
        shader.SetBool("isInstanced", false);
        batchGroup.Dispose();
        anythingDrawnThisFrame = true;
    }

    public override RenderMetrics BeginRender(RendererArgs args)
    {
        ClearColorBuffer();

        if (RenderAsset.Current.InstancedDrawing)
        {
            foreach (BatchGroup<IStaticMeshRenderer> batchGroup in
                     RendererHelpers.CreateBatchGroupsForOpaqueDrawables())
                RenderInstancing(batchGroup, args);
        }

        RenderOpaque(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, args);


        skyboxRenderer.PreRenderCallback(args);
        skyboxRenderer.RenderSkybox();
        skyboxRenderer.PostRenderCallback(args);
        if (!anythingDrawnThisFrame)
            window.SwapFrameBuffers();

        return new RenderMetrics();
    }

    public override void PostRenderCleanUp()
    {
        foreach (IStaticMeshRenderer opaqueDrawable in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            opaqueDrawable.RenderedThisFrame = false;
    }


    void ClearColorBuffer()
    {
        GL.ClearColor(0.4f, 0.55f, 0.65f, 1.0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                 ClearBufferMask.StencilBufferBit);
    }
}