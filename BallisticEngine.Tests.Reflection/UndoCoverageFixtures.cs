using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Fixtures for the F3 "undo coverage" suite (Phase F, undo unification). A trivial component with a
// scalar member the harness can edit, plus a record describing one enumerated mutating action and the
// undo entries it left. The component is deliberately plain (one int) so a member edit round-trips
// through the scoped capture/restore path without any asset/ImGui dependency.
public sealed class UndoSubjectBehaviour : Behaviour {
    public int Hp = 50;     // a plain leaf the "edit member" action mutates
    public string Label = "subject";
}

// A second component type so the "add/remove component" actions have something distinct to add.
public sealed class UndoExtraBehaviour : Behaviour {
    public float Speed = 1f;
}

// The result of running one enumerated action through the UndoModel: how many undo entries it pushed,
// whether the post-Undo state matched the pre-action snapshot, and whether the action is a KNOWN HOLE
// (a path that bypasses undo today -- documented, not failed, until F1/F2 close it).
public readonly record struct ActionCoverage(string Name, int EntriesPushed, bool Restored, bool KnownHole);
