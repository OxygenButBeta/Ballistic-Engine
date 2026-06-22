using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

internal static class EditorCommands {
    static IEditorGui gui => EditorGui.Shared;

    public static void Structural(string label, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.Push(label);
        mutate();
    }

    public static void EditEntity(Entity entity, string label, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.PushEntity(label, entity);
        mutate();
    }

    public static void EditScene(string label, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.Push(label);
        mutate();
    }

    public static void EditAsset(string label, Action applyOld, Action applyNew, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.PushCallback(label, applyOld, applyNew);
        mutate();
    }

    static string pendingYaml;
    static string pendingLabel;
    static Entity pendingEntity;
    static bool pendingScoped;
    static EntityDocument pendingDoc;

    public static bool TrackEdit(string label, Entity scopeEntity, bool changed) {
        if (gui.IsItemActivated()) {
            pendingLabel = label;
            pendingEntity = scopeEntity;
            pendingScoped = scopeEntity is not null;
            pendingYaml = pendingScoped ? null : SceneSerializer.Serialize(SceneManager.GetCurrentScene());
            if (pendingScoped)
                pendingDoc = SceneSerializer.CaptureEntity(scopeEntity);
        }

        if (gui.IsItemDeactivatedAfterEdit()) {
            if (pendingScoped && pendingDoc is not null)
                EditorUndo.PushEntitySnapshot(pendingLabel, pendingEntity, pendingDoc);
            else if (pendingYaml is not null)
                EditorUndo.PushSnapshot(pendingLabel, pendingYaml);
            ClearPending();
        }
        else if (gui.IsItemDeactivated()) {
            ClearPending();
        }

        return changed;
    }

    static void ClearPending() {
        pendingYaml = null;
        pendingDoc = null;
        pendingEntity = null;
        pendingScoped = false;
    }
}
