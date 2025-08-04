using BallisticEngine;

internal class Program {
    // Entry point of ballistic engine 
    public static void Main(string[] args) {
        IBallisticEngineRuntime runtime = new BallisticEngineWindow(1280, 720);
        BEngineEntry engineEntry = new(runtime);
        engineEntry.Run();
    }
}