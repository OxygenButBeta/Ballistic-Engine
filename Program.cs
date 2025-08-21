using BallisticEngine;

internal class Program {
    // Entry point of ballistic engine 
    public static void Main(string[] args) {
        GLBallisticEngineWindow runtime = new(1280, 720);
        BEngineEntry engineEntry = new(runtime);
        engineEntry.Run();
    }
}