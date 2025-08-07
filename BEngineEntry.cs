using System.Reflection;

namespace BallisticEngine;

public sealed class BEngineEntry
{
    static IBallisticEngineRuntime Runtime;

    public BEngineEntry(IBallisticEngineRuntime runtime)
    {
        Runtime = runtime;
        Runtime.RenderAsset.Initialize(); // Initialize the renderer
        Runtime.Window.SetFrequency(60); // Set the update frequency to 60Hz
        SystemAPI.Bind(Runtime);
        Runtime.WindowUpdateCallback += EngineUpdate;
        Runtime.WindowRenderCallback += EngineRender;

        // Deploy Single Services
        SingleServiceInstaller.InstallAllInAssembly(Assembly.GetEntryAssembly());
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