using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal static class ScenePicker {
    const int TriangleBudget = 200_000;

    public static Entity Pick(Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, SysVec2 mouse) {
        GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 ro, out Vector3 rd);

        Entity best = null;
        float bestT = float.MaxValue;

        foreach (Entity entity in SceneManager.GetCurrentScene().Entities) {
            if (!entity.IsActive)
                continue;
            if (entity.GetComponent<StaticMeshRenderer>() is not { SharedMesh: { } mesh } renderer)
                continue;

            Matrix4 world = entity.transform.WorldMatrix;

            if (!TryInvert(world, out Matrix4 invWorld))
                continue;
            Vector3 localO = Vector3.Transform(ro, invWorld);
            Vector3 localDir = Vector3.TransformNormal(rd, invWorld);

            mesh.GetLocalBounds(out Vector3 lo, out Vector3 hi);
            if (!RayHitsAabb(localO, localDir, lo, hi))
                continue;

            if (NearestTriangle(mesh, renderer.SubMeshIndex, localO, localDir, out float t) && t < bestT) {
                bestT = t;
                best = entity;
            }
        }

        return best;
    }

    static bool NearestTriangle(Mesh mesh, int subMeshIndex, Vector3 origin, Vector3 dir, out float bestT) {
        bestT = float.MaxValue;
        bool hit = false;

        uint[] indices = mesh.Indices;
        Vector3[] vertices = mesh.Vertices;

        (int start, int count) = IndexRange(mesh, subMeshIndex, indices.Length);
        int triangles = count / 3;
        int stride = triangles > TriangleBudget ? triangles / TriangleBudget : 1;

        for (int tri = 0; tri < triangles; tri += stride) {
            int i = start + tri * 3;
            Vector3 a = vertices[indices[i]];
            Vector3 b = vertices[indices[i + 1]];
            Vector3 c = vertices[indices[i + 2]];

            if (RayTriangle(origin, dir, a, b, c, out float t) && t < bestT) {
                bestT = t;
                hit = true;
            }
        }

        return hit;
    }

    static (int start, int count) IndexRange(Mesh mesh, int subMeshIndex, int indexCount) {
        if (subMeshIndex >= 0 && subMeshIndex < mesh.SubMeshes.Length) {
            SubMeshData sub = mesh.SubMeshes[subMeshIndex];
            return (sub.IndexStart, sub.IndexCount);
        }
        return (0, indexCount);
    }

    static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t) {
        t = 0f;
        Vector3 e1 = b - a;
        Vector3 e2 = c - a;
        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-8f)
            return false;

        float inv = 1f / det;
        Vector3 tv = o - a;
        float u = Vector3.Dot(tv, p) * inv;
        if (u < 0f || u > 1f)
            return false;

        Vector3 q = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f)
            return false;

        t = Vector3.Dot(e2, q) * inv;
        return t > 1e-5f;
    }

    static bool RayHitsAabb(Vector3 o, Vector3 d, Vector3 lo, Vector3 hi) {
        float tmin = 0f;
        float tmax = float.MaxValue;
        for (int axis = 0; axis < 3; axis++) {
            float origin = o[axis];
            float dir = d[axis];
            float min = lo[axis];
            float max = hi[axis];
            if (MathF.Abs(dir) < 1e-9f) {
                if (origin < min || origin > max)
                    return false;
            }
            else {
                float inv = 1f / dir;
                float t1 = (min - origin) * inv;
                float t2 = (max - origin) * inv;
                if (t1 > t2)
                    (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax)
                    return false;
            }
        }
        return true;
    }

    static bool TryInvert(Matrix4 m, out Matrix4 inverse) {
        if (MathF.Abs(m.GetDeterminant()) < 1e-12f) {
            inverse = Matrix4.Identity;
            return false;
        }
        inverse = m.Inverted();
        return true;
    }
}
