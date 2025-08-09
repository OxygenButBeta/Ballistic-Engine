using System.Reflection;

namespace BallisticEngine;

public sealed class BEngineEntry
{
    static IBallisticEngineRuntime Runtime;

    public BEngineEntry(IBallisticEngineRuntime runtime)
    {
        Runtime = runtime;
        SystemAPI.Bind(Runtime);
        Runtime.RenderAsset.Initialize(); // Initialize the renderer
        Runtime.Window.SetFrequency(0); // Set the update frequency to 60Hz
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

    int logFpsInNFrame = 60;
    int logFpsInterval = 0;
    void EngineUpdate(double delta)
    {
        Runtime.EngineTimer.Update(delta);
        if (logFpsInterval++ >= logFpsInNFrame)
        {
            logFpsInterval = 0;
          Console.WriteLine("FPS: " + (1 / delta));
        }
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