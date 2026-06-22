namespace BallisticEngine.InputSystem;

[Flags]
public enum Modifier {
    None = 0,
    Negate = 1 << 0,
    Swizzle = 1 << 1,
    Scale = 1 << 2,
}
