namespace BallisticEngine;

public interface IBallisticEngineRuntime
{
    event Action<double> WindowUpdateCallback;
    event Action<double> WindowRenderCallback;
    event Action OnWindowShow;
    IEngineTimer EngineTimer { get; }
    IInputProvider InputProvider { get; }
    IWindow Window { get; }
    RenderAsset RenderAsset { get; }
    ILogger Logger { get; }
}