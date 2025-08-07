using BallisticEngine;

internal class Program {
    // Entry point of ballistic engine 
    public static void Main(string[] args) {
        IBallisticEngineRuntime runtime = new GLBallisticEngineWindow(1280, 720);
        BEngineEntry engineEntry = new(runtime);
        engineEntry.Run();
    }
}