using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public sealed class Dx12HeadlessRuntime : IBallisticEngineRuntime {
    public event Action<double> WindowUpdateCallback;
    public event Action<double> WindowRenderCallback;
    public event Action OnWindowShow;

    public IEngineTimer EngineTimer { get; } = new ManualTimer();
    public IInputProvider InputProvider { get; } = new NullInput();
    public IWindow Window { get; }
    public RenderAsset RenderAsset { get; } = new DirectXRenderAsset();
    public ILogger Logger => null;

    readonly HeadlessWindow window;

    static readonly string ScreenshotPath = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT");
    static readonly int ScreenshotFrame = int.TryParse(
        Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_FRAME"), out int f) ? f : 180;
    static readonly bool ScreenshotExit = Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_EXIT") != "0";

    public Dx12HeadlessRuntime(int width = 1920, int height = 1080) {
        RenderThread.HeadlessSuppressed = true;
        window = new HeadlessWindow(width, height, this);
        Window = window;
    }

    void RunLoop() {
        OnWindowShow?.Invoke();
        const double dt = 1.0 / 60.0;

        if ((window.Width != 1920 || window.Height != 1080)
            && RenderAsset.Current.Renderer is DX12HDRenderer rr)
            rr.ResizeSceneTarget(window.Width, window.Height);

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_RESIZE_STRESS") == "1") {
            ResizeStress(dt);
            return;
        }

        string fpsBenchEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_FPSBENCH");
        if (int.TryParse(fpsBenchEnv, out int benchFrames) && benchFrames > 0) {
            int warm = Math.Min(30, benchFrames / 4);
            for (int f = 0; f < warm; f++) { WindowUpdateCallback?.Invoke(dt); WindowRenderCallback?.Invoke(dt); }

            long gcBytes0 = GC.GetTotalAllocatedBytes(precise: false);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < benchFrames; f++) { WindowUpdateCallback?.Invoke(dt); WindowRenderCallback?.Invoke(dt); }
            sw.Stop();
            long gcBytes = GC.GetTotalAllocatedBytes(precise: false) - gcBytes0;
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1, dg2 = GC.CollectionCount(2) - g2;
            double msPerFrame = sw.Elapsed.TotalMilliseconds / benchFrames;
            int fif = RenderAsset.Current.Renderer is DX12HDRenderer rb ? rb.Device.FramesInFlight : 1;
            Console.WriteLine($"[FpsBench] frames={benchFrames} warmup={warm} avgFrameMs={msPerFrame:0.000} fps={1000.0 / msPerFrame:0.0} framesInFlight={fif} overlap={(fif > 1 ? "ON" : "off")}");
            Console.WriteLine($"[FpsBench] gcAllocKB={gcBytes / 1024.0:0.0} bytesPerFrame={(double)gcBytes / benchFrames:0} gen0={dg0} gen1={dg1} gen2={dg2}");
            return;
        }

        bool queryMode = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BALLISTIC_QUERY"));
        int lastFrame = ScreenshotPath is not null ? ScreenshotFrame
            : queryMode ? 3 : 5;
        string motionDir = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_MOTION_DUMP");
        if (!string.IsNullOrWhiteSpace(motionDir)) {
            System.IO.Directory.CreateDirectory(motionDir);
            int total = ScreenshotFrame > 0 ? ScreenshotFrame : 60;
            int dumpTail = 8;
            for (int frame = 1; frame <= total; frame++) {
                WindowUpdateCallback?.Invoke(dt);
                WindowRenderCallback?.Invoke(dt);
                if (frame > total - dumpTail)
                    SaveScreenshotTo(System.IO.Path.Combine(motionDir, $"frame{frame:D3}.bmp"));
            }
            return;
        }

        for (int frame = 1; frame <= lastFrame; frame++) {
            WindowUpdateCallback?.Invoke(dt);
            WindowRenderCallback?.Invoke(dt);

            if (ScreenshotPath is not null && frame == ScreenshotFrame) {
                SaveScreenshot();
                if (ScreenshotExit && !queryMode) return;
            }
        }

        if (queryMode && RenderAsset.Current.Renderer is DX12HDRenderer qr) {
            DX12.Dx12QueryMode.Run(qr);
            return;
        }

        SceneQuerySmoke();
    }

    void ResizeStress(double dt) {
        if (RenderAsset.Current.Renderer is not DX12HDRenderer r) {
            Console.Error.WriteLine("[ResizeStress] DX12 renderer not active."); return;
        }

        for (int i = 0; i < 3; i++) { WindowUpdateCallback?.Invoke(dt); WindowRenderCallback?.Invoke(dt); }

        (int w, int h)[] sizes = {
            (1920,1080),(1600,900),(800,600),(1,1),(2,2),(7,3),(64,64),(1280,720),(3840,2160),
            (1920,1080),(1281,721),(33,1080),(1920,17),(640,360),(2560,1440),(1200,800),(1920,1080),
        };
        foreach (var (w, h) in sizes) {
            r.ResizeSceneTarget(w, h);
            r.Device.Flush();
            var afterResize = r.Device.Device.DeviceRemovedReason;
            if (!afterResize.Success) {
                Console.Error.WriteLine($"[ResizeStress] DEVICE REMOVED by REALLOC at {w}x{h}: reason={afterResize} DRED={r.Device.DrainDredReport()}");
                Environment.Exit(3);
            }
            WindowUpdateCallback?.Invoke(dt);
            WindowRenderCallback?.Invoke(dt);
            var reason = r.Device.Device.DeviceRemovedReason;
            if (!reason.Success) {
                Console.Error.WriteLine($"[ResizeStress] DEVICE REMOVED by RENDER at {w}x{h}: reason={reason} DRED={r.Device.DrainDredReport()}");
                if (r.Device.HasInfoQueue) Console.Error.WriteLine($"[ResizeStress] debug-msgs:\n{r.Device.DrainDebugMessages()}");
                Environment.Exit(3);
            }
            Console.WriteLine($"[ResizeStress] ok {w}x{h}");
        }
        Console.WriteLine("[ResizeStress] PASS (no device removal across the size sequence)");
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
        Vector3[] nudged = q.NudgeToFreeSpace(pts);
        int[] rooms = q.VisibilityClusters(pts);
        Console.WriteLine($"[SceneQuerySmoke] available={q.Available} renderers populated, {pts.Count} points:");
        for (int i = 0; i < pts.Count; i++) {
            string line = System.FormattableString.Invariant(
                $"  ({pts[i].X:0.##},{pts[i].Y:0.##},{pts[i].Z:0.##}) -> occupied={occ[i]} class={cls[i]} room={rooms[i]}");
            if (occ[i])
                line += System.FormattableString.Invariant(
                    $" nudged->({nudged[i].X:0.##},{nudged[i].Y:0.##},{nudged[i].Z:0.##})");
            Console.WriteLine(line);
        }
    }

    void SaveScreenshot() {
        if (RenderAsset.Current.Renderer is DX12HDRenderer r) {
            r.SaveFrame(ScreenshotPath);
            Console.WriteLine($"[Screenshot] saved {r.Width}x{r.Height} to {ScreenshotPath} (DX12)");
            PrintPerfStats();
            GBufferDump(r);
            HdrDump(r);
            DrainValidation(r);
            r.Device.SavePsoCache();
        }
        else {
            Console.Error.WriteLine("[Screenshot] DX12 renderer not active; nothing saved.");
        }
    }

    void SaveScreenshotTo(string path) {
        if (RenderAsset.Current.Renderer is DX12HDRenderer r)
            r.SaveFrame(path);
    }

    static void DrainValidation(DX12HDRenderer r) {
        int newErrors = DX12.Dx12ValidationBaseline.DrainReportAndGate(r.Device);
        if (newErrors > 0) {
            Console.Error.WriteLine($"[DX12-Validation] exiting non-zero ({newErrors} NEW error-class message(s)).");
            Environment.Exit(2);
        }
    }

    static void HdrDump(DX12HDRenderer r) {
        string file = Environment.GetEnvironmentVariable("BALLISTIC_DX12_HDR_DUMP");
        if (string.IsNullOrWhiteSpace(file)) return;
        string? d = System.IO.Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(d)) System.IO.Directory.CreateDirectory(d);
        object manifest = r.DumpHdrColor(file);
        System.IO.File.WriteAllText(file + ".manifest.json",
            System.Text.Json.JsonSerializer.Serialize(manifest));
        Console.WriteLine($"[HdrDump] wrote HDR scene color to {file} (DX12)");
    }

    static void GBufferDump(DX12HDRenderer r) {
        string dir = Environment.GetEnvironmentVariable("BALLISTIC_GBUFFER_DUMP");
        if (string.IsNullOrWhiteSpace(dir)) return;
        object manifest = r.DumpGBuffer(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
        Console.WriteLine($"[GBuffer] dumped depth/normal/albedo to {dir} (DX12)");
    }

    static void PrintPerfStats() {
        RenderStats rs = RenderStats.Scene;
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[PerfStats] draws={rs.DrawCalls} tris={rs.Triangles} (DX12)"));

        string statsOut = Environment.GetEnvironmentVariable("BALLISTIC_STATS_OUT");
        if (!string.IsNullOrWhiteSpace(statsOut)) {
            var payload = new {
                ok = true,
                drawCalls = rs.DrawCalls,
                depthOnlyDrawCalls = rs.DepthOnlyDrawCalls,
                instancedDrawCalls = rs.InstancedDrawCalls,
                drawsSavedByInstancing = rs.DrawsSavedByInstancing,
                triangles = rs.Triangles,
                subMeshesCulled = rs.SubMeshesCulled,
                punctualLights = rs.PunctualLights,
                shadowedLights = rs.ShadowedLights,
                cpuFrameMs = rs.CpuFrameMs,
                gpuFrameMs = rs.GpuFrameMs,
                gpuPasses = rs.GpuPasses.Select(p => new { name = p.Name, ms = p.Ms }).ToArray(),
                note = rs.GpuPasses.Count == 0 ? "per-pass GPU timing off (set BALLISTIC_DX12_PASS_TIMING=1); cpuFrameMs + counters are live" : null,
            };
            System.IO.File.WriteAllText(statsOut,
                System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    }));
            Console.WriteLine($"[PerfStats] wrote {statsOut} (DX12)");
        }
    }

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
