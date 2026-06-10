using BallisticEngine;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

public static class Input
{
    internal static IInputProvider Provider;

    // Master gate for game/engine input. The editor turns this off in edit mode (and when the
    // Game view isn't focused) so component and renderer debug keys don't react while you're
    // using editor panels. The standalone player leaves it on.
    public static bool Enabled { get; set; } = true;

    public static bool IsKeyDown(Keys key) => Enabled && Provider.IsKeyDown(key);
    public static bool IsKeyPressed(Keys key) => Enabled && Provider.IsKeyPressed(key);
    public static bool IsMouseButtonPressed(MouseButton button) => Enabled && Provider.IsMouseButtonPressed(button);
    public static bool IsMouseButtonDown(MouseButton button) => Enabled && Provider.IsMouseButtonDown(button);
    public static Vector2 ScrollDelta => Enabled ? Provider.ScrollDelta : Vector2.Zero;
    public static Vector2 MousePosition => Provider.MousePosition;
}
