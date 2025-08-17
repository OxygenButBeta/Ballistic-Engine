using BallisticEngine;

public interface ISkyboxDrawable : IDrawable {
    void RenderSkybox();
    public void PreRenderCallback(RendererArgs args) { }
    public void PostRenderCallback(RendererArgs args) { }
}