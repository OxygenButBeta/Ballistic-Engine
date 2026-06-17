namespace BallisticEngine.Editor;

// Inspector-widget undo facade. The deferred-commit state machine + scope->snapshot mapping now lives in
// EditorCommands.TrackEdit (F1 chunk 33: one undo choke point for synchronous AND per-widget edits); this
// type stays only to keep the inspector's call sites unchanged -- ScopeEntity is the slot InspectorPanel
// sets around a single-entity draw, and Track forwards (label, scope, changed) to EditorCommands. Undo
// output is byte-identical to the old inline machine: same activation-state transitions, same scoped vs
// whole-scene choice, same one-entry-per-drag.
internal static class InspectorUndo {
    // The entity currently being drawn in the inspector. Set by InspectorPanel before drawing an
    // entity's members so a member edit snapshots JUST that entity (scoped undo: undoing a value tweak
    // doesn't rebuild the whole scene -> no IrradianceVolume re-bake, selection survives). Null for
    // multi-selection broadcasts / scene-behaviour / asset edits, which fall back to a full-scene snapshot.
    public static Entity ScopeEntity { get; set; }

    // Wrap a widget's `changed` result. Returns it unchanged so call sites read naturally:
    //   bool changed = InspectorUndo.Track("Edit Speed", ImGui.DragFloat("##v", ref f, 0.05f));
    public static bool Track(string label, bool changed) => EditorCommands.TrackEdit(label, ScopeEntity, changed);
}
