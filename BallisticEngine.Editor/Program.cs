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
        window.Run();

        // JobSystem workers are foreground threads; without this the process never exits.
        JobSystem.Shutdown();

        // Close the OpenAL device/context cleanly on shutdown.
        Audio.Shutdown();
    }

    // BallisticEngine.Editor\bin\Debug\net9.0 -> repo root -> SampleProject
    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SampleProject"));
}
