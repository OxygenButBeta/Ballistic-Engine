namespace BallisticEngine;

public sealed class BallisticEngine
{
    static IBallisticEngineRuntime Runtime;

    public static IInputProvider Input => Runtime.InputProvider;
    public static IEngineTimer Time => Runtime.EngineTimer;
    public static IWindow Window => Runtime.Window;

    public BallisticEngine(IBallisticEngineRuntime runtime)
    {
        Runtime = runtime;
        Runtime.WindowUpdateCallback += EngineUpdate;
        Runtime.WindowRenderCallback += EngineRender;

        //TODO : REMOVE
        SceneInit.Init();
    }

    void EngineRender(double obj)
    {
        if (Scene.Instance.RenderCamera is null)
        {
            Console.WriteLine("No render camera set in scene.");
        }
        else
        {
            Scene.Instance.RenderCamera.Render();
        }
    }

    void EngineUpdate(double delta)
    {
        Scene.Instance.Update((float)delta);
    }
}