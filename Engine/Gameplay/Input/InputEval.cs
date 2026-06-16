using System.Numerics;
using BallisticEngine.InputSystem;

namespace BallisticEngine.Gameplay.Input;

// Evaluates an action's bindings through the IInputSource into a value (plan §7.2). The per-binding
// Negate/Swizzle modifiers compose scalar keys into an axis (WASD → Vector2) — Unreal's uniform model,
// no special composite container. The only place binding→value math lives, so the InputComponent stays
// about subscription/edges and this stays about device reads.
public static class InputEval {
    // A button is active if ANY of its bindings is currently held.
    public static bool ButtonActive(InputAction action, IInputSource source) {
        foreach (Binding b in action.Bindings)
            if (ReadButton(b, source))
                return true;
        return false;
    }

    // Axis1D: the binding with the largest magnitude wins (a held key = ±1, a trigger = 0..1).
    public static float Axis1(InputAction action, IInputSource source) {
        float best = 0f;
        foreach (Binding b in action.Bindings) {
            float v = ReadScalar(b, source);
            if ((b.Modifiers & Modifier.Negate) != 0) v = -v;
            if (MathF.Abs(v) > MathF.Abs(best)) best = v;
        }
        return best;
    }

    // Axis2D: native 2D bindings (sticks, mouse delta) provide the vector directly; scalar bindings
    // (keys) contribute to X by default, or Y when Swizzle, sign-flipped by Negate. The strongest
    // native vector wins; key contributions accumulate then clamp to the unit square's diagonal.
    public static Vector2 Axis2(InputAction action, IInputSource source) {
        Vector2 native = Vector2.Zero;
        Vector2 composed = Vector2.Zero;

        foreach (Binding b in action.Bindings) {
            if (TryReadVector(b, source, out Vector2 v)) {
                if (v.LengthSquared() > native.LengthSquared())
                    native = v;
                continue;
            }
            // Scalar key → axis component via modifiers.
            float s = ReadScalar(b, source);
            if (s == 0f) continue;
            if ((b.Modifiers & Modifier.Negate) != 0) s = -s;
            if ((b.Modifiers & Modifier.Swizzle) != 0) composed.Y += s;
            else composed.X += s;
        }

        // Native (stick) takes precedence when present; otherwise the composed key vector, clamped so a
        // diagonal isn't faster than a cardinal (length capped at 1).
        Vector2 result = native != Vector2.Zero ? native : composed;
        float len = result.Length();
        return len > 1f ? result / len : result;
    }

    // ---- single-binding reads ---------------------------------------------------------------------
    static bool ReadButton(Binding b, IInputSource source) => b.Device switch {
        DeviceKind.Keyboard => source.IsKeyDown(b.AsKey),
        DeviceKind.Mouse => b.AsMouse is MouseCtrl.Left or MouseCtrl.Right or MouseCtrl.Middle
            && source.IsMouseDown(b.AsMouse),
        DeviceKind.GamepadButton => source.IsPadButtonDown(b.AsPadButton),
        DeviceKind.GamepadAxis => source.PadTrigger(b.AsPadAxis) > 0.5f,   // a trigger as a button
        _ => false,
    };

    // Scalar magnitude for a binding (1 for a held key, 0..1 for a trigger). Used by axis composition.
    static float ReadScalar(Binding b, IInputSource source) => b.Device switch {
        DeviceKind.Keyboard => source.IsKeyDown(b.AsKey) ? 1f : 0f,
        DeviceKind.Mouse => b.AsMouse == MouseCtrl.ScrollY ? source.ScrollY
            : (b.AsMouse is MouseCtrl.Left or MouseCtrl.Right or MouseCtrl.Middle && source.IsMouseDown(b.AsMouse) ? 1f : 0f),
        DeviceKind.GamepadButton => source.IsPadButtonDown(b.AsPadButton) ? 1f : 0f,
        DeviceKind.GamepadAxis => b.AsPadAxis is PadAxis.LeftTrigger or PadAxis.RightTrigger
            ? source.PadTrigger(b.AsPadAxis) : 0f,
        _ => 0f,
    };

    // Native 2D read for a binding (stick / mouse delta). False for scalar bindings.
    static bool TryReadVector(Binding b, IInputSource source, out Vector2 v) {
        if (b.Device == DeviceKind.GamepadAxis && b.AsPadAxis is PadAxis.LeftStick or PadAxis.RightStick) {
            v = source.PadStick(b.AsPadAxis);
            return true;
        }
        if (b.Device == DeviceKind.Mouse && b.AsMouse == MouseCtrl.Delta) {
            v = source.MouseDelta;
            return true;
        }
        v = Vector2.Zero;
        return false;
    }
}
