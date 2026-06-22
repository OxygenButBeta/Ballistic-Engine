using BallisticEngine;

internal static class SystemAPI
{
    public static void Bind(IBallisticEngineRuntime runtime)
    {
        if (runtime is null)
            throw new ArgumentNullException(nameof(runtime), "Runtime cannot be null.");

        Input.Provider = runtime.InputProvider;
        Time.Timer = runtime.EngineTimer;
        Window.Current = runtime.Window;
        Debugging.Logger = runtime.Logger;
    }
}