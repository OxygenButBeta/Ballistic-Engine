using System.Reflection;
using BallisticEngine.AssetPipeline;

namespace BallisticEngine;

public sealed class BEngineEntry
{
    static IBallisticEngineRuntime Runtime;

    public BEngineEntry(IBallisticEngineRuntime runtime, string projectPath)
    {
        Runtime = runtime;
        SystemAPI.Bind(Runtime);

        // Deploy Single Services (before anything that might resolve them)
        SingleServiceInstaller.InstallAllInAssembly(Assembly.GetEntryAssembly());

        // Open the project and bring the Library up to date (CPU-side import only).
        BallisticProject project = BallisticProject.Open(projectPath);
        AssetDatabase.Initialize(project);
        AssetDatabase.Refresh();

        Runtime.RenderAsset.Initialize(); // Initialize the renderer (loads the default skybox via AssetDatabase)
        Runtime.Window.SetFrequency(600);
        Runtime.WindowUpdateCallback += EngineUpdate;
        Runtime.WindowRenderCallback += EngineRender;

        //TODO : REMOVE
        SceneInit.Init();
    }

    void EngineRender(double obj)
    {
        if (SceneManager.RenderCamera is null)
        {
            Console.WriteLine("No render camera set in scene.");
        }
        else
        {
            SceneManager.RenderCamera.RenderCamera();
        }
    }

    void EngineUpdate(double delta)
    {
        Runtime.EngineTimer.Update(delta);
        SceneManager.Update((float)delta);
    }

    public void Run()
    {
        Runtime.Window.Run();
    }

    public static void Exit()
    {
        Runtime.Window.Close();
    }
}
