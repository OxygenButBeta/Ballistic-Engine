using BallisticEngine.Rendering;

namespace BallisticEngine;

public abstract class HDRenderer {
    public abstract void Initialize();
    public abstract void RenderOpaque(IReadOnlyCollection<IOpaqueDrawable> renderTargets, RenderArgs args);
    public abstract void RenderInstancing(BatchGroup<IOpaqueDrawable> batchGroup, RenderArgs args);
    public abstract void BeginRender(RenderArgs args);
    public abstract void PostRenderCleanUp();

}