
namespace BallisticEngine;

public class Mesh : BObject
{
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    public readonly Vector4[] Tangents;
    public readonly uint[] Indices;
    public readonly Vector2[] UVs;

    public readonly SubMeshData[] SubMeshes;

    public readonly Matrix4[] InverseNodeTransforms;

    public readonly MeshNodeData[] Nodes;

    public readonly SkeletonData Skeleton;
    public readonly Vector4i[] BoneIndices;
    public readonly Vector4[] BoneWeights;

    // Per-mesh signed distance field (generated offline at import, FAZ 1; persisted in artifact v8). MESH-LOCAL
    // space (same space as Vertices). The global distance field (Lumen FAZ 2, Dx12GlobalSdf) uploads this to a
    // GPU 3D texture per unique mesh and composites it into a camera-centered clipmap. Null for meshes imported
    // before v8 or with SDF disabled — the global SDF skips those instances gracefully.
    public readonly MeshSdf Sdf;

    // Per-mesh card representation (offline, FAZ 3a; persisted in artifact v9). A small set of oriented
    // bounding-box cards a later surface cache will capture/light. MESH-LOCAL space. Built from Sdf, so
    // null whenever Sdf is null (skinned, disabled, or v8-and-earlier artifacts).
    public readonly MeshCards Cards;
    public bool IsSkinned { get; }
    public int BoneCount => Skeleton.BoneCount;

    readonly GPUBuffer<Vector3> vertexBuffer;
    readonly GPUBuffer<Vector2> UVBuffer;
    readonly GPUBuffer<Vector3> normalBuffer;
    readonly GPUBuffer<Vector4> tangentBuffer;
    readonly GPUBuffer<Vector4> boneIndexBuffer;
    readonly GPUBuffer<Vector4> boneWeightBuffer;
    public readonly InstancedBuffer InstanceBuffer;

    readonly GPUBuffer<uint> indexBuffer;
    readonly RenderContext renderContext;

    public GPUBuffer<Vector3> VertexBuffer => vertexBuffer;
    public GPUBuffer<Vector3> NormalBuffer => normalBuffer;
    public GPUBuffer<Vector2> UvBuffer => UVBuffer;
    public GPUBuffer<Vector4> TangentBuffer => tangentBuffer;
    public GPUBuffer<uint> IndexBuffer => indexBuffer;
    public GPUBuffer<Vector4> BoneIndexBuffer => boneIndexBuffer;
    public GPUBuffer<Vector4> BoneWeightBuffer => boneWeightBuffer;

    [ThreadStatic] public static bool DeferUpload;

    public bool IsUploaded { get; private set; }

    public void EnsureUploaded()
    {
        if (IsUploaded) return;
        renderContext.Activate();
        FillBuffers();
        renderContext.Deactivate();
        IsUploaded = true;
    }

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

        IsSkinned = data.IsSkinned;
        Skeleton = data.Skeleton;
        BoneIndices = data.BoneIndices;
        BoneWeights = data.BoneWeights;
        Sdf = data.Sdf;
        Cards = data.Cards;
        if (IsSkinned) {
            boneIndexBuffer = GraphicAPI.CreateBoneIndexBuffer(renderContext);
            boneWeightBuffer = GraphicAPI.CreateBoneWeightBuffer(renderContext);
        }
        SubMeshes = data.SubMeshes is { Length: > 0 }
            ? data.SubMeshes
            : [new SubMeshData(null, 0, data.Indices.Length, null)];
        Nodes = data.Nodes ?? [];

        InverseNodeTransforms = new Matrix4[SubMeshes.Length];
        for (var i = 0; i < SubMeshes.Length; i++) {
            Matrix4 node = SubMeshes[i].NodeTransform;
            InverseNodeTransforms[i] = Math.Abs(node.GetDeterminant()) > 1e-12f
                ? node.Inverted()
                : Matrix4.Identity;
        }

        InstanceBuffer = GraphicAPI.CreateInstancedBuffer(renderContext);
        InstanceBuffer.Create();

        if (DeferUpload)
            MeshUploadQueue.Enqueue(this);
        else
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

    public void GetLocalBounds(out Vector3 min, out Vector3 max)
    {
        if (!hasBounds) {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (Vector3 v in Vertices) {
                lo = Vector3.Min(lo, v);
                hi = Vector3.Max(hi, v);
            }
            boundsMin = lo;
            boundsMax = hi;
            hasBounds = true;
        }
        min = boundsMin;
        max = boundsMax;
    }

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
                    lo = Vector3.Min(lo, v);
                    hi = Vector3.Max(hi, v);
                }

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
        normalBuffer.SetBufferData(in Normals, BufferUsage.StaticDraw);

        UVBuffer.Create();
        UVBuffer.SetBufferData(in UVs, BufferUsage.StaticDraw);

        indexBuffer.Create();
        indexBuffer.SetBufferData(in Indices, BufferUsage.StaticDraw);

        vertexBuffer.Create();
        vertexBuffer.SetBufferData(in Vertices, BufferUsage.StaticDraw);

        tangentBuffer.Create();
        tangentBuffer.SetBufferData(in Tangents, BufferUsage.StaticDraw);

        if (IsSkinned) {
            var indicesAsFloat = new Vector4[BoneIndices.Length];
            for (var i = 0; i < BoneIndices.Length; i++) {
                Vector4i b = BoneIndices[i];
                indicesAsFloat[i] = new Vector4(b.X, b.Y, b.Z, b.W);
            }
            boneIndexBuffer.Create();
            boneIndexBuffer.SetBufferData(in indicesAsFloat, BufferUsage.StaticDraw);

            boneWeightBuffer.Create();
            boneWeightBuffer.SetBufferData(in BoneWeights, BufferUsage.StaticDraw);
        }
    }
}
