namespace BallisticEngine.InputSystem;

public enum Key {
    None = 0,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    Space, Enter, Tab, Backspace, Delete, Escape,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    Up, Down, Left, Right,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

public enum MouseCtrl {
    None = 0,
    Left, Right, Middle,
    Delta,
    ScrollY,
}

public enum PadButton {
    None = 0,
    A, B, X, Y,
    LeftBumper, RightBumper,
    Back, Start, Guide,
    LeftStickPress, RightStickPress,
    DPadUp, DPadDown, DPadLeft, DPadRight,
}

public enum PadAxis {
    None = 0,
    LeftStick, RightStick,
    LeftTrigger, RightTrigger,
}

public enum InputValueType {
    Button,
    Axis1D,
    Axis2D,
}

[Flags]
public enum Modifier {
    None = 0,
    Negate = 1 << 0,
    Swizzle = 1 << 1,
    Scale = 1 << 2,
}

public enum Phase {
    Started,
    Performed,
    Ongoing,
    Canceled,
}

public enum TriggerKind {
    Press,
    Release,
    Hold,
    Tap,
    DoubleTap,
    Pulse,
}
