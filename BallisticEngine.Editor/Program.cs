using BallisticEngine;
using BallisticEngine.Editor;

internal class Program {
    public static void Main(string[] args) {
        var projectPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : DefaultProjectPath();

        EditorPrefs.Load();

        BallisticEngine.Profiling.TracyProfiler.TryInstall("Ballistic Editor");

        OpenTK.Windowing.Desktop.GameWindow window = new Dx12BallisticEngineWindow(1600, 900);
        _ = new EditorApplication(window, projectPath);
        try {
            window.Run();
        }
        catch (Exception ex) {
            Console.Error.WriteLine("[DX12] FATAL: " + ex);
            try {
                BallisticEngine.DX12.Dx12Device d = BallisticEngine.DX12.Dx12Backend.Device;
                if (d != null) {
                    Console.Error.WriteLine("[DX12] DeviceRemovedReason: " + d.Device.DeviceRemovedReason);
                    Console.Error.WriteLine("[DX12] DRED: " + d.DrainDredReport());
                    Console.Error.WriteLine("[DX12] DebugMessages:\n" + d.DrainDebugMessages());
                }
            }
            catch {
            }
            throw;
        }

        JobSystem.Shutdown();

        Audio.Shutdown();
    }

    static string DefaultProjectPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SampleProject"));
}
