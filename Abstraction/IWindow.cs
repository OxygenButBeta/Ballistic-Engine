namespace BallisticEngine;

public interface IWindow {
    int Width { get; }
    int Height { get; }
    void Run();
    void Close();
    void SwapFrameBuffers();
}