namespace BallisticEngine;

public static class Cursor
{
    public static CursorMode Mode { get; set; } = CursorMode.Normal;

    public static bool Locked {
        get => Mode == CursorMode.Locked;
        set => Mode = value ? CursorMode.Locked : CursorMode.Normal;
    }

    public static void Apply(bool allowed) {
        if (Window.Current is null)
            return;
        CursorMode target = allowed ? Mode : CursorMode.Normal;
        if (Window.Current.CursorMode != target)
            Window.Current.CursorMode = target;
    }
}
