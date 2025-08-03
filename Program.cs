using BallisticEngine;

internal class Program
{
    // Entry point of ballistic engine 
    public static void Main(string[] args)
    {
        BallisticEngineWindow gameWindow = new BallisticEngineWindow(1280, 720);
        BallisticEngine.BallisticEngine engine = new BallisticEngine.BallisticEngine(gameWindow);
        gameWindow.Run();
    }
}