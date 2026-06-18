using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// SCREEN-SPACE RADIANCE PROBE final gather (GI plan Phase 4, P4.0). The published Lumen "screen probe gather":
// instead of one GI ray per full-res pixel (the per-pixel DDGI gather / 1-spp RT noise), drop ONE probe per
// DOWNSAMPLE x DOWNSAMPLE screen tile (default 16x16), snap it to that tile's G-buffer surface, trace a small
// hemisphere of SHORT rays (64) into an 8x8 octahedral RADIANCE tile, then upsample to full-res. ~64 rays
// amortised over ~256 pixels is both cheaper AND far less noisy than 256 independent rays, because the rays
// concentrate where light is. The screen-probe rays are SHORT (near/mid field); on a miss/far hit they hand
// off to the DDGI world cache (the far-field radiance) — exactly Lumen's screen-trace -> world-cache hierarchy.
// So this work sits IN FRONT of the DDGI cache we already built (P2): screen probes = near/mid, DDGI = far.
//
// Pipeline: Place -> Trace (cosine-hemisphere rays = diffuse-BRDF importance sample, miss->DDGI) -> Blend
// (rays -> octahedral tile + border) -> Integrate (BILATERAL 2x2-probe depth+normal upsample -> ssgiTarget).
// Phase 4 COMPLETE (P4.0 place/trace/blend, P4.1 bilateral + E->L energy fix, P4.3 determinism+budget; P4.2
// importance resolved by measurement). This is now the DEFAULT GI gather when DDGI is on; BALLISTIC_DX12_
// SCREENPROBE=0 opts out to the per-pixel DDGI gather (which reproduces the pre-flip image byte-for-byte).
//
// The probe grid + atlas are RESIZE-AWARE (sized from the render resolution like the SSGI targets). All compute
// passes run as their own ExecuteSync (each DX12 pass = its own submit), so each can be GPU-timed separately.
public sealed class Dx12ScreenProbe : IDisposable {
    readonly Dx12Device dev;

    public const int Downsample = 16;          // screen tile size → one probe per 16x16 block
    public const int OctTexels = 8;            // 8x8 octahedral radiance per probe
    public const int RaysPerProbe = 64;        // MUST match ScreenProbeTrace/Blend RaysPerProbe()
    const int Border = 1;
    const int OctTile = OctTexels + 2 * Border;   // 10

    int probesX, probesY;   // probe grid (derived from the render resolution)
    int screenW, screenH;
    public int ProbesX => probesX;
    public int ProbesY => probesY;
    public int ProbeCount => probesX * probesY;
    int AtlasW => probesX * OctTile;
    int AtlasH => probesY * OctTile;

    // GPU resources (all resize with the grid). RadianceTex = the per-probe octahedral radiance atlas (RGBA16F).
    // probePos / probeNormal = per-probe placement (world pos + validity, world normal). rayData = the trace's
    // per-(probe,ray) output the blend integrates.
    ID3D12Resource radianceTex;
    ID3D12Resource probePos, probeNormal, rayData;
    public ID3D12Resource RadianceTex => radianceTex;

    // Tracked current state per resource (so every transition goes from the ACTUAL state, not an assumed one —
    // the resources persist across frames, so a "from Common" transition is only valid on frame 0; after that
    // they sit in whatever state the previous frame left them). This is the GPU-hang guard for the new plumbing:
    // a wrong-state barrier is exactly the DDGI/RtGi device-removal class. radianceTex created NonPixelSRV;
    // probePos/probeNormal/rayData created Common.
    ResourceStates radianceState, probePosState, probeNormalState, rayDataState;

    // Place pass: 1 thread/probe, reads depth+normal SRVs → probePos/probeNormal UAVs. CBV b0.
    ID3D12RootSignature placeRootSig;
    ID3D12PipelineState placePso;
    Dx12DescriptorHeap placeHeap;   // 2 SRV (depth,normal) + 2 UAV (probePos,probeNormal)

    // Trace pass: 1 thread/(probe,ray), inline RayQuery. Root sig mirrors the DDGI trace (bindless tail table
    // {t0 TLAS, t3 irr cube, t4 DDGI atlas} + CBV b0/b1/b2 + root SRVs t5..t10 + UAV u0). Reuses the SAME
    // BindlessHeap as the RT-GI / DDGI passes (the bindless geo reads need it bound).
    ID3D12RootSignature traceRootSig;
    ID3D12PipelineState tracePso;

    // Blend pass: CSIntegrate (rays → octahedral tile) + CSBorder (octahedral border wrap). CBV b0 + SRV t0
    // rayData + t1 probePos + UAV u0 radiance atlas (own heap).
    ID3D12RootSignature blendRootSig;
    ID3D12PipelineState blendPso, borderPso;
    Dx12DescriptorHeap blendHeap;   // t0 rayData, t1 probePos, u0 radiance atlas

    // Integrate pass: 1 thread/pixel → ssgiTarget. CBV b0 + table {t0..t5 SRV, u0 UAV} + linear-clamp.
    ID3D12RootSignature integrateRootSig;
    ID3D12PipelineState integratePso;
    Dx12DescriptorHeap integrateHeap;   // 6 SRV + 1 UAV, rebuilt each frame (ssgiTarget can resize)

    // CBVs (upload, mapped). spCb = ScreenProbeConstants (b0 for place/trace/blend/integrate). ddgiGridCb =
    // the DDGI grid description the trace samples on miss (b2).
    ID3D12Resource spCb; unsafe byte* spCbMapped;
    ID3D12Resource ddgiGridCb; unsafe byte* ddgiGridMapped;

    bool built;
    int frameCounter;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ScreenProbeConstants {
        public Matrix4x4 InvViewProj;   // jittered, transposed
        public Vector4 SpParams0;       // x probesX y probesY z downsample w frameIndex
        public Vector4 SpParams1;       // x screenW y screenH z maxRayDist w preExposure
        public Vector4 SpParams2;       // x octTexels y normalBias z intensity w pad
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DdgiGridConstants {
        public Vector4 OriginSpacingX;  // xyz origin, w spacing.x
        public Vector4 SpacingYZ;       // x spacing.y, y spacing.z
        public Vector4 ProbeDims;       // xyz (ProbesX,ProbesY,ProbesZ), w ProbeCount
        public Vector4 Params;          // x irrTexels, y normalBias, z/w pad
    }

    // Emissive-as-GI-source: when set, the screen-probe trace's ShadeHit adds the hit's self-emission L_e
    // (rides SpParams2.w → ScreenProbeTrace emissiveEnable). Set by the renderer from BALLISTIC_DX12_GI_EMISSIVE
    // (default ON). Default true so a missing setter = the correct on behaviour.
    public bool EmissiveEnabled = true;

    public bool Allocated => radianceTex != null;
    public long GridVramBytes {
        get {
            long atlas = (long)AtlasW * AtlasH * 8;                       // RGBA16F
            long pos = (long)ProbeCount * 16 * 2;                          // probePos + probeNormal float4
            long ray = (long)ProbeCount * RaysPerProbe * 16;              // float4
            return atlas + pos + ray;
        }
    }

    public Dx12ScreenProbe(Dx12Device device) { dev = device; }

    // (Re)allocate the grid + buffers for the current render resolution. Idempotent if the size is unchanged.
    public unsafe void EnsureAllocated(int renderW, int renderH) {
        int px = (renderW + Downsample - 1) / Downsample;
        int py = (renderH + Downsample - 1) / Downsample;
        if (radianceTex != null && px == probesX && py == probesY) { screenW = renderW; screenH = renderH; return; }

        DisposeGridResources();
        probesX = px; probesY = py; screenW = renderW; screenH = renderH;

        radianceTex = CreateAtlas(AtlasW, AtlasH, Format.R16G16B16A16_Float);
        probePos = CreateStructuredUav(ProbeCount);
        probeNormal = CreateStructuredUav(ProbeCount);
        rayData = CreateStructuredUav(ProbeCount * RaysPerProbe);

        // Seed tracked states to match the resource creation states above.
        radianceState = ResourceStates.NonPixelShaderResource;
        probePosState = ResourceStates.Common;
        probeNormalState = ResourceStates.Common;
        rayDataState = ResourceStates.Common;
    }

    // Transition `res` from its tracked `state` to `target` (no-op if already there), updating the tracked state.
    static void To(ID3D12GraphicsCommandList cl, ID3D12Resource res, ref ResourceStates state, ResourceStates target) {
        if (state == target) return;
        cl.ResourceBarrierTransition(res, state, target);
        state = target;
    }

    public void Build() {
        if (built) return;
        built = true;
        BuildPlace();
        BuildTrace();
        BuildBlend();
        BuildIntegrate();

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<ScreenProbeConstants>() + 255) & ~255;
        spCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        unsafe { spCbMapped = spCb.Map<byte>(0); }
        int gcbSize = (System.Runtime.InteropServices.Marshal.SizeOf<DdgiGridConstants>() + 255) & ~255;
        ddgiGridCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)gcbSize), ResourceStates.GenericRead);
        unsafe { ddgiGridMapped = ddgiGridCb.Map<byte>(0); }
    }

    // Fill the shared ScreenProbeConstants + the DDGI grid CBV for this frame. `deterministic` (the capture
    // path's BALLISTIC_DETERMINISTIC) PINS the ray-jitter frame seed to a fixed value so the screen-probe atlas
    // is frame-INDEPENDENT (the captured image is identical regardless of SCREENSHOT_FRAME — the P2.5 contract,
    // since the probe is recomputed from scratch each frame with no cross-frame accumulation, only the seed
    // varies). In play it uses the live frameCounter so the jitter rotates + the downstream temporal converges.
    unsafe void FillConstants(Matrix4x4 invViewProjTransposed, float maxRayDist, float preExposure, float intensity,
        bool deterministic, in Dx12Ddgi.DdgiConstants ddgiC) {
        int seed = deterministic ? 0 : frameCounter;
        *(ScreenProbeConstants*)spCbMapped = new ScreenProbeConstants {
            InvViewProj = invViewProjTransposed,
            SpParams0 = new Vector4(probesX, probesY, Downsample, seed),
            SpParams1 = new Vector4(screenW, screenH, maxRayDist, preExposure),
            // SpParams2.w = emissiveEnable (was pad) — the trace adds emissive self-emission at hits when >0.5.
            SpParams2 = new Vector4(OctTexels, 0.05f, intensity, EmissiveEnabled ? 1f : 0f),
        };
        // Mirror the DDGI grid fields the trace needs for the far-field sample. DdgiConstants packs the same
        // grid layout; copy the relevant float4s (irrTexels = Params0.x, normalBias = Params1.y).
        *(DdgiGridConstants*)ddgiGridMapped = new DdgiGridConstants {
            OriginSpacingX = ddgiC.OriginSpacingX,
            SpacingYZ = ddgiC.SpacingYZ,
            ProbeDims = ddgiC.ProbeDims,
            Params = new Vector4(ddgiC.Params0.X, ddgiC.Params1.Y, 0f, 0f),
        };
    }

    // ---- The full Phase-4 dispatch (Place -> Trace -> Blend -> Integrate). Called by the renderer inside
    // DrawRtGi by DEFAULT when DDGI is on (BALLISTIC_DX12_SCREENPROBE=0 opts out); the screen-probe rays need
    // the DDGI field for the far-field handoff. All the bindless/RT addresses + the DDGI atlas SRVs are supplied by the renderer
    // (it owns the BindlessHeap reservation + the DDGI resource). Each sub-pass is its own ExecuteSync. ----

    // PLACE: probePos/probeNormal from the G-buffer. depthSrv/normalSrv are CPU handles copied into placeHeap.
    public unsafe void DispatchPlace(CpuDescriptorHandle depthSrv, CpuDescriptorHandle normalSrv) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        placeHeap.Reset();
        dev.Device.CopyDescriptorsSimple(1, placeHeap.Cpu(0), depthSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, placeHeap.Cpu(1), normalSrv, heapType);
        dev.Device.CreateUnorderedAccessView(probePos, null, StructUav(ProbeCount), placeHeap.Cpu(2));
        dev.Device.CreateUnorderedAccessView(probeNormal, null, StructUav(ProbeCount), placeHeap.Cpu(3));
        dev.ExecuteSync(cl => {
            To(cl, probePos, ref probePosState, ResourceStates.UnorderedAccess);
            To(cl, probeNormal, ref probeNormalState, ResourceStates.UnorderedAccess);
            cl.SetDescriptorHeaps(placeHeap.Heap);
            cl.SetComputeRootSignature(placeRootSig);
            cl.SetPipelineState(placePso);
            cl.SetComputeRootConstantBufferView(0, spCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, placeHeap.Gpu(0));
            cl.Dispatch((uint)((probesX + 7) / 8), (uint)((probesY + 7) / 8), 1);
            To(cl, probePos, ref probePosState, ResourceStates.NonPixelShaderResource);
            To(cl, probeNormal, ref probeNormalState, ResourceStates.NonPixelShaderResource);
        });
    }

    // TRACE: 1 thread/(probe,ray). Reuses the renderer's BindlessHeap (geo reads). `traceTableGpu` = the GPU
    // handle of the 3-descriptor bindless-tail block the renderer wrote ([0]=TLAS, [1]=irr cube, [2]=DDGI atlas).
    // probePos/probeNormal are NonPixelSRV (left so by DispatchPlace). rayData starts UnorderedAccess.
    public unsafe void DispatchTrace(Dx12DescriptorHeap bindless, GpuDescriptorHandle traceTableGpu,
        ulong sunCbAddress, ulong materialsAddr, ulong instancesAddr, ulong lightsAddr, ulong ddgiProbeStateAddr) {
        dev.ExecuteSync(cl => {
            // rayData (root UAV u0) must be in UnorderedAccess for the write; probePos/probeNormal (root SRVs)
            // are in NonPixelShaderResource (left by Place). Transition from the tracked state idempotently.
            To(cl, rayData, ref rayDataState, ResourceStates.UnorderedAccess);
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(traceRootSig);
            cl.SetPipelineState(tracePso);
            cl.SetComputeRootConstantBufferView(0, spCb.GPUVirtualAddress);        // b0 ScreenProbeConstants
            cl.SetComputeRootConstantBufferView(1, sunCbAddress);                  // b1 RtGiSun
            cl.SetComputeRootConstantBufferView(2, ddgiGridCb.GPUVirtualAddress);  // b2 DdgiGridConstants
            cl.SetComputeRootDescriptorTable(3, traceTableGpu);                    // t0 TLAS + t3 cube + t4 DDGI atlas
            cl.SetComputeRootShaderResourceView(4, materialsAddr);                 // t5 GpuMaterials
            cl.SetComputeRootShaderResourceView(5, instancesAddr);                 // t6 RtInstance[]
            cl.SetComputeRootShaderResourceView(6, lightsAddr);                    // t7 Lights
            cl.SetComputeRootShaderResourceView(7, ddgiProbeStateAddr);            // t8 DDGI ProbeState
            cl.SetComputeRootShaderResourceView(8, probePos.GPUVirtualAddress);    // t9 ScreenProbePos
            cl.SetComputeRootShaderResourceView(9, probeNormal.GPUVirtualAddress); // t10 ScreenProbeNormal
            cl.SetComputeRootUnorderedAccessView(10, rayData.GPUVirtualAddress);   // u0 RayData
            int total = ProbeCount * RaysPerProbe;
            cl.Dispatch((uint)((total + 63) / 64), 1, 1);
            cl.ResourceBarrierUnorderedAccessView(rayData);
        });
        frameCounter++;
    }

    // BLEND: rays → octahedral radiance tile + border. rayData is read as t0 (transitioned to NonPixelSRV).
    public unsafe void DispatchBlend() {
        blendHeap.Reset();
        dev.Device.CreateUnorderedAccessView(radianceTex, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, blendHeap.Cpu(0));   // u0 = slot 0
        dev.ExecuteSync(cl => {
            // rayData (root SRV t0) read needs NonPixelSRV (it's in UnorderedAccess after Trace); probePos
            // (root SRV t1) is already NonPixelSRV; radianceTex (UAV u0) → UnorderedAccess for the write.
            To(cl, rayData, ref rayDataState, ResourceStates.NonPixelShaderResource);
            To(cl, radianceTex, ref radianceState, ResourceStates.UnorderedAccess);
            cl.SetDescriptorHeaps(blendHeap.Heap);
            cl.SetComputeRootSignature(blendRootSig);
            cl.SetComputeRootConstantBufferView(0, spCb.GPUVirtualAddress);          // b0
            cl.SetComputeRootShaderResourceView(1, rayData.GPUVirtualAddress);       // t0 RayData
            cl.SetComputeRootShaderResourceView(2, probePos.GPUVirtualAddress);      // t1 ProbePos
            cl.SetComputeRootDescriptorTable(3, blendHeap.Gpu(0));                   // u0 radiance atlas
            cl.SetPipelineState(blendPso);
            cl.Dispatch((uint)((AtlasW + 7) / 8), (uint)((AtlasH + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(radianceTex);
            cl.SetPipelineState(borderPso);
            cl.Dispatch((uint)((AtlasW + 7) / 8), (uint)((AtlasH + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(radianceTex);
            // radianceTex → NonPixelSRV for the integrate read; rayData → UnorderedAccess for next frame's trace.
            To(cl, radianceTex, ref radianceState, ResourceStates.NonPixelShaderResource);
            To(cl, rayData, ref rayDataState, ResourceStates.UnorderedAccess);
        });
    }

    // INTEGRATE: full-res, nearest-probe upsample → ssgiTarget (pre-exposed albedo*E). G-buffer depth/normal/
    // albedo SRVs supplied by the renderer; radiance atlas + probePos + probeNormal bound from our buffers.
    public unsafe void DispatchIntegrate(CpuDescriptorHandle depthSrv, CpuDescriptorHandle normalSrv,
        CpuDescriptorHandle albedoSrv, ID3D12Resource ssgiTargetRes, int w, int h) {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        integrateHeap.Reset();
        dev.Device.CopyDescriptorsSimple(1, integrateHeap.Cpu(0), depthSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, integrateHeap.Cpu(1), normalSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, integrateHeap.Cpu(2), albedoSrv, heapType);
        dev.Device.CreateShaderResourceView(radianceTex, new ShaderResourceViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, integrateHeap.Cpu(3));
        dev.Device.CreateShaderResourceView(probePos, StructSrv(ProbeCount), integrateHeap.Cpu(4));
        dev.Device.CreateShaderResourceView(probeNormal, StructSrv(ProbeCount), integrateHeap.Cpu(5));
        dev.Device.CreateUnorderedAccessView(ssgiTargetRes, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, integrateHeap.Cpu(6));
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(integrateHeap.Heap);
            cl.SetComputeRootSignature(integrateRootSig);
            cl.SetPipelineState(integratePso);
            cl.SetComputeRootConstantBufferView(0, spCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, integrateHeap.Gpu(0));
            cl.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        });
    }

    public unsafe void PrepareConstants(Matrix4x4 invViewProjTransposed, float maxRayDist, float preExposure,
        float intensity, bool deterministic, in Dx12Ddgi.DdgiConstants ddgiC) =>
        FillConstants(invViewProjTransposed, maxRayDist, preExposure, intensity, deterministic, ddgiC);

    // ---- builders ----
    unsafe void BuildPlace() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 0);   // t0 depth, t1 normal
        var uav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0);  // u0 probePos, u1 probeNormal
        var table = new RootParameter1(new RootDescriptorTable1(srv, uav), ShaderVisibility.All);
        placeRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, new[] { LinearClampSampler() })));
        placePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = placeRootSig,
            ComputeShader = Compile("ScreenProbePlace.hlsl", "CSPlace"),
        });
        placeHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true);
    }

    unsafe void BuildTrace() {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var cbv2 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var tlas = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0);   // t0
        var cube = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 3, 0, 1);   // t3
        var ddgiAtlas = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 4, 0, 2);   // t4
        var ddgiDepth = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 11, 0, 3);  // t11 DDGI depth (leak gate)
        var table = new RootParameter1(new RootDescriptorTable1(tlas, cube, ddgiAtlas, ddgiDepth), ShaderVisibility.All);
        var mat = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All);
        var inst = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0), ShaderVisibility.All);
        var light = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var ddgiState = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var spPos = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
        var spNorm = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);
        var uav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        traceRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, cbv2, table, mat, inst, light, ddgiState, spPos, spNorm, uav },
                new[] { LinearClampSampler(0), LinearWrapSampler(1) })));
        tracePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = traceRootSig,
            ComputeShader = Compile("ScreenProbeTrace.hlsl", "CSMain"),
        });
    }

    unsafe void BuildBlend() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRay = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // t0 rayData
        var srvPos = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All);   // t1 probePos
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0);   // u0 radiance atlas
        var uavTable = new RootParameter1(new RootDescriptorTable1(uavRange), ShaderVisibility.All);
        blendRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvRay, srvPos, uavTable })));
        blendPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = blendRootSig, ComputeShader = Compile("ScreenProbeBlend.hlsl", "CSIntegrate"),
        });
        borderPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = blendRootSig, ComputeShader = Compile("ScreenProbeBlend.hlsl", "CSBorder"),
        });
        blendHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true);
    }

    unsafe void BuildIntegrate() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, 0);   // t0..t5
        var uav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0);  // u0 ssgiTarget
        var table = new RootParameter1(new RootDescriptorTable1(srv, uav), ShaderVisibility.All);
        integrateRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, new[] { LinearClampSampler() })));
        integratePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = integrateRootSig, ComputeShader = Compile("ScreenProbeIntegrate.hlsl", "CSIntegrate"),
        });
        integrateHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 7, shaderVisible: true);
    }

    static byte[] Compile(string file, string entry) =>
        Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, EmbeddedShaderSource.ReadHlsl(file), entry, file);

    static StaticSamplerDescription LinearClampSampler(int reg = 0) => new(ShaderVisibility.All, (uint)reg, 0) {
        Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
        AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
        MinLOD = 0, MaxLOD = float.MaxValue,
    };
    static StaticSamplerDescription LinearWrapSampler(int reg = 1) => new(ShaderVisibility.All, (uint)reg, 0) {
        Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
        AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
        MinLOD = 0, MaxLOD = float.MaxValue,
    };

    ID3D12Resource CreateAtlas(int w, int h, Format fmt) {
        var desc = ResourceDescription.Texture2D(fmt, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        return dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.NonPixelShaderResource);
    }
    ID3D12Resource CreateStructuredUav(int count) =>
        dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)count * 16, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);

    static UnorderedAccessViewDescription StructUav(int count) => new() {
        Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
        Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)count, StructureByteStride = 16 },
    };
    static ShaderResourceViewDescription StructSrv(int count) => new() {
        Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)count, StructureByteStride = 16 },
    };

    void DisposeGridResources() {
        radianceTex?.Dispose(); radianceTex = null;
        probePos?.Dispose(); probePos = null;
        probeNormal?.Dispose(); probeNormal = null;
        rayData?.Dispose(); rayData = null;
    }

    public void Dispose() {
        DisposeGridResources();
        placePso?.Dispose(); placeRootSig?.Dispose(); placeHeap?.Dispose();
        tracePso?.Dispose(); traceRootSig?.Dispose();
        blendPso?.Dispose(); borderPso?.Dispose(); blendRootSig?.Dispose(); blendHeap?.Dispose();
        integratePso?.Dispose(); integrateRootSig?.Dispose(); integrateHeap?.Dispose();
        if (spCb != null) { spCb.Unmap(0); spCb.Dispose(); spCb = null; }
        if (ddgiGridCb != null) { ddgiGridCb.Unmap(0); ddgiGridCb.Dispose(); ddgiGridCb = null; }
        built = false;
    }
}
