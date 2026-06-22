using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12ReflectionsPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Reflections;
    public string Name => "Reflections";

    public bool Enabled(Dx12FrameContext ctx) {
        if (reflForceEnvUnread) { reflForceEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_REFLECTIONS"); reflForceEnvUnread = false; }
        if (ctx.Doors.Minimal) return false;
        if (reflForceEnv == "1") return true;
        if (reflForceEnv == "0") return false;
        if (ctx.PostFX.SsrIntensity <= 0f) return false;
        return ctx.PostFX.SsrEnabled;
    }

    float ForcedIntensity(Dx12FrameContext ctx) =>
        reflForceEnv == "1" && ctx.PostFX.SsrIntensity <= 0f ? EnvF("BALLISTIC_DX12_REFLECTIONS_INTENSITY", 1f) : ctx.PostFX.SsrIntensity;
    static float EnvF(string n, float f) => float.TryParse(Environment.GetEnvironmentVariable(n),
        System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : f;
    string reflForceEnv; bool reflForceEnvUnread = true;

    static bool? reflTemporalEnabled;
    static bool ReflTemporalEnabled =>
        reflTemporalEnabled ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_REFL_NOTEMPORAL") != "1";

    static string? rtrEnvCached; static bool rtrEnvRead;
    static string RtrEnv() { if (!rtrEnvRead) { rtrEnvCached = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_REFLECTIONS"); rtrEnvRead = true; } return rtrEnvCached!; }
    static bool? reflNoCards;
    static bool ReflCardsAllowed => !(reflNoCards ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_REFL_NOCARDS") == "1");

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    readonly Dx12Device dev;

    ID3D12RootSignature ssrRootSig;
    ID3D12PipelineState ssrMarchPso, ssrCombinePso;
    ID3D12Resource ssrCb;
    unsafe byte* ssrCbMapped;
    int ssrCbStride;

    long SsrCbOffset => (long)dev.FrameSlot * ssrCbStride;
    Dx12OffscreenTarget ssrTarget;
    Dx12OffscreenTarget ssrScene;

    Dx12DescriptorHeap ssrSrvVisible;

    Dx12OffscreenTarget ssrHistoryA, ssrHistoryB;
    ID3D12PipelineState ssrTemporalPso;
    bool ssrHistWriteB, ssrHistValid;
    [StructLayout(LayoutKind.Sequential)]
    struct SsrConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 ViewMatrix;
        public float Intensity; public Vector3 Pad;
        public Vector2 TexelSize; public Vector2 Pad2;
    }

    ID3D12RootSignature rtReflRootSig;
    ID3D12StateObject rtReflPso;
    ID3D12Resource rtReflSbt;

    Dx12FrameCb<RtReflConstants> rtReflCb;
    Dx12FrameCb<RtGiSun> rtReflSunCb;
    Dx12FrameCb<RtReflGridConstants> rtReflGridCb;
    bool rtReflBuilt;
    const int RtSbtSlot = 64;

    const int RtReflTableBase = Dx12BindlessTail.RtReflTableBase;
    [StructLayout(LayoutKind.Sequential)]
    struct RtReflConstants {
        public Matrix4x4 InvViewProj; public Vector3 CameraPos; public float Intensity;
        public float PrefilterMaxMip; public float NormalBias; public float UseCards; public float FrameIndex;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RtGiSun { public Vector3 SunDir; public float NormalBias; public Vector3 SunColor; public float LightCount; }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    struct RtReflGridConstants
    {
        public System.Numerics.Vector3 Origin;  public float Pad0;
        public System.Numerics.Vector3 Spacing; public float Pad1;
        public uint CountX, CountY, CountZ;      public uint Pad2;
    }

    public unsafe Dx12ReflectionsPass(Dx12Device device, int width, int height) {
        dev = device;
        BuildSsr();
        Resize(width, height);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "ssrTarget", "ssrScene");
        string rtrEnv = RtrEnv();
        bool rtReflWanted = rtrEnv == "1" || (rtrEnv != "0" && ctx.PostFX.ReflectionMode == ReflectionMode.RayTraced);
        if (rtReflWanted && EnsureRtReflections(ctx))
            DrawRtReflections(ctx);
        else
            DrawSsr(ctx);
    }

    unsafe void DrawSsr(Dx12FrameContext ctx) {
        var dev = ctx.Dev; var target = ctx.SceneColor; var gbuffer = ctx.GBuffer;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)(ssrCbMapped + SsrCbOffset) = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view),
            Intensity = ForcedIntensity(ctx),
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };

        if (!ctx.BarriersDerived) {
            target.ColorToShaderResource();
            gbuffer.DepthToShaderResource();
        }

        ssrSrvVisible.Reset();
        int mb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 4), ssrTarget.ColorSrvCpu, heapType);
        ssrTarget.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrMarchPso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress + (ulong)SsrCbOffset);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(mb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        ssrTarget.ColorToShaderResource();
        Dx12OffscreenTarget reflForCombine = ReflTemporalEnabled
            ? DenoiseReflectionTemporal(ctx, gbuffer)
            : ssrTarget;

        Matrix4x4.Invert(proj, out Matrix4x4 invProjC);
        *(SsrConstants*)(ssrCbMapped + SsrCbOffset) = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProjC),
            ViewMatrix = Matrix4x4.Transpose(view), Intensity = ForcedIntensity(ctx),
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };
        ssrSrvVisible.Reset();
        int cb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 4), reflForCombine.ColorSrvCpu, heapType);
        ssrScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrCombinePso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress + (ulong)SsrCbOffset);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssrScene.ColorToShaderResource();
        target.CopyColorFrom(ssrScene);
    }

    unsafe Dx12OffscreenTarget DenoiseReflectionTemporal(Dx12FrameContext ctx, Dx12GBuffer gbuffer) {
        var dev = ctx.Dev;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget histRead = ssrHistWriteB ? ssrHistoryA : ssrHistoryB;
        Dx12OffscreenTarget histWrite = ssrHistWriteB ? ssrHistoryB : ssrHistoryA;
        histRead.ColorToShaderResource();
        *(SsrConstants*)(ssrCbMapped + SsrCbOffset) = new SsrConstants {
            Intensity = ssrHistValid ? 1f : 0f, TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };
        ssrSrvVisible.Reset();
        int tb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 0), ssrTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 1), histRead.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 3), ssrTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 4), ssrTarget.ColorSrvCpu, heapType);
        histWrite.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrTemporalPso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress + (ulong)SsrCbOffset);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(tb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        histWrite.ColorToShaderResource();
        ssrHistWriteB = !ssrHistWriteB; ssrHistValid = true;
        return histWrite;
    }

    unsafe bool EnsureRtReflections(Dx12FrameContext ctx) {
        if (!ctx.Dxr.CheckAvailable("RTReflections")) return false;
        if (rtReflBuilt) return true;
        rtReflBuilt = true;

        var dev = ctx.Dev;
        var device5 = ctx.Dxr.Device5;

        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var cbv2 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 7, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
        var probeSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);
        var cardSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(11, 0), ShaderVisibility.All);
        var metaSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(12, 0), ShaderVisibility.All);
        var triClusterSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(13, 0), ShaderVisibility.All);
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rtReflRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, cbv2, table, matSrv, instSrv, lightSrv, probeSrv, cardSrv, metaSrv, triClusterSrv }, new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrReflections.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrReflections.hlsl");
        var subs = new[] {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(16, 8)), new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtReflRootSig)),
        };
        rtReflPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtReflPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtReflSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtReflSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot, (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtReflSbt.Unmap(0);

        rtReflCb = new Dx12FrameCb<RtReflConstants>(dev);
        rtReflSunCb = new Dx12FrameCb<RtGiSun>(dev);
        rtReflGridCb = new Dx12FrameCb<RtReflGridConstants>(dev);
        _ = ctx.Dxr.RtGeometry;
        return true;
    }

    unsafe void DrawRtReflections(Dx12FrameContext ctx) {
        var dev = ctx.Dev; var target = ctx.SceneColor; var gbuffer = ctx.GBuffer; var ibl = ctx.Ibl;
        var gpuDriven = ctx.GpuDriven; var clusteredLights = ctx.ClusteredLights;
        var sceneAS = ctx.Dxr.SceneAS; var rtGeometry = ctx.Dxr.RtGeometry;
        Matrix4x4 view = ctx.View, viewProj = ctx.ViewProj, proj = ctx.Proj;
        Vector3 camPos = ctx.CamPos, lightDir = ctx.LightDir, lightColor = ctx.LightColor;

        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) { DrawSsr(ctx); return; }

        gpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        rtGeometry.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, gpuDriven);

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        // Aurora radiance cache at reflection hits when Aurora is active this frame + has a valid cache.
        bool useCards = ctx.AuroraActiveThisFrame && ctx.AuroraScene is { Valid: true } && ctx.PostFX.AuroraReflections
                        && ReflCardsAllowed;
        float reflFrameIndex = ctx.DeterministicCapture ? -1f : (ctx.FrameCounter & 1023);
        rtReflCb.Write(new RtReflConstants {
            InvViewProj = Matrix4x4.Transpose(invVP), CameraPos = camPos, Intensity = ForcedIntensity(ctx),
            PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f, NormalBias = 0.05f,
            UseCards = useCards ? 1f : 0f,
            FrameIndex = reflFrameIndex,
        });
        Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        rtReflSunCb.Write(new RtGiSun {
            SunDir = sunDir, NormalBias = 0.03f, SunColor = lightColor, LightCount = clusteredLights.LightCount,
        });
        rtReflGridCb.Write(default);

        target.ColorToShaderResource();
        gbuffer.DepthToNonPixelShaderResource();

        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        sceneAS.CreateTlasSrv(bindless.Cpu(RtReflTableBase + 0));
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 4), ibl.IrradianceSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 5), ibl.PrefilterSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 6), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CreateUnorderedAccessView(ssrTarget.RenderTarget, null, new UnorderedAccessViewDescription {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(RtReflTableBase + 7));

        ssrTarget.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(rtReflRootSig);
            cl.SetPipelineState1(rtReflPso);
            cl.SetComputeRootConstantBufferView(0, rtReflCb.Gpu);
            cl.SetComputeRootConstantBufferView(1, rtReflSunCb.Gpu);
            cl.SetComputeRootConstantBufferView(2, rtReflGridCb.Gpu);
            cl.SetComputeRootDescriptorTable(3, bindless.Gpu(RtReflTableBase));
            cl.SetComputeRootShaderResourceView(4, gpuDriven.MaterialsGpuAddress);
            cl.SetComputeRootShaderResourceView(5, rtGeometry.InstancesGpuAddress);
            cl.SetComputeRootShaderResourceView(6, clusteredLights.LightBufGpuAddress);
            cl.SetComputeRootShaderResourceView(7, clusteredLights.LightBufGpuAddress);   // t10 probe (unused by Aurora; valid filler)
            // Aurora card cache (this frame's lit + multi-bounce radiance, post-swap) + per-instance meta + tri→cluster map.
            // When Aurora is off, bind valid filler (the light buffer) — UseCards=0 gates the shader read anyway.
            ulong cardAddr = useCards ? ctx.AuroraScene.CardRadianceReadGpu : clusteredLights.LightBufGpuAddress;
            ulong metaAddr = useCards ? ctx.AuroraScene.InstanceMetaGpuAddress : clusteredLights.LightBufGpuAddress;
            ulong triClusAddr = useCards && ctx.AuroraScene.TriToClusterGpuAddress != 0
                ? ctx.AuroraScene.TriToClusterGpuAddress : clusteredLights.LightBufGpuAddress;
            cl.SetComputeRootShaderResourceView(8, cardAddr);       // t11 CardRadiance
            cl.SetComputeRootShaderResourceView(9, metaAddr);       // t12 InstanceMeta
            cl.SetComputeRootShaderResourceView(10, triClusAddr);   // t13 TriToCluster
            cl.DispatchRays(new DispatchRaysDescription {
                Width = (uint)ssrTarget.Width, Height = (uint)ssrTarget.Height, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange { StartAddress = rtReflSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
                HitGroupTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
            });
        });
        ssrTarget.ColorToShaderResource();

        Dx12OffscreenTarget reflForCombine = ReflTemporalEnabled
            ? DenoiseReflectionTemporal(ctx, gbuffer)
            : ssrTarget;

        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)(ssrCbMapped + SsrCbOffset) = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view), Intensity = ForcedIntensity(ctx),
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };
        gbuffer.DepthToShaderResource();
        ssrSrvVisible.Reset();
        int cb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 4), reflForCombine.ColorSrvCpu, heapType);
        ssrScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrCombinePso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress + (ulong)SsrCbOffset);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssrScene.ColorToShaderResource();
        target.CopyColorFrom(ssrScene);
    }

    unsafe void BuildSsr() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: DescriptorRangeFlags.DataVolatile);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssrRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Ssr.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssr.hlsl");
        ID3D12PipelineState MakePso(string entry, Format rtFmt) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = ssrRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssr.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { rtFmt }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        ssrMarchPso = MakePso("PSMarch", Dx12OffscreenTarget.HdrFormat);
        ssrCombinePso = MakePso("PSCombine", Dx12OffscreenTarget.HdrFormat);
        ssrTemporalPso = MakePso("PSTemporal", Dx12OffscreenTarget.HdrFormat);

        ssrCbStride = (Marshal.SizeOf<SsrConstants>() + 255) & ~255;
        ssrCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(ssrCbStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        ssrCbMapped = ssrCb.Map<byte>(0);
        ssrSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    public void Resize(int w, int h) {
        if (ssrTarget is { IsPlaced: false }) ssrTarget.Dispose();
        if (ssrScene is { IsPlaced: false }) ssrScene.Dispose();
        ssrHistoryA?.Dispose(); ssrHistoryB?.Dispose();
        int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
        ssrTarget = Dx12RenderTargetPool.AllocOrPool(dev, "ssrTarget", hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        ssrScene = Dx12RenderTargetPool.AllocOrPool(dev, "ssrScene", w, h, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        ssrHistoryA = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssrHistoryB = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssrHistValid = false;
    }

    public void Dispose() {
        ssrMarchPso?.Dispose(); ssrCombinePso?.Dispose(); ssrTemporalPso?.Dispose();
        ssrRootSig?.Dispose(); ssrCb?.Dispose(); ssrSrvVisible?.Dispose();
        ssrTarget?.Dispose(); ssrScene?.Dispose();
        ssrHistoryA?.Dispose(); ssrHistoryB?.Dispose();
        rtReflPso?.Dispose(); rtReflRootSig?.Dispose();
        rtReflSbt?.Dispose();
        rtReflCb?.Dispose(); rtReflSunCb?.Dispose(); rtReflGridCb?.Dispose();
    }
}
