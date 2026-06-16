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

    // GI Phase-6 MOTION-stability harness (BALLISTIC_DX12_GI_MOTION_DUMP=<dir>): instead of one capture, save a
    // SEQUENCE of the last K consecutive frames (frame_00.bmp … frame_{K-1}.bmp) so a python helper can measure
    // frame-to-frame "boiling" (mean abs delta of consecutive GI-isolate frames). The point of Phase 6 is
    // "stable under motion / no boiling", which a single PAUSED capture cannot show. Run this with a STATIC
    // serialized camera + temporal ACTIVE (i.e. NOT BALLISTIC_DETERMINISTIC, which makes the temporal pass a
    // pass-through) + GI-isolate on + exposure pinned: a STABLE temporal chain → consecutive deltas decay toward
    // 0; a BOILING chain → deltas stay high. The total frame count runs to ScreenshotFrame so the field is
    // converged before the dumped window; the last K frames are written. K = BALLISTIC_DX12_GI_MOTION_FRAMES (8).
    static readonly string MotionDumpDir = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_MOTION_DUMP");
    static readonly int MotionDumpFrames = int.TryParse(
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_MOTION_FRAMES"), out int mf) && mf > 0 ? mf : 8;

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
        // frames then stop (nothing to present without a swapchain). Query mode also needs a frame or two so
        // the AS-feeding RuntimeSet<IStaticMeshRenderer> is populated before the query runs.
        bool queryMode = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BALLISTIC_QUERY"));
        bool motionMode = !string.IsNullOrWhiteSpace(MotionDumpDir);
        // Motion harness runs to ScreenshotFrame total (field converged), dumping the LAST MotionDumpFrames.
        int motionTotal = motionMode ? Math.Max(ScreenshotFrame, MotionDumpFrames) : 0;
        int lastFrame = motionMode ? motionTotal
            : ScreenshotPath is not null ? ScreenshotFrame
            : queryMode ? 3 : 5;
        for (int frame = 1; frame <= lastFrame; frame++) {
            WindowUpdateCallback?.Invoke(dt);
            WindowRenderCallback?.Invoke(dt);

            if (motionMode && frame > motionTotal - MotionDumpFrames) {
                SaveMotionFrame(frame - (motionTotal - MotionDumpFrames) - 1);   // 0-based index in the window
                if (frame == motionTotal) { Console.WriteLine($"[GI-Motion] dumped {MotionDumpFrames} frames to {MotionDumpDir}"); return; }
                continue;   // skip the single-shot screenshot logic while dumping the sequence
            }

            if (ScreenshotPath is not null && frame == ScreenshotFrame) {
                SaveScreenshot();
                if (ScreenshotExit && !queryMode) return;
            }
        }

        // Scene-query mode for `bal query` (BALLISTIC_QUERY=<spec.json> -> BALLISTIC_QUERY_OUT): run the query
        // against the live scene TLAS and write the result JSON, then exit. Same subprocess pattern as render.
        if (queryMode && RenderAsset.Current.Renderer is DX12HDRenderer qr) {
            DX12.Dx12QueryMode.Run(qr);
            return;
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
        }
        else {
            Console.Error.WriteLine("[Screenshot] DX12 renderer not active; nothing saved.");
        }
    }

    // P6.0 motion harness: save one frame of the consecutive-frame sequence as frame_NN.bmp into MotionDumpDir.
    void SaveMotionFrame(int index) {
        if (RenderAsset.Current.Renderer is not DX12HDRenderer r) {
            Console.Error.WriteLine("[GI-Motion] DX12 renderer not active; nothing saved.");
            return;
        }
        System.IO.Directory.CreateDirectory(MotionDumpDir);
        string path = System.IO.Path.Combine(MotionDumpDir,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"frame_{index:00}.bmp"));
        r.SaveFrame(path);
    }

    // Raw G-buffer dump for `bal gbuffer` (BALLISTIC_GBUFFER_DUMP=<dir>): after the frame, write depth/normal/
    // albedo as raw .bin + a manifest.json the agent decodes. Runs in the screenshot path (a frame is rendered).
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

        // Structured perf surface for `bal perf` (BALLISTIC_STATS_OUT=<json>): emit RenderStats as JSON so the
        // agent does autonomous perf work from numbers, not screenshots. (Per-pass GPU timestamp queries are a
        // renderer-track follow-up; CPU frame ms + draw/tri/cull/light counters are wired today.)
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
                note = rs.GpuPasses.Count == 0 ? "per-pass GPU timestamp queries not yet wired on DX12 (renderer follow-up); cpuFrameMs + counters are live" : null,
            };
            System.IO.File.WriteAllText(statsOut,
                System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions {
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    }));
            Console.WriteLine($"[PerfStats] wrote {statsOut} (DX12)");
        }
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
