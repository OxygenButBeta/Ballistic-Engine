using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine;

// Device-agnostic action mapping (Unity's classic InputManager: GetAxis("Horizontal") /
// GetButton("Jump")). A game script asks for a logical action by NAME and gets a value fused from
// the keyboard, mouse, and gamepad bindings — so "move forward" works on WASD and the left stick
// without the script ever naming a device. This is exactly the abstraction an AI agent wants:
// "make the player jump on Jump" instead of wiring Space + gamepad A by hand.
//
// v1 bindings are code-defined (defaults below, mutable at runtime). Serialized .inputactions assets
// are a later layer — the registry is the seam they'd populate.
public static class InputActions {
    // ---- Binding types ----

    // A logical 1D axis: -1..1, summed from negative/positive key sets and an optional gamepad stick
    // axis (with the facade's deadzone already applied), clamped to [-1, 1].
    public sealed class AxisBinding {
        public readonly List<Keys> Positive = new();
        public readonly List<Keys> Negative = new();
        public GamepadAxis? Gamepad;       // null = no stick contribution
        public bool InvertGamepad;
    }

    // A logical button: down if ANY bound key / mouse button / gamepad button is down.
    public sealed class ButtonBinding {
        public readonly List<Keys> Keys = new();
        public readonly List<MouseButton> MouseButtons = new();
        public readonly List<GamepadButton> GamepadButtons = new();
    }

    static readonly Dictionary<string, AxisBinding> axes = new();
    static readonly Dictionary<string, ButtonBinding> buttons = new();

    // Per-button "pressed this frame" edge tracking, since the engine has no cross-device
    // press-edge primitive — we diff against last frame's down-state, advanced by Update().
    static readonly Dictionary<string, bool> wasDown = new();

    static InputActions() => InstallDefaults();

    // ---- Query API (what game scripts call) ----

    // -1..1 logical axis. Unknown name -> 0.
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

    // A 2D move vector from two axes (e.g. "Horizontal"/"Vertical"), length-clamped to 1 so diagonal
    // keyboard movement isn't faster than straight.
    public static Vector2 GetVector(string xAxis, string yAxis) {
        var v = new Vector2(GetAxis(xAxis), GetAxis(yAxis));
        return v.LengthSquared() > 1f ? v.Normalized() : v;
    }

    // True while any bound input for the action is held. Unknown name -> false.
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

    // True only on the frame the action first goes down (edge). Requires Update() once per frame.
    public static bool GetButtonDown(string name) =>
        GetButton(name) && !(wasDown.TryGetValue(name, out bool w) && w);

    // True only on the frame the action is released.
    public static bool GetButtonUp(string name) =>
        !GetButton(name) && wasDown.TryGetValue(name, out bool w) && w;

    // Advances press-edge state. The engine calls this once per frame (after input is polled, before
    // scripts Tick) so GetButtonDown/Up are correct.
    public static void Update() {
        foreach (string name in buttons.Keys)
            wasDown[name] = GetButton(name);
    }

    // ---- Registration (runtime-mutable) ----

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

    // Unity-parity default actions so a fresh project's movement/jump/fire just work.
    static void InstallDefaults() {
        AxisBinding h = DefineAxis("Horizontal");
        h.Positive.Add(Keys.D); h.Positive.Add(Keys.Right);
        h.Negative.Add(Keys.A); h.Negative.Add(Keys.Left);
        h.Gamepad = GamepadAxis.LeftX;

        AxisBinding v = DefineAxis("Vertical");
        v.Positive.Add(Keys.W); v.Positive.Add(Keys.Up);
        v.Negative.Add(Keys.S); v.Negative.Add(Keys.Down);
        v.Gamepad = GamepadAxis.LeftY; v.InvertGamepad = true; // stick Y is down-positive; W = forward

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
