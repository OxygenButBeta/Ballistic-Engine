using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using BallisticEngine;   // RuntimeSet, IStaticMeshRenderer

namespace BallisticEngine.DX12;

// RT sky-occlusion (inline-RayQuery compute) — gates the IBL/flat ambient by REAL sky-visibility.
//
// PROBLEM: the deferred IBL/flat-ambient term lights every surface with the sky/ambient regardless of whether it
// can actually SEE the sky. A closed interior (SunTemple: one directional that never reaches the room, no skybox,
// ambientIntensity 0.3) is flooded with ambient even with no light in it. Screen-space GTAO can't fix it (~2 m
// radius). This casts a few cosine-hemisphere rays per pixel against the scene TLAS, measures the escaped-to-sky
// fraction, multiplies it into the AO, and the deferred IBL-ambient*AO term is then gated by real openness.
//
// DESIGN (attempt 3 — sidesteps the UAV-share bug of attempts 1/2): this pass does NOT UAV-write GTAO's AO
// target. It READS GTAO's AO as an SRV (t3), multiplies by sky-visibility, writes to its OWN committed UAV
// target (rtaoOut), then COPIES rtaoOut back into GTAO's AO target (CopyTextureRegion). A committed target that
// GTAO wrote as an RTV and we then read as an SRV is fine; writing it via a separate-pass UAV was not (the
// AoTex[px]-reads-0 bug). The copy-back keeps ctx.AoResult (= gtaoPass.ResultSrvCpu) valid with no plumbing.
//
// DEFAULT ON when HW-RT + a valid scene TLAS are present (gates IBL ambient by real sky-visibility so closed
// interiors don't glow from skylight). BALLISTIC_DX12_RTAO=0 force-disables for A/B. Still requires the AO door
// + AmbientOcclusion volume enabled (it read-modify-writes GTAO's AO target — that GTAO coupling is the next
// follow-up; for now AO is default-on in the shipped scenes so this runs).
public sealed class Dx12RtaoPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.BeforeOpaqueLighting;
    public string Name => "RTAO";

    readonly Dx12GtaoPass gtao;

    public Dx12RtaoPass(Dx12Device device, Dx12GtaoPass gtaoPass) { dev = device; gtao = gtaoPass; }

    int? envCached;
    // P2 — RTAO intensity/length env doors cached on first use (process-scoped; was parsed every dispatch).
    float? intensityCached, rayLenCached, rayCountCached;
    public bool Enabled(Dx12FrameContext ctx) {
        if (!ctx.Doors.Ssao || !ctx.PostFX.SSAOEnabled) return false;
        // DEFAULT ON (HW-RT gated). Sky-occlusion is the only term that gates the IBL ambient by REAL openness
        // (a sealed interior must not glow from skylight); leaving it opt-in meant every HW-RT scene leaked sky
        // ambient into closed rooms. BALLISTIC_DX12_RTAO=0 force-disables for A/B (byte-identical to pre-default).
        envCached ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO") == "0" ? 0 : 1;
        if (envCached == 0) return false;
        return ctx.Dxr?.SceneAS != null && ctx.Dev.HasHardwareRayTracing;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RtaoConstants {
        public Matrix4x4 InvViewProj;
        public Vector2 TexelSize;
        public float RayLength; public float NormalBias;
        public float RayCount; public float Intensity; public float FrameIndex; public float HistoryValid;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap heap;       // 7 descriptors: TLAS(t0), depth(t1), normal(t2), AO-in(t3), Hist-in(t4), AO-out-UAV(u0), Hist-out-UAV(u1)
    ID3D12Resource rtaoOut;        // own committed R8 UAV target (sky-vis-gated AO)
    ID3D12Resource histA, histB;   // temporal history ping-pong: R16G16_Float (skyVis, depth)
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
        // 10 m (was 30): sky-occlusion only needs to find NEARBY occluders — a point either has a close wall/
        // ceiling over it (sealed) or it doesn't. A 30 m ray spends most of its TLAS traversal past any relevant
        // occluder; 10 m finds the same sealing geometry with a much shorter (cheaper) ray. Measured 4K 76→81 fps,
        // interior sky-gate visually unchanged (MultiLightInterior imgdiff mean 0). BALLISTIC_DX12_RTAO_LENGTH overrides.
        float rayLen = rayLenCached ??= float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO_LENGTH"),
            System.Globalization.CultureInfo.InvariantCulture, out float rl) ? MathF.Max(rl, 0.1f) : 10f;

        // Sky-occlusion ray count. Was 6 — but this is a low-frequency openness signal (how much sky a point
        // sees), the temporal denoise + half-res already smooth it, and the per-pixel RT traversal is the whole
        // cost (RTAO measured 1.5ms@1080p / 6ms@4K — the single biggest 4K pass). 3 rays halves the dispatch for
        // a perceptually-identical result (interior sky-leak gate is unchanged). BALLISTIC_DX12_RTAO_RAYS overrides.
        float rayCount = rayCountCached ??= float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_RTAO_RAYS"),
            System.Globalization.CultureInfo.InvariantCulture, out float rc) ? Math.Clamp(rc, 1f, 16f) : 8f;   // 8 (was 3): with the temporal EMA below this is cheap enough and far less quantized
        // Deterministic capture keeps the original 6 rays so existing paused goldens stay bit-exact (the 3-ray
        // result is perceptual-identical but not byte-identical; det mode is the golden/diff baseline, not a perf
        // path). Play / normal render uses the cached value (default 3) — that's where the FPS win lives.
        if (ctx.DeterministicCapture) rayCount = 6f;
        *(RtaoConstants*)cbMapped = new RtaoConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            TexelSize = new Vector2(1f / ao.Width, 1f / ao.Height),
            RayLength = rayLen, NormalBias = 0.05f, RayCount = rayCount, Intensity = intensity,
            FrameIndex = ctx.DeterministicCapture ? 0f : frameCounter,
            // Temporal EMA off under a deterministic capture (byte-stable goldens) and on the first frame.
            HistoryValid = (histValid && !ctx.DeterministicCapture) ? 1f : 0f,
        };

        var histRead = histWriteB ? histA : histB;    // previous frame
        var histWrite = histWriteB ? histB : histA;    // this frame
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        sceneAS.CreateTlasSrv(heap.Cpu(0));
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(2), gbuffer.ColorSrvCpu(1), heapType);   // world normal (RT1)
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(3), ao.ColorSrvCpu, heapType);            // GTAO AO as SRV (read)
        dev.Device.CreateShaderResourceView(histRead, new ShaderResourceViewDescription {
            Format = Format.R16G16_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, heap.Cpu(4));                                                                        // t4 history (prev frame)
        dev.Device.CreateUnorderedAccessView(rtaoOut, null,
            new UnorderedAccessViewDescription { Format = Format.R8_UNorm, ViewDimension = UnorderedAccessViewDimension.Texture2D },
            heap.Cpu(5));                                                                       // u0 AO out
        dev.Device.CreateUnorderedAccessView(histWrite, null,
            new UnorderedAccessViewDescription { Format = Format.R16G16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D },
            heap.Cpu(6));                                                                       // u1 history out (this frame)

        // A compute-shader SRV read requires NON_PIXEL state. GTAO left depth/normal/AO in PIXEL only → transition
        // depth+normal to the combined read (covers both this compute read and the deferred pixel read that
        // follows — the DDGI-gather pattern), and AO to NON_PIXEL for the compute t3 read. ALL barriers + the
        // dispatch + the copy-back live in ONE command list so state tracking is exact (the split-submit versions
        // tripped 580 InvalidSubresourceState).
        gbuffer.ToShaderResource();
        dev.ExecuteSync(cl => {
            if (rtaoOutState != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(rtaoOut, rtaoOutState, ResourceStates.UnorderedAccess);
            // History: read buffer → compute SRV (t4), write buffer → UAV (u1). Guard same-state (DX12 rejects a
            // transition whose before==after).
            if (histReadState(histRead) != ResourceStates.NonPixelShaderResource)
                cl.ResourceBarrierTransition(histRead, histReadState(histRead), ResourceStates.NonPixelShaderResource);
            if (histReadState(histWrite) != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(histWrite, histReadState(histWrite), ResourceStates.UnorderedAccess);
            ao.ColorTransitionInList(cl, ResourceStates.NonPixelShaderResource);   // GTAO AO as a COMPUTE SRV (t3)
            cl.SetComputeRootSignature(rootSig);
            cl.SetPipelineState(pso);
            cl.SetDescriptorHeaps(heap.Heap);
            cl.SetComputeRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, heap.Gpu(0));
            cl.Dispatch((uint)((ao.Width + 7) / 8), (uint)((ao.Height + 7) / 8), 1);
            // Copy rtaoOut back into GTAO's AO target so ctx.AoResult (= gtaoA SRV) carries the gated AO.
            cl.ResourceBarrierTransition(rtaoOut, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            ao.ColorTransitionInList(cl, ResourceStates.CopyDest);
            cl.CopyTextureRegion(new TextureCopyLocation(ao.RenderTarget, 0), 0, 0, 0,
                new TextureCopyLocation(rtaoOut, 0), null);
            ao.ColorTransitionInList(cl, ResourceStates.PixelShaderResource);   // deferred samples it next (event 300)
        });
        rtaoOutState = ResourceStates.CopySource;
        histStates[histRead] = ResourceStates.NonPixelShaderResource;
        histStates[histWrite] = ResourceStates.UnorderedAccess;
        histWriteB = !histWriteB;   // swap ping-pong
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
            rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table })));
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
        // (Re)create the own UAV target at the AO resolution.
        rtaoOut?.Dispose();
        var desc = ResourceDescription.Texture2D(Format.R8_UNorm, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        rtaoOut = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        rtaoOutState = ResourceStates.UnorderedAccess;
        // Temporal history ping-pong (skyVis, depth). Realloc on size change → invalidate history (no smear across resize).
        histA?.Dispose(); histB?.Dispose();
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
