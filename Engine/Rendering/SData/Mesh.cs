using OpenTK.Mathematics;
using BufferUsageHint = OpenTK.Graphics.OpenGL4.BufferUsageHint;

namespace BallisticEngine;

public class Mesh : BObject
{
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    public readonly Vector3[] Tangents;
    public readonly uint[] Indices;
    public readonly Vector2[] UVs;

    readonly GPUBuffer<Vector3> vertexBuffer;
    readonly GPUBuffer<Vector2> UVBuffer;
    readonly GPUBuffer<Vector3> normalBuffer;
    readonly GPUBuffer<Vector3> tangentBuffer;
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
