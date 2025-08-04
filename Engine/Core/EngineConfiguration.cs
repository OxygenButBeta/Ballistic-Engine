using BallisticEngine;

[EngineService]
public class EngineConfiguration {
    public HDRenderer Renderer;

    public EngineConfiguration() {
        // Prepare Engine...
        Renderer = new OpenGLHDRenderer();
        Renderer.Initialize();
    }
}