using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12CapsuleShadowPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.BeforeOpaqueLighting;
    public string Name => "CapsuleShadows";

    // FAZ -1d-FINAL — when render-graph v2 owns the whole frame (v1 bypassed) it drives CapsuleShadows itself;
    // the v1 graph then SKIPS this pass via RgV2OwnsCapsuleShadow. Door off (and door-on-while-plumbing) =>
    // RgV2OwnsCapsuleShadow is false => Enabled unchanged. See Dx12FrameContext.RgV2OwnsCapsuleShadow.
    public bool Enabled(Dx12FrameContext ctx) => ActiveCasterCount() > 0 && !ctx.RgV2OwnsCapsuleShadow;

    // FAZ -1d-FINAL — render-graph v2 entry point (mirrors Dx12ReflectionsPass.RecordV2). v2 imports GBuffer
    // (shader read) + the capsule-shadow mask (write), declares the access, then calls this to run the SAME
    // record body (byte-identical to the v1 path). Under v2 the v1 barrier deriver is bypassed (pass skipped
    // in v1) AND v2 emits no barrier for the imports (equal states by design), so the body MUST own its input
    // transitions — and it DOES, unconditionally (it is not BarriersDerived-aware): the Record body samples
    // gbuffer depth + normal SRVs, calls `gbuffer.ToShaderResource()` UNCONDITIONALLY (full color + depth),
    // and drives the maskOut UAV->PixelShaderResource transition with explicit in-list barriers. So no
    // pre-forced state is strictly required; we force `gbuffer.ToShaderResource()` here for symmetry with the
    // established pattern and as an explicit guarantee (idempotent — state-tracked, so the body's repeat is a
    // no-op). maskOut is a self-owned committed resource transitioned in-list by Record.
    public void RecordV2(Dx12FrameContext ctx) {
        ctx.GBuffer.ToShaderResource();
        Record(ctx);
    }

    static int ActiveCasterCount() {
        int n = 0;
        foreach (CapsuleShadowCaster c in RuntimeSet<CapsuleShadowCaster>.ReadOnlyCollection)
            if (c is { IsActive: true }) n++;
        return n;
    }

    // FAZ -1d-FINAL — public mirror of the Enabled() run condition, so the frame-context build can set
    // RgV2OwnsCapsuleShadow from the SAME predicate (v2 owns this pass IFF v1 would have run it this frame).
    public static bool WouldRun() => ActiveCasterCount() > 0;

    const int MaxCapsules = 64;

    [StructLayout(LayoutKind.Sequential)]
    struct CapsuleConstants {
        public Matrix4x4 InvViewProj;
        public Vector3 SunDir; public float SunAngularRadius;
        public int CapsuleCount; public float NormalBias; public Vector2 ScreenSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GpuCapsule {
        public Vector3 A; public float Radius;
        public Vector3 B; public float Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    ID3D12Resource capsuleBuf;
    unsafe byte* capsuleMapped;
    Dx12DescriptorHeap heap;
    ID3D12Resource maskOut;
    ResourceStates maskState = ResourceStates.UnorderedAccess;
    int maskSrvIndex = -1;
    int outW, outH;
    bool built;

    public CpuDescriptorHandle MaskSrvCpu => Dx12Backend.SrvStore.Cpu(maskSrvIndex);

    public Dx12CapsuleShadowPass(Dx12Device device) { dev = device; }

    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12GBuffer gbuffer = ctx.GBuffer;
        int w = ctx.TargetW, h = ctx.TargetH;
        EnsureBuilt(w, h);

        int count = 0;
        GpuCapsule* dst = (GpuCapsule*)capsuleMapped;
        foreach (CapsuleShadowCaster c in RuntimeSet<CapsuleShadowCaster>.ReadOnlyCollection) {
            if (c is not { IsActive: true }) continue;
            if (count >= MaxCapsules) break;
            c.GetWorldSegment(out Vector3 a, out Vector3 b, out float r);
            dst[count] = new GpuCapsule { A = a, Radius = MathF.Max(r, 1e-3f), B = b, Pad = 0f };
            count++;
        }
        if (count == 0) { ctx.CapsuleShadowsThisFrame = false; return; }

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        Vector3 sun = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(ctx.LightDir);
        float angularDiamDeg = DirectionalLight.Instance?.AngularDiameter ?? 0.53f;
        float sunAngularRadius = MathF.Max(angularDiamDeg * 0.5f * (MathF.PI / 180f), 1e-4f);

        *(CapsuleConstants*)cbMapped = new CapsuleConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            SunDir = sun, SunAngularRadius = sunAngularRadius,
            CapsuleCount = count, NormalBias = 0.05f, ScreenSize = new Vector2(w, h),
        };

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CreateShaderResourceView(capsuleBuf, new ShaderResourceViewDescription {
            ViewDimension = ShaderResourceViewDimension.Buffer, Format = Format.Unknown,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = MaxCapsules, StructureByteStride = (uint)sizeof(GpuCapsule),
            },
        }, heap.Cpu(2));
        dev.Device.CreateUnorderedAccessView(maskOut, null,
            new UnorderedAccessViewDescription { Format = Format.R8_UNorm, ViewDimension = UnorderedAccessViewDimension.Texture2D },
            heap.Cpu(3));

        gbuffer.ToShaderResource();
        dev.ExecuteSync(cl => {
            if (maskState != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(maskOut, maskState, ResourceStates.UnorderedAccess);
            cl.SetComputeRootSignature(rootSig);
            cl.SetPipelineState(pso);
            cl.SetDescriptorHeaps(heap.Heap);
            cl.SetComputeRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, heap.Gpu(0));
            cl.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
            cl.ResourceBarrierTransition(maskOut, ResourceStates.UnorderedAccess, ResourceStates.PixelShaderResource);
        });
        maskState = ResourceStates.PixelShaderResource;

        ctx.CapsuleShadowMask = MaskSrvCpu;
        ctx.CapsuleShadowsThisFrame = true;
    }

    unsafe void EnsureBuilt(int w, int h) {
        if (built && outW == w && outH == h) return;
        if (!built) {
            var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
            var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 3);
            var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
            rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table })));
            string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("CapsuleShadows.hlsl");
            byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "CapsuleShadows.hlsl");
            pso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = rootSig, ComputeShader = cs });

            int cbSize = (Marshal.SizeOf<CapsuleConstants>() + 255) & ~255;
            cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
            cbMapped = cb.Map<byte>(0);

            int capBytes = MaxCapsules * sizeof(GpuCapsule);
            capsuleBuf = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)capBytes), ResourceStates.GenericRead);
            capsuleMapped = capsuleBuf.Map<byte>(0);

            heap = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true, framesInFlight: dev.FramesInFlight);
            built = true;
        }

        maskOut?.Dispose();
        var desc = ResourceDescription.Texture2D(Format.R8_UNorm, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        maskOut = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        maskState = ResourceStates.UnorderedAccess;
        if (maskSrvIndex < 0) maskSrvIndex = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(maskOut, new ShaderResourceViewDescription {
            ViewDimension = ShaderResourceViewDimension.Texture2D, Format = Format.R8_UNorm,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, Dx12Backend.SrvStore.Cpu(maskSrvIndex));
        outW = w; outH = h;
    }

    public void Dispose() {
        pso?.Dispose(); rootSig?.Dispose(); cb?.Dispose(); heap?.Dispose(); maskOut?.Dispose(); capsuleBuf?.Dispose();
    }
}
