using BallisticEngine.Serialization;
using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

// ONE choke point for every editor mutation (Phase F / F1 == Phase D / D1). The goal is to turn
// undo from MUST-REMEMBER (94 scattered manual EditorUndo.Push() calls that each call site has to
// remember) into CAN'T-FORGET: a call site states its INTENT (structural / per-entity / scene-wide /
// asset) and EditorCommands snapshots BEFORE applying, automatically picking the right undo scope.
// (Named EditorCommands, not EditorActions -- that name is already the input-action Id table in
// EditorInputRouter.cs; "commands" also matches the F1==D1 command-layer / command-registry language.)
//
// This is the human/gizmo/remote/agent-shared command surface: the same EditEntity / Structural the
// editor UI calls is what a remote handler or an AI command will call, so undo + remote parity + agent
// invocation are ONE path BY CONSTRUCTION (the F1==D1 interlock).
//
// MIGRATION CONTRACT (no big-bang): this layer COEXISTS with the remaining manual EditorUndo.Push()
// calls. It does nothing more than wrap the existing EditorUndo entry points, so the undo output is
// byte-identical to the hand-rolled "Push(); mutate();" it replaces -- a call site just collapses to
// one line. Call sites are migrated ONE AT A TIME, each guarded by the F3 undo-coverage harness.
//
// Scope -> EditorUndo path mapping (the choice that USED to live in every call site's head):
//   Structural  -> Push          (whole-scene YAML snapshot: add/remove/reparent/delete/create)
//   EditEntity  -> PushEntity     (scoped single entity: value/component edit, selection survives,
//                                  no IrradianceVolume re-bake -- the preferred path for one-entity edits)
//   EditScene   -> Push          (scene-wide edit; today there is no scene-scoped snapshot path, so it
//                                  routes through the same whole-scene Push -- a distinct INTENT name so
//                                  the call site reads right and a future scene-scoped path is a 1-line swap)
//   EditAsset   -> PushCallback   (non-scene asset edit -- material / volume profile / curve; the caller
//                                  supplies the before/after revert pair, same as today's callback undo)
//
// All methods no-op the snapshot while playing (EditorUndo itself guards SceneManager.IsPlaying) and
// still run the mutation, exactly like the manual pattern.
//
// DEFERRED-COMMIT (the inspector-widget path, F1 chunk 33): the four methods above snapshot the
// instant they are called -- right for discrete actions, but a slider/drag/typing session emits its
// `changed` result EVERY frame, so a naive Push() per frame would spam the undo stack. TrackEdit owns
// the activation-state machine that collapses one drag into exactly one entry, and it routes through
// the SAME Structural/EditEntity scope choice (scopeEntity null -> whole-scene snapshot; non-null ->
// scoped entity snapshot), so the inspector's per-widget undo and the discrete-action undo are now ONE
// choke point. (Was InspectorUndo.Track -- relocated here so EditorCommands is the only undo entry
// point; InspectorUndo now forwards to keep its call sites byte-identical.)
internal static class EditorCommands {
    // A discrete structural change to the scene graph (add/remove/reparent/delete/create/group). Takes
    // a whole-scene snapshot first, then applies. Equivalent to "EditorUndo.Push(label); mutate();".
    public static void Structural(string label, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.Push(label);
        mutate();
    }

    // A value / component edit on ONE entity. Scoped snapshot (PushEntity) so undo restores just that
    // entity in place -- the selection and scene-wide components (IrradianceVolume bakes) are left
    // alone. Falls back to a full snapshot when the entity is null (PushEntity already handles that).
    // Equivalent to "EditorUndo.PushEntity(label, entity); mutate();".
    public static void EditEntity(Entity entity, string label, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.PushEntity(label, entity);
        mutate();
    }

    // A scene-wide edit (scene-behaviour / lighting settings). Snapshots the whole scene, like
    // Structural, but named for intent so call sites and a future scene-scoped path stay clear.
    // Equivalent to "EditorUndo.Push(label); mutate();".
    public static void EditScene(string label, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.Push(label);
        mutate();
    }

    // A non-scene ASSET edit (material / mesh / terrain / volume profile / curve). The caller captures
    // the before/after state and supplies the revert (applyOld) + re-apply (applyNew) callbacks, then
    // EditAsset records the undo step and runs the mutation. Equivalent to
    // "EditorUndo.PushCallback(label, applyOld, applyNew); mutate();". F2 will move the remaining
    // direct-save asset edits onto this path; F1 only establishes it.
    public static void EditAsset(string label, Action applyOld, Action applyNew, Action mutate) {
        if (mutate is null)
            return;
        EditorUndo.PushCallback(label, applyOld, applyNew);
        mutate();
    }

    // ---- Deferred-commit (inspector widgets) ----------------------------------------------------
    //
    // A single static pending slot is enough because ImGui edits one item at a time, so the per-axis
    // TrackEdit calls in a Vector3 row don't race (only one is active at a time).
    static string pendingYaml;
    static string pendingLabel;
    static Entity pendingEntity;
    static bool pendingScoped;
    static EntityDocument pendingDoc;

    // Per-WIDGET edit with deferred commit. Call immediately AFTER the widget and BEFORE applying its new
    // value, every frame; returns `changed` unchanged so call sites read naturally. Maps onto ImGui's
    // per-item activation state to emit exactly one undo entry per drag / typing session, none for
    // aborted no-change edits:
    //   IsItemActivated()            -> the edit BEGAN this frame: snapshot NOW (still the OLD value,
    //                                   since TrackEdit runs before the value is applied) into the slot.
    //   IsItemDeactivatedAfterEdit() -> the edit FINISHED with a real change: commit the snapshot.
    //   IsItemDeactivated()          -> the edit finished with NO change (hover/abort): drop it.
    // Instantaneous widgets (checkbox/combo/color swatch) fire activate + deactivated-after-edit on the
    // same frame, so they get exactly one entry too. `scopeEntity` picks the scope EXACTLY like the
    // synchronous methods: non-null -> scoped entity snapshot (EditEntity-equivalent: undo restores just
    // that entity, selection survives, no IrradianceVolume re-bake); null -> whole-scene snapshot
    // (Structural-equivalent, for multi-selection broadcasts / scene-behaviour / asset edits).
    public static bool TrackEdit(string label, Entity scopeEntity, bool changed) {
        if (ImGui.IsItemActivated()) {
            pendingLabel = label;
            pendingEntity = scopeEntity;
            pendingScoped = scopeEntity is not null;
            // Scoped: capture just the entity (cheap, side-effect-free undo). Otherwise the whole scene.
            pendingYaml = pendingScoped ? null : SceneSerializer.Serialize(SceneManager.GetCurrentScene());
            if (pendingScoped)
                pendingDoc = SceneSerializer.CaptureEntity(scopeEntity);
        }

        if (ImGui.IsItemDeactivatedAfterEdit()) {
            if (pendingScoped && pendingDoc is not null)
                EditorUndo.PushEntitySnapshot(pendingLabel, pendingEntity, pendingDoc);
            else if (pendingYaml is not null)
                EditorUndo.PushSnapshot(pendingLabel, pendingYaml);
            ClearPending();
        }
        else if (ImGui.IsItemDeactivated()) {
            ClearPending(); // aborted / no net change
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
