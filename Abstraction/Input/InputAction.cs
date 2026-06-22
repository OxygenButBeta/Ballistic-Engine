namespace BallisticEngine.InputSystem;

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
