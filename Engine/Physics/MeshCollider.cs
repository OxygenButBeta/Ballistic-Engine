
namespace BallisticEngine;

// Triangle-accurate collision from a mesh asset. STATIC ONLY: concave triangle soups cannot
// ride on a Rigidbody (the body logs and ignores it). With no mesh assigned it falls back to
// the entity's StaticMeshRenderer, including its SubMeshIndex — a split-by-nodes child entity
// collides with just its own part, un-baking the node transform exactly like the renderer.
[Component("Mesh Collider", "Physics")]
public class MeshCollider : Collider {
    [Tooltip("Mesh to collide with. Empty = the entity's StaticMeshRenderer mesh.")]
    public Mesh SharedMesh { get; set; }

    internal override bool ValidForDynamic => false;

    internal override PhysicsShape BuildShape(Vector3 worldScale) {
        Mesh mesh = SharedMesh;
        int subMeshIndex = -1;

        if (mesh is null && entity.GetComponent<StaticMeshRenderer>() is { } renderer) {
            mesh = renderer.SharedMesh;
            subMeshIndex = renderer.SubMeshIndex;
        }

        if (mesh is null) {
            Debugging.LogWarning($"Physics: MeshCollider on '{entity.Name}' has no mesh (assign SharedMesh or add a StaticMeshRenderer); collider skipped.");
            return null;
        }

        if (subMeshIndex < 0 || subMeshIndex >= mesh.SubMeshes.Length)
            return new MeshShape(mesh.Vertices, mesh.Indices, worldScale);

        // One submesh: slice its index range and remap to a compact vertex array, applying the
        // inverse node transform so the triangles live in entity-local space (the entity carries
        // the node's pivot; the renderer un-bakes it the same way).
        SubMeshData subMesh = mesh.SubMeshes[subMeshIndex];
        Matrix4 inverseNode = mesh.InverseNodeTransforms[subMeshIndex];

        var remap = new Dictionary<uint, uint>(capacity: subMesh.IndexCount);
        var vertices = new List<Vector3>(capacity: subMesh.IndexCount / 2);
        var indices = new uint[subMesh.IndexCount];

        for (int i = 0; i < subMesh.IndexCount; i++) {
            uint source = mesh.Indices[subMesh.IndexStart + i];
            if (!remap.TryGetValue(source, out uint mapped)) {
                mapped = (uint)vertices.Count;
                remap[source] = mapped;
                vertices.Add(Vector3.Transform(mesh.Vertices[source], inverseNode));
            }
            indices[i] = mapped;
        }

        return new MeshShape(vertices.ToArray(), indices, worldScale);
    }

    // ---- Selection gizmo: wireframe of the collision mesh ----------------------

    // Huge meshes would drown the ImGui draw list, so triangles are strided to a budget —
    // the wireframe thins out instead of disappearing or tanking the editor.
    const int MaxGizmoEdges = 4000;

    Mesh edgeCacheMesh;
    int edgeCacheSubMesh = -2;
    Vector3[] edgeCache; // flat endpoint pairs: [a0, b0, a1, b1, ...]

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        Mesh mesh = SharedMesh;
        int subMeshIndex = -1;
        if (mesh is null && entity.GetComponent<StaticMeshRenderer>() is { } renderer) {
            mesh = renderer.SharedMesh;
            subMeshIndex = renderer.SubMeshIndex;
        }
        if (mesh is null)
            return;

        if (!ReferenceEquals(edgeCacheMesh, mesh) || edgeCacheSubMesh != subMeshIndex)
            BuildEdgeCache(mesh, subMeshIndex);
        if (edgeCache.Length == 0)
            return;

        // Mirror the physics pose: position + rotation with the world scale baked into the
        // local point (matches how the static body is built from this collider).
        Vector3 position = transform.WorldPosition;
        Quaternion rotation = transform.WorldRotation;
        Vector3 scale = transform.WorldMatrix.ExtractScale();
        Vector3 offset = Center;

        gizmos.Color = new Vector3(0.35f, 1f, 0.4f);
        for (int i = 0; i < edgeCache.Length; i += 2) {
            gizmos.DrawLine(
                position + Vector3.Transform(scale * (offset + edgeCache[i]), rotation),
                position + Vector3.Transform(scale * (offset + edgeCache[i + 1]), rotation));
        }
    }

    void BuildEdgeCache(Mesh mesh, int subMeshIndex) {
        edgeCacheMesh = mesh;
        edgeCacheSubMesh = subMeshIndex;
        edgeCache = [];

        if (BuildShape(Vector3.One) is not MeshShape shape)
            return;

        uint[] indices = shape.Indices;
        Vector3[] vertices = shape.Vertices;
        int triangleCount = indices.Length / 3;
        int stride = Math.Max(1, (triangleCount * 3 + MaxGizmoEdges - 1) / MaxGizmoEdges);

        var seen = new HashSet<ulong>(Math.Min(triangleCount * 3, MaxGizmoEdges * 2));
        var edges = new List<Vector3>(Math.Min(triangleCount * 6, MaxGizmoEdges * 2));
        for (int triangle = 0; triangle < triangleCount; triangle += stride) {
            for (int e = 0; e < 3; e++) {
                uint i0 = indices[triangle * 3 + e];
                uint i1 = indices[triangle * 3 + (e + 1) % 3];
                ulong key = i0 < i1 ? ((ulong)i0 << 32) | i1 : ((ulong)i1 << 32) | i0;
                if (!seen.Add(key))
                    continue;
                edges.Add(vertices[i0]);
                edges.Add(vertices[i1]);
            }
        }

        edgeCache = edges.ToArray();
    }
}
