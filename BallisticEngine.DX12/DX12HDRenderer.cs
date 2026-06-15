using System.Numerics;
using BallisticEngine.DX12;
using BallisticEngine.Rendering;   // BatchGroup<T>
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using GLMatrix4 = OpenTK.Mathematics.Matrix4;
using GLVector3 = OpenTK.Mathematics.Vector3;

namespace BallisticEngine;

// The DX12 forward renderer. Minimal opaque path (first light on a real scene): iterate the scene's
// static mesh renderers, draw each submesh with its material's diffuse map under a directional N·L +
// ambient, ACES-tonemapped, into an offscreen color+depth target. NO shadows/IBL/full-PBR/post yet —
// those layer on in later milestones (Docs/Plans/dx-native-abstraction-redesign.md). This proves the
// real path end-to-end: engine mesh buffers -> input layout -> per-draw CBV + per-material SRV table ->
// depth-tested draw -> readback.
//
// Drives shading via constant buffers + descriptor tables directly (NOT the GL per-name uniform API),
// and uses NO reflection on the per-frame path (standing rule): it iterates a typed RuntimeSet and reads
// typed properties only.
public sealed class DX12HDRenderer : HDRenderer {
    readonly Dx12Device dev;
    Dx12OffscreenTarget target;
    int targetW = 1920, targetH = 1080;

    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;

    // Skybox pass (background): its own root sig (CBV b0 + cube SRV t0 + clamp sampler) + PSO (LEqual,
    // no depth write, cull none, SV_VertexID cube). Drawn after opaque in the same command list.
    ID3D12RootSignature skyRootSig;
    ID3D12PipelineState skyPso;
    ID3D12Resource skyCb;          // upload heap, one SkyboxConstants, rewritten per frame
    unsafe byte* skyCbMapped;
    Dx12DescriptorHeap skySrvVisible;   // one cube SRV copied per frame

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SkyboxConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Matrix4x4 SkyRotation;
        public float Exposure; public Vector3 Pad;
    }

    // Procedural sky pass (atmosphere marched per-pixel; no cubemap, no SRV — pure ALU).
    ID3D12RootSignature procSkyRootSig;
    ID3D12PipelineState procSkyPso;
    ID3D12Resource procSkyCb;
    unsafe byte* procSkyCbMapped;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ProcSkyConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Vector3 SunDirection; public float SunAngularRadius;
        public Vector3 SunRadiance; public float SunDiskIntensity;
        public Vector3 GroundAlbedo; public float AirDensity;
        public float Haze, HazeAnisotropy, OzoneDensity, MultiScatter;
        public float Exposure; public Vector3 Pad;
    }

    // Per-draw constant buffer ring: one upload heap sub-allocated in 256-byte slots, one slot per draw.
    ID3D12Resource cbRing;
    int cbSlotSize;
    int cbSlotCount;
    unsafe byte* cbMapped;

    // Shader-visible SRV heap: per draw we copy the material's diffuse SRV into the next slot and point
    // the root descriptor table at it. Reset each frame.
    Dx12DescriptorHeap srvVisible;

    // Matches StandardOpaque.hlsl's cbuffer DrawConstants byte-for-byte (16-byte-aligned rows).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DrawConstants {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Vector3 LightDir; public float Exposure;
        public Vector3 LightColor; public float Metallic;
        public Vector3 Ambient; public float Roughness;
        public Vector3 CameraPos; public float SpecularReflectance;
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor; public float HasEmissive;
        public float NormalStrength, NormalFlipY, HasMetallicMap, HasRoughnessMap;
        public float PackedOrm, Cutout, Pad0, Pad1;
    }

    // The 6 material maps in HLSL register(t0..t5) order.
    const int MaterialSrvCount = 6;

    public DX12HDRenderer(Dx12Device device) {
        dev = device;
    }

    public override RenderHandle SceneColorHandle => RenderHandle.None;
    public override RenderHandle GameColorHandle => RenderHandle.None;

    public override void ResizeSceneTarget(int width, int height) => Resize(width, height);
    public override void ResizeGameTarget(int width, int height) => Resize(width, height);

    void Resize(int width, int height) {
        if (width <= 0 || height <= 0) return;
        if (target != null && width == targetW && height == targetH) return;
        targetW = width; targetH = height;
        target?.Dispose();
        target = new Dx12OffscreenTarget(dev, width, height, withDepth: true);
    }

    public override unsafe void Initialize() {
        target = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: true);
        BuildRootSignature();
        BuildPipeline();

        cbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<DrawConstants>() + 255) & ~255;
        cbSlotCount = 8192;   // submesh draws per frame ceiling (SunTemple ~hundreds)
        cbRing = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cbSlotSize * cbSlotCount)), ResourceStates.GenericRead);
        cbMapped = cbRing.Map<byte>(0);

        // 6 SRVs per draw (the material table) — size the ring for the worst-case draw count.
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            cbSlotCount * MaterialSrvCount, shaderVisible: true);

        BuildSkybox();
        BuildProcSky();
    }

    unsafe void BuildProcSky() {
        // CBV-only root sig (the atmosphere is pure ALU — no textures).
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        procSkyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ProceduralSky.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "ProceduralSky.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "ProceduralSky.hlsl");
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = procSkyRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        procSkyPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<ProcSkyConstants>() + 255) & ~255;
        procSkyCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        procSkyCbMapped = procSkyCb.Map<byte>(0);
    }

    unsafe void BuildSkybox() {
        // Root sig: CBV b0 + 1 cube SRV table (t0) + static clamp sampler.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        skyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { sampler })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Skybox.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Skybox.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Skybox.hlsl");
        // Depth: test LEqual, NO write — fills only far-plane (uncovered) pixels behind opaque geometry.
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = skyRootSig, VertexShader = vs, PixelShader = ps,
            InputLayout = null,   // SV_VertexID cube, no vertex buffer
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque, DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        skyPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<SkyboxConstants>() + 255) & ~255;
        skyCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        skyCbMapped = skyCb.Map<byte>(0);
        skySrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true);
    }

    void BuildRootSignature() {
        // b0 = per-draw constants (root CBV); table0 = 6 SRVs (diffuse/normal/metallic/roughness/AO/
        // emissive) at t0..t5; static sampler s0.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);

        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[] { cbv, srvTable }, new[] { sampler });
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(desc));
    }

    void BuildPipeline() {
        // Fully-qualified: the GL backend also has a BallisticEngine.EmbeddedShaderSource (ReadGlsl), and
        // this file is in namespace BallisticEngine, so the unqualified name would resolve to the GL one.
        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("StandardOpaque.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "StandardOpaque.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "StandardOpaque.hlsl");

        // Separate input slots: the engine keeps pos/normal/uv/tangent in separate GPU buffers — one
        // InputElement per slot, each at offset 0 in its own slot. (Interleaving is a later optimization.)
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));

        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue,
            // RH mesh wound CCW-from-front; DX default front face is clockwise, so CullClockwise culls
            // back faces for CCW geometry (matches the cube test).
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public override unsafe RenderMetrics BeginRender(RendererArgs args) {
        IViewProjectionProvider vp = args.viewProjectionProvider;
        if (vp is null || target is null)
            return default;

        // Camera. The provider's view (LookAt) is convention-agnostic — convert 1:1. Rebuild the
        // projection DX-style (RH, z in [0,1]) since the provider's is OpenTK GL-convention (z in [-1,1]).
        Matrix4x4 view = ToNumerics(vp.GetViewMatrix());
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            45f * (MathF.PI / 180f), (float)targetW / targetH, 0.1f, 1000f);
        Matrix4x4 viewProj = view * proj;

        Vector3 camPos = ToNumerics(vp.Transform.WorldPosition);
        LightUniforms light = LightUniforms.Resolve();
        Vector3 lightDir = ToNumerics(light.Direction);
        Vector3 lightColor = ToNumerics(light.Color);
        Vector3 ambient = ToNumerics(vp.AmbientColor) * MathF.Max(0.05f, light.AmbientIntensity);
        // The sun radiance is HDR (lux-scaled, ~80000); a fixed pre-exposure brings it into a viewable
        // range before the ACES tonemap (the GL path auto-meters EV100; this is a constant stand-in for
        // first light). Tunable via BALLISTIC_DX12_EXPOSURE while dialing against the frozen baseline.
        // 1e-5 lands the PBR path (energy-conserving ÷π diffuse) near the GL baseline brightness; the DX12
        // image is intentionally a touch dimmer (no IBL ambient / shadows yet — those are next milestones).
        float exposure = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
            System.Globalization.CultureInfo.InvariantCulture, out float e) ? e : 1.0e-5f;

        int draws = 0;
        long tris = 0;
        srvVisible.Reset();
        int slot = 0;

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;

        target.RenderIntoCleared(0.0f, 0.0f, 0.0f, cl => {
            cl.SetGraphicsRootSignature(rootSig);
            cl.SetPipelineState(pso);
            cl.SetDescriptorHeaps(srvVisible.Heap);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;

                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<OpenTK.Mathematics.Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<OpenTK.Mathematics.Vector4>;
                if (vb?.Resource is null || ib?.Resource is null ||
                    nb?.Resource is null || ub?.Resource is null || tb?.Resource is null) continue;

                Matrix4x4 model = ToNumerics(r.Transform.WorldMatrix);
                Matrix4x4 mvp = model * viewProj;

                Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
                vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                cl.IASetVertexBuffers(0, vbViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;

                for (int s = first; s <= last; s++) {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    Material mat = r.MaterialFor(s);
                    if (mat is null) continue;
                    if (slot >= cbSlotCount) break;

                    bool hasMetal = mat.Metallic is not null;
                    bool hasRough = mat.Roughness is not null;
                    bool emissive = mat.IsEmissive;
                    var c = new DrawConstants {
                        Mvp = Matrix4x4.Transpose(mvp),
                        Model = Matrix4x4.Transpose(model),
                        LightDir = lightDir, Exposure = exposure,
                        LightColor = lightColor, Metallic = mat.MetallicFactor,
                        Ambient = ambient, Roughness = mat.RoughnessFactor,
                        CameraPos = camPos, SpecularReflectance = mat.SpecularReflectance,
                        BaseColorFactor = ToNumerics(mat.BaseColorFactor),
                        EmissiveFactor = ToNumerics(mat.EmissiveColor) * mat.EmissiveIntensity,
                        HasEmissive = emissive ? 1f : 0f,
                        NormalStrength = mat.NormalStrength, NormalFlipY = mat.NormalFlipY ? 1f : 0f,
                        HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                        PackedOrm = mat.PackedOrm ? 1f : 0f, Cutout = mat.Cutout ? 1f : 0f,
                    };
                    *(DrawConstants*)(cbMapped + (long)slot * cbSlotSize) = c;
                    cl.SetGraphicsRootConstantBufferView(0,
                        cbRing.GPUVirtualAddress + (ulong)((long)slot * cbSlotSize));

                    // Copy the 6 material SRVs into a contiguous shader-visible table region (t0..t5).
                    // Null slots resolve to the type's neutral default so every descriptor is valid.
                    int tableStart = srvVisible.AllocateRange(MaterialSrvCount);
                    BindSrv(tableStart + 0, mat.Diffuse, TextureType.Diffuse, fallbackDiffuse);
                    BindSrv(tableStart + 1, mat.Normal, TextureType.Normal, null);
                    BindSrv(tableStart + 2, mat.Metallic, TextureType.Metallic, null);
                    BindSrv(tableStart + 3, mat.Roughness, TextureType.Roughness, null);
                    BindSrv(tableStart + 4, mat.AO, TextureType.AO, null);
                    BindSrv(tableStart + 5, mat.Emissive, TextureType.Emissive, null);
                    cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(tableStart));

                    cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                    draws++;
                    tris += sub.IndexCount / 3;
                    slot++;
                }
            }

            // --- Sky background (after opaque, same command list) ---
            // ProceduralSky takes precedence over an asset cubemap Skybox (matches the GL renderer).
            if (ProceduralSky.Active is not null)
                DrawProcSky(cl, view, proj, light);
            else
                DrawSkybox(cl, view, proj);
        });

        RenderStats.Scene.DrawCalls = draws;
        RenderStats.Scene.Triangles = tris;
        return new RenderMetrics(draws, 0, (int)tris, 0, 0f);
    }

    // Draw the environment cubemap as the far-plane background (LEqual, no depth write) where opaque
    // geometry didn't cover. No-op if the scene has no Skybox or its cubemap isn't a DX12 cube yet.
    unsafe void DrawSkybox(ID3D12GraphicsCommandList4 cl, Matrix4x4 view, Matrix4x4 proj) {
        if (Skybox.Active?.Cubemap is not Dx12Texture3D cube || cube.Resource is null)
            return;

        // View with translation stripped (the sky cube is centred on the camera).
        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        OpenTK.Mathematics.Vector3 euler = Skybox.Active.RotationEuler;
        Matrix4x4 rot = Matrix4x4.CreateRotationX(euler.X * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationY(euler.Y * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationZ(euler.Z * (MathF.PI / 180f));
        // The skybox texels are HDR scaled by sky.Exposure; fold in the same pre-exposure the opaque pass
        // uses so the sky brightness tracks the scene. (Skybox.Exposure defaults ~5000 for .hdr cubes.)
        float skyExposure = Skybox.Active.Exposure * 1.0e-5f;

        var sc = new SkyboxConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SkyRotation = Matrix4x4.Transpose(rot),
            Exposure = skyExposure,
        };
        *(SkyboxConstants*)skyCbMapped = sc;

        dev.Device.CopyDescriptorsSimple(1, skySrvVisible.Cpu(0), cube.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        cl.SetGraphicsRootSignature(skyRootSig);
        cl.SetPipelineState(skyPso);
        cl.SetDescriptorHeaps(skySrvVisible.Heap);
        cl.SetGraphicsRootConstantBufferView(0, skyCb.GPUVirtualAddress);
        cl.SetGraphicsRootDescriptorTable(1, skySrvVisible.Gpu(0));
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.DrawInstanced(36, 1, 0, 0);
    }

    // Draw the procedural atmosphere as the far-plane background (pure-ALU march by view direction).
    unsafe void DrawProcSky(ID3D12GraphicsCommandList4 cl, Matrix4x4 view, Matrix4x4 proj, LightUniforms light) {
        ProceduralSky sky = ProceduralSky.Active;
        if (sky is null) return;

        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        // Sun: DirectionalLight drives it (LightUniforms.Direction is TOWARD the light = toward the sun).
        Vector3 sunDir = ToNumerics(light.Direction);
        if (sunDir.LengthSquared() < 1e-8f) sunDir = Vector3.UnitY;
        sunDir = Vector3.Normalize(sunDir);
        float sunAngularRadius = (DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f * (MathF.PI / 180f);

        var sc = new ProcSkyConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SunDirection = sunDir, SunAngularRadius = MathF.Max(sunAngularRadius, 1e-4f),
            SunRadiance = ToNumerics(light.Color), SunDiskIntensity = MathF.Max(sky.SunDiskIntensity, 0f),
            GroundAlbedo = ToNumerics(sky.GroundColor), AirDensity = MathF.Max(sky.AirDensity, 0f),
            Haze = MathF.Max(sky.Haze, 0f), HazeAnisotropy = Math.Clamp(sky.HazeAnisotropy, 0f, 0.99f),
            OzoneDensity = MathF.Max(sky.OzoneDensity, 0f), MultiScatter = MathF.Max(sky.MultipleScattering, 1f),
            Exposure = MathF.Max(sky.Exposure, 0f),
        };
        *(ProcSkyConstants*)procSkyCbMapped = sc;

        cl.SetGraphicsRootSignature(procSkyRootSig);
        cl.SetPipelineState(procSkyPso);
        cl.SetGraphicsRootConstantBufferView(0, procSkyCb.GPUVirtualAddress);
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.DrawInstanced(36, 1, 0, 0);
    }

    // Copy one material texture's persistent SRV into the shader-visible table at `visibleSlot`. A null
    // texture resolves to that slot's neutral default (DefaultTextures.Neutral) so the descriptor is
    // always valid — matching the GL Material.Activate fallback (metallic 0, roughness 1, AO 1, flat +Z
    // normal, dark emissive). `explicitFallback` lets diffuse use a white fallback.
    void BindSrv(int visibleSlot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback) {
        var dx = (tex as Dx12Texture2D)
                 ?? explicitFallback
                 ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(visibleSlot), dx.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public override void PostRenderCleanUp() {
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            if (r != null) r.RenderedThisFrame = false;
    }

    public void SaveFrame(string path) => target?.SaveBmp(path);
    public int Width => targetW;
    public int Height => targetH;

    // Internal pipeline steps — no engine/editor caller (BeginRender draws opaques itself).
    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets,
        RendererArgs args, bool isShadowPass) { }
    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args) { }
    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args) { }
    public override void RenderInstancing(Mesh mesh, Material material, GLMatrix4[] transforms, RendererArgs args) { }

    static Matrix4x4 ToNumerics(GLMatrix4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
    static Vector3 ToNumerics(GLVector3 v) => new(v.X, v.Y, v.Z);
    static Vector4 ToNumerics(OpenTK.Mathematics.Vector4 v) => new(v.X, v.Y, v.Z, v.W);
}
