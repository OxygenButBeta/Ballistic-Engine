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

    // True when the mouse pointer is over the actual game surface. Always true in the standalone
    // player (the whole window is the game); in the editor it's true only while the cursor is over
    // the Game view image — NOT over the Inspector/Hierarchy/etc. Use it to gate "click to (re)capture
    // the cursor" so a click on an editor panel can't grab the mouse back. (Once the cursor is locked,
    // it's centred over the game, so this stays true and the lock holds.)
    public static bool PointerInGameView { get; set; } = true;

    public static bool IsKeyDown(Keys key) => Enabled && Provider.IsKeyDown(key);
    public static bool IsKeyPressed(Keys key) => Enabled && Provider.IsKeyPressed(key);
    public static bool IsMouseButtonPressed(MouseButton button) => Enabled && Provider.IsMouseButtonPressed(button);
    public static bool IsMouseButtonDown(MouseButton button) => Enabled && Provider.IsMouseButtonDown(button);
    public static Vector2 ScrollDelta => Enabled ? Provider.ScrollDelta : Vector2.Zero;
    public static Vector2 MousePosition => Provider.MousePosition;

    // Raw per-frame mouse movement (pixels). Works while the cursor is grabbed/locked, so it's the
    // right source for first-person look. Gated by Enabled like the rest, so editor edit-mode doesn't
    // leak mouse motion into game scripts.
    public static Vector2 MouseDelta => Enabled ? Provider.MouseDelta : Vector2.Zero;
}
