using OpenTK.Mathematics;

namespace BallisticEngine.Editor;

// Unity-style entity copy/paste (Ctrl+C / Ctrl+V). Holds a reference to the copied source entity
// and clones it on paste through the same EntityClone path as Ctrl+D, so a paste duplicates the
// transform and every component (asset refs shared, descendants included). Pasting again from the
// same copy keeps producing fresh siblings of the original.
//
// The clipboard stores the live entity, not a serialized snapshot: copying then deleting the source
// before pasting yields nothing (HasCopy goes false once it leaves the scene) — same as if you never
// copied. A snapshot-based clipboard is a later refinement; this matches the existing clone semantics.
internal static class EditorClipboard {
    static Entity copied;

    public static bool HasCopy => copied is not null;

    public static void Copy(Entity entity) => copied = entity;

    // Clone the copied entity into `scene` and return the new copy (null if nothing valid to paste).
    // The paste is nudged off the original so it doesn't sit exactly on top (Unity nudges too); the
    // caller pushes undo and selects the result.
    public static Entity Paste(Scene scene) {
        if (copied is null || !scene.Entities.Contains(copied))
            return null;

        Entity copy = EntityClone.Duplicate(scene, copied);
        copy.transform.Position += new Vector3(0.5f, 0f, 0.5f);
        return copy;
    }
}
