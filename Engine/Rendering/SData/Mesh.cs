using OpenTK.Mathematics;
using Assimp;
using BallisticEngine.Shared.Runtime_Set;
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

    Mesh(in Vector3[] vertices, in uint[] indices, Vector2[] uVs, Vector3[] normals, Vector3[] tangents,
        bool CreateInstanceBuffer = true)
    {
        renderContext = RenderAsset.Current.CreateRenderContext();
        renderContext.Activate();

        vertexBuffer = GraphicAPI.CreateVertexBuffer3(renderContext);
        UVBuffer = GraphicAPI.CreateUVBuffer(renderContext);
        normalBuffer = GraphicAPI.CreateNormalBuffer(renderContext);
        tangentBuffer = GraphicAPI.CreateTangentBuffer(renderContext);
        indexBuffer = GraphicAPI.CreateIndexBuffer(renderContext);

        Vertices = vertices;
        Indices = indices;
        Tangents = tangents;
        UVs = uVs;
        Normals = normals;

        InstanceBuffer = GraphicAPI.CreateInstancedBuffer(renderContext);
        InstanceBuffer.Create();
        FillBuffers();

        renderContext.Deactivate();
        Debugging.SystemLog("Texture Created ", SystemLogPriority.High);
    }

    public static Mesh ImportFromFile(string filePath)
    {
        if (RuntimeCache<string, Mesh>.TryGetValue(filePath, out var cachedMesh))
            return cachedMesh;

        AssimpContext context = new();

        Assimp.Scene scene = context.ImportFile(filePath,
            PostProcessSteps.Triangulate | PostProcessSteps.FlipUVs);

        if (scene == null || scene.MeshCount == 0)
            throw new IOException("Mesh import failed or no meshes found in the file.");

        Assimp.Mesh mesh = scene.Meshes[0];

        Vector3[] vertices = new Vector3[mesh.VertexCount];
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            Vector3D v = mesh.Vertices[i];
            vertices[i] = new Vector3(v.X, v.Y, v.Z);
        }

        Vector2[] uvs = new Vector2[mesh.VertexCount];
        if (mesh.TextureCoordinateChannelCount > 0 && mesh.HasTextureCoords(0))
        {
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                Vector3D uv = mesh.TextureCoordinateChannels[0][i];
                uvs[i] = new Vector2(uv.X, uv.Y);
            }
        }
        else
        {
            for (var i = 0; i < mesh.VertexCount; i++)
                uvs[i] = Vector2.Zero;
        }

        var triangleCount = mesh.Faces.Count(f => f.IndexCount == 3);
        var indices = new uint[triangleCount * 3];

        var index = 0;
        foreach (Face face in mesh.Faces.Where(face => face.IndexCount == 3))
        {
            indices[index++] = (uint)face.Indices[0];
            indices[index++] = (uint)face.Indices[1];
            indices[index++] = (uint)face.Indices[2];
        }

        Vector3[] normals = new Vector3[mesh.VertexCount];
        if (mesh.HasNormals)
        {
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                Vector3D n = mesh.Normals[i];
                normals[i] = new Vector3(n.X, n.Y, n.Z);
            }
        }

        Vector3[] tangents = new Vector3[mesh.VertexCount];
        if (mesh.HasTangentBasis)
        {
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                Vector3D t = mesh.Tangents[i];
                tangents[i] = new Vector3(t.X, t.Y, t.Z);
            }
        }
        else
        {
            for (var i = 0; i < mesh.VertexCount; i++)
                tangents[i] = Vector3.UnitX;
        }

        Mesh importedMesh = new(vertices, indices, uvs, normals, tangents);
        RuntimeCache<string, Mesh>.Add(filePath, importedMesh);
        return importedMesh;
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