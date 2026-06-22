using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12RtaoPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.BeforeOpaqueLighting;
    public string Name => "RTAO";

    readonly Dx12GtaoPass gtao;

    public Dx12RtaoPass(Dx12Device device, Dx12GtaoPass gtaoPass) { dev = device; gtao = gtaoPass; }

    int? envCached;
    float? intensityCached, rayLenCached, rayCountCached, skyFloorCached;
    // FAZ -1d-FINAL — when render-graph v2 owns the whole frame (v1 bypassed) it drives RTAO itself; the v1
    // graph then SKIPS this pass via RgV2OwnsRtao. Door off (and door-on-while-plumbing) => RgV2OwnsRtao is
    // false => Enabled unchanged. See Dx12FrameContext.RgV2OwnsRtao.
    public bool Enabled(Dx12FrameContext ctx) =>
        WillRun(ctx.Doors, ctx.PostFX, ctx.Dxr, ctx.Dev) && !ctx.RgV2OwnsRtao;

    // FAZ -1d-FINAL — render-graph v2 entry point (mirrors Dx12ReflectionsPass.RecordV2). v2 imports GBuffer
    // (shader read) + the Ao target (read/write) + the scene TLAS, declares the access, then calls this to run
    // the SAME record body (byte-identical to the v1 path). Under v2 the v1 barrier deriver is bypassed (pass
    // skipped in v1) AND v2 emits no barrier for the imports (equal states by design), so the body MUST own
    // its input transitions — and it DOES, unconditionally (it is not BarriersDerived-aware): the Record body
    // ensures the TLAS via sceneAS.Ensure, calls `gbuffer.ToShaderResource()` UNCONDITIONALLY (full color +
    // depth), and drives every UAV/copy transition for rtaoOut / the history textures / the AO target with
    // explicit in-list ResourceBarrierTransition + ColorTransitionInList calls. So no pre-forced state is
    // strictly required; we force `gbuffer.ToShaderResource()` here too for symmetry with the established
    // pattern and as an explicit guarantee (idempotent — the transition is state-tracked, so the body's repeat
    // is a no-op). The AO target it reads (gtao.AoTarget) is transitioned in-list by Record itself.
    public void RecordV2(Dx12FrameContext ctx) {
        ctx.GBuffer.ToShaderResource();
        Record(ctx);
    }

    public bool WillRun(Dx12RenderDoors doors, PostProcessSettings postFx, Dx12DxrShared dxr, Dx12Device device) {
        if (!doors.Ssao || !postFx.SSAOEnabled) return false;
        envCached ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO") == "0" ? 0 : 1;
        if (envCached == 0) return false;
        return dxr?.SceneAS != null && device.HasHardwareRayTracing;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RtaoConstants {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 PrevViewProj;   // last frame's world->clip, to reproject the history sample
        public Vector2 TexelSize;
        public float RayLength; public float NormalBias;
        public float RayCount; public float Intensity; public float FrameIndex; public float HistoryValid;
        public Vector3 CameraPos; public float SkyVisFloor;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap heap;
    ID3D12Resource rtaoOut;
    ID3D12Resource histA, histB;
    bool histWriteB;
    bool histValid;
    int outW, outH;
    bool built;
    int frameCounter;

    public unsafe void Record(Dx12FrameContext ctx) {
        var sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) return;

        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget ao = gtao.AoTarget;
        EnsureBuilt(ao.Width, ao.Height);
        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);

        float intensity = intensityCached ??= float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO_INTENSITY"),
            System.Globalization.CultureInfo.InvariantCulture, out float ri) ? Math.Clamp(ri, 0f, 1f) : 1f;
        // 6 m default (was 10): a long ray counts a distant building across the street as an occluder and over-
        // darkens open ground; sky-occlusion only needs to catch nearby roofs/arches/ceilings.
        float rayLen = rayLenCached ??= float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO_LENGTH"),
            System.Globalization.CultureInfo.InvariantCulture, out float rl) ? MathF.Max(rl, 0.1f) : 6f;
        // Floor on the sky-vis gate (default 0.3): a covered passage/arcade still gets bounced + side light, so the
        // IBL ambient never drops below 30% — no pure-black blotches under arches. 0 = old behaviour (full gate).
        float skyFloor = skyFloorCached ??= float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO_FLOOR"),
            System.Globalization.CultureInfo.InvariantCulture, out float sf) ? Math.Clamp(sf, 0f, 1f) : 0.3f;

        float rayCount = rayCountCached ??= float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO_RAYS"),
            System.Globalization.CultureInfo.InvariantCulture, out float rc) ? Math.Clamp(rc, 1f, 16f) : 8f;
        if (ctx.DeterministicCapture) rayCount = 6f;
        // History is reprojected through last frame's ViewProj (the engine's own motion-vector matrix); without a
        // valid previous frame (first frame, resize, scene swap) the reproject would alias, so gate on it too.
        bool histUsable = histValid && ctx.MotionPrevValid && !ctx.DeterministicCapture;
        *(RtaoConstants*)cbMapped = new RtaoConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            PrevViewProj = Matrix4x4.Transpose(ctx.PrevViewProjUnjittered),
            TexelSize = new Vector2(1f / ao.Width, 1f / ao.Height),
            RayLength = rayLen, NormalBias = 0.05f, RayCount = rayCount, Intensity = intensity,
            FrameIndex = ctx.DeterministicCapture ? 0f : frameCounter,
            HistoryValid = histUsable ? 1f : 0f,
            CameraPos = ctx.CamPos,
            SkyVisFloor = skyFloor,
        };

        var histRead = histWriteB ? histA : histB;
        var histWrite = histWriteB ? histB : histA;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        sceneAS.CreateTlasSrv(heap.Cpu(0));
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(3), ao.ColorSrvCpu, heapType);
        dev.Device.CreateShaderResourceView(histRead, new ShaderResourceViewDescription {
            Format = Format.R16G16_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, heap.Cpu(4));
        dev.Device.CreateUnorderedAccessView(rtaoOut, null,
            new UnorderedAccessViewDescription { Format = Format.R8_UNorm, ViewDimension = UnorderedAccessViewDimension.Texture2D },
            heap.Cpu(5));
        dev.Device.CreateUnorderedAccessView(histWrite, null,
            new UnorderedAccessViewDescription { Format = Format.R16G16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D },
            heap.Cpu(6));

        gbuffer.ToShaderResource();
        dev.ExecuteSync(cl => {
            if (rtaoOutState != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(rtaoOut, rtaoOutState, ResourceStates.UnorderedAccess);
            if (histReadState(histRead) != ResourceStates.NonPixelShaderResource)
                cl.ResourceBarrierTransition(histRead, histReadState(histRead), ResourceStates.NonPixelShaderResource);
            if (histReadState(histWrite) != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(histWrite, histReadState(histWrite), ResourceStates.UnorderedAccess);
            ao.ColorTransitionInList(cl, ResourceStates.NonPixelShaderResource);
            cl.SetComputeRootSignature(rootSig);
            cl.SetPipelineState(pso);
            cl.SetDescriptorHeaps(heap.Heap);
            cl.SetComputeRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, heap.Gpu(0));
            cl.Dispatch((uint)((ao.Width + 7) / 8), (uint)((ao.Height + 7) / 8), 1);
            cl.ResourceBarrierTransition(rtaoOut, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            ao.ColorTransitionInList(cl, ResourceStates.CopyDest);
            cl.CopyTextureRegion(new TextureCopyLocation(ao.RenderTarget, 0), 0, 0, 0,
                new TextureCopyLocation(rtaoOut, 0), null);
            ao.ColorTransitionInList(cl, ResourceStates.PixelShaderResource);
        });
        rtaoOutState = ResourceStates.CopySource;
        histStates[histRead] = ResourceStates.NonPixelShaderResource;
        histStates[histWrite] = ResourceStates.UnorderedAccess;
        histWriteB = !histWriteB;
        histValid = true;
        frameCounter++;
    }

    ResourceStates rtaoOutState = ResourceStates.UnorderedAccess;
    readonly System.Collections.Generic.Dictionary<ID3D12Resource, ResourceStates> histStates = new();
    ResourceStates histReadState(ID3D12Resource r) => histStates.TryGetValue(r, out var s) ? s : ResourceStates.UnorderedAccess;

    unsafe void EnsureBuilt(int w, int h) {
        if (built && outW == w && outH == h) return;
        if (!built) {
            var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
            var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 5);
            var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
            var linearClamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
            };
            rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, new[] { linearClamp })));
            string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("RtSkyOcclusion.hlsl");
            byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "RtSkyOcclusion.hlsl");
            pso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = cs });
            int cbSize = (Marshal.SizeOf<RtaoConstants>() + 255) & ~255;
            cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
            cbMapped = cb.Map<byte>(0);
            heap = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 7, shaderVisible: true, framesInFlight: dev.FramesInFlight);
            built = true;
        }

        if (rtaoOut != null) dev.DeferredRelease(rtaoOut);
        var desc = ResourceDescription.Texture2D(Format.R8_UNorm, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        rtaoOut = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        rtaoOutState = ResourceStates.UnorderedAccess;
        if (histA != null) dev.DeferredRelease(histA);
        if (histB != null) dev.DeferredRelease(histB);
        var hdesc = ResourceDescription.Texture2D(Format.R16G16_Float, (uint)w, (uint)h, 1, 1);
        hdesc.Flags = ResourceFlags.AllowUnorderedAccess;
        histA = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, hdesc, ResourceStates.UnorderedAccess);
        histB = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, hdesc, ResourceStates.UnorderedAccess);
        histValid = false; histWriteB = false;
        outW = w; outH = h;
    }

    public void Dispose() {
        pso?.Dispose(); rootSig?.Dispose(); cb?.Dispose(); heap?.Dispose(); rtaoOut?.Dispose();
        histA?.Dispose(); histB?.Dispose();
    }
}
