using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal static class VertexSnap {
    public static bool Held;

    const int PerMeshBudget = 4000;

    public static bool Found { get; private set; }
    public static Vector3 SourceWorld { get; private set; }
    public static Vector3 TargetWorld { get; private set; }

    public static void Solve(Entity selected, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, SysVec2 mouse) {
        Found = false;
        if (selected is null)
            return;

        bool haveSource = NearestVertexInHierarchy(selected.transform, vp, viewMin, viewSize, mouse,
            out Vector3 source, out _);
        if (!haveSource)
            return;

        bool haveTarget = NearestVertexInScene(selected, vp, viewMin, viewSize, mouse, out Vector3 target);

        SourceWorld = source;
        TargetWorld = haveTarget ? target : source;
        Found = true;
    }

    static bool NearestVertexInScene(Entity exclude, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, out Vector3 best) {
        best = default;
        float bestDist = float.MaxValue;
        bool found = false;

        foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
            if (!entity.IsActive || IsInSubtree(entity.transform, exclude.transform))
                continue;
            if (entity.GetComponent<StaticMeshRenderer>() is not { SharedMesh: { } mesh })
                continue;

            if (NearestVertexInMesh(mesh, entity.transform.WorldMatrix, vp, viewMin, viewSize, mouse,
                    out Vector3 candidate, out float dist) && dist < bestDist) {
                bestDist = dist;
                best = candidate;
                found = true;
            }
        }

        return found;
    }

    static bool NearestVertexInHierarchy(Transform root, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, out Vector3 best, out float bestDist) {
        best = default;
        bestDist = float.MaxValue;
        bool found = false;

        if (root.Entity.GetComponent<StaticMeshRenderer>() is { SharedMesh: { } mesh } &&
            NearestVertexInMesh(mesh, root.WorldMatrix, vp, viewMin, viewSize, mouse,
                out Vector3 candidate, out float dist) && dist < bestDist) {
            bestDist = dist;
            best = candidate;
            found = true;
        }

        foreach (Entity other in SceneManager.GetCurrentScene().Entities) {
            if (!ReferenceEquals(other.transform.Parent, root))
                continue;
            if (NearestVertexInHierarchy(other.transform, vp, viewMin, viewSize, mouse,
                    out Vector3 childBest, out float childDist) && childDist < bestDist) {
                bestDist = childDist;
                best = childBest;
                found = true;
            }
        }

        return found;
    }

    static bool NearestVertexInMesh(Mesh mesh, Matrix4 world, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, out Vector3 bestWorld, out float bestDist) {
        bestWorld = default;
        bestDist = float.MaxValue;
        bool found = false;

        Vector3[] vertices = mesh.Vertices;
        int count = vertices.Length;
        if (count == 0)
            return false;

        int stride = count > PerMeshBudget ? (count + PerMeshBudget - 1) / PerMeshBudget : 1;
        for (int i = 0; i < count; i += stride) {
            Vector3 worldVertex = Vector3.Transform(vertices[i], world);
            if (!GizmoMath.Project(worldVertex, vp, viewMin, viewSize, out SysVec2 pixel))
                continue;

            float dist = SysVec2.Distance(pixel, mouse);
            if (dist < bestDist) {
                bestDist = dist;
                bestWorld = worldVertex;
                found = true;
            }
        }

        return found;
    }

    static bool IsInSubtree(Transform node, Transform root) {
        for (Transform t = node; t is not null; t = t.Parent)
            if (ReferenceEquals(t, root))
                return true;
        return false;
    }
}
