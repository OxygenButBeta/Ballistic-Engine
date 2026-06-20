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

    // Decoupled render thread (BALLISTIC_DX12_RENDER_THREAD=1): present the renderer's just-drawn LDR target
    // from the RENDER thread (DXGI Present is callable off the window thread). On the default single-threaded
    // path the host presents from OnRenderFrame and this is never called — the no-op default keeps other hosts
    // unaffected.
    void PresentFromRenderThread() { }
}