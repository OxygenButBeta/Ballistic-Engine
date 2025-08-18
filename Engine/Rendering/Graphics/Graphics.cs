using System.Runtime.CompilerServices;
using BallisticEngine;
using OpenTK.Mathematics;

public static class Graphics
{
    public static StandardShader CreateStandardShader(string vertexCode, string fragmentCode)
    {
        if (SharedResources<Shader>.TryGetResource(ResourceIdentity.Combine(vertexCode, fragmentCode),
                out Shader cachedShader))
            return cachedShader as StandardShader;

        return new GLStandardShader(vertexCode, fragmentCode);
    }

    public static HDRenderer Renderer => RenderAsset.Current.Renderer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RenderContext CreateRenderContext() => RenderAsset.Current.CreateRenderContext();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<uint> CreateIndexBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateIndexBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector3> CreateVertexBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateVertexBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector2> CreateUVBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateUVBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector3> CreateNormalBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateNormalBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector3> CreateTangentBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateTangentBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<T> CreateBuffer<T>(RenderContext renderContext) where T : unmanaged =>
        RenderAsset.Current.CreateBuffer<T>(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InstancedBuffer CreateInstancedBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateInstancedBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Texture2D CreateTexture2D(string filePath, TextureType type) =>
        RenderAsset.Current.CreateTexture2D(filePath, type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Texture3D CreateTexture3D(string[] paths) => RenderAsset.Current.CreateTexture3D( paths);
}