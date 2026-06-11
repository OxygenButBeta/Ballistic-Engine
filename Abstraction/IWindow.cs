namespace BallisticEngine;

// How the OS mouse cursor behaves over the window.
public enum CursorMode
{
    Normal,  // visible, free to move (default; the editor uses this)
    Hidden,  // invisible but still free to move
    Locked,  // invisible AND locked to the window centre — first-person look (raw MouseDelta)
}

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
    float FrameRate { get; }
   public event Action<int,int> OnResizeCallback;

    // Cursor visibility/lock. Locked hides and pins the cursor to the centre so the player can turn
    // past the window edge; pair with Input.MouseDelta for look. The standalone player owns this; the
    // editor forces Normal whenever the Game view isn't the focused, playing surface.
    CursorMode CursorMode { get; set; }
}