using BallisticEngine;
using OpenTK.Mathematics;

public abstract class RenderAsset
{
    public static RenderAsset Current { get; protected set; }
    public abstract HDRenderer Renderer { get; protected set; }
    public abstract void Initialize();
    public abstract RenderContext CreateRenderContext();
    public abstract GPUBuffer<uint> CreateIndexBuffer(RenderContext renderContext);
    public abstract GPUBuffer<Vector3> CreateVertexBuffer3(RenderContext renderContext);
    public abstract GPUBuffer<Vector2> CreateUVBuffer(RenderContext renderContext);
    public abstract GPUBuffer<Vector3> CreateNormalBuffer(RenderContext renderContext);

    public abstract GPUBuffer<Vector4> CreateTangentBuffer(RenderContext renderContext);

    // Skinning vertex attributes (location 8 = bone indices, 9 = weights); only skinned meshes
    // create them. Both are Vector4 float buffers (indices are rounded to ints in the shader).
    public abstract GPUBuffer<Vector4> CreateBoneIndexBuffer(RenderContext renderContext);
    public abstract GPUBuffer<Vector4> CreateBoneWeightBuffer(RenderContext renderContext);

    public abstract GPUBuffer<T> CreateBuffer<T>(RenderContext renderContext) where T : unmanaged;
    public abstract InstancedBuffer CreateInstancedBuffer(RenderContext renderContext);
    public abstract Texture2D CreateTexture2D(in TextureData data, TextureType type);
    public abstract Texture3D CreateCubemap(TextureData[] faces);
    public abstract GPUBuffer<Vector2> CreateVertexBuffer2(RenderContext renderContext);

    // Backend-created shader program. The factory lives here (not hardcoded in GraphicAPI) so the
    // active backend decides the concrete type — GL builds a GLSL program, a DX12 backend would build
    // an HLSL one — exactly like the buffer/texture factories above.
    public abstract StandardShader CreateStandardShader(string vertexCode, string fragmentCode);
}