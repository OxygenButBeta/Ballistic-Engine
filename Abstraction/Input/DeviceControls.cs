namespace BallisticEngine.InputSystem;

// The engine's OWN device-control enums (plan §7.2) — BCL-only, NO OpenTK dependency. Bindings are
// captured against THESE (not OpenTK's Keys), so gameplay + the whole input system are backend-agnostic;
// swapping OpenTK out (the DX12 endgame) touches only the provider's mapping table, not one action
// definition. (Today's Input.cs leaking OpenTK Keys is exactly the dependency the migration removes.)
//
// These are NOT the rejected free-form string paths — they're a typed, refactor-safe vocabulary. The
// .inputmap text asset spells them as value-name tokens (Key.R), validated against the enum on load.

// Keyboard keys. Names follow the common US-QWERTY layout; the provider maps them to the physical key.
public enum Key {
    None = 0,
    // Letters
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    // Digits (top row)
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    // Whitespace / editing
    Space, Enter, Tab, Backspace, Delete, Escape,
    // Modifiers
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    // Arrows
    Up, Down, Left, Right,
    // Function row
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

// Mouse controls. Buttons + the two continuous controls (Delta for look, ScrollY for wheel).
public enum MouseCtrl {
    None = 0,
    Left, Right, Middle,
    Delta,     // per-frame movement (Axis2D) — first-person look
    ScrollY,   // wheel (Axis1D)
}

// Gamepad face/shoulder/dpad buttons — LOGICAL (cross-vendor: A = bottom face on Xbox/PS/Switch),
// the provider maps to the physical controller via SDL_GameControllerDB (plan §7.8).
public enum PadButton {
    None = 0,
    A, B, X, Y,
    LeftBumper, RightBumper,
    Back, Start, Guide,
    LeftStickPress, RightStickPress,
    DPadUp, DPadDown, DPadLeft, DPadRight,
}

// Gamepad continuous controls — sticks are Axis2D, triggers are Axis1D (0..1).
public enum PadAxis {
    None = 0,
    LeftStick, RightStick,     // Axis2D
    LeftTrigger, RightTrigger, // Axis1D, 0..1
}

// The SHAPE of an action's value (plan §7.2) — on the action, not a subclass.
public enum InputValueType {
    Button,   // bool/float 0..1 (pressed)
    Axis1D,   // float -1..1 (or 0..1 for triggers)
    Axis2D,   // Vector2 (WASD, stick)
}

// Per-binding value transforms (Unreal's Modifiers, §7.6) — turn a scalar key into an axis component.
[Flags]
public enum Modifier {
    None = 0,
    Negate = 1 << 0,   // flip the sign (S = -Y, A = -X)
    Swizzle = 1 << 1,  // route this scalar to the Y component instead of X (W/S are vertical)
    Scale = 1 << 2,    // reserved (per-binding scale; P0 has no parameter, so a placeholder)
}

// When in a press's life an event fires (plan §7.6 axis 1).
public enum Phase {
    Started,    // the frame the input first goes active (key down edge)
    Performed,  // the trigger condition is met (for a plain Press, same frame as Started)
    Ongoing,    // held / in progress
    Canceled,   // released / aborted
}

// The condition under which an action counts (plan §7.6 axis 2). P0 ships Press/Release/Hold; Tap/
// DoubleTap/Pulse/Chord are declared so the surface is final and land in a later input pass.
public enum TriggerKind {
    Press,      // fires on key-down (the default)
    Release,    // fires on key-up
    Hold,       // fires after Param seconds held
    Tap,        // fires on a quick press-release (< Param seconds) — later
    DoubleTap,  // two presses within Param seconds — later
    Pulse,      // repeats every Param seconds while held — later
}
