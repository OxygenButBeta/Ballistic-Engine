using BallisticEngine.Rendering;
using OpenTK.Mathematics;

namespace BallisticEngine;

public abstract class HDRenderer {
    public abstract void Initialize();
    public abstract void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets, RendererArgs args);
    public abstract void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args);
    public abstract void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args);
    public abstract RenderMetrics BeginRender(RendererArgs args);
    public abstract void PostRenderCleanUp();
    public abstract void RenderInstancing(Mesh mesh, Material material, Matrix4[] transforms, RendererArgs args);
}