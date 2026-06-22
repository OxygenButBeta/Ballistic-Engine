using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

public static class InputActions {
    public sealed class AxisBinding {
        public readonly List<Keys> Positive = new();
        public readonly List<Keys> Negative = new();
        public GamepadAxis? Gamepad;
        public bool InvertGamepad;
    }

    public sealed class ButtonBinding {
        public readonly List<Keys> Keys = new();
        public readonly List<MouseButton> MouseButtons = new();
        public readonly List<GamepadButton> GamepadButtons = new();
    }

    static readonly Dictionary<string, AxisBinding> axes = new();
    static readonly Dictionary<string, ButtonBinding> buttons = new();

    static readonly Dictionary<string, bool> wasDown = new();

    static InputActions() => InstallDefaults();

    public static float GetAxis(string name) {
        if (!axes.TryGetValue(name, out AxisBinding a))
            return 0f;

        float v = 0f;
        foreach (Keys k in a.Positive)
            if (Input.IsKeyDown(k)) { v += 1f; break; }
        foreach (Keys k in a.Negative)
            if (Input.IsKeyDown(k)) { v -= 1f; break; }

        if (a.Gamepad is { } axis) {
            float g = Input.GetGamepadAxis(axis);
            if (a.InvertGamepad) g = -g;
            v += g;
        }
        return MathHelper.Clamp(v, -1f, 1f);
    }

    public static Vector2 GetVector(string xAxis, string yAxis) {
        var v = new Vector2(GetAxis(xAxis), GetAxis(yAxis));
        return v.LengthSquared() > 1f ? v.Normalized() : v;
    }

    public static bool GetButton(string name) {
        if (!buttons.TryGetValue(name, out ButtonBinding b))
            return false;
        foreach (Keys k in b.Keys)
            if (Input.IsKeyDown(k)) return true;
        foreach (MouseButton m in b.MouseButtons)
            if (Input.IsMouseButtonDown(m)) return true;
        foreach (GamepadButton g in b.GamepadButtons)
            if (Input.IsGamepadButtonDown(g)) return true;
        return false;
    }

    public static bool GetButtonDown(string name) =>
        GetButton(name) && !(wasDown.TryGetValue(name, out bool w) && w);

    public static bool GetButtonUp(string name) =>
        !GetButton(name) && wasDown.TryGetValue(name, out bool w) && w;

    public static void Update() {
        foreach (string name in buttons.Keys)
            wasDown[name] = GetButton(name);
    }

    public static AxisBinding DefineAxis(string name) {
        if (!axes.TryGetValue(name, out AxisBinding a)) {
            a = new AxisBinding();
            axes[name] = a;
        }
        return a;
    }

    public static ButtonBinding DefineButton(string name) {
        if (!buttons.TryGetValue(name, out ButtonBinding b)) {
            b = new ButtonBinding();
            buttons[name] = b;
        }
        return b;
    }

    public static bool HasAxis(string name) => axes.ContainsKey(name);
    public static bool HasButton(string name) => buttons.ContainsKey(name);

    static void InstallDefaults() {
        AxisBinding h = DefineAxis("Horizontal");
        h.Positive.Add(Keys.D); h.Positive.Add(Keys.Right);
        h.Negative.Add(Keys.A); h.Negative.Add(Keys.Left);
        h.Gamepad = GamepadAxis.LeftX;

        AxisBinding v = DefineAxis("Vertical");
        v.Positive.Add(Keys.W); v.Positive.Add(Keys.Up);
        v.Negative.Add(Keys.S); v.Negative.Add(Keys.Down);
        v.Gamepad = GamepadAxis.LeftY; v.InvertGamepad = true;

        ButtonBinding jump = DefineButton("Jump");
        jump.Keys.Add(Keys.Space);
        jump.GamepadButtons.Add(GamepadButton.A);

        ButtonBinding fire = DefineButton("Fire");
        fire.MouseButtons.Add(MouseButton.Left);
        fire.GamepadButtons.Add(GamepadButton.RightBumper);

        ButtonBinding interact = DefineButton("Interact");
        interact.Keys.Add(Keys.E);
        interact.GamepadButtons.Add(GamepadButton.X);
    }
}
