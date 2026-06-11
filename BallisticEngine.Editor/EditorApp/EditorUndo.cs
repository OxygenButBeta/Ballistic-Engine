using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Named snapshot-based undo/redo (Unity-style): every undoable interaction pushes the whole scene
// as YAML BEFORE mutating it, tagged with a human-readable action label so the editor can show
// "Undo Move", "Redo Add Component", a history list, etc. Cheap at editor scene sizes. Ctrl+Z
// restores; Ctrl+Y re-applies. Disabled while playing (play mode has its own snapshot/restore).
internal static class EditorUndo {
    const int Capacity = 100;

    readonly record struct Entry(string Label, string Yaml);

    static readonly List<Entry> undo = new();
    static readonly List<Entry> redo = new();

    // Restore re-creates every entity as a FRESH object (deserialize), so a raw selection pointer
    // would dangle and the inspector would blank on every Ctrl+Z. To keep Unity's "the same thing
    // stays selected after undo" feel, the editor registers these two hooks at bootstrap: capture
    // the current selection as a stable token (entity InstanceId / scene-behaviour type+index)
    // before the scene is rebuilt, then re-select the equivalent live object afterwards. Kept as
    // delegates so EditorUndo (engine-agnostic) doesn't depend on EditorState.
    public static Func<object> CaptureSelection;
    public static Action<object> RestoreSelection;

    // Set whenever the scene is mutated through an undoable action; cleared on save/load. Drives the
    // "*" in the title and the unsaved-changes prompt before New/Open/Exit.
    public static bool IsDirty { get; private set; }

    public static void MarkClean() => IsDirty = false;
    public static void MarkDirty() => IsDirty = true;

    public static bool CanUndo => undo.Count > 0 && !SceneManager.IsPlaying;
    public static bool CanRedo => redo.Count > 0 && !SceneManager.IsPlaying;

    // Labels of the next undo/redo action (for buttons/menu/tooltips). Empty when nothing to do.
    public static string UndoLabel => undo.Count > 0 ? undo[^1].Label : "";
    public static string RedoLabel => redo.Count > 0 ? redo[^1].Label : "";

    // Most-recent-first labels for the history dropdown (newest undo step first).
    public static IEnumerable<string> History() {
        for (var i = undo.Count - 1; i >= 0; i--)
            yield return undo[i].Label;
    }

    public static int Count => undo.Count;

    // Resets undo/redo history (e.g. after loading a different scene).
    public static void Clear() {
        undo.Clear();
        redo.Clear();
        IsDirty = false;
    }

    public static void Push() => Push("Edit");

    // Snapshot the scene under `label`, to be restored by the next Undo. Call BEFORE mutating.
    // Use this for DISCRETE actions (add/remove component, reparent, delete, gizmo drag start) where
    // the snapshot is naturally taken just before the change.
    public static void Push(string label) {
        if (SceneManager.IsPlaying)
            return;

        PushSnapshot(label, SceneSerializer.Serialize(SceneManager.GetCurrentScene()));
    }

    // Commit a PRE-CAPTURED snapshot under `label`. Use this for the deferred-commit pattern
    // (InspectorUndo.Track): the snapshot was taken when an edit BEGAN — before any value changed —
    // and is committed only when the edit FINISHES with a real change, so one drag / typing session
    // produces exactly one undo entry and aborted no-change edits produce none.
    public static void PushSnapshot(string label, string yaml) {
        if (SceneManager.IsPlaying)
            return;

        undo.Add(new Entry(label, yaml));
        if (undo.Count > Capacity)
            undo.RemoveAt(0);
        redo.Clear();
        IsDirty = true;
    }

    public static void Undo() {
        if (!CanUndo)
            return;

        Entry entry = undo[^1];
        redo.Add(new Entry(entry.Label, SceneSerializer.Serialize(SceneManager.GetCurrentScene())));
        Restore(entry.Yaml);
        undo.RemoveAt(undo.Count - 1);
        IsDirty = true;
    }

    public static void Redo() {
        if (!CanRedo)
            return;

        Entry entry = redo[^1];
        undo.Add(new Entry(entry.Label, SceneSerializer.Serialize(SceneManager.GetCurrentScene())));
        Restore(entry.Yaml);
        redo.RemoveAt(redo.Count - 1);
        IsDirty = true;
    }

    // Undo repeatedly back to a given history index (0 = newest). Used by the history dropdown.
    public static void UndoTo(int historyIndex) {
        int steps = undo.Count - historyIndex;
        for (var i = 0; i < steps && CanUndo; i++)
            Undo();
    }

    static void Restore(string yaml) {
        // Capture the selection as a stable token BEFORE the live objects are destroyed, restore the
        // equivalent live object AFTER the scene is rebuilt — InstanceIds round-trip through the YAML
        // (SceneSerializer), so the same entity stays selected and the inspector doesn't blank.
        object token = CaptureSelection?.Invoke();

        Scene scene = SceneManager.GetCurrentScene();
        scene.Clear();
        SceneSerializer.Deserialize(yaml);

        RestoreSelection?.Invoke(token);
    }
}
