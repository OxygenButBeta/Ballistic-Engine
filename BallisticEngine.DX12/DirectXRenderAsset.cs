using BallisticEngine.DX12;
using OpenTK.Mathematics;

namespace BallisticEngine;

// The DX12 backend's RenderAsset — the single factory the engine creates GPU resources through
// (RenderAsset.Current). Mirrors OpenGLRenderAsset but mints DX12 resource types. Set as Current and
// brings up the device in Initialize, exactly like the GL asset, so the rest of the engine (Mesh,
// Material, GraphicAPI) is backend-agnostic and unchanged.
//
// Full-DX strategy: this is THE backend going forward (GL is being retired), not a side-by-side peer.
public sealed class DirectXRenderAsset : RenderAsset {
    public override HDRenderer Renderer { get; protected set; }

    Dx12Device device;

    public override void Initialize() {
        Current = this;
        // Debug layer OFF by default: the D3D12 debug layer is not reliably thread-safe under the heavy
        // concurrent resource creation the engine's worker-thread asset loading does (it spuriously
        // E_FAILs CreateCommittedResource). Opt in with BALLISTIC_DX12_DEBUG=1 for single-threaded debugging.
        bool debugLayer = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DEBUG") == "1";
        device = new Dx12Device(enableDebugLayer: debugLayer);
        Dx12Backend.Initialize(device);

        Renderer = new DX12HDRenderer(device);
        Renderer.Initialize();
    }

    public override RenderContext CreateRenderContext() => new Dx12RenderContext();

    public override GPUBuffer<uint> CreateIndexBuffer(RenderContext renderContext) =>
        new Dx12IndexBuffer(renderContext);

    public override GPUBuffer<Vector3> CreateVertexBuffer3(RenderContext renderContext) =>
        new Dx12Buffer<Vector3>(renderContext);

    public override GPUBuffer<Vector2> CreateVertexBuffer2(RenderContext renderContext) =>
        new Dx12Buffer<Vector2>(renderContext);

    public override GPUBuffer<Vector2> CreateUVBuffer(RenderContext renderContext) =>
        new Dx12Buffer<Vector2>(renderContext);

    public override GPUBuffer<Vector3> CreateNormalBuffer(RenderContext renderContext) =>
        new Dx12Buffer<Vector3>(renderContext);

    public override GPUBuffer<Vector4> CreateTangentBuffer(RenderContext renderContext) =>
        new Dx12Buffer<Vector4>(renderContext);

    public override GPUBuffer<Vector4> CreateBoneIndexBuffer(RenderContext renderContext) =>
        new Dx12Buffer<Vector4>(renderContext);

    public override GPUBuffer<Vector4> CreateBoneWeightBuffer(RenderContext renderContext) =>
        new Dx12Buffer<Vector4>(renderContext);

    public override GPUBuffer<T> CreateBuffer<T>(RenderContext renderContext) =>
        new Dx12Buffer<T>(renderContext);

    public override InstancedBuffer CreateInstancedBuffer(RenderContext renderContext) =>
        new Dx12InstancedBuffer(renderContext);

    public override Texture2D CreateTexture2D(in TextureData data, TextureType type) {
        var tex = new Dx12Texture2D();
        tex.UploadPublic(in data, type);   // Upload is protected; invoke via the internal entry below
        return tex;
    }

    public override Texture3D CreateCubemap(TextureData[] faces) {
        var tex = new Dx12Texture3D();
        tex.UploadFacesPublic(faces);
        return tex;
    }

    public override StandardShader CreateStandardShader(string vertexCode, string fragmentCode) =>
        new Dx12StandardShader(vertexCode, fragmentCode);
}
