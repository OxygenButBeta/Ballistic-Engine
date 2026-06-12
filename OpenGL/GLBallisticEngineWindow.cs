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
    // borderless: a borderless window at width x height (no title bar, not monitor-sized) — ignored
    // when fullscreen is set. title: the window caption (the shipped game's product name).
    public GLBallisticEngineWindow(int width, int height, bool fullscreen = false,
        bool borderless = false, string title = "Ballistic") : base(GameWindowSettings.Default,
        // GL 4.6 core: unlocks compute shaders + SSBOs (4.3), MultiDrawIndirect (4.3), persistent
        // mapping (4.4) and DSA (4.5) for GPU-driven rendering work. macOS (the old 4.1 ceiling)
        // is off the table for GL anyway — a future Mac path means another backend, not 4.1.
        // A driver that refuses 4.6 throws here at startup (any Windows GPU since ~2017 is fine).
        new NativeWindowSettings {
            APIVersion = new Version(4, 6),
            Profile = ContextProfile.Core,
            WindowBorder = fullscreen || borderless ? WindowBorder.Hidden : WindowBorder.Resizable,
            // Come up focused and in front. GLFW defaults these true, but launching from an IDE/debugger
            // (or while another window holds focus) often leaves the window created BEHIND the launcher —
            // these hints plus the explicit Focus() in OnLoad force it to the foreground.
            StartFocused = true,
            StartVisible = true,
        }) {
        this.width = width;
        this.height = height;
        Title = string.IsNullOrWhiteSpace(title) ? "Ballistic" : title;

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
            // Windowed or borderless-windowed: centre the requested size on the primary monitor.
            CenterWindow(new Vector2i(width, height));
        }
    }


    protected override void OnLoad() {
        base.OnLoad();
        // Force the window to the foreground once its surface exists. Without this it can open BEHIND
        // the launching terminal/IDE — StartFocused alone is only a creation hint the OS may ignore,
        // so we also explicitly focus and raise after Load.
        IsVisible = true;
        Focus();
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
        DrainScreenshotRequests(); // reads the backbuffer, so it must run BEFORE the swap
        base.OnRenderFrame(args);
        using (Profiler.Zone("SwapBuffers"))
            Context.SwapBuffers();
        Profiler.FrameMark(); // Tracy frame boundary: right after present.
        UpdateFrameRate();
    }

    // BALLISTIC_SCREENSHOT=<path.bmp>: save the rendered frame number BALLISTIC_SCREENSHOT_FRAME
    // (default 180 — enough for asset streaming, auto exposure and TAA to settle) and exit
    // (BALLISTIC_SCREENSHOT_EXIT=0 keeps running). Headless visual verification for agents/CI.
    // Implemented as the first consumer of the on-demand Screenshots queue — Screenshots.Capture
    // works the same way for scripts and the editor command port, without the exit.
    static readonly string ScreenshotPath = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT");
    // BALLISTIC_IDMAP=<path> — entity-ID map capture (writes <path>.json + <path>.bmp; see IdMaps).
    // Captured within one frame of the screenshot; combine with BALLISTIC_DETERMINISTIC=1 when the
    // two must correspond exactly.
    static readonly string IdMapPath = Environment.GetEnvironmentVariable("BALLISTIC_IDMAP");
    static readonly int ScreenshotFrame = int.TryParse(
        Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_FRAME"), out var f) ? f : 180;
    static readonly bool ScreenshotExit = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_EXIT") != "0";
    bool envScreenshotQueued, envShotDone, envIdMapDone;

    void DrainScreenshotRequests() {
        if (!envScreenshotQueued) {
            envScreenshotQueued = true;
            envShotDone = ScreenshotPath is null;
            envIdMapDone = IdMapPath is null;
            if (ScreenshotPath is not null)
                Screenshots.Capture(ScreenshotPath, ScreenshotFrame - 1, _ => {
                    PrintPerfStats();
                    envShotDone = true;
                    MaybeCloseAfterEnvCaptures();
                });
            if (IdMapPath is not null)
                IdMaps.Capture(IdMapPath, ScreenshotFrame - 1, _ => {
                    envIdMapDone = true;
                    MaybeCloseAfterEnvCaptures();
                });
        }

        var due = Screenshots.DueThisFrame();
        if (due is null)
            return;

        // One backbuffer read serves every request due this frame.
        var pixels = new byte[width * height * 3];
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Bgr, PixelType.UnsignedByte, pixels);

        foreach (Screenshots.Request request in due) {
            try {
                BmpWriter.Write(request.Path, width, height, pixels); // GL rows are bottom-up like BMP
                WriteStatsSidecar(request.Path);
                Console.WriteLine($"[Screenshot] saved {width}x{height} to {request.Path}");
                request.OnSaved?.Invoke(request.Path);
            }
            catch (Exception ex) {
                Debugging.LogError($"Screenshot to '{request.Path}' failed: {ex.Message}");
            }
        }
    }

    // Run-and-exit for the env capture harness: close once every REQUESTED env capture is done
    // (a screenshot-only, idmap-only, or combined run all exit exactly once, after the last file).
    void MaybeCloseAfterEnvCaptures() {
        if (ScreenshotExit && envShotDone && envIdMapDone)
            Close();
    }

    // Perf snapshot console lines (the original [PerfStats] contract — agents parse these).
    // Invariant culture: a Turkish-locale machine was printing "0,653" and breaking float parsing.
    static void PrintPerfStats() {
        RenderStats rs = RenderStats.Scene;
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[PerfStats] draws={rs.DrawCalls} depthDraws={rs.DepthOnlyDrawCalls} " +
            $"instanced={rs.InstancedDrawCalls} savedByInstancing={rs.DrawsSavedByInstancing} " +
            $"tris={rs.Triangles} visible={rs.RenderersVisible} culled={rs.RenderersCulled} " +
            $"submeshesCulled={rs.SubMeshesCulled} gpuFrameMs={rs.GpuFrameMs:0.000}"));
        foreach ((string name, double ms) in rs.GpuPasses)
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[PerfStats] pass {name} = {ms:0.000} ms"));
    }

    // Machine-readable twin of the [PerfStats] lines, written next to every capture as
    // <image>.stats.json — same frame as the pixels, so numbers and image always agree.
    // Hand-built JSON (flat object) to keep the GL layer free of serializer dependencies.
    static void WriteStatsSidecar(string imagePath) {
        RenderStats rs = RenderStats.Scene;
        var sb = new System.Text.StringBuilder(512);
        sb.Append("{\n");
        sb.Append($"  \"draws\": {rs.DrawCalls},\n");
        sb.Append($"  \"depthDraws\": {rs.DepthOnlyDrawCalls},\n");
        sb.Append($"  \"instanced\": {rs.InstancedDrawCalls},\n");
        sb.Append($"  \"savedByInstancing\": {rs.DrawsSavedByInstancing},\n");
        sb.Append($"  \"triangles\": {rs.Triangles},\n");
        sb.Append($"  \"renderersVisible\": {rs.RenderersVisible},\n");
        sb.Append($"  \"renderersCulled\": {rs.RenderersCulled},\n");
        sb.Append($"  \"submeshesCulled\": {rs.SubMeshesCulled},\n");
        sb.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"  \"gpuFrameMs\": {rs.GpuFrameMs:0.000},\n"));
        sb.Append("  \"gpuPasses\": {\n");
        for (int i = 0; i < rs.GpuPasses.Count; i++) {
            (string name, double ms) = rs.GpuPasses[i];
            sb.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"    \"{name}\": {ms:0.000}"));
            sb.Append(i < rs.GpuPasses.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  }\n}\n");
        File.WriteAllText(imagePath + ".stats.json", sb.ToString());
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