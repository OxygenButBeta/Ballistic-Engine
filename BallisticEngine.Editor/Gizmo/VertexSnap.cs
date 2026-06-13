using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Unity-style vertex snapping for the move gizmo (hold V while dragging an object). Finds two world
// points each frame:
//   - the SOURCE: the vertex of the *selected* entity's mesh nearest the mouse cursor on screen,
//   - the TARGET: the vertex of ANY scene mesh nearest the mouse cursor on screen.
// The gizmo then translates the selection by (target - source), so the picked source vertex lands
// exactly on the target vertex — letting you weld a corner of one mesh to a corner of another.
//
// Picking is screen-space (project vertex -> pixel, keep the closest to the cursor) so it tracks the
// cursor the way Unity's does. Big meshes (Bistro scale) are STRIDED to a per-mesh vertex budget so
// the per-frame projection cost stays bounded — exact enough for snapping, cheap enough to run live.
internal static class VertexSnap {
    // Hold V to arm snapping (queried from raw OpenTK via EditorInput so it's independent of ImGui focus).
    public static bool Held;

    // The vertices examined per mesh. Meshes with more are strided down to roughly this many samples
    // so a million-vertex mesh doesn't stall the drag; corners still get hit because AABB extremes
    // survive most strides and the cursor only needs to be near one sampled vertex.
    const int PerMeshBudget = 4000;

    // The picked source/target in world space, valid only when Found is true (set by Solve each frame).
    public static bool Found { get; private set; }
    public static Vector3 SourceWorld { get; private set; }
    public static Vector3 TargetWorld { get; private set; }

    // Recompute the source (on `selected`) and target (on any entity) vertices nearest the cursor.
    // Call once per frame while armed; reads Found/SourceWorld/TargetWorld afterwards.
    public static void Solve(Entity selected, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, SysVec2 mouse) {
        Found = false;
        if (selected is null)
            return;

        // Source: restricted to the selected entity (and its mesh children) — that's the vertex that
        // will be moved onto the target.
        bool haveSource = NearestVertexInHierarchy(selected.transform, vp, viewMin, viewSize, mouse,
            out Vector3 source, out _);
        if (!haveSource)
            return;

        // Target: the nearest vertex anywhere in the scene EXCEPT the selection's own vertices (snapping
        // an object onto itself is meaningless). Falls back to the source if nothing else is near, which
        // makes the drag a no-op rather than a jump.
        bool haveTarget = NearestVertexInScene(selected, vp, viewMin, viewSize, mouse, out Vector3 target);

        SourceWorld = source;
        TargetWorld = haveTarget ? target : source;
        Found = true;
    }

    // Nearest projected vertex across every mesh in the scene that does NOT belong to `exclude`'s subtree.
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

    // Nearest projected vertex within `root` and its mesh-bearing descendants.
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

    // Project the mesh's vertices (strided to PerMeshBudget) and keep the one closest to the cursor.
    // `world` maps local vertices to world space; `vp` projects world to clip.
    static bool NearestVertexInMesh(Mesh mesh, Matrix4 world, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, out Vector3 bestWorld, out float bestDist) {
        bestWorld = default;
        bestDist = float.MaxValue;
        bool found = false;

        Vector3[] vertices = mesh.Vertices;
        int count = vertices.Length;
        if (count == 0)
            return false;

        // Ceiling division: floor (count / budget) leaves stride==1 for counts up to 2x the budget,
        // so a 1.x-over-budget mesh would sample every vertex anyway. Round up to keep samples <= budget.
        int stride = count > PerMeshBudget ? (count + PerMeshBudget - 1) / PerMeshBudget : 1;
        for (int i = 0; i < count; i += stride) {
            Vector3 worldVertex = Vector3.TransformPosition(vertices[i], world);
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
