using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public interface IInputProvider
{
    bool IsKeyDown(Keys key);
    bool IsKeyPressed(Keys key);
    bool IsMouseButtonPressed(MouseButton button);
    bool IsMouseButtonDown(MouseButton button);
    Vector2 ScrollDelta { get; }
    Vector2 MousePosition{ get; }
}