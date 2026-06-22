using BallisticEngine.InputSystem;

namespace BallisticEngine.Gameplay.Input;

public static class InputEval {
    public static bool ButtonActive(InputAction action, IInputSource source) {
        foreach (Binding b in action.Bindings)
            if (ReadButton(b, source))
                return true;
        return false;
    }

    public static float Axis1(InputAction action, IInputSource source) {
        float best = 0f;
        foreach (Binding b in action.Bindings) {
            float v = ReadScalar(b, source);
            if ((b.Modifiers & Modifier.Negate) != 0) v = -v;
            if (MathF.Abs(v) > MathF.Abs(best)) best = v;
        }
        return best;
    }

    public static Vector2 Axis2(InputAction action, IInputSource source) {
        Vector2 native = Vector2.Zero;
        Vector2 composed = Vector2.Zero;

        foreach (Binding b in action.Bindings) {
            if (TryReadVector(b, source, out Vector2 v)) {
                if (v.LengthSquared() > native.LengthSquared())
                    native = v;
                continue;
            }

            float s = ReadScalar(b, source);
            if (s == 0f) continue;
            if ((b.Modifiers & Modifier.Negate) != 0) s = -s;
            if ((b.Modifiers & Modifier.Swizzle) != 0) composed.Y += s;
            else composed.X += s;
        }

        Vector2 result = native != Vector2.Zero ? native : composed;
        float len = result.Length();
        return len > 1f ? result / len : result;
    }

    static bool ReadButton(Binding b, IInputSource source) => b.Device switch {
        DeviceKind.Keyboard => source.IsKeyDown(b.AsKey),
        DeviceKind.Mouse => b.AsMouse is MouseCtrl.Left or MouseCtrl.Right or MouseCtrl.Middle
            && source.IsMouseDown(b.AsMouse),
        DeviceKind.GamepadButton => source.IsPadButtonDown(b.AsPadButton),
        DeviceKind.GamepadAxis => source.PadTrigger(b.AsPadAxis) > 0.5f,
        _ => false,
    };

    static float ReadScalar(Binding b, IInputSource source) => b.Device switch {
        DeviceKind.Keyboard => source.IsKeyDown(b.AsKey) ? 1f : 0f,
        DeviceKind.Mouse => b.AsMouse == MouseCtrl.ScrollY ? source.ScrollY
            : (b.AsMouse is MouseCtrl.Left or MouseCtrl.Right or MouseCtrl.Middle && source.IsMouseDown(b.AsMouse) ? 1f : 0f),
        DeviceKind.GamepadButton => source.IsPadButtonDown(b.AsPadButton) ? 1f : 0f,
        DeviceKind.GamepadAxis => b.AsPadAxis is PadAxis.LeftTrigger or PadAxis.RightTrigger
            ? source.PadTrigger(b.AsPadAxis) : 0f,
        _ => 0f,
    };

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
