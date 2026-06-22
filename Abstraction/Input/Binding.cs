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
