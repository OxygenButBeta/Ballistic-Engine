using BallisticEngine.InputSystem;
using OpenTKKeys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;
using OpenTKMouse = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;

namespace BallisticEngine.Gameplay.Input;

// The default IInputSource: bridges OUR device enums to the existing Input facade (OpenTK today). The
// ONE place the OpenTK enum mapping lives (plan §7.2 "wire the backend later") — the DX12 endgame
// replaces THIS file, not a single action definition. Lives at the Engine layer because it touches both
// our enums (Abstraction/Input) and the OpenTK-based Input facade (Abstraction/API Bindings); the
// abstraction seam (IInputSource) stays OpenTK-free.
//
// Honors Input.Enabled: Enabled forwards it, and the Input facade methods are themselves gated, so
// events never fire while the editor has input off (no debug-key leak — §7.2).
public sealed class EngineInputSource : IInputSource {
    public static readonly EngineInputSource Instance = new();

    public bool Enabled => global::Input.Enabled;

    public bool IsKeyDown(Key key) {
        OpenTKKeys k = MapKey(key);
        return k != OpenTKKeys.Unknown && global::Input.IsKeyDown(k);
    }

    public bool IsMouseDown(MouseCtrl button) => button switch {
        MouseCtrl.Left => global::Input.IsMouseButtonDown(OpenTKMouse.Left),
        MouseCtrl.Right => global::Input.IsMouseButtonDown(OpenTKMouse.Right),
        MouseCtrl.Middle => global::Input.IsMouseButtonDown(OpenTKMouse.Middle),
        _ => false,
    };

    public bool IsPadButtonDown(PadButton button, int player = 0) {
        GamepadButton g = MapPadButton(button);
        return g != (GamepadButton)(-1) && global::Input.IsGamepadButtonDown(g, player);
    }

    public System.Numerics.Vector2 MouseDelta {
        get { var d = global::Input.MouseDelta; return new System.Numerics.Vector2(d.X, d.Y); }
    }

    public float ScrollY => global::Input.ScrollDelta.Y;

    public System.Numerics.Vector2 PadStick(PadAxis stick, int player = 0) {
        // Input.GetLeftStick/GetRightStick return the engine's Vector2; read components so we don't
        // depend on which Vector2 type the global alias resolves to (System.Numerics vs OpenTK).
        var v = stick switch {
            PadAxis.LeftStick => global::Input.GetLeftStick(player),
            PadAxis.RightStick => global::Input.GetRightStick(player),
            _ => default,
        };
        return new System.Numerics.Vector2(v.X, v.Y);
    }

    public float PadTrigger(PadAxis trigger, int player = 0) => trigger switch {
        PadAxis.LeftTrigger => global::Input.GetGamepadAxis(GamepadAxis.LeftTrigger, player),
        PadAxis.RightTrigger => global::Input.GetGamepadAxis(GamepadAxis.RightTrigger, player),
        _ => 0f,
    };

    // ---- the mapping tables (the only OpenTK-coupled code in the input system) ---------------------
    static OpenTKKeys MapKey(Key key) => key switch {
        Key.A => OpenTKKeys.A, Key.B => OpenTKKeys.B, Key.C => OpenTKKeys.C, Key.D => OpenTKKeys.D,
        Key.E => OpenTKKeys.E, Key.F => OpenTKKeys.F, Key.G => OpenTKKeys.G, Key.H => OpenTKKeys.H,
        Key.I => OpenTKKeys.I, Key.J => OpenTKKeys.J, Key.K => OpenTKKeys.K, Key.L => OpenTKKeys.L,
        Key.M => OpenTKKeys.M, Key.N => OpenTKKeys.N, Key.O => OpenTKKeys.O, Key.P => OpenTKKeys.P,
        Key.Q => OpenTKKeys.Q, Key.R => OpenTKKeys.R, Key.S => OpenTKKeys.S, Key.T => OpenTKKeys.T,
        Key.U => OpenTKKeys.U, Key.V => OpenTKKeys.V, Key.W => OpenTKKeys.W, Key.X => OpenTKKeys.X,
        Key.Y => OpenTKKeys.Y, Key.Z => OpenTKKeys.Z,
        Key.D0 => OpenTKKeys.D0, Key.D1 => OpenTKKeys.D1, Key.D2 => OpenTKKeys.D2, Key.D3 => OpenTKKeys.D3,
        Key.D4 => OpenTKKeys.D4, Key.D5 => OpenTKKeys.D5, Key.D6 => OpenTKKeys.D6, Key.D7 => OpenTKKeys.D7,
        Key.D8 => OpenTKKeys.D8, Key.D9 => OpenTKKeys.D9,
        Key.Space => OpenTKKeys.Space, Key.Enter => OpenTKKeys.Enter, Key.Tab => OpenTKKeys.Tab,
        Key.Backspace => OpenTKKeys.Backspace, Key.Delete => OpenTKKeys.Delete, Key.Escape => OpenTKKeys.Escape,
        Key.LeftShift => OpenTKKeys.LeftShift, Key.RightShift => OpenTKKeys.RightShift,
        Key.LeftControl => OpenTKKeys.LeftControl, Key.RightControl => OpenTKKeys.RightControl,
        Key.LeftAlt => OpenTKKeys.LeftAlt, Key.RightAlt => OpenTKKeys.RightAlt,
        Key.Up => OpenTKKeys.Up, Key.Down => OpenTKKeys.Down, Key.Left => OpenTKKeys.Left, Key.Right => OpenTKKeys.Right,
        Key.F1 => OpenTKKeys.F1, Key.F2 => OpenTKKeys.F2, Key.F3 => OpenTKKeys.F3, Key.F4 => OpenTKKeys.F4,
        Key.F5 => OpenTKKeys.F5, Key.F6 => OpenTKKeys.F6, Key.F7 => OpenTKKeys.F7, Key.F8 => OpenTKKeys.F8,
        Key.F9 => OpenTKKeys.F9, Key.F10 => OpenTKKeys.F10, Key.F11 => OpenTKKeys.F11, Key.F12 => OpenTKKeys.F12,
        _ => OpenTKKeys.Unknown,
    };

    static GamepadButton MapPadButton(PadButton b) => b switch {
        PadButton.A => GamepadButton.A, PadButton.B => GamepadButton.B,
        PadButton.X => GamepadButton.X, PadButton.Y => GamepadButton.Y,
        PadButton.LeftBumper => GamepadButton.LeftBumper, PadButton.RightBumper => GamepadButton.RightBumper,
        PadButton.Back => GamepadButton.Back, PadButton.Start => GamepadButton.Start, PadButton.Guide => GamepadButton.Guide,
        PadButton.LeftStickPress => GamepadButton.LeftStick, PadButton.RightStickPress => GamepadButton.RightStick,
        PadButton.DPadUp => GamepadButton.DPadUp, PadButton.DPadDown => GamepadButton.DPadDown,
        PadButton.DPadLeft => GamepadButton.DPadLeft, PadButton.DPadRight => GamepadButton.DPadRight,
        _ => (GamepadButton)(-1),
    };
}
