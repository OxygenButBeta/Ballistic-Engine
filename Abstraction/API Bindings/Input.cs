using BallisticEngine;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

public static class Input
{
    internal static IInputProvider Provider;
    public static bool IsKeyDown(Keys key) => Provider.IsKeyDown(key);
    public static bool IsKeyPressed(Keys key) => Provider.IsKeyPressed(key);
    public static bool IsMouseButtonPressed(MouseButton button) => Provider.IsMouseButtonPressed(button);
    public static bool IsMouseButtonDown(MouseButton button) => Provider.IsMouseButtonDown(button);
    public static Vector2 ScrollDelta => Provider.ScrollDelta;
    public static Vector2 MousePosition => Provider.MousePosition;
}