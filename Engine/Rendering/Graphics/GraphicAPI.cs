using System.Runtime.CompilerServices;
using BallisticEngine;

public static class GraphicAPI
{
    // identityExtra distinguishes shaders that share the same vertex/fragment GLSL but DIFFER in their
    // declared properties or custom surface body (a custom .shader reuses the Standard Vert/Frag.glsl but
    // adds a `surface:`/`properties:` block). Without it, SharedResources would hand the custom shader and
    // the plain Standard shader the SAME cached instance — and the loader's SetProperties/SurfaceSource on
    // one would leak onto the other (every Standard material would inherit the custom surface). Pass the
    // .shader asset path for such shaders; pass null for the plain Standard shader so its cache key (and
    // the single shared instance every Standard material uses) is unchanged → byte-identical.
    public static StandardShader CreateStandardShader(string vertexCode, string fragmentCode,
        string identityExtra = null)
    {
        var identity = identityExtra is null
            ? ResourceIdentity.Combine(vertexCode, fragmentCode)
            : ResourceIdentity.Combine(vertexCode, fragmentCode, identityExtra);
        if (SharedResources<Shader>.TryGetResource(identity, out Shader cachedShader))
            return cachedShader as StandardShader;

        // The active backend builds the concrete shader (GL -> GLSL program, DX12 -> HLSL) — no
        // hardcoded GL type here, so GraphicAPI is backend-agnostic.
        return RenderAsset.Current.CreateStandardShader(vertexCode, fragmentCode, identityExtra);
    }
    public static HDRenderer Renderer => RenderAsset.Current.Renderer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RenderContext CreateRenderContext() => RenderAsset.Current.CreateRenderContext();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<uint> CreateIndexBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateIndexBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector3> CreateVertexBuffer3(RenderContext renderContext) =>
        RenderAsset.Current.CreateVertexBuffer3(renderContext);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector2> CreateVertexBuffer2(RenderContext renderContext) =>
        RenderAsset.Current.CreateVertexBuffer2(renderContext);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector2> CreateVertexBuffer2D(RenderContext renderContext) =>
        RenderAsset.Current.CreateVertexBuffer2(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector2> CreateUVBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateUVBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector3> CreateNormalBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateNormalBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector4> CreateTangentBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateTangentBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector4> CreateBoneIndexBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateBoneIndexBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<Vector4> CreateBoneWeightBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateBoneWeightBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GPUBuffer<T> CreateBuffer<T>(RenderContext renderContext) where T : unmanaged =>
        RenderAsset.Current.CreateBuffer<T>(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InstancedBuffer CreateInstancedBuffer(RenderContext renderContext) =>
        RenderAsset.Current.CreateInstancedBuffer(renderContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Texture2D CreateTexture2D(in TextureData data, TextureType type) =>
        RenderAsset.Current.CreateTexture2D(in data, type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Texture3D CreateCubemap(TextureData[] faces) => RenderAsset.Current.CreateCubemap(faces);
}