namespace BallisticEngine.Editor;

internal static class EditorWindows {
    public static Action<string> ToggleHandler;

    public static Action<string> OpenHandler;

    public static Func<string, bool> IsOpenHandler;
    public static Func<string, bool> IsEnabledHandler;

    public static void Bind(Action<string> toggle, Action<string> open, Func<string, bool> isOpen,
        Func<string, bool> isEnabled) {
        ToggleHandler = toggle;
        OpenHandler = open;
        IsOpenHandler = isOpen;
        IsEnabledHandler = isEnabled;
    }

    public static void Toggle(string key) => ToggleHandler?.Invoke(key);
    public static void Open(string key) => OpenHandler?.Invoke(key);
    public static bool IsOpen(string key) => IsOpenHandler?.Invoke(key) ?? false;
    public static bool IsEnabled(string key) => IsEnabledHandler?.Invoke(key) ?? true;
}
