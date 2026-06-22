namespace BallisticEngine.Editor;

internal sealed class MaximizeController {
    public string Maximized { get; private set; }

    public bool IsMaximized => Maximized is not null;

    public void Toggle(string key) => Maximized = Maximized == key ? null : key;

    public void Clear() => Maximized = null;

    public bool DropIfUnavailable(System.Func<string, bool> isAvailable) {
        if (Maximized is null) return false;
        if (isAvailable(Maximized)) return false;
        Maximized = null;
        return true;
    }
}
