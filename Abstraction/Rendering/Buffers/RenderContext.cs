namespace BallisticEngine;

public abstract class RenderContext : IDisposable
{
    protected static RenderContext activeRenderContext;
    public abstract int UID { get; protected set; }
    protected bool IsActiveContext => activeRenderContext != this;

    protected RenderContext()
    {
        RuntimeSet<RenderContext>.Add(this);
    }

    public abstract void Dispose();

    public abstract void Activate();

    public abstract void Deactivate();
}