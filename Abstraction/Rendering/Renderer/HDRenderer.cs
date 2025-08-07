namespace BallisticEngine;

public abstract class HDRenderer {
    public abstract void Initialize();
    public abstract void Render(IReadOnlyCollection<IRenderTarget> renderTargets,RenderArgs args);
    
}