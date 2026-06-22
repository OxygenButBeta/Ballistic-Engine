using BallisticEngine.DX12;

namespace BallisticEngine;

public sealed class DirectXRenderAsset : RenderAsset {
    public override HDRenderer Renderer { get; protected set; }

    Dx12Device device;

    public override void Initialize() {
        Current = this;

        var project = AssetDatabase.Project;
        if (project is not null) {
            Dx12ShaderCompiler.CacheDirectory = System.IO.Path.Combine(project.LibraryPath, "ShaderCache");
            Dx12Device.PsoCacheDirectory = System.IO.Path.Combine(project.LibraryPath, "PsoCache");
        }

        bool debugLayer = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DEBUG") == "1";
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV") == "1") debugLayer = true;
        device = new Dx12Device(enableDebugLayer: debugLayer);
        Dx12Backend.Initialize(device);

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_COMPUTE_TEST") == "1") {
            bool pass = DX12.Dx12ComputeProbe.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_BINDLESS_TEST") == "1") {
            bool pass = DX12.Dx12BindlessProbe.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_FSR_TEST") == "1") {
            bool pass = DX12.Dx12FsrUpscaler.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_DXR_TEST") == "1") {
            bool pass = DX12.Dx12DxrProbe.SelfTest(device);
            Environment.Exit(pass ? 0 : 1);
        }

        // FAZ -1: render-graph v2 (Dx12RgGraph) compile-pipeline self-test on the real device.
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_RG_SELFTEST") == "1") {
            string report;
            try { report = DX12.Dx12RgSelfTest.Run(device); }
            catch (Exception ex) { report = "FAILED:\n" + ex; }
            // Also drive the REAL GPU-execute path (barriers + transient aliasing + readback verify).
            string executeReport;
            try { executeReport = DX12.Dx12RgExecuteSelfTest.RunExecute(device); }
            catch (Exception ex) { executeReport = "[Dx12RgExecuteSelfTest] FAILED (outer):\n" + ex; }
            report = report + "\n" + executeReport;
            Console.Error.WriteLine("[DX12] Render-graph v2 (Dx12RgGraph) SELF-TEST:\n" + report);
            string outPath = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RG_SELFTEST_OUT");
            if (!string.IsNullOrEmpty(outPath)) System.IO.File.WriteAllText(outPath, report);
        }

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
        tex.UploadPublic(in data, type);
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
