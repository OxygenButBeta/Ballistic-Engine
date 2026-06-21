using BallisticEngine.DX12;

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

        // Persist compiled DXIL under Library\ShaderCache so a relaunch with unchanged .hlsl skips DXC
        // entirely (boot used to re-compile ~150 shaders every launch — seconds between device-ready and
        // first frame). Keyed by source hash, so a shader edit auto-invalidates. Headless tools without a
        // project leave this null → always compile (old behaviour).
        var project = AssetDatabase.Project;
        if (project is not null) {
            Dx12ShaderCompiler.CacheDirectory = System.IO.Path.Combine(project.LibraryPath, "ShaderCache");
            // PSO disk cache (ID3D12PipelineLibrary) lives next to the DXIL cache. The shader cache skips the DXC
            // FRONT-end (HLSL→DXIL); the PSO cache skips the driver BACK-end (DXIL→native PSO) on a warm start — the
            // two stack for the full cold-start compile-stutter win. Both keyed/validated so a hit == a fresh build.
            Dx12Device.PsoCacheDirectory = System.IO.Path.Combine(project.LibraryPath, "PsoCache");
        }
        // Debug layer OFF by default: the D3D12 debug layer is not reliably thread-safe under the heavy
        // concurrent resource creation the engine's worker-thread asset loading does (it spuriously
        // E_FAILs CreateCommittedResource). Opt in with BALLISTIC_DX12_DEBUG=1 for single-threaded debugging.
        bool debugLayer = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DEBUG") == "1";
        // GPU-Based Validation REQUIRES the debug layer; requesting GBV without it would be a silent no-op.
        // So BALLISTIC_DX12_GBV=1 implies the debug layer is on (the device reads BALLISTIC_DX12_GBV itself).
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV") == "1") debugLayer = true;
        device = new Dx12Device(enableDebugLayer: debugLayer);
        Dx12Backend.Initialize(device);

        // Compute-foundation self-test door (BALLISTIC_DX12_COMPUTE_TEST=1): verify the compute PSO + UAV +
        // InterlockedAdd + readback path the GPU-driven cull is built on, then exit. Isolated harness.
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_COMPUTE_TEST") == "1") {
            bool pass = DX12.Dx12ComputeProbe.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }
        // Bindless-foundation self-test door (SM6.6 ResourceDescriptorHeap) — see Dx12BindlessProbe.
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_BINDLESS_TEST") == "1") {
            bool pass = DX12.Dx12BindlessProbe.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }
        // FSR upscaler self-test door (BALLISTIC_DX12_FSR_TEST=1): loads the FidelityFX loader+provider
        // DLLs, creates an upscale context, and queries the render resolution per quality mode, then exits.
        // Proves the P/Invoke ABI + native DLL deployment before FSR is wired into the frame.
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_FSR_TEST") == "1") {
            bool pass = DX12.Dx12FsrUpscaler.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }
        // DXR foundation self-test door (BALLISTIC_DX12_DXR_TEST=1): builds a tiny BLAS/TLAS + RT PSO + SBT
        // and DispatchRays a triangle, verifying hit/miss. Proves the ray-tracing pipeline before RT effects.
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_DXR_TEST") == "1") {
            bool pass = DX12.Dx12DxrProbe.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }
        // GpuSceneQuery self-test door (BALLISTIC_DX12_SCENEQUERY_TEST=1): builds a known box AS and verifies
        // the inline-RayQuery occupancy/visibility/classify primitives + determinism against ground truth.
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_SCENEQUERY_TEST") == "1") {
            bool pass = DX12.Dx12SceneQueryProbe.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }

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

    public override StandardShader CreateStandardShader(string vertexCode, string fragmentCode, string identityExtra = null) =>
        new Dx12StandardShader(vertexCode, fragmentCode, identityExtra);
}
