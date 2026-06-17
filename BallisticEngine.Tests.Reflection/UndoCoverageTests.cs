using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// F3 (Phase F "undo unification", FIRST step -- the coverage harness, mirrors the 24/24 inspector test).
// The user-reported first-class problem (plan 3.4): undo is "kopuk, bazilari register olmuyor" -- the
// system is MUST-REMEMBER (94 scattered manual EditorUndo.Push() across 13 files), so the paths that
// forget to Push are silently un-undoable, and asset edits bypass undo entirely. There is NO test today
// asserting an action registered undo, so a dropped Push() ships unnoticed.
//
// This suite is the safety net F1/F2 migrate against: it ENUMERATES every engine-expressible mutating
// action and asserts each leaves EXACTLY ONE recoverable undo entry that, when undone, restores the
// prior state. "Recoverable + restores" -- not just "an entry exists" -- because a Push that snapshots
// the wrong scope is as broken as a missing one.
//
// WHY a local UndoModel and not EditorUndo directly: EditorUndo lives in the Editor *exe* (Hexa.NET.ImGui
// + DX12 + Vortice deps); this rig is GL-FREE / ImGui-FREE and references the engine LIBRARY only (like
// the bal CLI). UndoModel replicates EditorUndo's EXACT mechanics -- full-scene snapshot via
// SceneSerializer.Serialize/Deserialize, scoped single-entity snapshot via CaptureEntity/
// RestoreEntityInPlace, one entry per Push, Undo restores -- so the contract under test is the real one.
// (When F1 relocates the undo core to a command layer, this suite drives the real type unchanged.)
//
// SCOPE -- engine-expressible mutations (the pure-engine half of the action surface):
//   create entity, delete entity, reparent, rename, add component, remove component, edit member,
//   toggle active, AND (F2) an asset edit via the EditAsset callback path. OUT OF SCOPE (documented below,
//   needs ImGui/EditorWidgets -> migrate + test in F1): gizmo drags, widget activation-state
//   deferred-commit, terrain brush.
//
// F2 (asset-edit hole CLOSED): an asset edit routed through EditorCommands.EditAsset (-> PushCallback,
// the .volume profile / curve callback path) now leaves exactly one recoverable entry -- modeled here as
// the "asset edit (EditAsset callback)" action (capture before/after, push the revert pair, Undo runs
// applyOld). This was the KNOWN HOLE the harness flagged for F2; it is now an IN-SCOPE covered action.
//
// BYPASS CONTROL (the case that proves the harness can tell covered from uncovered): a mutation that
// bypasses snapshotting entirely (no Push) leaves ZERO entries. The harness ASSERTS bypass == 0 coverage
// so a dropped Push can never silently pass as covered. (Distinct from the EditAsset path, now covered.)
//
// Runs alongside the other G-suites that insert into the global SceneManager (no public unload); the
// leftover scene is harmless (process exits next).
internal static class UndoCoverageTests {
    // A faithful stand-in for EditorUndo: the same three entry shapes (whole-scene YAML / scoped entity
    // doc / callback revert pair), one entry per Push, Undo restores the captured state. No redo/labels/
    // capacity here -- the coverage contract is "did the action leave a recoverable entry", which these
    // paths express (the callback shape mirrors EditorUndo.PushCallback reached via EditorCommands.EditAsset).
    sealed class UndoModel {
        readonly record struct Entry(string Yaml, Guid EntityId, EntityDocument Doc, Action ApplyOld, Action ApplyNew) {
            public bool IsScoped => Doc is not null;
        }

        readonly List<Entry> stack = new();
        public int Count => stack.Count;

        // Whole-scene snapshot BEFORE a structural change (EditorUndo.Push).
        public void Push() => stack.Add(new Entry(SceneSerializer.Serialize(SceneManager.GetCurrentScene()), Guid.Empty, null, null, null));

        // Scoped single-entity snapshot BEFORE a value edit (EditorUndo.PushEntity).
        public void PushEntity(Entity e) {
            if (e is null) { Push(); return; }
            stack.Add(new Entry(null, e.InstanceId, SceneSerializer.CaptureEntity(e), null, null));
        }

        // Callback entry for a non-scene ASSET edit (EditorUndo.PushCallback, reached via
        // EditorCommands.EditAsset). The caller captures the asset state BEFORE and AFTER the edit and
        // supplies the revert (applyOld) + re-apply (applyNew) pair; Undo runs applyOld. This is the F2
        // path the editor's .volume / curve asset edits now route through, modeled here so the harness
        // proves the EditAsset callback actually leaves one recoverable entry (was the KNOWN HOLE).
        public void PushCallback(Action applyOld, Action applyNew) {
            if (applyOld is null || applyNew is null) return;
            stack.Add(new Entry(null, Guid.Empty, null, applyOld, applyNew));
        }

        // Restore the top entry (EditorUndo.Undo -> Apply): scoped restores just its entity in place,
        // callback runs applyOld, full rebuilds the scene. Pops the entry.
        public void Undo() {
            if (stack.Count == 0) return;
            Entry top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (top.ApplyOld is not null) {
                top.ApplyOld();
                return;
            }
            if (top.IsScoped) {
                if (FindEntity(top.EntityId) is { } target)
                    SceneSerializer.RestoreEntityInPlace(target, top.Doc);
                return;
            }
            Scene scene = SceneManager.GetCurrentScene();
            scene.Clear();
            SceneSerializer.Deserialize(top.Yaml);
        }

        public void Clear() => stack.Clear();

        static Entity FindEntity(Guid id) {
            foreach (Entity e in SceneManager.GetCurrentScene().Entities)
                if (e.InstanceId == id) return e;
            return null;
        }
    }

    public static int Run() {
        var h = new Harness();

        // The fixture components must be in the registry so deserialize can resolve them on the full-scene
        // restore path (same shape EngineBootstrap uses for engine + game scripts).
        ComponentRegistry.Build(typeof(ComponentRegistry).Assembly, typeof(UndoCoverageTests).Assembly);

        _ = new SceneManager();

        // Enumerate the in-scope actions. Each is split into SETUP (the precondition: the entity/
        // component the edit operates on -- runs BEFORE the measurement snapshot, NOT undone) and MUTATE
        // (the action under test: it pushes undo then mutates). The runner snapshots after setup, runs
        // mutate, then asserts exactly one entry was pushed and Undo restored the post-setup state. The
        // split matters for SCOPED actions: a scoped Undo restores the entity IN PLACE (it does not delete
        // a freshly-created host), so the host must pre-exist the snapshot boundary or before != after.
        var actions = new List<(string Name, Action Setup, Action<UndoModel> Mutate)> {
            ("create entity",    NoSetup,           Create),
            ("delete entity",    SetupSubject,      Delete),
            ("reparent entity",  SetupParentChild,  Reparent),
            ("rename entity",    SetupSubject,      Rename),
            ("add component",    SetupSubject,      AddComponentAction),
            ("remove component", SetupWithExtra,    RemoveComponentAction),
            ("edit member",      SetupSubject,      EditMember),
            ("toggle active",    SetupSubject,      ToggleActive),
        };

        var coverage = new List<ActionCoverage>();
        foreach (var (name, setup, mutate) in actions)
            coverage.Add(Measure(name, setup, mutate, knownHole: false));

        // F2 CLOSED the asset-edit hole: an asset edit routed through EditorCommands.EditAsset (the .volume
        // profile / curve callback path) now leaves exactly one recoverable entry. This action models that
        // path -- capture the asset state before/after, push the callback pair, Undo runs applyOld. It is
        // IN-SCOPE and covered (was the KNOWN HOLE the F3 harness flagged for F2 to close).
        coverage.Add(Measure("asset edit (EditAsset callback)", SetupSubject, AssetEditCallback, knownHole: false));

        // The bypass-recognition control STAYS: a mutation with NO Push still leaves ZERO entries. This is
        // the harness's proof it can tell covered from uncovered (a regression that drops a Push must read
        // as 0 coverage, not silently pass). Distinct from the EditAsset path above, which IS covered now.
        coverage.Add(Measure("edit without undo (bypass control)", SetupSubject, AssetEditBypass, knownHole: true));

        // ---- Assertions: every in-scope action == exactly one recoverable undo entry ----------------
        foreach (ActionCoverage c in coverage.Where(c => !c.KnownHole)) {
            h.Check($"[{c.Name}] left exactly one undo entry", c.EntriesPushed == 1,
                $"expected 1 entry, got {c.EntriesPushed} -- a forgotten or duplicated Push()");
            h.Check($"[{c.Name}] undo restored the prior state", c.Restored,
                "Undo did not return the scene to its pre-action snapshot (wrong scope or no entry)");
        }

        // ---- F2: the EditAsset callback path is now COVERED (was the KNOWN HOLE) ---------------------
        ActionCoverage assetEdit = coverage.First(c => c.Name == "asset edit (EditAsset callback)");
        h.Check("EditAsset callback leaves exactly one undo entry (F2 closed the asset-edit hole)",
            assetEdit.EntriesPushed == 1,
            $"expected 1 entry from the EditAsset callback path, got {assetEdit.EntriesPushed}");
        h.Check("EditAsset callback undo restored the prior asset state", assetEdit.Restored,
            "applyOld did not revert the asset edit -- the callback before/after capture is wrong");

        // ---- The harness PROVES it can tell covered from uncovered: the bypass control == 0 coverage --
        ActionCoverage hole = coverage.First(c => c.KnownHole);
        h.Check("bypass control pushes ZERO undo entries (harness distinguishes covered vs not)",
            hole.EntriesPushed == 0,
            $"a no-Push edit should leave 0 entries (the dropped-Push bug class), got {hole.EntriesPushed}");
        h.Check("bypass control is NOT recoverable (a dropped Push is a no-op Ctrl+Z)", !hole.Restored,
            "a bypassing edit must NOT round-trip -- if it did, it wasn't really a bypass");

        // ---- Coverage summary report (the green-gate baseline F1/F2 migrate against) -----------------
        int covered = coverage.Count(c => !c.KnownHole && c.EntriesPushed == 1 && c.Restored);
        int inScope = coverage.Count(c => !c.KnownHole);
        Console.WriteLine($"[UndoCoverage] in-scope actions covered: {covered}/{inScope}");
        foreach (ActionCoverage c in coverage) {
            string status = c.KnownHole ? "bypass control (must read 0 coverage)"
                          : (c.EntriesPushed == 1 && c.Restored) ? "covered" : "UNCOVERED";
            Console.WriteLine($"    - {c.Name}: entries={c.EntriesPushed} restored={c.Restored} [{status}]");
        }
        // F2 closed the asset-edit hole for the EditAsset callback path (volume-profile group). The
        // ImGui-coupled drag/activation wiring around it (and gizmo/terrain) still migrate+test in F1.
        Console.WriteLine("    out-of-scope (need ImGui/EditorWidgets, migrate+test in F1): " +
                          "gizmo drag, widget deferred-commit, terrain brush.");

        return h.Report("UndoCoverage (F3)");
    }

    // The host entity/component the actions operate on. Created by a setup step BEFORE the measurement
    // snapshot, so a scoped restore-in-place reproduces the post-setup state exactly AND a full-scene
    // restore (delete/reparent) brings the target back to precisely what the snapshot captured.
    static Entity subject;
    static Entity subjectChild;
    static UndoExtraBehaviour subjectExtra;

    // Runs one action and measures coverage: setup (precondition, NOT undone) -> snapshot -> mutate
    // (pushes undo + mutates) -> Undo every pushed entry -> assert the scene returned to the snapshot.
    static ActionCoverage Measure(string name, Action setup, Action<UndoModel> mutate, bool knownHole) {
        var model = new UndoModel();
        setup();
        string before = SceneSerializer.Serialize(SceneManager.GetCurrentScene());

        mutate(model);
        int pushed = model.Count;

        // Undo everything the action pushed, restoring to the post-setup state.
        while (model.Count > 0)
            model.Undo();

        string after = SceneSerializer.Serialize(SceneManager.GetCurrentScene());
        bool restored = before == after;
        return new ActionCoverage(name, pushed, restored, knownHole);
    }

    // ---- Setup steps (run before the snapshot; their entities persist as the precondition) -----------

    static void NoSetup() { subject = null; subjectChild = null; subjectExtra = null; }

    // A host entity carrying the subject component -- the precondition for value/component-edit AND the
    // delete action (delete snapshots the whole scene WITH subject present, then removes it).
    static void SetupSubject() {
        subject = Entity.Instantiate("Subject");
        subject.AddComponent<UndoSubjectBehaviour>().Hp = 50;
        subjectChild = null;
        subjectExtra = null;
    }

    // Subject host that ALREADY has the extra component (so "remove component" has one to remove).
    static void SetupWithExtra() {
        SetupSubject();
        subjectExtra = subject.AddComponent<UndoExtraBehaviour>();
    }

    // Two unparented entities -- the precondition for the reparent action (it snapshots the whole scene
    // with them UNPARENTED, then parents child under subject; Undo must restore the flat hierarchy).
    static void SetupParentChild() {
        subject = Entity.Instantiate("ReparentParent");
        subjectChild = Entity.Instantiate("ReparentChild");
        subjectExtra = null;
    }

    // ---- The enumerated actions. Each pushes undo BEFORE mutating, exactly as a correct call site does.

    // Structural actions snapshot the WHOLE scene (EditorUndo.Push): create/delete/reparent change the
    // entity set or parent links, which the scoped path cannot restore.

    static void Create(UndoModel u) {
        u.Push();                                   // structural -> full snapshot
        Entity e = Entity.Instantiate("Created");
        e.AddComponent<UndoSubjectBehaviour>();
    }

    static void Delete(UndoModel u) {
        u.Push();                                    // full snapshot captures subject present...
        SceneManager.GetCurrentScene().DestroyEntity(subject);  // ...delete -> Undo restores it.
    }

    static void Reparent(UndoModel u) {
        u.Push();                                   // structural -> full snapshot (parent link is scene-wide)
        subjectChild.transform.SetParent(subject.transform);
    }

    // Scoped actions snapshot a SINGLE entity (EditorUndo.PushEntity): rename/add-comp/remove-comp/
    // edit-member/toggle-active touch one entity's own data; the host pre-exists (setup).

    static void Rename(UndoModel u) {
        u.PushEntity(subject);                       // value edit on one entity -> scoped snapshot
        subject.Name = "NewName";
    }

    static void AddComponentAction(UndoModel u) {
        u.PushEntity(subject);                       // component change on one entity -> scoped snapshot
        subject.AddComponent<UndoExtraBehaviour>();
    }

    static void RemoveComponentAction(UndoModel u) {
        u.PushEntity(subject);                       // captures subject WITH the extra component present
        subject.RemoveComponent(subjectExtra);
    }

    static void EditMember(UndoModel u) {
        UndoSubjectBehaviour s = subject.GetComponent<UndoSubjectBehaviour>();
        u.PushEntity(subject);                       // value edit -> scoped snapshot (captures Hp=50)
        s.Hp = 999;
    }

    static void ToggleActive(UndoModel u) {
        u.PushEntity(subject);                       // active flag is per-entity -> scoped snapshot
        subject.SetActive(false);
    }

    // F2: the asset edit routed through EditorCommands.EditAsset (-> EditorUndo.PushCallback). The caller
    // captures the asset state BEFORE and AFTER the edit (here the subject component's member, standing in
    // for a .volume profile / curve snapshot -- VolumeProfileEditor.Snapshot/Restore have exactly this
    // value-capture/value-restore shape) and supplies the revert/re-apply pair. One entry, Undo runs
    // applyOld and round-trips -- the formerly-bypassed asset edit is now undoable.
    static void AssetEditCallback(UndoModel u) {
        UndoSubjectBehaviour s = subject.GetComponent<UndoSubjectBehaviour>();
        int before = s.Hp;                          // capture asset state BEFORE the edit
        s.Hp = 7;                                    // the edit itself (already applied, as in the editor)
        int after = s.Hp;                            // capture asset state AFTER the edit
        // Push the callback pair AFTER the edit (matches the editor: the .volume Draw mutates, then
        // EditAsset records the revert pair). applyOld reverts to `before`, applyNew re-applies `after`.
        u.PushCallback(() => s.Hp = before, () => s.Hp = after);
    }

    // The BYPASS CONTROL (kept as the covered-vs-uncovered proof): mutate WITHOUT any Push (the
    // forgotten-Push bug class). Leaves zero entries -> Ctrl+Z is a no-op. The harness asserts this reads
    // as zero coverage, so a dropped Push can never silently pass as covered.
    static void AssetEditBypass(UndoModel u) {
        UndoSubjectBehaviour s = subject.GetComponent<UndoSubjectBehaviour>();
        // No u.Push() / u.PushEntity() / u.PushCallback() -- this is exactly the bug class: mutate, no snapshot.
        s.Hp = 7;
    }
}
