namespace BallisticEngine.InputSystem;

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
