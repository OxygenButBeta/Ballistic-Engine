using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Core.GL;

public class Input : IInputProvider
{
    MouseState mouseState;
    KeyboardState keyboardState;


    public Input(KeyboardState keyboardState, MouseState mouseState)
    {
        this.keyboardState = keyboardState;
        this.mouseState = mouseState;
    }

    public bool IsKeyDown(Keys key)
    {
        return keyboardState.IsKeyDown(key);
    }

    public bool IsKeyPressed(Keys key)
    {
        return keyboardState.IsKeyPressed(key);
    }

    public bool IsMouseButtonPressed(MouseButton button)
    {
        return mouseState.IsButtonPressed(button);
    }

    public Vector2 ScrollDelta => mouseState.ScrollDelta;

    public Vector2 GetMousePosition()
    {
        return mouseState.Position;
    }
}