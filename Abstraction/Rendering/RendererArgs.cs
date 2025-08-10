namespace BallisticEngine;

public readonly struct RendererArgs {
    public readonly IViewProjectionProvider viewProjectionProvider;
    public readonly RenderingMethod RenderingMethod;

    public RendererArgs(IViewProjectionProvider viewProjectionProvider) {
        this.viewProjectionProvider = viewProjectionProvider;
    }
}