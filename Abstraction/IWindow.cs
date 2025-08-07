namespace BallisticEngine;

/// <summary>
/// This interface defines the methods and properties required for a window in the Ballistic Engine.
/// </summary>
public interface IWindow
{
    int Width { get; }
    int Height { get; }
    void SetFrequency(int frequency);
    void Run();
    void Close();
    void SwapFrameBuffers();
}