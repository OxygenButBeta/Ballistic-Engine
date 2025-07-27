using BallisticEngine.Rendering;

public class OpenGLRenderer : IRenderer {
    public void Initialize() {
        GLGameWindow gameWindow = new(1280, 720);
        gameWindow.Run();
    }

    public void Render(IRenderTarget renderTarget) {
    }

    public void Cleanup() {
    }
}