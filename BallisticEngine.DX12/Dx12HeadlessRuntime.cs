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

        // EF3 resize-stress diagnostic (BALLISTIC_DX12_RESIZE_STRESS=1): reproduce the editor's drag-resize
        // GPU HANG headlessly. DRED reported PageFaultVA=0x0 on the live crash → NOT a use-after-free but a
        // runaway/degenerate-extent shader. The editor renders the scene at the PANEL pixel size, which
        // during a drag changes every frame (and can go tiny/odd). This drives ResizeSceneTarget over such a
        // sequence with a real render between each, on the fully-bootstrapped renderer (DefaultTextures etc.
        // all live), so a bad-extent dispatch hangs HERE — letting the pass be bisected without ever
        // relaunching the live editor (GPU-hang rule). Runs with DRED always-on; prints OK per step.
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_RESIZE_STRESS") == "1") {
            ResizeStress(dt);
            return;
        }

        // Run until the screenshot frame, render it, save, exit. With no screenshot requested, run a few
        // frames then stop (nothing to present without a swapchain). Query mode also needs a frame or two so
        // the AS-feeding RuntimeSet<IStaticMeshRenderer> is populated before the query runs.
        bool queryMode = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BALLISTIC_QUERY"));
        int lastFrame = ScreenshotPath is not null ? ScreenshotFrame
            : queryMode ? 3 : 5;
        // GI MOTION DUMP (BALLISTIC_DX12_GI_MOTION_DUMP=<dir>): render a sequence with per-frame camera yaw
        // (BALLISTIC_DX12_GI_MOTION_YAW) and save the LAST K frames as frameNNN.bmp so a script can measure
        // frame-to-frame GI noise/boiling under REAL motion (a static capture can't). K = SCREENSHOT_FRAME's tail.
        string motionDir = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_MOTION_DUMP");
        if (!string.IsNullOrWhiteSpace(motionDir)) {
            System.IO.Directory.CreateDirectory(motionDir);
            int total = ScreenshotFrame > 0 ? ScreenshotFrame : 60;
            int dumpTail = 8;   // save the last 8 frames (warmed-up, in motion)
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

    // EF3 resize-stress: render the scene across a sequence of sizes (the editor's drag-resize pattern) on
    // the real renderer, checking DeviceRemovedReason after each. A single optional env BALLISTIC_DX12_
    // RESIZE_STRESS_ONLYPASS=<n> isn't needed — bisection is done by reading which size logs the removal.
    void ResizeStress(double dt) {
        if (RenderAsset.Current.Renderer is not DX12HDRenderer r) {
            Console.Error.WriteLine("[ResizeStress] DX12 renderer not active."); return;
        }
        // Warm up a few frames at the default size so the scene + AS + shadows are built before we resize.
        for (int i = 0; i < 3; i++) { WindowUpdateCallback?.Invoke(dt); WindowRenderCallback?.Invoke(dt); }

        // Sizes that mimic a drag-resize: shrink, grow, tiny, odd/non-aligned, 4K, back. The editor clamps
        // the panel size to >=1 (ViewportRenderer), so we never pass 0 — but we DO pass small/odd extents that
        // a group-count or mip computation might mishandle.
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
                // Debug/GBV messages (only present under BALLISTIC_DX12_DEBUG/GBV) name a bad bind/barrier.
                if (r.Device.HasInfoQueue) Console.Error.WriteLine($"[ResizeStress] debug-msgs:\n{r.Device.DrainDebugMessages()}");
                Environment.Exit(3);   // stop on first removal — do not keep hammering the GPU
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
        }
        else {
            Console.Error.WriteLine("[Screenshot] DX12 renderer not active; nothing saved.");
        }
    }

    // Plain frame save to an explicit path (the GI motion-dump sequence). No perf/gbuffer/validation side effects.
    void SaveScreenshotTo(string path) {
        if (RenderAsset.Current.Renderer is DX12HDRenderer r)
            r.SaveFrame(path);
    }

    // W2/W4 — drain the debug/GBV info queue at end-of-headless-render, normalize each message to a
    // signature, partition against the captured baseline, print the report to STDERR (so `bal render`
    // surfaces it — the CLI forwards the player's stderr, discards stdout), and FAIL LOUD on NEW
    // error-class messages when BALLISTIC_DX12_BREAK_ON_ERROR=1. This is the headless render path's drain
    // — before this, only the probe self-tests + the editor crash handler drained the queue, so a
    // `bal render` GBV run stored validation messages but never printed them. GATED on HasInfoQueue
    // inside DrainReportAndGate: a normal `bal render` (no debug layer / no GBV) is a silent no-op, so the
    // non-debug render stays byte-identical and unchanged.
    static void DrainValidation(DX12HDRenderer r) {
        int newErrors = DX12.Dx12ValidationBaseline.DrainReportAndGate(r.Device);
        // A nonzero count only ever returns when break-on-error is set AND there were NEW (non-baseline)
        // error-class messages. Exit code 2 so the CLI (`bal render`) reports the validation failure; this
        // runs AFTER the screenshot+stats are written, so artifacts still exist for inspection.
        if (newErrors > 0) {
            Console.Error.WriteLine($"[DX12-Validation] exiting non-zero ({newErrors} NEW error-class message(s)).");
            Environment.Exit(2);
        }
    }

    // W3 noise-floor HDR dump (BALLISTIC_DX12_HDR_DUMP=<file>): after the frame, write the HDR scene-color
    // target back as raw R32F-triple .bin so the determinism floor can be measured in LINEAR/HDR space (the
    // tonemapped LDR PNG can round away a sub-floor HDR diff). Measurement-only; same end-of-frame readback
    // pattern as GBufferDump.
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
