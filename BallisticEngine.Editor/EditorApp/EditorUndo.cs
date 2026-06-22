using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

internal static class EditorUndo {
    const int Capacity = 100;

    readonly record struct Entry(string Label, string Yaml, Guid EntityId, EntityDocument EntityDoc,
        Action ApplyOld, Action ApplyNew) {
        public bool IsScoped => EntityDoc is not null;
        public bool IsCallback => ApplyOld is not null;
    }

    static readonly List<Entry> undo = new();
    static readonly List<Entry> redo = new();

    public static Func<object> CaptureSelection;
    public static Action<object> RestoreSelection;

    public static bool IsDirty { get; private set; }

    public static void MarkClean() => IsDirty = false;
    public static void MarkDirty() => IsDirty = true;

    public static bool CanUndo => undo.Count > 0 && !SceneManager.IsPlaying;
    public static bool CanRedo => redo.Count > 0 && !SceneManager.IsPlaying;

    public static string UndoLabel => undo.Count > 0 ? undo[^1].Label : "";
    public static string RedoLabel => redo.Count > 0 ? redo[^1].Label : "";

    public static IEnumerable<string> History() {
        for (var i = undo.Count - 1; i >= 0; i--)
            yield return undo[i].Label;
    }

    public static int Count => undo.Count;

    public static void Clear() {
        undo.Clear();
        redo.Clear();
        IsDirty = false;
    }

    public static void Push() => Push("Edit");

    public static void Push(string label) {
        if (SceneManager.IsPlaying)
            return;

        PushSnapshot(label, SceneSerializer.Serialize(SceneManager.GetCurrentScene()));
    }

    public static void PushEntity(string label, Entity entity) {
        if (SceneManager.IsPlaying)
            return;
        if (entity is null) { Push(label); return; }

        EntityDocument doc = SceneSerializer.CaptureEntity(entity);
        AddEntry(new Entry(label, null, entity.InstanceId, doc, null, null));
    }

    public static void PushCallback(string label, Action applyOld, Action applyNew) {
        if (SceneManager.IsPlaying || applyOld is null || applyNew is null)
            return;
        AddEntry(new Entry(label, null, Guid.Empty, null, applyOld, applyNew));
    }

    public static void PushSnapshot(string label, string yaml) {
        if (SceneManager.IsPlaying)
            return;
        AddEntry(new Entry(label, yaml, Guid.Empty, null, null, null));
    }

    public static void PushEntitySnapshot(string label, Entity entity, EntityDocument doc) {
        if (SceneManager.IsPlaying || entity is null || doc is null)
            return;
        AddEntry(new Entry(label, null, entity.InstanceId, doc, null, null));
    }

    static void AddEntry(Entry entry) {
        undo.Add(entry);
        if (undo.Count > Capacity)
            undo.RemoveAt(0);
        redo.Clear();
        IsDirty = true;
    }

    public static void Undo() {
        if (!CanUndo)
            return;

        Entry entry = undo[^1];
        redo.Add(Inverse(entry));
        Apply(entry);
        undo.RemoveAt(undo.Count - 1);
        IsDirty = true;
    }

    public static void Redo() {
        if (!CanRedo)
            return;

        Entry entry = redo[^1];
        undo.Add(Inverse(entry));
        Apply(entry);
        redo.RemoveAt(redo.Count - 1);
        IsDirty = true;
    }

    static Entry Inverse(Entry entry) {
        if (entry.IsCallback)
            return new Entry(entry.Label, null, Guid.Empty, null, entry.ApplyNew, entry.ApplyOld);
        if (entry.IsScoped && FindEntity(entry.EntityId) is { } live)
            return new Entry(entry.Label, null, entry.EntityId, SceneSerializer.CaptureEntity(live), null, null);
        return new Entry(entry.Label, SceneSerializer.Serialize(SceneManager.GetCurrentScene()), Guid.Empty, null, null, null);
    }

    static void Apply(Entry entry) {
        if (entry.IsCallback) {
            entry.ApplyOld();
            return;
        }
        if (entry.IsScoped) {
            if (FindEntity(entry.EntityId) is { } target) {
                object token = CaptureSelection?.Invoke();
                SceneSerializer.RestoreEntityInPlace(target, entry.EntityDoc);
                RestoreSelection?.Invoke(token);
            }
            return;
        }
        Restore(entry.Yaml);
    }

    static Entity FindEntity(Guid id) {
        foreach (Entity e in SceneManager.GetCurrentScene().Entities)
            if (e.InstanceId == id) return e;
        return null;
    }

    public static void UndoTo(int historyIndex) {
        int steps = historyIndex + 1;
        for (var i = 0; i < steps && CanUndo; i++)
            Undo();
    }

    static void Restore(string yaml) {
        object token = CaptureSelection?.Invoke();

        Scene scene = SceneManager.GetCurrentScene();
        scene.Clear();
        SceneSerializer.Deserialize(yaml);

        RestoreSelection?.Invoke(token);
    }
}
