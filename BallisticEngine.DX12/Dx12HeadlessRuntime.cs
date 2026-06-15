using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

// A windowless DX12 host for the headless screenshot path (BALLISTIC_SCREENSHOT). Implements
// IBallisticEngineRuntime with the REAL DirectXRenderAsset (a true DX12 device + offscreen render
// target) but a fake window/timer/input — no OS window, no swapchain. Its Run() drives the engine
// loop for a fixed number of frames, then reads the DX12 render target back to a BMP and exits,
// exactly like GLBallisticEngineWindow's screenshot harness but with DX12 readback instead of
// glReadPixels. A windowed DX12 host (swapchain + present + Windows input) comes later.
//
// Lives in the DX12 project (references Vortice); the Runtime exe selects it when BALLISTIC_BACKEND=dx12.
public sealed class Dx12HeadlessRuntime : IBallisticEngineRuntime {
    public event Action<double> WindowUpdateCallback;
    public event Action<double> WindowRenderCallback;
    public event Action OnWindowShow;

    public IEngineTimer EngineTimer { get; } = new ManualTimer();
    public IInputProvider InputProvider { get; } = new NullInput();
    public IWindow Window { get; }
    public RenderAsset RenderAsset { get; } = new DirectXRenderAsset();
    public ILogger Logger => null;   // hosts subscribe Debugging.OnMessage

    readonly HeadlessWindow window;

    // Screenshot harness env vars (same contract as the GL window host).
    static readonly string ScreenshotPath = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT");
    static readonly int ScreenshotFrame = int.TryParse(
        Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_FRAME"), out int f) ? f : 180;
    static readonly bool ScreenshotExit = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_EXIT") != "0";

    public Dx12HeadlessRuntime(int width = 1920, int height = 1080) {
        window = new HeadlessWindow(width, height, this);
        Window = window;
    }

    // Drive the engine loop headlessly. EngineLoop subscribed WindowUpdateCallback/WindowRenderCallback;
    // we fire them per frame at a fixed dt (deterministic), capture on the screenshot frame, then exit.
    void RunLoop() {
        OnWindowShow?.Invoke();
        const double dt = 1.0 / 60.0;   // fixed step — deterministic frames for verification

        // Run until the screenshot frame, render it, save, exit. With no screenshot requested, run a few
        // frames then stop (nothing to present without a swapchain).
        int lastFrame = ScreenshotPath is not null ? ScreenshotFrame : 5;
        for (int frame = 1; frame <= lastFrame; frame++) {
            WindowUpdateCallback?.Invoke(dt);
            WindowRenderCallback?.Invoke(dt);

            if (ScreenshotPath is not null && frame == ScreenshotFrame) {
                SaveScreenshot();
                if (ScreenshotExit) return;
            }
        }

        // GpuSceneQuery real-scene smoke probe (BALLISTIC_DX12_SCENEQUERY_SMOKE="x,y,z;x,y,z;..."): after the
        // scene has rendered (the AS-feeding RuntimeSet<IStaticMeshRenderer> is populated), build a
        // GpuSceneQuery over the REAL scene TLAS and print occupancy + classify for each given world point.
        // Validates the production AS-from-renderers path that the self-test door (synthetic box) can't.
        SceneQuerySmoke();
    }

    static void SceneQuerySmoke() {
        string spec = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SCENEQUERY_SMOKE");
        if (string.IsNullOrWhiteSpace(spec)) return;
        if (RenderAsset.Current.Renderer is not DX12HDRenderer r) return;

        var pts = new List<Vector3>();
        foreach (string tok in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            string[] c = tok.Split(',');
            if (c.Length == 3
                && float.TryParse(c[0], System.Globalization.CultureInfo.InvariantCulture, out float x)
                && float.TryParse(c[1], System.Globalization.CultureInfo.InvariantCulture, out float y)
                && float.TryParse(c[2], System.Globalization.CultureInfo.InvariantCulture, out float z))
                pts.Add(new Vector3(x, y, z));
        }
        if (pts.Count == 0) { Console.WriteLine("[SceneQuerySmoke] no valid points in spec"); return; }

        using DX12.GpuSceneQuery q = r.CreateSceneQuery();
        bool[] occ = q.OccupancyAt(pts);
        DX12.GpuSceneQuery.SpaceClass[] cls = q.ClassifySpace(pts);
        Console.WriteLine($"[SceneQuerySmoke] available={q.Available} renderers populated, {pts.Count} points:");
        for (int i = 0; i < pts.Count; i++)
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"  ({pts[i].X:0.##},{pts[i].Y:0.##},{pts[i].Z:0.##}) -> occupied={occ[i]} class={cls[i]}"));
    }

    void SaveScreenshot() {
        if (RenderAsset.Current.Renderer is DX12HDRenderer r) {
            r.SaveFrame(ScreenshotPath);
            Console.WriteLine($"[Screenshot] saved {r.Width}x{r.Height} to {ScreenshotPath} (DX12)");
            PrintPerfStats();
        }
        else {
            Console.Error.WriteLine("[Screenshot] DX12 renderer not active; nothing saved.");
        }
    }

    static void PrintPerfStats() {
        RenderStats rs = RenderStats.Scene;
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[PerfStats] draws={rs.DrawCalls} tris={rs.Triangles} (DX12)"));
    }

    // Host-driven clock (mirrors HeadlessRuntime.ManualTimer).
    sealed class ManualTimer : IEngineTimer {
        public double DeltaTime { get; private set; }
        public double TotalTime { get; private set; }
        void IEngineTimer.Update(double deltaTime) { DeltaTime = deltaTime; TotalTime += deltaTime; }
    }

    sealed class NullInput : IInputProvider {
        public bool IsKeyDown(Keys key) => false;
        public bool IsKeyPressed(Keys key) => false;
        public bool IsMouseButtonPressed(MouseButton button) => false;
        public bool IsMouseButtonDown(MouseButton button) => false;
        public Vector2 ScrollDelta => Vector2.Zero;
        public Vector2 MousePosition => Vector2.Zero;
        public Vector2 MouseDelta => Vector2.Zero;
        public bool IsGamepadConnected(int playerIndex) => false;
        public bool IsGamepadButtonDown(int playerIndex, int button) => false;
        public bool IsGamepadButtonPressed(int playerIndex, int button) => false;
        public float GetGamepadAxis(int playerIndex, int axis) => 0f;
    }

    // A fake window: carries the render resolution and drives the loop in Run(). The renderer's offscreen
    // target is sized to (Width, Height) at Initialize; resizing isn't needed headless.
    sealed class HeadlessWindow : IWindow {
        readonly Dx12HeadlessRuntime owner;
        public HeadlessWindow(int w, int h, Dx12HeadlessRuntime o) { Width = w; Height = h; owner = o; }
        public int Width { get; }
        public int Height { get; }
        public void SetFrequency(int frequency) { }
        public void Run() => owner.RunLoop();
        public void Close() { }
        public void SwapFrameBuffers() { }
        public float FrameRate => 60f;
        public event Action<int, int> OnResizeCallback { add { } remove { } }
        public CursorMode CursorMode { get; set; }
    }
}
