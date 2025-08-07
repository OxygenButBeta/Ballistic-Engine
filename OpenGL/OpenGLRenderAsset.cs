using BallisticEngine;
using BallisticEngine.OpenGL;
using OpenTK.Mathematics;

public sealed class OpenGLRenderAsset : RenderAsset
{
    public override HDRenderer Renderer { get; protected set; }

    public override void Initialize()
    {
        Renderer = new OpenGLHDRenderer();
        Renderer.Initialize();
        Current = this;
    }

    public override RenderContext CreateRenderContext() => new OpenGLRenderContext();
    public override GPUBuffer<uint> CreateIndexBuffer(RenderContext renderContext) => new GLIndexBuffer(renderContext);
    public override GPUBuffer<Vector3> CreateVertexBuffer(RenderContext renderContext) => new GL3DBuffer(renderContext);
    public override GPUBuffer<Vector2> CreateUVBuffer(RenderContext renderContext) => new GLUVBuffer2D(renderContext);

    public override Texture2D CreateTexture2D(string filePath, TextureType type) =>
        Texture.ImportFromFile<GLTexture2D>(filePath, type);

    public override Texture3D CreateTexture3D(string filePath) =>
        Texture.ImportFromFile<GLTexture3D>(filePath, TextureType.Diffuse);
}