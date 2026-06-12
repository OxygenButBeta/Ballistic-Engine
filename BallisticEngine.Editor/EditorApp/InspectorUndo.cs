using BallisticEngine.Serialization;
using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

// Centralized, can't-forget auto-undo for inspector widgets (Unity-style: every property edit
// registers itself, exactly once per logical change). Call Track(...) immediately AFTER a widget
// and BEFORE applying its new value, every frame. It uses ImGui's per-item activation state:
//
//   IsItemActivated()            -> an edit BEGAN this frame: snapshot the scene NOW (still the OLD
//                                   value, since Track runs before SetValue) into a PENDING buffer.
//   IsItemDeactivatedAfterEdit() -> the edit FINISHED with a real change: commit the pending
//                                   snapshot to the undo stack as one entry.
//   IsItemDeactivated()          -> edit finished with NO change (hover/abort): drop the pending.
//
// This yields exactly one undo entry per drag or typing session, none for aborted no-change edits,
// and one for instantaneous widgets (checkbox/combo/color swatch fire activate + deactivated-after-
// edit on the same frame). A single static pending slot is enough because ImGui edits one item at a
// time, so the per-axis Track calls in a Vector3 row don't race.
internal static class InspectorUndo {
    static string pendingYaml;
    static string pendingLabel;
    static Entity pendingEntity;
    static bool pendingScoped;

    // The entity currently being drawn in the inspector. Set by InspectorPanel before drawing an
    // entity's members so a member edit snapshots JUST that entity (scoped undo: undoing a value tweak
    // doesn't rebuild the whole scene → no IrradianceVolume re-bake, selection survives). Null for
    // scene-behaviour / asset edits, which fall back to a full-scene snapshot.
    public static Entity ScopeEntity { get; set; }

    // Wrap a widget's `changed` result. Returns it unchanged so call sites read naturally:
    //   bool changed = InspectorUndo.Track("Edit Speed", ImGui.DragFloat("##v", ref f, 0.05f));
    public static bool Track(string label, bool changed) {
        if (ImGui.IsItemActivated()) {
            pendingLabel = label;
            pendingEntity = ScopeEntity;
            pendingScoped = ScopeEntity is not null;
            // Scoped: capture just the entity (cheap, side-effect-free undo). Otherwise the whole scene.
            pendingYaml = pendingScoped ? null : SceneSerializer.Serialize(SceneManager.GetCurrentScene());
            if (pendingScoped)
                pendingDoc = SceneSerializer.CaptureEntity(ScopeEntity);
        }

        if (ImGui.IsItemDeactivatedAfterEdit()) {
            if (pendingScoped && pendingDoc is not null)
                EditorUndo.PushEntitySnapshot(pendingLabel, pendingEntity, pendingDoc);
            else if (pendingYaml is not null)
                EditorUndo.PushSnapshot(pendingLabel, pendingYaml);
            Clear();
        }
        else if (ImGui.IsItemDeactivated()) {
            Clear(); // aborted / no net change
        }

        return changed;
    }

    static BallisticEngine.Serialization.EntityDocument pendingDoc;

    static void Clear() {
        pendingYaml = null;
        pendingDoc = null;
        pendingEntity = null;
        pendingScoped = false;
    }
}
