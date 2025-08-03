
public interface IRenderer {
    /// Initializes the renderer.
    void Initialize();

    /// Renders the specified render target.
    /// <param name="renderTarget">The render target to render.</param>
    void Render(IRenderTarget renderTarget);

    /// Cleans up resources used by the renderer.
    void Cleanup();
}
