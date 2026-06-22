using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

internal sealed class DockPanelHost {
    sealed class Kind {
        public string Title;
        public string Icon;
        public bool Singleton;
        public Func<object> Factory;
        public Action<object> Draw;
    }

    sealed class Instance {
        public string KindKey;
        public int Id;
        public object Panel;
        public bool Open = true;
        public bool JustOpened = true;
    }

    readonly Dictionary<string, Kind> kinds = new();
    readonly List<Instance> instances = new();
    int nextId = 1;

    public Action<string> OnTitleStrip;

    public IEnumerable<string> Kinds => kinds.Keys;
    public string TitleOf(string key) => kinds.TryGetValue(key, out Kind k) ? k.Title : key;
    public string IconOf(string key) => kinds.TryGetValue(key, out Kind k) ? k.Icon : "";

    public void Register(string key, string title, string icon, Func<object> factory, Action<object> draw,
        bool singleton = false) {
        kinds[key] = new Kind { Title = title, Icon = icon, Factory = factory, Draw = draw, Singleton = singleton };
    }

    public int CountOf(string key) {
        int n = 0;
        foreach (Instance i in instances) if (i.KindKey == key) n++;
        return n;
    }

    public void Open(string key, bool forceNew = true) {
        if (!kinds.TryGetValue(key, out Kind kind)) return;

        if (kind.Singleton || !forceNew) {
            foreach (Instance existing in instances)
                if (existing.KindKey == key) { existing.Open = true; return; }
        }

        instances.Add(new Instance { KindKey = key, Id = nextId++, Panel = kind.Factory() });
    }

    public void EnsureOne(string key) {
        if (CountOf(key) == 0) Open(key, forceNew: true);
    }

    static string Label(Kind kind, Instance inst) {
        if (inst.Id == 0)
            return $"{kind.Icon}  {kind.Title}###{inst.KindKey}";
        return $"{kind.Icon}  {kind.Title} {inst.Id}###{inst.KindKey}_{inst.Id}";
    }

    public void DrawAll() {
        for (int idx = 0; idx < instances.Count; idx++) {
            Instance inst = instances[idx];
            if (!kinds.TryGetValue(inst.KindKey, out Kind kind)) { inst.Open = false; continue; }

            string label = Label(kind, inst);
            if (inst.JustOpened) {
                inst.JustOpened = false;
                ImGuiViewportPtr vp = ImGui.GetMainViewport();
                ImGui.SetNextWindowSize(new System.Numerics.Vector2(420, 540), ImGuiCond.Appearing);
                ImGui.SetNextWindowPos(
                    new System.Numerics.Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f),
                    ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
            }
            bool open = inst.Open;
            if (ImGui.Begin(label, ref open)) {
                OnTitleStrip?.Invoke(label);
                kind.Draw(inst.Panel);
            }
            ImGui.End();
            inst.Open = open;
        }
        instances.RemoveAll(i => !i.Open);
    }

    public string FirstLabel(string key) {
        foreach (Instance i in instances)
            if (i.KindKey == key && kinds.TryGetValue(key, out Kind k))
                return Label(k, i);
        return null;
    }

    public bool OwnsLabel(string label) {
        foreach (Instance i in instances)
            if (kinds.TryGetValue(i.KindKey, out Kind k) && Label(k, i) == label)
                return true;
        return false;
    }

    public bool DrawMaximizedInstance(string label, System.Numerics.Vector2 pos, System.Numerics.Vector2 size,
        Action<string> runStrip) {
        foreach (Instance inst in instances) {
            if (!kinds.TryGetValue(inst.KindKey, out Kind kind) || Label(kind, inst) != label)
                continue;
            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings;
            string title = inst.Id == 0 ? $"{kind.Icon}  {kind.Title}" : $"{kind.Icon}  {kind.Title} {inst.Id}";
            bool open = inst.Open;
            if (ImGui.Begin($"{title}###maxinstance", ref open, flags)) {
                runStrip?.Invoke(label);
                kind.Draw(inst.Panel);
            }
            ImGui.End();
            inst.Open = open;
            return !open;
        }
        return false;
    }
}
