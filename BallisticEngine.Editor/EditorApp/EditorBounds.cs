
namespace BallisticEngine.Editor;

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
        radius = MathF.Max(0.001f, ((max - min) * 0.5f).Length());
        return true;
    }

    static void Accumulate(Transform node, ref Vector3 min, ref Vector3 max, ref bool any) {
        Entity entity = node.Entity;

        if (entity.GetComponent<StaticMeshRenderer>() is { SharedMesh: { } mesh })
            AccumulateMesh(mesh, node.WorldMatrix, ref min, ref max, ref any);

        foreach (Entity other in SceneManager.GetCurrentScene().Entities)
            if (ReferenceEquals(other.transform.Parent, node))
                Accumulate(other.transform, ref min, ref max, ref any);
    }

    static void AccumulateMesh(Mesh mesh, Matrix4 world, ref Vector3 min, ref Vector3 max, ref bool any) {
        mesh.GetLocalBounds(out Vector3 lo, out Vector3 hi);

        for (int i = 0; i < 8; i++) {
            var corner = new Vector3(
                (i & 1) == 0 ? lo.X : hi.X,
                (i & 2) == 0 ? lo.Y : hi.Y,
                (i & 4) == 0 ? lo.Z : hi.Z);
            Vector3 worldCorner = Vector3.Transform(corner, world);
            min = Vector3.Min(min, worldCorner);
            max = Vector3.Max(max, worldCorner);
        }

        any = true;
    }
}
