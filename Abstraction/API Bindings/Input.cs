using BallisticEngine;
using OpenTK.Windowing.GraphicsLibraryFramework;

public enum GamepadButton {
    A = 0, B = 1, X = 2, Y = 3,
    LeftBumper = 4, RightBumper = 5,
    Back = 6, Start = 7, Guide = 8,
    LeftStick = 9, RightStick = 10,
    DPadUp = 11, DPadRight = 12, DPadDown = 13, DPadLeft = 14,
}

public enum GamepadAxis {
    LeftX = 0, LeftY = 1, RightX = 2, RightY = 3, LeftTrigger = 4, RightTrigger = 5,
}

public static class Input
{
    internal static IInputProvider Provider;

    public static bool Enabled { get; set; } = true;

    public static bool PointerInGameView { get; set; } = true;

    public static bool IsKeyDown(Keys key) => Enabled && Provider.IsKeyDown(key);
    public static bool IsKeyPressed(Keys key) => Enabled && Provider.IsKeyPressed(key);
    public static bool IsMouseButtonPressed(MouseButton button) => Enabled && Provider.IsMouseButtonPressed(button);
    public static bool IsMouseButtonDown(MouseButton button) => Enabled && Provider.IsMouseButtonDown(button);
    public static Vector2 ScrollDelta => Enabled ? Provider.ScrollDelta : Vector2.Zero;
    public static Vector2 MousePosition => Provider.MousePosition;

    public static Vector2 MouseDelta => Enabled ? Provider.MouseDelta : Vector2.Zero;

    static readonly System.Collections.Generic.Queue<char> _typed = new();

    public static void PushTypedChar(char c) { if (Enabled) _typed.Enqueue(c); }

    public static bool TryReadTypedChar(out char c)
    {
        if (_typed.Count > 0) { c = _typed.Dequeue(); return true; }
        c = '\0'; return false;
    }

    public static void ClearTypedChars() => _typed.Clear();

    public static float StickDeadzone { get; set; } = 0.15f;

    public static bool IsGamepadConnected(int player = 0) => Provider.IsGamepadConnected(player);

    public static bool IsGamepadButtonDown(GamepadButton button, int player = 0) =>
        Enabled && Provider.IsGamepadButtonDown(player, (int)button);

    public static bool IsGamepadButtonPressed(GamepadButton button, int player = 0) =>
        Enabled && Provider.IsGamepadButtonPressed(player, (int)button);

    public static float GetGamepadAxis(GamepadAxis axis, int player = 0) {
        if (!Enabled)
            return 0f;
        float raw = Provider.GetGamepadAxis(player, (int)axis);
        if (axis is GamepadAxis.LeftTrigger or GamepadAxis.RightTrigger)
            return MathHelper.Clamp((raw + 1f) * 0.5f, 0f, 1f);
        return MathF.Abs(raw) < StickDeadzone ? 0f : raw;
    }

    public static Vector2 GetLeftStick(int player = 0) => Stick(GamepadAxis.LeftX, GamepadAxis.LeftY, player);
    public static Vector2 GetRightStick(int player = 0) => Stick(GamepadAxis.RightX, GamepadAxis.RightY, player);

    static Vector2 Stick(GamepadAxis xAxis, GamepadAxis yAxis, int player) {
        if (!Enabled)
            return Vector2.Zero;
        var v = new Vector2(Provider.GetGamepadAxis(player, (int)xAxis), -Provider.GetGamepadAxis(player, (int)yAxis));
        return v.Length() < StickDeadzone ? Vector2.Zero : v;
    }
}
