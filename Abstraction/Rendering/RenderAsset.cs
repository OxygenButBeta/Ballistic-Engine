using BallisticEngine;
using OpenTK.Mathematics;

public abstract class RenderAsset
{
    public static RenderAsset Current { get; protected set; }
    public abstract bool InstancedDrawing { get; }
    public abstract HDRenderer Renderer { get; protected set; }
    public abstract void Initialize();
    public abstract RenderContext CreateRenderContext();
    public abstract GPUBuffer<uint> CreateIndexBuffer(RenderContext renderContext);
    public abstract GPUBuffer<Vector3> CreateVertexBuffer3(RenderContext renderContext);
    public abstract GPUBuffer<Vector2> CreateUVBuffer(RenderContext renderContext);
    public abstract GPUBuffer<Vector3> CreateNormalBuffer(RenderContext renderContext);

    public abstract GPUBuffer<Vector3> CreateTangentBuffer(RenderContext renderContext);

    public abstract GPUBuffer<T> CreateBuffer<T>(RenderContext renderContext) where T : unmanaged;
    public abstract InstancedBuffer CreateInstancedBuffer(RenderContext renderContext);
    public abstract Texture2D CreateTexture2D(string filePath, TextureType type);
    public abstract Texture3D CreateTexture3D(string[] paths);
    public abstract GPUBuffer<Vector2> CreateVertexBuffer2(RenderContext renderContext);
}