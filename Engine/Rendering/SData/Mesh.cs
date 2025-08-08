using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Assimp;
using Assimp.Configs;
using BallisticEngine.Shared.Runtime_Set;
using BufferUsageHint = OpenTK.Graphics.OpenGL4.BufferUsageHint;

namespace BallisticEngine;

public class Mesh : BObject {
    public readonly Vector3[] Vertices;
    public readonly uint[] Indices;
    public readonly Vector2[] UVs;

    readonly GPUBuffer<Vector3> vertexBuffer;
    readonly GPUBuffer<Vector2> UVBuffer;
    public readonly InstancedBuffer InstanceBuffer;

    readonly GPUBuffer<uint> indexBuffer;
    readonly RenderContext renderContext;

    Mesh(in Vector3[] vertices, in uint[] indices, Vector2[] uVs, bool CreateInstanceBuffer = true) {
        renderContext = RenderAsset.Current.CreateRenderContext();
        vertexBuffer = RenderAsset.Current.CreateVertexBuffer(renderContext);
        UVBuffer = RenderAsset.Current.CreateUVBuffer(renderContext);
        indexBuffer = RenderAsset.Current.CreateIndexBuffer(renderContext);
        Vertices = vertices;
        Indices = indices;
        UVs = uVs;
        renderContext.Activate();
        InstanceBuffer = RenderAsset.Current.CreateInstancedBuffer(renderContext);
        InstanceBuffer.Create();
        FillBuffers();
        renderContext.Deactivate();
        Debugging.SystemLog("Texture Created ", SystemLogPriority.High);
    }

    public static Mesh ImportFromFile(string filePath) {
        if (RuntimeCache<string, Mesh>.TryGetValue(filePath, out var cachedMesh))
            return cachedMesh;

        var context = new AssimpContext();

        Assimp.Scene scene = context.ImportFile(filePath,
            PostProcessSteps.Triangulate | PostProcessSteps.FlipUVs);

        if (scene == null || scene.MeshCount == 0)
            throw new IOException("Mesh import failed or no meshes found in the file.");

        var mesh = scene.Meshes[0];

        var vertices = new Vector3[mesh.VertexCount];
        for (var i = 0; i < mesh.VertexCount; i++) {
            Vector3D v = mesh.Vertices[i];
            vertices[i] = new Vector3(v.X, v.Y, v.Z);
        }

        var uvs = new Vector2[mesh.VertexCount];
        if (mesh.TextureCoordinateChannelCount > 0 && mesh.HasTextureCoords(0)) {
            for (var i = 0; i < mesh.VertexCount; i++) {
                Vector3D uv = mesh.TextureCoordinateChannels[0][i];
                uvs[i] = new Vector2(uv.X, uv.Y);
            }
        }
        else {
            for (var i = 0; i < mesh.VertexCount; i++)
                uvs[i] = Vector2.Zero;
        }

        var triangleCount = mesh.Faces.Count(f => f.IndexCount == 3);
        var indices = new uint[triangleCount * 3];

        var index = 0;
        foreach (Face face in mesh.Faces.Where(face => face.IndexCount == 3)) {
            indices[index++] = (uint)face.Indices[0];
            indices[index++] = (uint)face.Indices[1];
            indices[index++] = (uint)face.Indices[2];
        }

        var importedMesh = new Mesh(vertices, indices, uvs);
        RuntimeCache<string, Mesh>.Add(filePath, importedMesh);
        return importedMesh;
    }


    public void Activate() {
        renderContext.Activate();
    }

    public void Deactivate() {
        renderContext.Deactivate();
    }

    void FillBuffers() {
        UVBuffer.Create();
        UVBuffer.SetBufferData(UVs, BufferUsageHint.StaticDraw);

        indexBuffer.Create();
        indexBuffer.SetBufferData(in Indices, BufferUsageHint.StaticDraw);
        
        vertexBuffer.Create();
        vertexBuffer.SetBufferData(in Vertices, BufferUsageHint.StaticDraw);

        
    }
}