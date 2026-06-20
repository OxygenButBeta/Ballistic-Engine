using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using BallisticEngine;   // RuntimeSet, CapsuleShadowCaster

namespace BallisticEngine.DX12;

// Capsule shadows (the Unreal capsule-shadow feature): cheap ANALYTIC soft sun shadows from character proxy
// capsules onto the world. Each CapsuleShadowCaster contributes one world-space capsule; this pass gathers the
// active casters into a StructuredBuffer, then a compute shader (CapsuleShadows.hlsl) computes per-pixel soft
// sun occlusion (closest approach ray-vs-segment + sphere-cone soft occlusion using the sun's angular radius)
// into an R8 mask. The deferred sun term multiplies it with the cascade / RT shadow (min over all shadowers).
//
// Runs at BeforeOpaqueLighting (250) — after the G-buffer is readable, before deferred lighting consumes the
// mask (mirrors RTAO's slot). OFF when no caster exists in the scene → Enabled returns false → the pass never
// records, ctx.CapsuleShadowsThisFrame stays false, and the deferred path is byte-identical (the t16 bind
// falls back to a valid unused SRV, gated off by UseCapsuleShadows=0).
//
// V1 = single capsule per caster. FOLLOW-UP: multi-capsule (a flat capsule array per articulated skeleton) —
// the buffer + shader already loop over an array, so it's just a wider gather.
public sealed class Dx12CapsuleShadowPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.BeforeOpaqueLighting;
    public string Name => "CapsuleShadows";

    // Active only when at least one caster is in the scene. No casters → no work → byte-identical default path.
    public bool Enabled(Dx12FrameContext ctx) => ActiveCasterCount() > 0;

    static int ActiveCasterCount() {
        int n = 0;
        foreach (CapsuleShadowCaster c in RuntimeSet<CapsuleShadowCaster>.ReadOnlyCollection)
            if (c is { IsActive: true }) n++;
        return n;
    }

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
    ID3D12Resource capsuleBuf;       // UploadHeap StructuredBuffer<GpuCapsule>, filled per frame
    unsafe byte* capsuleMapped;
    Dx12DescriptorHeap heap;         // 4 descriptors: depth(t0), normal(t1), capsules(t2), occlusion-UAV(u0)
    ID3D12Resource maskOut;          // own committed R8 UAV target (the occlusion mask)
    ResourceStates maskState = ResourceStates.UnorderedAccess;
    int maskSrvIndex = -1;           // persistent SRV "home" in the SrvStore (rebuilt to point at maskOut)
    int outW, outH;
    bool built;

    public CpuDescriptorHandle MaskSrvCpu => Dx12Backend.SrvStore.Cpu(maskSrvIndex);

    public Dx12CapsuleShadowPass(Dx12Device device) { dev = device; }

    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12GBuffer gbuffer = ctx.GBuffer;
        int w = ctx.TargetW, h = ctx.TargetH;
        EnsureBuilt(w, h);

        // Gather active casters into the upload buffer (world-space segment + radius).
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
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);   // world normal (RT1)
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

        // Compute reads depth+normal as NON_PIXEL SRVs; the deferred PIXEL read follows, so emit the combined
        // read (covers both). All barriers + dispatch in ONE list so state tracking is exact.
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
        // (Re)create the own UAV mask target at render resolution.
        maskOut?.Dispose();
        var desc = ResourceDescription.Texture2D(Format.R8_UNorm, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        maskOut = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        maskState = ResourceStates.UnorderedAccess;
        // A stable SRV "home" (separate from the per-frame compute heap) so the deferred pass can bind it.
        // Allocate the index once; rebuild the view to point at the (possibly re-created) maskOut on resize.
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
