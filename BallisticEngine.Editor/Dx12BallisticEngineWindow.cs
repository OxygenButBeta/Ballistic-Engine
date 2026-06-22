using BallisticEngine.Core.GL;
using BallisticEngine.DX12;
using BallisticEngine.GLImplementation;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Editor;

public sealed class Dx12BallisticEngineWindow : GameWindow, IBallisticEngineRuntime, IWindow {
    public event Action<double> WindowUpdateCallback;
    public event Action<double> WindowRenderCallback;
    public event Action OnWindowShow {
        add => Load += value;
        remove => Load -= value;
    }

    public IEngineTimer EngineTimer { get; }
    public IInputProvider InputProvider { get; }
    public IWindow Window => this;
    public RenderAsset RenderAsset { get; } = new DirectXRenderAsset();
    public ILogger Logger { get; } = new GLLogger();

    int width, height;
    public int Width => width;
    public int Height => height;
    public float FrameRate => currentFps;
    public event Action<int, int> OnResizeCallback;

    Dx12SwapChain swapChain;
    public Dx12SwapChain SwapChain => swapChain;

    public void SetFrequency(int frequency) => UpdateFrequency = frequency;
    public void SwapFrameBuffers() { }

    public CursorMode CursorMode {
        get => CursorState switch {
            CursorState.Hidden => CursorMode.Hidden,
            CursorState.Grabbed => CursorMode.Locked,
            _ => CursorMode.Normal,
        };
        set => CursorState = value switch {
            CursorMode.Hidden => CursorState.Hidden,
            CursorMode.Locked => CursorState.Grabbed,
            _ => CursorState.Normal,
        };
    }

    public Dx12BallisticEngineWindow(int width, int height) : base(GameWindowSettings.Default,
        new NativeWindowSettings {
            API = ContextAPI.NoAPI,
            WindowBorder = WindowBorder.Resizable,
            StartFocused = true,
            StartVisible = true,
        }) {
        this.width = width;
        this.height = height;
        Title = "Ballistic (DX12)";

        EngineTimer = new GLTime();
        InputProvider = new GLInput(KeyboardState, MouseState, () => JoystickStates);
    }

    protected override void OnLoad() {
        base.OnLoad();
        IsVisible = true;
        Focus();
        width = ClientSize.X; height = ClientSize.Y;
        swapChain = new Dx12SwapChain(Dx12Backend.Device, GetHwnd(), width, height);
    }

    unsafe nint GetHwnd() => GLFW.GetWin32Window(WindowPtr);

    protected override void OnResize(ResizeEventArgs e) {
        base.OnResize(e);
        width = e.Width;
        height = e.Height;
        swapChain?.Resize(e.Width, e.Height);
        OnResizeCallback?.Invoke(e.Width, e.Height);
    }

    protected override void OnRenderFrame(FrameEventArgs args) {
        if (swapChain == null) { base.OnRenderFrame(args); return; }

        swapChain.BeginFrame(0.05f, 0.05f, 0.06f);
        using (Profiler.Zone("RenderFrame"))
            WindowRenderCallback?.Invoke(args.Time);
        swapChain.EndFrame();
        DrainScreenshotRequests();
        using (Profiler.Zone("Present"))
            swapChain.Present(vsync: true);
        base.OnRenderFrame(args);
        Profiler.FrameMark();
        UpdateFrameRate();
    }

    protected override void OnUpdateFrame(FrameEventArgs args) {
        using (Profiler.Zone("UpdateFrame"))
            WindowUpdateCallback?.Invoke(args.Time);
        base.OnUpdateFrame(args);
    }

    protected override void OnUnload() {
        swapChain?.Dispose();
        swapChain = null;
        base.OnUnload();
    }

    static readonly string ScreenshotPath = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT");
    static readonly int ScreenshotFrame = int.TryParse(
        Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_FRAME"), out var f) ? f : 180;
    static readonly bool ScreenshotExit = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_EXIT") != "0";
    bool envScreenshotQueued;

    void DrainScreenshotRequests() {
        if (!envScreenshotQueued) {
            envScreenshotQueued = true;
            if (ScreenshotPath is not null)
                Screenshots.Capture(ScreenshotPath, ScreenshotFrame - 1, _ => {
                    PrintPerfStats();
                    if (ScreenshotExit) Close();
                });
        }

        var due = Screenshots.DueThisFrame();
        if (due is null)
            return;

        foreach (Screenshots.Request request in due) {
            try {
                swapChain.SaveBackbufferBmp(request.Path);
                Console.WriteLine($"[Screenshot] saved {width}x{height} to {request.Path} (DX12 editor)");
                request.OnSaved?.Invoke(request.Path);
            }
            catch (Exception ex) {
                Debugging.LogError($"Screenshot to '{request.Path}' failed: {ex.Message}");
            }
        }
    }

    static void PrintPerfStats() {
        RenderStats rs = RenderStats.Scene;
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[PerfStats] draws={rs.DrawCalls} tris={rs.Triangles} (DX12 editor)"));
    }

    readonly System.Diagnostics.Stopwatch fpsWatch = System.Diagnostics.Stopwatch.StartNew();
    int framesSinceSample;
    float currentFps;
    bool consoleTitleAvailable = true;

    void UpdateFrameRate() {
        framesSinceSample++;
        double elapsed = fpsWatch.Elapsed.TotalSeconds;
        if (elapsed < 0.5)
            return;
        currentFps = (float)(framesSinceSample / elapsed);
        framesSinceSample = 0;
        fpsWatch.Restart();
        if (consoleTitleAvailable) {
            try { Console.Title = $"FPS: {currentFps:0} (DX12)"; }
            catch { consoleTitleAvailable = false; }
        }
    }
}
