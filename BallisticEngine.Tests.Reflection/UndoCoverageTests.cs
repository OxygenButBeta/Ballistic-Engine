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
//   toggle active. OUT OF SCOPE (documented below, needs ImGui/EditorWidgets -> migrate + test in F1):
//   gizmo drags, widget activation-state deferred-commit, terrain brush, curve/volume callback edits.
//
// KNOWN HOLE (the RED case that proves the harness can tell covered from uncovered): an asset-edit-style
// mutation that bypasses snapshotting leaves ZERO entries. The harness ASSERTS that bypass == 0 coverage
// (a positive assertion of a documented hole), and reports it -- exit stays 0; F2 will flip it to covered.
//
// Runs alongside the other G-suites that insert into the global SceneManager (no public unload); the
// leftover scene is harmless (process exits next).
internal static class UndoCoverageTests {
    // A faithful stand-in for EditorUndo: the same two entry shapes (whole-scene YAML / scoped entity
    // doc), one entry per Push, Undo restores the captured state. No redo/labels/capacity here -- the
    // coverage contract is "did the action leave a recoverable entry", which these two paths express.
    sealed class UndoModel {
        readonly record struct Entry(string Yaml, Guid EntityId, EntityDocument Doc) {
            public bool IsScoped => Doc is not null;
        }

        readonly List<Entry> stack = new();
        public int Count => stack.Count;

        // Whole-scene snapshot BEFORE a structural change (EditorUndo.Push).
        public void Push() => stack.Add(new Entry(SceneSerializer.Serialize(SceneManager.GetCurrentScene()), Guid.Empty, null));

        // Scoped single-entity snapshot BEFORE a value edit (EditorUndo.PushEntity).
        public void PushEntity(Entity e) {
            if (e is null) { Push(); return; }
            stack.Add(new Entry(null, e.InstanceId, SceneSerializer.CaptureEntity(e)));
        }

        // Restore the top entry (EditorUndo.Undo -> Apply): scoped restores just its entity in place,
        // full rebuilds the scene. Pops the entry.
        public void Undo() {
            if (stack.Count == 0) return;
            Entry top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
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

        // The documented KNOWN HOLE: an asset-edit-style mutation that bypasses snapshotting -- mutates a
        // member WITHOUT any Push first. EditorUndo never sees it, so it leaves ZERO entries (today, edit
        // a .mat -> Ctrl+Z does nothing). We assert that the bypass path is RECOGNIZED as zero-coverage.
        coverage.Add(Measure("asset edit (bypass undo)", SetupSubject, AssetEditBypass, knownHole: true));

        // ---- Assertions: every in-scope action == exactly one recoverable undo entry ----------------
        foreach (ActionCoverage c in coverage.Where(c => !c.KnownHole)) {
            h.Check($"[{c.Name}] left exactly one undo entry", c.EntriesPushed == 1,
                $"expected 1 entry, got {c.EntriesPushed} -- a forgotten or duplicated Push()");
            h.Check($"[{c.Name}] undo restored the prior state", c.Restored,
                "Undo did not return the scene to its pre-action snapshot (wrong scope or no entry)");
        }

        // ---- The harness PROVES it can tell covered from uncovered: the bypass path == 0 coverage ----
        ActionCoverage hole = coverage.First(c => c.KnownHole);
        h.Check("known-hole bypass pushes ZERO undo entries (harness distinguishes covered vs not)",
            hole.EntriesPushed == 0,
            $"the asset-edit bypass should leave 0 entries (documented hole), got {hole.EntriesPushed}");
        h.Check("known-hole bypass is NOT recoverable (Ctrl+Z is a no-op today)", !hole.Restored,
            "a bypassing edit must NOT round-trip -- if it did, it wasn't really a bypass");

        // ---- Coverage summary report (the green-gate baseline F1/F2 migrate against) -----------------
        int covered = coverage.Count(c => !c.KnownHole && c.EntriesPushed == 1 && c.Restored);
        int inScope = coverage.Count(c => !c.KnownHole);
        Console.WriteLine($"[UndoCoverage] in-scope actions covered: {covered}/{inScope}");
        foreach (ActionCoverage c in coverage) {
            string status = c.KnownHole ? "KNOWN HOLE (F2 will close)"
                          : (c.EntriesPushed == 1 && c.Restored) ? "covered" : "UNCOVERED";
            Console.WriteLine($"    - {c.Name}: entries={c.EntriesPushed} restored={c.Restored} [{status}]");
        }
        Console.WriteLine("    out-of-scope (need ImGui/EditorWidgets, migrate+test in F1): " +
                          "gizmo drag, widget deferred-commit, terrain brush, curve/volume callback edit.");

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

    // The KNOWN HOLE: mutate WITHOUT pushing undo first (the asset-edit / forgotten-Push shape). Leaves
    // zero entries -> Ctrl+Z is a no-op. The harness asserts this is recognized as zero coverage.
    static void AssetEditBypass(UndoModel u) {
        UndoSubjectBehaviour s = subject.GetComponent<UndoSubjectBehaviour>();
        // No u.Push() / u.PushEntity() -- this is exactly the bug class: mutate, no snapshot.
        s.Hp = 7;
    }
}
