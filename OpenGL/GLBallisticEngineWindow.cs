using BallisticEngine.Core.GL;
using BallisticEngine.GLImplementation;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace BallisticEngine;

public class GLBallisticEngineWindow : GameWindow, IBallisticEngineRuntime, IWindow {
    public event Action<double> WindowUpdateCallback;
    public event Action<double> WindowRenderCallback;

    public event Action OnWindowShow {
        add => Load += value;
        remove => Load -= value;
    }

    public IEngineTimer EngineTimer { get; }
    public IInputProvider InputProvider { get; }
    public IWindow Window => this;
    public RenderAsset RenderAsset { get; } = new OpenGLRenderAsset();
    public ILogger Logger { get; } = new GLLogger();
    int width, height;
    public int Width => width;
    public int Height => height;


    public void SetFrequency(int frequency) {
        UpdateFrequency = frequency;
    }

    public void SwapFrameBuffers() => Context.SwapBuffers();
    public float FrameRate => currentFps;
    public event Action<int, int> OnResizeCallback;

    // Maps the engine's CursorMode onto OpenTK's CursorState. Grabbed = hidden + locked to centre,
    // which is what feeds raw MouseState.Delta for first-person look.
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


    // fullscreen: borderless fullscreen at the primary monitor's resolution (shipped player). The
    // width/height become the windowed fallback size; in fullscreen the monitor's mode wins.
    public GLBallisticEngineWindow(int width, int height, bool fullscreen = false) : base(GameWindowSettings.Default,
        // GL 4.6 core: unlocks compute shaders + SSBOs (4.3), MultiDrawIndirect (4.3), persistent
        // mapping (4.4) and DSA (4.5) for GPU-driven rendering work. macOS (the old 4.1 ceiling)
        // is off the table for GL anyway — a future Mac path means another backend, not 4.1.
        // A driver that refuses 4.6 throws here at startup (any Windows GPU since ~2017 is fine).
        new NativeWindowSettings {
            APIVersion = new Version(4, 6),
            Profile = ContextProfile.Core,
            WindowBorder = fullscreen ? WindowBorder.Hidden : WindowBorder.Resizable,
        }) {
        this.width = width;
        this.height = height;
        Title = "Ballistic ";

        EngineTimer = new GLTime();
        // JoystickStates is live on the window — pass a getter so the input provider always reads the
        // current frame's controller state (and picks up hot-plugged pads).
        InputProvider = new GLInput(KeyboardState, MouseState, () => JoystickStates);

        if (fullscreen) {
            // Borderless fullscreen on the primary monitor: cover the whole screen at its native
            // resolution. (Borderless over exclusive: instant alt-tab, no mode switch flicker.)
            var monitor = Monitors.GetPrimaryMonitor();
            var area = monitor.ClientArea;
            this.width = area.Size.X;
            this.height = area.Size.Y;
            WindowState = WindowState.Fullscreen;
        }
        else {
            CenterWindow(new Vector2i(width, height));
        }
    }


    protected override void OnResize(ResizeEventArgs e) {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        width = e.Width;
        height = e.Height;
        OnResizeCallback?.Invoke(e.Width, e.Height);
    }

    protected override void OnRenderFrame(FrameEventArgs args) {
        using (Profiler.Zone("RenderFrame"))
            WindowRenderCallback!.Invoke(args.Time);
        MaybeCaptureScreenshot(); // reads the backbuffer, so it must run BEFORE the swap
        base.OnRenderFrame(args);
        using (Profiler.Zone("SwapBuffers"))
            Context.SwapBuffers();
        Profiler.FrameMark(); // Tracy frame boundary: right after present.
        UpdateFrameRate();
    }

    // BALLISTIC_SCREENSHOT=<path.bmp>: save the rendered frame number BALLISTIC_SCREENSHOT_FRAME
    // (default 180 — enough for asset streaming, auto exposure and TAA to settle) and exit.
    // Headless visual verification for agents/CI: run, grab the file, diff against a baseline.
    static readonly string ScreenshotPath = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT");
    static readonly int ScreenshotFrame = int.TryParse(
        Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_FRAME"), out var f) ? f : 180;
    int presentedFrames;

    void MaybeCaptureScreenshot() {
        if (ScreenshotPath is null || ++presentedFrames != ScreenshotFrame)
            return;

        var pixels = new byte[width * height * 3];
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Bgr, PixelType.UnsignedByte, pixels);
        BmpWriter.Write(ScreenshotPath, width, height, pixels); // GL rows are bottom-up like BMP
        Console.WriteLine($"[Screenshot] saved {width}x{height} frame {ScreenshotFrame} to {ScreenshotPath}");

        // Perf snapshot alongside the image: per-pass GPU times + submission counters, so a
        // headless agent/CI run gets numbers and pixels from the same frame.
        RenderStats rs = RenderStats.Scene;
        Console.WriteLine($"[PerfStats] draws={rs.DrawCalls} depthDraws={rs.DepthOnlyDrawCalls} " +
            $"instanced={rs.InstancedDrawCalls} savedByInstancing={rs.DrawsSavedByInstancing} " +
            $"tris={rs.Triangles} visible={rs.RenderersVisible} culled={rs.RenderersCulled} " +
            $"gpuFrameMs={rs.GpuFrameMs:0.000}");
        foreach ((string name, double ms) in rs.GpuPasses)
            Console.WriteLine($"[PerfStats] pass {name} = {ms:0.000} ms");

        Close();
    }

    protected override void OnUpdateFrame(FrameEventArgs args) {
        using (Profiler.Zone("UpdateFrame"))
            WindowUpdateCallback!.Invoke(args.Time);
        base.OnUpdateFrame(args);
    }

    readonly System.Diagnostics.Stopwatch fpsWatch = System.Diagnostics.Stopwatch.StartNew();
    int framesSinceSample;
    float currentFps;

    // Presented-frames per wall-clock second, sampled twice a second. Measured in the RENDER loop
    // against a stopwatch: counting update ticks mis-reads whenever OpenTK's update cadence differs
    // from the presented rate, and the old fixed "1.0s" bucket credited its overshoot to the next
    // window (a steady 60 fps displayed as a jittery 58-59).
    void UpdateFrameRate() {
        framesSinceSample++;
        double elapsed = fpsWatch.Elapsed.TotalSeconds;
        if (elapsed < 0.5)
            return;

        currentFps = (float)(framesSinceSample / elapsed);
        framesSinceSample = 0;
        fpsWatch.Restart();

        // FPS in the console title is a dev convenience. A SHIPPED player is a WinExe with NO console,
        // so set_Title throws "The handle is invalid" — which, on the render thread, crashed the game
        // every half-second. Swallow it: the title is cosmetic and only meaningful when run from a
        // terminal (dev `dotnet run`). One-time check so we don't throw+catch twice a second forever.
        if (consoleTitleAvailable) {
            try { Console.Title = $"FPS: {currentFps:0}"; }
            catch { consoleTitleAvailable = false; }
        }
    }

    // True until a Console.Title write throws (no console attached — a shipped WinExe build).
    bool consoleTitleAvailable = true;
}