using BallisticEngine.Core.GL;            // GLTime, GLInput (OpenTK input/timer — not GL-API-coupled)
using BallisticEngine.DX12;               // Dx12Backend, Dx12SwapChain
using BallisticEngine.GLImplementation;   // GLLogger (console logger — not GL-API-coupled)
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Editor;

// The EDITOR's windowed DX12 host: an OpenTK GameWindow created with ContextAPI.NoAPI (NO GL context) whose
// Win32 HWND drives a DX12 swapchain. Mirrors GLBallisticEngineWindow but presents through Dx12SwapChain
// instead of Context.SwapBuffers — input, events, cursor, DPI, monitor and frame pacing are all GLFW and
// context-independent, so they're reused unchanged (the runtime DirectXRenderAsset renders the scene into
// offscreen targets exactly as headless; only the present surface is new). Selected by Program.cs when
// BALLISTIC_BACKEND=dx12. The DX12 ImGui backend records into swapChain.CommandList between BeginFrame and
// Present (see ImGuiDx12Renderer / ImGuiController).
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

    // The DX12 present surface (flip-model swapchain). Created in OnLoad (device + HWND both ready by then);
    // the ImGui backend resolves its open UI command list through this.
    Dx12SwapChain swapChain;
    public Dx12SwapChain SwapChain => swapChain;

    public void SetFrequency(int frequency) => UpdateFrequency = frequency;
    // Present is driven by OnRenderFrame; the engine's IWindow.SwapFrameBuffers seam is unused by the editor.
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
            // NO OpenGL context — DX12 owns the surface via the HWND. With ContextAPI.NoAPI, GLFW creates the
            // window without a client API and NativeWindow.Context is null (so we never call Context.*).
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
        // The DX12 device is up (DirectXRenderAsset.Initialize ran during the EditorApplication ctor, before
        // Run()); the HWND exists. Create the swapchain at the current (already-maximized) client size.
        width = ClientSize.X; height = ClientSize.Y;
        swapChain = new Dx12SwapChain(Dx12Backend.Device, GetHwnd(), width, height);
    }

    unsafe nint GetHwnd() => GLFW.GetWin32Window(WindowPtr);

    protected override void OnResize(ResizeEventArgs e) {
        base.OnResize(e);
        width = e.Width;
        height = e.Height;
        swapChain?.Resize(e.Width, e.Height);   // null before OnLoad — created at current size there
        OnResizeCallback?.Invoke(e.Width, e.Height);
    }

    protected override void OnRenderFrame(FrameEventArgs args) {
        if (swapChain == null) { base.OnRenderFrame(args); return; }   // before OnLoad (shouldn't happen)

        // Open the UI command list + clear the backbuffer (graphite editor base), run the editor frame (the
        // ImGui DX12 backend records into swapChain.CommandList), then execute, optionally read back, present.
        swapChain.BeginFrame(0.05f, 0.05f, 0.06f);
        using (Profiler.Zone("RenderFrame"))
            WindowRenderCallback?.Invoke(args.Time);
        swapChain.EndFrame();              // execute the UI list + GPU flush — backbuffer now holds the UI
        DrainScreenshotRequests();         // reads the backbuffer, so it must run BEFORE the present flip
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

    // ---- Headless screenshot harness (same env contract as the GL window) ---------------------------
    // BALLISTIC_SCREENSHOT=<path.bmp> captures the editor frame BALLISTIC_SCREENSHOT_FRAME and exits — the
    // primary way to verify the DX12 editor headlessly. Also drains the Screenshots queue (MCP editor_screenshot
    // / scripts). Reads the swapchain backbuffer (which holds the full UI after EndFrame). IdMaps is GL-only,
    // skipped here.
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
