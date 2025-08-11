using System.Buffers;
using BallisticEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

public class GLHDRenderer : HDRenderer {
    IWindow window;
    bool anythingDrawnThisFrame;


    public override void Initialize() {
        window = Window.Current;
        GL.Enable(EnableCap.DepthTest);
        GL.CullFace(TriangleFace.Back);
        GL.Enable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);
    }


    public override void RenderOpaque(IReadOnlyCollection<IOpaqueDrawable> renderTargets, RendererArgs args) {
        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();
        const float radius = 15.0f;
        foreach (IOpaqueDrawable target in renderTargets) {
            if (target.RenderedThisFrame)
                continue;

            Mesh mesh = target.SharedMesh;
            Shader shader = target.SharedMaterial.Shader;
            target.Activate();

            Matrix4 WorldMatrix = target.Transform.WorldMatrix;
            shader.SetMatrix4("view", ref view, true);
            shader.SetMatrix4("projection", ref projection, true);
            shader.SetMatrix4("model", ref WorldMatrix, true);

            GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);

            target.Deactivate();
            target.RenderedThisFrame = true;
            anythingDrawnThisFrame = true;
        }
    }

    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args) {
        throw new NotImplementedException();
    }

    public override void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args) {
        throw new NotImplementedException(
            "Instancing is handled in RenderInstancing(BatchGroup<IOpaqueDrawable> batchGroup, RendererArgs args) method.");
    }

    public override void RenderInstancing(BatchGroup<IOpaqueDrawable> batchGroup, RendererArgs args) {
        var instanceCount = batchGroup.Matrix4s.Count;
        if (instanceCount == 0)
            return;

        Matrix4 view = args.viewProjectionProvider.GetViewMatrix();
        Matrix4 projection = args.viewProjectionProvider.GetProjectionMatrix();

        IOpaqueDrawable target = batchGroup.Drawable;
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

    public override RenderMetrics BeginRender(RendererArgs args) {
        ClearColorBuffer();

        if (RenderAsset.Current.InstancedDrawing) {
            foreach (BatchGroup<IOpaqueDrawable> batchGroup in RendererHelpers.CreateBatchGroupsForOpaqueDrawables())
                RenderInstancing(batchGroup, args);
        }

        RenderOpaque(RuntimeSet<IOpaqueDrawable>.ReadOnlyCollection, args);

        if (!anythingDrawnThisFrame)
            window.SwapFrameBuffers();


        return new RenderMetrics();
    }

    public override void PostRenderCleanUp() {
        foreach (IOpaqueDrawable opaqueDrawable in RuntimeSet<IOpaqueDrawable>.ReadOnlyCollection)
            opaqueDrawable.RenderedThisFrame = false;
    }


    void ClearColorBuffer() {
        GL.ClearColor(0.0f, 0.4f, 0.7f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                 ClearBufferMask.StencilBufferBit);
    }
}