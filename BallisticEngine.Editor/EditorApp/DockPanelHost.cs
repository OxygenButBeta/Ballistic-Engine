using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

// Lets the user open as MANY instances of a panel TYPE as they want (Unity/VS docking), instead of a
// fixed "Inspector / Inspector 2" pair. Each registered panel KIND has a factory (makes a fresh panel
// instance — its own lock/folder/etc. state) and a draw delegate. Open(kind) spawns a new instance
// with a unique ImGui id; closing its window (the X) removes it. The host owns the list and the ids.
//
// A kind can be marked SINGLETON (Scene / Game views back onto the one renderer target, so they must
// not be duplicated) — Open() on a singleton just re-opens the single instance.
internal sealed class DockPanelHost {
    sealed class Kind {
        public string Title;            // display title (also the window label base)
        public string Icon;
        public bool Singleton;
        public Func<object> Factory;    // makes a fresh panel-state object
        public Action<object> Draw;     // draws that object's contents
    }

    sealed class Instance {
        public string KindKey;
        public int Id;                  // unique within the kind
        public object Panel;
        public bool Open = true;
        public bool JustOpened = true;  // size + center it on its first frame (else it opens tiny)
    }

    readonly Dictionary<string, Kind> kinds = new();
    readonly List<Instance> instances = new();
    int nextId = 1;          // ids for EXTRA instances; the first instance of a kind uses id 0

    // Called per-instance after Begin so the host can run the tab strip's maximize / add-tab menu.
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

    // Opens a new instance of the kind (or focuses the existing one for singletons / when the kind has
    // a live instance and forceNew is false).
    public void Open(string key, bool forceNew = true) {
        if (!kinds.TryGetValue(key, out Kind kind)) return;

        if (kind.Singleton || !forceNew) {
            foreach (Instance existing in instances)
                if (existing.KindKey == key) { existing.Open = true; return; }
        }
        // The host only ever holds EXTRA instances — the primary (id-0, "###Inspector") panel is a
        // field on EditorApplication and matches the default-layout dock builder. So host ids always
        // start at 1 ("Inspector 2###Inspector_2") and can't collide with the primary window.
        instances.Add(new Instance { KindKey = key, Id = nextId++, Panel = kind.Factory() });
    }

    // Ensures at least one instance of a kind exists (used to seed the default layout).
    public void EnsureOne(string key) {
        if (CountOf(key) == 0) Open(key, forceNew: true);
    }

    // The window label for an instance. The ### id is the KIND KEY for the first instance (id 0) so it
    // matches EditorLayout.BuildDefault's DockBuilderDockWindow targets; extra instances append _id.
    // The visible part shows the icon + title (+ a number for extras) and can repeat across instances.
    static string Label(Kind kind, Instance inst) {
        if (inst.Id == 0)
            return $"{kind.Icon}  {kind.Title}###{inst.KindKey}";
        return $"{kind.Icon}  {kind.Title} {inst.Id}###{inst.KindKey}_{inst.Id}";
    }

    public void DrawAll() {
        // Snapshot — Draw can mutate selection but not the instance list; closing is handled here.
        for (int idx = 0; idx < instances.Count; idx++) {
            Instance inst = instances[idx];
            if (!kinds.TryGetValue(inst.KindKey, out Kind kind)) { inst.Open = false; continue; }

            string label = Label(kind, inst);
            // A freshly-opened, UNDOCKED window opens tiny by default — give it a sensible size and
            // center it on the viewport for its first frame (the user then docks/resizes as they like).
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

    // The label of the FIRST instance of a kind (for the default-layout dock builder, which targets
    // windows by name). Null if none open.
    public string FirstLabel(string key) {
        foreach (Instance i in instances)
            if (i.KindKey == key && kinds.TryGetValue(key, out Kind k))
                return Label(k, i);
        return null;
    }

    // True if `label` belongs to one of this host's (extra-instance) windows. Used by the fullscreen
    // router to recognise a maximized duplicated tab, which the primary-name switch can't match.
    public bool OwnsLabel(string label) {
        foreach (Instance i in instances)
            if (kinds.TryGetValue(i.KindKey, out Kind k) && Label(k, i) == label)
                return true;
        return false;
    }

    // Draws the host instance identified by `label` as a single fixed window filling pos/size, so a
    // duplicated panel can be shown fullscreen exactly like a primary one. `runStrip` runs the title
    // double-click/right-click handler (so the maximized window can be restored). EF9a: threads a
    // `ref open` so the window's X button is drawn + HONORED while maximized — clicking it flips the
    // instance's Open flag (the next DrawAll removes the instance) and returns true so the caller can
    // exit fullscreen the same frame (no stuck-maximized panel). Returns false if not found / not closed.
    public bool DrawMaximizedInstance(string label, System.Numerics.Vector2 pos, System.Numerics.Vector2 size,
        Action<string> runStrip) {
        foreach (Instance inst in instances) {
            if (!kinds.TryGetValue(inst.KindKey, out Kind kind) || Label(kind, inst) != label)
                continue;
            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
            bool open = inst.Open;
            if (ImGui.Begin(label, ref open, flags)) {
                runStrip?.Invoke(label);
                kind.Draw(inst.Panel);
            }
            ImGui.End();
            inst.Open = open;
            return !open;   // closed this frame → caller exits fullscreen
        }
        return false;
    }
}
