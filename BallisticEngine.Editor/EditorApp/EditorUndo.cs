using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Snapshot-based undo/redo: every undoable interaction pushes the whole scene as YAML
// BEFORE mutating it (cheap at editor scene sizes). Ctrl+Z restores; Ctrl+Y re-applies.
// Disabled while playing (play mode has its own snapshot/restore).
internal static class EditorUndo {
    const int Capacity = 64;

    static readonly List<string> undo = new();
    static readonly List<string> redo = new();

    public static void Push() {
        if (SceneManager.IsPlaying)
            return;

        undo.Add(SceneSerializer.Serialize(SceneManager.GetCurrentScene()));
        if (undo.Count > Capacity)
            undo.RemoveAt(0);
        redo.Clear();
    }

    public static void Undo() {
        if (SceneManager.IsPlaying || undo.Count == 0)
            return;

        redo.Add(SceneSerializer.Serialize(SceneManager.GetCurrentScene()));
        Restore(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
    }

    public static void Redo() {
        if (SceneManager.IsPlaying || redo.Count == 0)
            return;

        undo.Add(SceneSerializer.Serialize(SceneManager.GetCurrentScene()));
        Restore(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
    }

    static void Restore(string yaml) {
        Scene scene = SceneManager.GetCurrentScene();
        scene.Clear();
        SceneSerializer.Deserialize(yaml);
        // Selection pointing at replaced entities is cleared by EditorState.ClearIfDestroyed.
    }
}
