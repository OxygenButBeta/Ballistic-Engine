using BallisticEngine;
using BallisticEngine.Editor;

internal class Program {
    public static void Main(string[] args) {
        var projectPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : DefaultProjectPath();

        // Load persisted editor settings before the UI/theme is built so the saved accent applies.
        EditorPrefs.Load();

        BallisticEngine.Profiling.TracyProfiler.TryInstall("Ballistic Editor");

        // Backend seam (DX12Migration.md ENDGAME 2): BALLISTIC_BACKEND=dx12 brings up the windowed DX12 host
        // (swapchain + ImGui DX12 backend) instead of the GL window. GL is the default until the DX12 editor
        // reaches parity (then GL is deleted). Both are GameWindow + IBallisticEngineRuntime + IWindow.
        OpenTK.Windowing.Desktop.GameWindow window =
            RenderBackendSelector.Selected == RenderBackend.Dx12
                ? new Dx12BallisticEngineWindow(1600, 900)
                : new GLBallisticEngineWindow(1600, 900);
        _ = new EditorApplication(window, projectPath);
        try {
            window.Run();
        }
        catch (Exception ex) when (RenderBackendSelector.Selected == RenderBackend.Dx12) {
            // On a DX12 device-removal, surface the real cause (debug-layer messages + removed reason) instead
            // of the opaque HRESULT, so a GPU fault is diagnosable without a driver reset (run BALLISTIC_DX12_DEBUG=1).
            Console.Error.WriteLine("[DX12] FATAL: " + ex);
            try {
                BallisticEngine.DX12.Dx12Device d = BallisticEngine.DX12.Dx12Backend.Device;
                if (d != null) {
                    Console.Error.WriteLine("[DX12] DeviceRemovedReason: " + d.Device.DeviceRemovedReason);
                    Console.Error.WriteLine("[DX12] DebugMessages:\n" + d.DrainDebugMessages());
                }
            }
            catch { /* best-effort diagnostics */ }
            throw;
        }

        // JobSystem workers are foreground threads; without this the process never exits.
        JobSystem.Shutdown();

        // Close the OpenAL device/context cleanly on shutdown.
        Audio.Shutdown();
    }

    // BallisticEngine.Editor\bin\Debug\net9.0 -> repo root -> SampleProject
    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SampleProject"));
}
