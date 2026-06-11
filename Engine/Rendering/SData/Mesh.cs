using OpenTK.Mathematics;
using BufferUsageHint = OpenTK.Graphics.OpenGL4.BufferUsageHint;

namespace BallisticEngine;

public class Mesh : BObject
{
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    public readonly Vector4[] Tangents; // w = bitangent handedness
    public readonly uint[] Indices;
    public readonly Vector2[] UVs;

    // Index-buffer ranges per source material (always at least one, spanning everything).
    // SubMeshData.MaterialRef carries the .mat the importer generated for that range.
    public readonly SubMeshData[] SubMeshes;

    // Per-submesh inverse of SubMeshData.NodeTransform, precomputed for the renderer: when a
    // renderer draws a single submesh (SubMeshIndex >= 0), its entity carries the node's
    // transform, so the baked-in node placement must be undone (model = inverse * world).
    // Identity for merged imports and pre-v4 artifacts.
    public readonly Matrix4[] InverseNodeTransforms;

    // The source model's node hierarchy (split-by-nodes imports; empty otherwise) — the editor
    // instantiates one entity per node so the authored tree survives.
    public readonly MeshNodeData[] Nodes;

    readonly GPUBuffer<Vector3> vertexBuffer;
    readonly GPUBuffer<Vector2> UVBuffer;
    readonly GPUBuffer<Vector3> normalBuffer;
    readonly GPUBuffer<Vector4> tangentBuffer;
    public readonly InstancedBuffer InstanceBuffer;

    readonly GPUBuffer<uint> indexBuffer;
    readonly RenderContext renderContext;

    Mesh(in MeshData data)
    {
        renderContext = RenderAsset.Current.CreateRenderContext();
        renderContext.Activate();

        vertexBuffer = GraphicAPI.CreateVertexBuffer3(renderContext);
        UVBuffer = GraphicAPI.CreateUVBuffer(renderContext);
        normalBuffer = GraphicAPI.CreateNormalBuffer(renderContext);
        tangentBuffer = GraphicAPI.CreateTangentBuffer(renderContext);
        indexBuffer = GraphicAPI.CreateIndexBuffer(renderContext);

        Vertices = data.Vertices;
        Indices = data.Indices;
        Tangents = data.Tangents;
        UVs = data.UVs;
        Normals = data.Normals;
        SubMeshes = data.SubMeshes is { Length: > 0 }
            ? data.SubMeshes
            : [new SubMeshData(null, 0, data.Indices.Length, null)];
        Nodes = data.Nodes ?? [];

        InverseNodeTransforms = new Matrix4[SubMeshes.Length];
        for (var i = 0; i < SubMeshes.Length; i++) {
            Matrix4 node = SubMeshes[i].NodeTransform;
            // Guard degenerate matrices (default-constructed SubMeshData is all zeros).
            InverseNodeTransforms[i] = Math.Abs(node.Determinant) > 1e-12f
                ? Matrix4.Invert(node)
                : Matrix4.Identity;
        }

        InstanceBuffer = GraphicAPI.CreateInstancedBuffer(renderContext);
        InstanceBuffer.Create();
        FillBuffers();

        renderContext.Deactivate();
    }

    public static Mesh Create(in MeshData data)
    {
        if (!data.IsValid)
            throw new ArgumentException("MeshData has no vertices or indices.");

        return new Mesh(in data);
    }

    Vector3 boundsMin, boundsMax;
    bool hasBounds;
    Vector3[] subBoundsMin, subBoundsMax;

    // Local-space AABB, computed lazily from the CPU vertex copy. Used by the probe-bake
    // occupancy grid, the irradiance volume's fit-to-scene, and frustum culling.
    public void GetLocalBounds(out Vector3 min, out Vector3 max)
    {
        if (!hasBounds) {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (Vector3 v in Vertices) {
                lo = Vector3.ComponentMin(lo, v);
                hi = Vector3.ComponentMax(hi, v);
            }
            boundsMin = lo;
            boundsMax = hi;
            hasBounds = true;
        }
        min = boundsMin;
        max = boundsMax;
    }

    // Per-submesh local AABB (baked model space, same space as Vertices). Split-by-nodes
    // imports share one huge mesh across many entities; culling each entity with the WHOLE
    // mesh's bounds would make every part as big as the building — these make culling real.
    // All ranges are computed in one pass over the index buffer on first use.
    public void GetSubMeshBounds(int index, out Vector3 min, out Vector3 max)
    {
        if ((uint)index >= (uint)SubMeshes.Length) {
            GetLocalBounds(out min, out max);
            return;
        }

        if (subBoundsMin is null) {
            subBoundsMin = new Vector3[SubMeshes.Length];
            subBoundsMax = new Vector3[SubMeshes.Length];
            for (var s = 0; s < SubMeshes.Length; s++) {
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                int start = SubMeshes[s].IndexStart, end = start + SubMeshes[s].IndexCount;
                for (var i = start; i < end; i++) {
                    Vector3 v = Vertices[Indices[i]];
                    lo = Vector3.ComponentMin(lo, v);
                    hi = Vector3.ComponentMax(hi, v);
                }
                // Degenerate (empty) ranges collapse to a point so they cull away cleanly.
                subBoundsMin[s] = SubMeshes[s].IndexCount > 0 ? lo : Vector3.Zero;
                subBoundsMax[s] = SubMeshes[s].IndexCount > 0 ? hi : Vector3.Zero;
            }
        }

        min = subBoundsMin[index];
        max = subBoundsMax[index];
    }

    public void Activate()
    {
        renderContext.Activate();
    }

    public void Deactivate()
    {
        renderContext.Deactivate();
    }

    void FillBuffers()
    {
        normalBuffer.Create();
        normalBuffer.SetBufferData(in Normals, BufferUsageHint.StaticDraw);

        UVBuffer.Create();
        UVBuffer.SetBufferData(in UVs, BufferUsageHint.StaticDraw);

        indexBuffer.Create();
        indexBuffer.SetBufferData(in Indices, BufferUsageHint.StaticDraw);

        vertexBuffer.Create();
        vertexBuffer.SetBufferData(in Vertices, BufferUsageHint.StaticDraw);

        tangentBuffer.Create();
        tangentBuffer.SetBufferData(in Tangents, BufferUsageHint.StaticDraw);
    }
}
