using BallisticEngine;
using OpenTK.Windowing.GraphicsLibraryFramework;

// Xbox-style gamepad buttons; the int values are the standard GLFW/SDL gamepad button indices, so a
// recognized controller maps directly. (DInput/unmapped pads may differ — these assume a gamepad.)
public enum GamepadButton {
    A = 0, B = 1, X = 2, Y = 3,
    LeftBumper = 4, RightBumper = 5,
    Back = 6, Start = 7, Guide = 8,
    LeftStick = 9, RightStick = 10,
    DPadUp = 11, DPadRight = 12, DPadDown = 13, DPadLeft = 14,
}

// Xbox-style gamepad axes (standard GLFW/SDL indices). Sticks are -1..1; triggers are -1 (released)
// to 1 (pressed) in GLFW's raw mapping — the facade rescales triggers to 0..1.
public enum GamepadAxis {
    LeftX = 0, LeftY = 1, RightX = 2, RightY = 3, LeftTrigger = 4, RightTrigger = 5,
}

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

    // ---- Typed text (for text fields) -----------------------------------------------------------
    // Character input is event-driven (the window's text-input callback), not pollable device state, so
    // it doesn't live on IInputProvider. The host pushes each typed char here; UI text fields drain the
    // buffer per frame. Gated by Enabled so editor edit-mode doesn't leak typing into a game field.
    static readonly System.Collections.Generic.Queue<char> _typed = new();

    // Host: call from the window's OnTextInput (or equivalent) for each character produced.
    public static void PushTypedChar(char c) { if (Enabled) _typed.Enqueue(c); }

    // Consumer (UI): dequeue the next typed char, or '\0' if none this frame. Drain in a loop.
    public static bool TryReadTypedChar(out char c)
    {
        if (_typed.Count > 0) { c = _typed.Dequeue(); return true; }
        c = '\0'; return false;
    }

    // Clear any buffered typed chars (e.g. when focus leaves all fields).
    public static void ClearTypedChars() => _typed.Clear();

    // ---- Gamepad (Xbox-style, player 0 by default) ----------------------------------------------
    // All gated by Enabled like the rest, and safe when no controller is connected (false / 0).

    // Sticks below this magnitude read as 0 to ignore resting drift. 0..1.
    public static float StickDeadzone { get; set; } = 0.15f;

    public static bool IsGamepadConnected(int player = 0) => Provider.IsGamepadConnected(player);

    public static bool IsGamepadButtonDown(GamepadButton button, int player = 0) =>
        Enabled && Provider.IsGamepadButtonDown(player, (int)button);

    public static bool IsGamepadButtonPressed(GamepadButton button, int player = 0) =>
        Enabled && Provider.IsGamepadButtonPressed(player, (int)button);

    // Single axis with deadzone applied to sticks. Triggers (LeftTrigger/RightTrigger) are rescaled
    // from GLFW's -1..1 raw range to a friendly 0 (released) .. 1 (fully pressed).
    public static float GetGamepadAxis(GamepadAxis axis, int player = 0) {
        if (!Enabled)
            return 0f;
        float raw = Provider.GetGamepadAxis(player, (int)axis);
        if (axis is GamepadAxis.LeftTrigger or GamepadAxis.RightTrigger)
            return MathHelper.Clamp((raw + 1f) * 0.5f, 0f, 1f);
        return MathF.Abs(raw) < StickDeadzone ? 0f : raw;
    }

    // Left/right stick as a Vector2 with a radial deadzone (Y up = positive, so the raw Y is flipped).
    public static Vector2 GetLeftStick(int player = 0) => Stick(GamepadAxis.LeftX, GamepadAxis.LeftY, player);
    public static Vector2 GetRightStick(int player = 0) => Stick(GamepadAxis.RightX, GamepadAxis.RightY, player);

    static Vector2 Stick(GamepadAxis xAxis, GamepadAxis yAxis, int player) {
        if (!Enabled)
            return Vector2.Zero;
        var v = new Vector2(Provider.GetGamepadAxis(player, (int)xAxis), -Provider.GetGamepadAxis(player, (int)yAxis));
        return v.Length() < StickDeadzone ? Vector2.Zero : v;
    }
}
