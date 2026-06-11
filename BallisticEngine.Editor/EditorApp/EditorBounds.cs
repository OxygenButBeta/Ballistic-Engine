using OpenTK.Mathematics;

namespace BallisticEngine.Editor;

// Computes the world-space bounds of an entity's renderable geometry for the editor (F-to-frame,
// and anything else that needs to know how big a selection is on screen). Walks the entity and its
// transform descendants, unioning each StaticMeshRenderer's local AABB transformed into world space.
// Returns a bounding sphere (center + radius) since that's all the camera framing needs and it's
// rotation-invariant. False when the selection has no mesh geometry at all (empty/light/camera).
internal static class EditorBounds {
    public static bool TryGetWorldBounds(Entity root, out Vector3 center, out float radius) {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        Accumulate(root.transform, ref min, ref max, ref any);

        if (!any) {
            center = root.transform.WorldPosition;
            radius = 0f;
            return false;
        }

        center = (min + max) * 0.5f;
        radius = MathF.Max(0.001f, ((max - min) * 0.5f).Length);
        return true;
    }

    // Recurse the transform tree; the editor's hierarchy is parent-linked, so children are found by
    // scanning the scene for transforms whose Parent is this one (same pattern as EntityClone).
    static void Accumulate(Transform node, ref Vector3 min, ref Vector3 max, ref bool any) {
        Entity entity = node.Entity;

        if (entity.GetComponent<StaticMeshRenderer>() is { SharedMesh: { } mesh })
            AccumulateMesh(mesh, node.WorldMatrix, ref min, ref max, ref any);

        foreach (Entity other in SceneManager.GetCurrentScene().Entities)
            if (ReferenceEquals(other.transform.Parent, node))
                Accumulate(other.transform, ref min, ref max, ref any);
    }

    // Transform the mesh's local AABB by `world` and union the 8 transformed corners (an oriented box
    // re-bounded as an axis-aligned box — slightly conservative under rotation, which is fine for framing).
    static void AccumulateMesh(Mesh mesh, Matrix4 world, ref Vector3 min, ref Vector3 max, ref bool any) {
        mesh.GetLocalBounds(out Vector3 lo, out Vector3 hi);

        for (int i = 0; i < 8; i++) {
            var corner = new Vector3(
                (i & 1) == 0 ? lo.X : hi.X,
                (i & 2) == 0 ? lo.Y : hi.Y,
                (i & 4) == 0 ? lo.Z : hi.Z);
            Vector3 worldCorner = Vector3.TransformPosition(corner, world);
            min = Vector3.ComponentMin(min, worldCorner);
            max = Vector3.ComponentMax(max, worldCorner);
        }

        any = true;
    }
}
