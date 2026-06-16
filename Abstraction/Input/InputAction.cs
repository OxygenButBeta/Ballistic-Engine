namespace BallisticEngine.InputSystem;

// A bound device control (plan §7.2). Names a control by ENUM (never a string/OpenTK enum) plus the
// per-binding modifiers that compose scalars into an axis (WASD → Vector2 via Negate/Swizzle).
public readonly struct Binding {
    public readonly DeviceKind Device;
    public readonly int Control;       // the enum value (Key/MouseCtrl/PadButton/PadAxis), kept as int
    public readonly Modifier Modifiers;

    internal Binding(DeviceKind device, int control, Modifier modifiers) {
        Device = device;
        Control = control;
        Modifiers = modifiers;
    }

    public Key AsKey => (Key)Control;
    public MouseCtrl AsMouse => (MouseCtrl)Control;
    public PadButton AsPadButton => (PadButton)Control;
    public PadAxis AsPadAxis => (PadAxis)Control;
}

public enum DeviceKind { Keyboard, Mouse, GamepadButton, GamepadAxis }

// The trigger condition + its parameter, declared WITH the action (plan §7.6) so the callback stays
// bare. Press by default; Hold(0.5f) etc. via the factory helpers.
public readonly struct Trigger {
    public readonly TriggerKind Kind;
    public readonly float Param;   // seconds for Hold/Tap/DoubleTap; rate for Pulse
    public Trigger(TriggerKind kind, float param = 0f) { Kind = kind; Param = param; }

    public static readonly Trigger Press = new(TriggerKind.Press);
    public static readonly Trigger Release = new(TriggerKind.Release);
    public static Trigger Hold(float seconds) => new(TriggerKind.Hold, seconds);
    public static Trigger Tap(float seconds = 0.2f) => new(TriggerKind.Tap, seconds);
    public static Trigger DoubleTap(float seconds = 0.3f) => new(TriggerKind.DoubleTap, seconds);
    public static Trigger Pulse(float rate) => new(TriggerKind.Pulse, rate);
}

// ONE action class (plan §7.2): the value type is a FIELD, bindings are a SEPARATE list, the trigger
// lives in the definition. No per-shape subclasses, no string paths, no OpenTK. The constructor
// SELF-REGISTERS into InputRegistry (§7.3.1) so there's no central InstallDefaults to edit and forget.
//
// Authoring (code-first):
//   public static readonly InputAction Move = new InputAction("Move", InputValueType.Axis2D)
//       .Bind(Key.W, Modifier.Swizzle).Bind(Key.S, Modifier.Swizzle | Modifier.Negate)
//       .Bind(Key.A, Modifier.Negate).Bind(Key.D)
//       .Bind(PadAxis.LeftStick);
public sealed class InputAction {
    public string Name { get; }
    public InputValueType Value { get; }
    public Trigger Trigger { get; private set; } = Trigger.Press;

    readonly List<Binding> bindings = new();
    public IReadOnlyList<Binding> Bindings => bindings;

    public InputAction(string name, InputValueType value) {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value;
        InputRegistry.Register(this);   // self-register on construction (§7.3.1)
    }

    // Fluent Bind overloads — one per OUR device enum, so each call is fully type-checked.
    public InputAction Bind(Key key, Modifier modifiers = Modifier.None) {
        bindings.Add(new Binding(DeviceKind.Keyboard, (int)key, modifiers));
        return this;
    }

    public InputAction Bind(MouseCtrl ctrl, Modifier modifiers = Modifier.None) {
        bindings.Add(new Binding(DeviceKind.Mouse, (int)ctrl, modifiers));
        return this;
    }

    public InputAction Bind(PadButton button, Modifier modifiers = Modifier.None) {
        bindings.Add(new Binding(DeviceKind.GamepadButton, (int)button, modifiers));
        return this;
    }

    public InputAction Bind(PadAxis axis, Modifier modifiers = Modifier.None) {
        bindings.Add(new Binding(DeviceKind.GamepadAxis, (int)axis, modifiers));
        return this;
    }

    // Set the trigger in the definition (the §7.6 "resolved once" comfort — the callback stays bare).
    public InputAction WithTrigger(Trigger trigger) {
        Trigger = trigger;
        return this;
    }
}
