namespace BallisticEngine;

public readonly struct RenderHandle {
    public readonly nint Value;
    public RenderHandle(nint value) => Value = value;
    public static implicit operator nint(RenderHandle h) => h.Value;
    public static readonly RenderHandle None = new(0);
}
