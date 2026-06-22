namespace BallisticEngine.Editor;

internal sealed class EditorPanelRegistry {
    internal sealed class Descriptor {
        public string Key;
        public string Title;
        public string Icon;
        public EditorWindow Window;
        public bool IsViewport;
        public bool Shown = true;
    }

    readonly Dictionary<string, Descriptor> byKey = new();
    readonly List<Descriptor> ordered = new();

    public IReadOnlyList<Descriptor> All => ordered;

    public void Register(EditorWindow window, string key, string title, string icon,
        bool isViewport = false) {
        var d = new Descriptor {
            Key = key, Title = title, Icon = icon,
            Window = window, IsViewport = isViewport,
        };
        byKey[key] = d;
        ordered.Add(d);
    }

    public void Register(string key, string title, string icon, System.Action drawContents,
        bool isViewport = false) {
        EditorWindow win = isViewport ? null : new LegacyWindow(key, title, icon, drawContents);
        Register(win, key, title, icon, isViewport);
    }

    public Descriptor Get(string key) => byKey.TryGetValue(key, out Descriptor d) ? d : null;

    public bool Contains(string key) => byKey.ContainsKey(key);

    public bool IsCorePanel(string key) => Get(key) is { IsViewport: false };

    public bool IsShown(string key) {
        Descriptor d = Get(key);
        if (d is null) return false;
        return d.IsViewport || d.Shown;
    }

    public bool Toggle(string key) {
        if (Get(key) is not { IsViewport: false } d) return false;
        d.Shown = !d.Shown;
        return d.Shown;
    }

    public bool Show(string key) {
        if (Get(key) is not { IsViewport: false } d) return false;
        if (d.Shown) return false;
        d.Shown = true;
        return true;
    }

    public void SetShown(string key, bool shown) {
        if (Get(key) is { IsViewport: false } d) d.Shown = shown;
    }

    public void ResetVisibility() {
        foreach (Descriptor d in ordered)
            if (!d.IsViewport) d.Shown = true;
    }

    public IEnumerable<string> HiddenKeys() {
        foreach (Descriptor d in ordered)
            if (!d.IsViewport && !d.Shown) yield return d.Key;
    }

    public void ApplyHidden(IReadOnlyCollection<string> hidden) {
        foreach (Descriptor d in ordered)
            if (!d.IsViewport) d.Shown = !hidden.Contains(d.Key);
    }

    public bool IsAvailable(string key) => IsShown(key);

    public void DrawCore(IEditorGui gui, System.Func<string, bool> requestFocus,
        System.Action<string> titleStrip) {
        foreach (Descriptor d in ordered) {
            if (d.IsViewport || !d.Shown || d.Window is null) continue;
            bool shown = d.Shown;
            WindowShell.Draw(d.Window, gui, ref shown, requestFocus(d.Key), titleStrip);
            d.Shown = shown;
        }
    }
}
