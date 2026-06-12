using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Named snapshot-based undo/redo (Unity-style): every undoable interaction pushes the whole scene
// as YAML BEFORE mutating it, tagged with a human-readable action label so the editor can show
// "Undo Move", "Redo Add Component", a history list, etc. Cheap at editor scene sizes. Ctrl+Z
// restores; Ctrl+Y re-applies. Disabled while playing (play mode has its own snapshot/restore).
internal static class EditorUndo {
    const int Capacity = 100;

    // An undo entry is EITHER a whole-scene YAML snapshot (structural changes: add/remove/reparent) OR
    // a single-entity scoped snapshot (a value/component edit on one entity). The scoped form restores
    // JUST that entity in place, so undoing a tweak doesn't tear down + rebuild every scene-wide
    // component (which re-fired IrradianceVolume bakes and dropped the selection — bugs 2a/7).
    readonly record struct Entry(string Label, string Yaml, Guid EntityId, EntityDocument EntityDoc,
        Action ApplyOld, Action ApplyNew) {
        public bool IsScoped => EntityDoc is not null;
        public bool IsCallback => ApplyOld is not null;
    }

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

    // Snapshot a SINGLE entity (its components + transform) before a value edit on it. Undoing this
    // restores just that entity in place — no whole-scene rebuild, so unrelated scene components
    // (IrradianceVolume bakes!) aren't disturbed and the selection survives. Falls back to a full
    // scene snapshot if the entity is null. Call BEFORE mutating.
    public static void PushEntity(string label, Entity entity) {
        if (SceneManager.IsPlaying)
            return;
        if (entity is null) { Push(label); return; }

        EntityDocument doc = SceneSerializer.CaptureEntity(entity);
        AddEntry(new Entry(label, null, entity.InstanceId, doc, null, null));
    }

    // Push a CALLBACK undo step for edits that aren't scene/entity data — e.g. a volume PROFILE (a
    // .volume asset). `applyOld` reverts to the state BEFORE the change (undo); `applyNew` re-applies
    // the change (redo). The caller captures both snapshots around the edit.
    public static void PushCallback(string label, Action applyOld, Action applyNew) {
        if (SceneManager.IsPlaying || applyOld is null || applyNew is null)
            return;
        AddEntry(new Entry(label, null, Guid.Empty, null, applyOld, applyNew));
    }

    // Commit a PRE-CAPTURED snapshot under `label`. Use this for the deferred-commit pattern
    // (InspectorUndo.Track): the snapshot was taken when an edit BEGAN — before any value changed —
    // and is committed only when the edit FINISHES with a real change, so one drag / typing session
    // produces exactly one undo entry and aborted no-change edits produce none.
    public static void PushSnapshot(string label, string yaml) {
        if (SceneManager.IsPlaying)
            return;
        AddEntry(new Entry(label, yaml, Guid.Empty, null, null, null));
    }

    // Commit a PRE-CAPTURED single-entity snapshot (deferred-commit pattern from InspectorUndo.Track:
    // the doc was captured when the edit BEGAN — before the value changed — and committed when it
    // FINISHED with a real change). Scoped restore, like PushEntity.
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
        redo.Add(Inverse(entry));   // capture the CURRENT state (same scope) for redo
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

    // The opposite-direction entry: captures the CURRENT state in the same scope as `entry`, so
    // undo<->redo round-trip exactly. A scoped entry whose entity vanished degrades to a full snapshot.
    static Entry Inverse(Entry entry) {
        // Callback: swap the two directions, so undo->redo->undo keeps round-tripping.
        if (entry.IsCallback)
            return new Entry(entry.Label, null, Guid.Empty, null, entry.ApplyNew, entry.ApplyOld);
        if (entry.IsScoped && FindEntity(entry.EntityId) is { } live)
            return new Entry(entry.Label, null, entry.EntityId, SceneSerializer.CaptureEntity(live), null, null);
        return new Entry(entry.Label, SceneSerializer.Serialize(SceneManager.GetCurrentScene()), Guid.Empty, null, null, null);
    }

    // Applies an entry: a scoped one restores just its entity in place (selection + scene-wide
    // components untouched); a full one rebuilds the whole scene. A scoped entry whose entity is gone
    // is a no-op (nothing to restore in place — a value edit can't have outlived its entity's deletion,
    // which would itself have been a full-snapshot undo step).
    static void Apply(Entry entry) {
        if (entry.IsCallback) {
            entry.ApplyOld();   // revert to the captured state (Inverse swaps directions for redo)
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
