namespace BallisticEngine.InputSystem;

public readonly struct Binding {
    public readonly DeviceKind Device;
    public readonly int Control;
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

public readonly struct Trigger {
    public readonly TriggerKind Kind;
    public readonly float Param;
    public Trigger(TriggerKind kind, float param = 0f) { Kind = kind; Param = param; }

    public static readonly Trigger Press = new(TriggerKind.Press);
    public static readonly Trigger Release = new(TriggerKind.Release);
    public static Trigger Hold(float seconds) => new(TriggerKind.Hold, seconds);
    public static Trigger Tap(float seconds = 0.2f) => new(TriggerKind.Tap, seconds);
    public static Trigger DoubleTap(float seconds = 0.3f) => new(TriggerKind.DoubleTap, seconds);
    public static Trigger Pulse(float rate) => new(TriggerKind.Pulse, rate);
}

public sealed class InputAction {
    public string Name { get; }
    public InputValueType Value { get; }
    public Trigger Trigger { get; private set; } = Trigger.Press;

    readonly List<Binding> bindings = new();
    public IReadOnlyList<Binding> Bindings => bindings;

    public InputAction(string name, InputValueType value) {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value;
        InputRegistry.Register(this);
    }

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

    public InputAction WithTrigger(Trigger trigger) {
        Trigger = trigger;
        return this;
    }
}
