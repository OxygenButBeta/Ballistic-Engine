using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// DDGI world-probe radiance cache (GI plan P2 — the chosen P2, replacing the Lumen mesh-card surface cache;
// see Docs/Plans/dx12-lumen-gi-plan.md Phase 2). A camera-centered 3D grid of irradiance probes: each probe
// stores incoming radiance as a small OCTAHEDRAL irradiance map + a depth-moments map (mean + mean-squared
// distance) for the Chebyshev visibility (leak) test. Probes are updated by tracing rays against the scene
// TLAS and shading the hit with the EXISTING P1 world-radiance path (DxrGi.hlsl), blended over time with a
// hysteresis EMA — so multi-bounce is free (update rays read last frame's probe field) and the result is
// stable under camera motion. A shading point gathers the 8 enclosing probes, trilinear + Chebyshev-weighted.
// Published technique (Majercik et al. 2019 JCGT; NVIDIA RTXGI) — no bake, no authoring, fully dynamic.
//
// P2.0 (this file's first cut): the GRID + the two atlas textures (irradiance + depth) as UAV/SRV, the
// constants, and camera-centered placement. The update/blend/gather compute passes land in P2.1+.
public sealed class Dx12Ddgi : IDisposable {
    readonly Dx12Device dev;

    // --- Grid dimensions (probes per axis). Start modest; tune to the GTX-1660 VRAM/ray budget in P2.5.
    // 16 x 8 x 16 = 2048 probes. Camera-centered, snapped to the probe spacing so it slides smoothly.
    public const int ProbesX = 16, ProbesY = 8, ProbesZ = 16;
    public const int ProbeCount = ProbesX * ProbesY * ProbesZ;

    // --- Octahedral tile sizes (interior texels; a 1px border is added for correct bilinear wrap at edges).
    public const int IrradianceTexels = 6;    // 6x6 octahedral irradiance per probe (RGBA16F)
    public const int DepthTexels = 16;         // 16x16 octahedral depth moments per probe (RG16F)
    const int Border = 1;
    const int IrrTile = IrradianceTexels + 2 * Border;   // 8
    const int DepthTile = DepthTexels + 2 * Border;       // 18

    // Atlas layout: probes flattened as a 2D grid of tiles, (ProbesX*ProbesZ) columns x ProbesY rows. So a
    // probe (px,py,pz) → tile column = pz*ProbesX + px, tile row = py. One draw/dispatch covers the atlas.
    public const int TilesWide = ProbesX * ProbesZ;       // 256
    public const int TilesHigh = ProbesY;                  // 8
    public static int IrradianceAtlasW => TilesWide * IrrTile;      // 2048
    public static int IrradianceAtlasH => TilesHigh * IrrTile;      // 64
    public static int DepthAtlasW => TilesWide * DepthTile;         // 4608
    public static int DepthAtlasH => TilesHigh * DepthTile;         // 144

    // Atlas textures (compute-written UAV + gather-read SRV). The atlases are PERSISTENT resources; their
    // descriptors are created per-dispatch into a shader-visible heap by the update/gather passes (P2.1+) —
    // NOT registered in Dx12Backend.BindlessHeap, which the material table Resets (would clobber them).
    public ID3D12Resource IrradianceTex => irradianceTex;
    public ID3D12Resource DepthTex => depthTex;
    ID3D12Resource irradianceTex, depthTex;

    // --- Grid placement (world space). Origin = the corner probe; spacing = metres between probes. The grid
    // is camera-centered: re-snapped each frame to the camera so coverage follows the view (a single clipmap
    // cascade for now). ProbeSpacing sets the covered volume = spacing * (probes-1) per axis.
    public Vector3 Origin { get; private set; }
    public Vector3 Spacing { get; private set; } = new(2.0f, 2.0f, 2.0f);   // 2m → ~30x14x30m covered volume

    public bool Allocated => irradianceTex != null;

    // Per-pass constants shared by update/blend/gather (std140-ish; matches Ddgi.hlsl). Kept here so every
    // pass sees ONE grid definition.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct DdgiConstants {
        public Vector4 OriginSpacingX;   // xyz = grid origin (world), w = spacing.x
        public Vector4 SpacingYZ;        // x = spacing.y, y = spacing.z, z/w = pad
        public Vector4 ProbeDims;        // xyz = (ProbesX,ProbesY,ProbesZ) as floats, w = ProbeCount
        public Vector4 Params0;          // x=irrTexels y=depthTexels z=hysteresis w=frameIndex
        public Vector4 Params1;          // x=maxRayDist y=normalBias z=viewBias w=intensity
    }

    // --- P2.1 probe-update plumbing ---
    public const int RaysPerProbe = 144;   // MUST match DdgiTrace/DdgiBlend HLSL (SphericalFibonacci ray count)

    // RayData[probe*RaysPerProbe + ray] = (radiance.rgb, hitDistance), written by the trace pass, read by the
    // blend pass. ProbeCount*144*16B = ~4.7 MB — sized once for the static grid; persistent UAV.
    ID3D12Resource rayData;

    // Trace pass (DdgiTrace.hlsl): inline RayQuery in a COMPUTE PSO (not an RT PSO — RayQuery needs no SBT).
    // Root sig mirrors DxrGi exactly so the bindless hit-shading is byte-identical: CBV b0/b1, a table for
    // {t0 TLAS, t3 irradiance cube} living in the BindlessHeap's reserved tail (so ResourceDescriptorHeap[]
    // geo reads share the one bound heap), root SRV t5/t6/t7, root UAV u0 RayData, static samplers s0/s1.
    ID3D12RootSignature traceRootSig;
    ID3D12PipelineState tracePso;

    // Blend pass (DdgiBlend.hlsl): two compute entry points (CSIrradiance→u0 irr atlas, CSDepth→u1 depth
    // atlas). Self-contained — no bindless: CBV b0, root SRV t0 RayData, and the atlas UAV via this own tiny
    // shader-visible heap (irr at slot 0 = u0, depth at slot 1 = u1).
    ID3D12RootSignature blendRootSig;
    ID3D12PipelineState blendIrrPso, blendDepthPso;
    Dx12DescriptorHeap blendHeap;       // 2 UAVs: [0]=irradiance (u0), [1]=depth (u1)

    // CBV for the per-dispatch DdgiConstants (upload heap, mapped, refilled each frame).
    ID3D12Resource constCb;
    unsafe byte* constCbMapped;

    bool built;
    int frameCounter;   // drives ray-rotation jitter + the first-frame hard-set in the blend EMA

    public Dx12Ddgi(Dx12Device device) { dev = device; }

    public void EnsureAllocated() {
        if (Allocated) return;
        irradianceTex = CreateAtlas(IrradianceAtlasW, IrradianceAtlasH, Format.R16G16B16A16_Float);
        depthTex = CreateAtlas(DepthAtlasW, DepthAtlasH, Format.R16G16_Float);
    }

    // Camera-centered snap: place the grid so the camera sits near its centre, snapped to whole probe
    // spacings (so probes don't swim under sub-cell camera motion → temporal stability). Call per frame.
    public void Update(Vector3 cameraPos) {
        Vector3 half = new(
            Spacing.X * (ProbesX - 1) * 0.5f,
            Spacing.Y * (ProbesY - 1) * 0.5f,
            Spacing.Z * (ProbesZ - 1) * 0.5f);
        Vector3 snapped = new(
            MathF.Round(cameraPos.X / Spacing.X) * Spacing.X,
            MathF.Round(cameraPos.Y / Spacing.Y) * Spacing.Y,
            MathF.Round(cameraPos.Z / Spacing.Z) * Spacing.Z);
        Origin = snapped - half;
    }

    public DdgiConstants Constants(int frameIndex, float hysteresis, float intensity) => new() {
        OriginSpacingX = new Vector4(Origin, Spacing.X),
        SpacingYZ = new Vector4(Spacing.Y, Spacing.Z, 0, 0),
        ProbeDims = new Vector4(ProbesX, ProbesY, ProbesZ, ProbeCount),
        Params0 = new Vector4(IrradianceTexels, DepthTexels, hysteresis, frameIndex),
        Params1 = new Vector4(40f, 0.25f, 0.1f, intensity),
    };

    // World position of probe (px,py,pz) — for the debug gizmo + the update pass.
    public Vector3 ProbePosition(int px, int py, int pz) =>
        Origin + new Vector3(px * Spacing.X, py * Spacing.Y, pz * Spacing.Z);

    // Build the trace + blend compute PSOs and the RayData buffer (once). The atlas UAVs are registered into
    // blendHeap here too (persistent atlases → persistent descriptors). Idempotent.
    public unsafe void Build() {
        if (built) return;
        built = true;
        EnsureAllocated();

        // RayData UAV buffer (DEFAULT heap, AllowUnorderedAccess), zero-seeded.
        var zero = new Vector4[ProbeCount * RaysPerProbe];
        rayData = dev.CreateUavBuffer<Vector4>(zero, ResourceStates.UnorderedAccess);

        // --- TRACE root sig (mirrors DxrGi). The TLAS + irradiance cube are NON-CONTIGUOUS registers (t0,t3)
        // so the table holds TWO descriptor ranges; they're written to ADJACENT bindless-tail slots so one
        // GPU base handle covers both (range 0 → slot+0 = t0, range 1 → slot+1 = t3). ---
        var t_cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var t_cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var tlasRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0,  // t0
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var cubeRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 3,  // t3
            registerSpace: 0, offsetInDescriptorsFromTableStart: 1);
        var t_table = new RootParameter1(new RootDescriptorTable1(tlasRange, cubeRange), ShaderVisibility.All);
        var t_mat = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All);
        var t_inst = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0), ShaderVisibility.All);
        var t_light = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var t_uav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);  // u0 RayData
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
        traceRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { t_cbv0, t_cbv1, t_table, t_mat, t_inst, t_light, t_uav },
                new[] { clampSamp, wrapSamp })));

        string traceHlsl = EmbeddedShaderSource.ReadHlsl("DdgiTrace.hlsl");
        byte[] traceCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, traceHlsl, "CSMain", "DdgiTrace.hlsl");
        tracePso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = traceRootSig, ComputeShader = traceCs });

        // --- BLEND root sig: CBV b0, root SRV t0 RayData, table covering BOTH UAVs (u0 irr + u1 depth) so one
        // root sig serves both entry points. The table base is blendHeap slot 0, so u0→heap[0]=irr,
        // u1→heap[1]=depth; CSIrradiance writes only u0, CSDepth only u1 (each ignores the other). ---
        var b_cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var b_srv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);  // t0 RayData
        var b_uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, baseShaderRegister: 0);  // u0 irr + u1 depth
        var b_table = new RootParameter1(new RootDescriptorTable1(b_uavRange), ShaderVisibility.All);
        blendRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { b_cbv, b_srv, b_table })));

        string blendHlsl = EmbeddedShaderSource.ReadHlsl("DdgiBlend.hlsl");
        byte[] irrCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, blendHlsl, "CSIrradiance", "DdgiBlend.hlsl");
        byte[] depCs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, blendHlsl, "CSDepth", "DdgiBlend.hlsl");
        blendIrrPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = blendRootSig, ComputeShader = irrCs });
        blendDepthPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = blendRootSig, ComputeShader = depCs });

        // blendHeap: 2 persistent UAV descriptors for the two atlases (irradiance@slot0 = u0, depth@slot1 = u1)
        // laid out CONTIGUOUSLY so the blend root sig's 2-descriptor table (base = slot 0) maps u0→irr,
        // u1→depth for BOTH entry points. CSIrradiance writes only u0; CSDepth writes only u1.
        blendHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, shaderVisible: true);
        dev.Device.CreateUnorderedAccessView(irradianceTex, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, blendHeap.Cpu(0));
        dev.Device.CreateUnorderedAccessView(depthTex, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, blendHeap.Cpu(1));

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<DdgiConstants>() + 255) & ~255;
        constCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        constCbMapped = constCb.Map<byte>(0);
    }

    // Run the probe update: TRACE (rays/probe → RayData) then BLEND (RayData → irradiance + depth atlases).
    // Must be called from inside DrawRtGi AFTER EnsureMaterialTable + rtGeometry.Ensure (bindless ids fresh)
    // and AFTER the RtGi reserved-tail descriptors are written, with the SAME bindless heap bound. The caller
    // supplies the shared root-SRV addresses (materials/instances/lights), the irradiance cube SRV, and the
    // RtGiSun CBV address. `traceTableGpu` is the GPU handle of the 2-descriptor bindless-tail base ([0]=TLAS,
    // [1]=irr cube) the caller has already written; the trace table root param points there. The atlases stay
    // UnorderedAccess state (caller transitions to SRV before the gather pass — P2.2).
    public unsafe void DispatchDdgi(ID3D12GraphicsCommandList4 cl,
        Dx12DescriptorHeap bindless, GpuDescriptorHandle traceTableGpu,
        ulong sunCbAddress, ulong materialsAddr, ulong instancesAddr, ulong lightsAddr,
        float hysteresis, float intensity) {
        *(DdgiConstants*)constCbMapped = Constants(frameCounter, hysteresis, intensity);
        frameCounter++;

        // --- TRACE: ProbeCount*RaysPerProbe threads, 64/group. RayData UAV starts in UnorderedAccess. ---
        cl.SetComputeRootSignature(traceRootSig);
        cl.SetPipelineState(tracePso);
        cl.SetComputeRootConstantBufferView(0, constCb.GPUVirtualAddress);  // b0 DdgiConstants
        cl.SetComputeRootConstantBufferView(1, sunCbAddress);              // b1 RtGiSun
        cl.SetComputeRootDescriptorTable(2, traceTableGpu);               // t0 TLAS + t3 irr cube
        cl.SetComputeRootShaderResourceView(3, materialsAddr);            // t5 GpuMaterials
        cl.SetComputeRootShaderResourceView(4, instancesAddr);           // t6 RtInstance[]
        cl.SetComputeRootShaderResourceView(5, lightsAddr);             // t7 Lights
        cl.SetComputeRootUnorderedAccessView(6, rayData.GPUVirtualAddress);  // u0 RayData
        int totalThreads = ProbeCount * RaysPerProbe;
        cl.Dispatch((uint)((totalThreads + 63) / 64), 1, 1);

        // RayData write → read barrier before blend.
        cl.ResourceBarrierUnorderedAccessView(rayData);

        // --- BLEND: switch heaps to blendHeap (its own shader-visible heap). RayData must be readable as a
        // root SRV (t0): it was created in UnorderedAccess — a root SRV reads it fine in any GenericRead-
        // compatible state, but to be correct transition it to NonPixelShaderResource for the read, then back.
        cl.ResourceBarrierTransition(rayData, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.SetDescriptorHeaps(blendHeap.Heap);
        cl.SetComputeRootSignature(blendRootSig);
        cl.SetComputeRootConstantBufferView(0, constCb.GPUVirtualAddress);   // b0
        cl.SetComputeRootShaderResourceView(1, rayData.GPUVirtualAddress);   // t0 RayData

        // Both passes bind the SAME 2-descriptor table base (slot 0): u0→irr, u1→depth. Each shader writes
        // only its own register.
        cl.SetComputeRootDescriptorTable(2, blendHeap.Gpu(0));

        cl.SetPipelineState(blendIrrPso);
        cl.Dispatch((uint)((IrradianceAtlasW + 7) / 8), (uint)((IrradianceAtlasH + 7) / 8), 1);

        cl.SetPipelineState(blendDepthPso);
        cl.Dispatch((uint)((DepthAtlasW + 7) / 8), (uint)((DepthAtlasH + 7) / 8), 1);

        cl.ResourceBarrierUnorderedAccessView(irradianceTex);
        cl.ResourceBarrierUnorderedAccessView(depthTex);
        // Restore the bindless heap for whatever the caller does next (it bound bindless before us).
        cl.SetDescriptorHeaps(bindless.Heap);
        cl.ResourceBarrierTransition(rayData, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
    }

    // DEBUG (BALLISTIC_DX12_DDGI_DEBUG=1): read the irradiance atlas back to the CPU and report min/max/mean +
    // non-zero fraction, so we can confirm the probe-update pipeline produced sensible, non-zero, smooth data
    // (the P2.1 success gate) WITHOUT a gather pass yet. CPU-side readback, called once after Dispatch; not in
    // the hot path. The atlas is left in UnorderedAccess by DispatchDdgi → transition to CopySource here + back.
    public unsafe void DumpIrradianceStats() {
        if (!built || irradianceTex == null) { Console.WriteLine("[DDGI-DBG] not built"); return; }
        int w = IrradianceAtlasW, h = IrradianceAtlasH;
        const int bpp = 8;   // RGBA16F = 8 bytes/texel
        // Placed footprint of subresource 0 (D3D12 fills the 256-byte-aligned row pitch).
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(irradianceTex.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];
        int rowPitch = (int)fp.Footprint.RowPitch;
        var rb = dev.Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.CopyTextureRegion(new TextureCopyLocation(rb, fp), 0, 0, 0,
                new TextureCopyLocation(irradianceTex, 0), null);
            cl.ResourceBarrierTransition(irradianceTex, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        });
        byte* p = rb.Map<byte>(0);
        double sum = 0; float mn = float.MaxValue, mx = float.MinValue; long nonzero = 0, total = 0;
        for (int y = 0; y < h; y++) {
            byte* row = p + (long)y * rowPitch;
            for (int x = 0; x < w; x++) {
                Half* px = (Half*)(row + x * bpp);
                for (int c = 0; c < 3; c++) {   // RGB only (A is the blend's written-flag)
                    float v = (float)px[c];
                    if (float.IsNaN(v) || float.IsInfinity(v)) { Console.WriteLine($"[DDGI-DBG] NaN/Inf at ({x},{y}) ch{c}"); continue; }
                    sum += v; if (v < mn) mn = v; if (v > mx) mx = v; if (v > 1e-6f) nonzero++; total++;
                }
            }
        }
        rb.Unmap(0); rb.Dispose();
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[DDGI-DBG] irradiance atlas {w}x{h}: mean={sum / Math.Max(total, 1):0.000000} min={mn:0.000000} max={mx:0.000000} nonzero={100.0 * nonzero / Math.Max(total, 1):0.0}% ({nonzero}/{total} RGB samples)"));
    }

    ID3D12Resource CreateAtlas(int w, int h, Format fmt) {
        var desc = ResourceDescription.Texture2D(fmt, (uint)w, (uint)h, 1, 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        return dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
    }

    public void Dispose() {
        irradianceTex?.Dispose(); irradianceTex = null;
        depthTex?.Dispose(); depthTex = null;
        rayData?.Dispose(); rayData = null;
        tracePso?.Dispose(); tracePso = null;
        traceRootSig?.Dispose(); traceRootSig = null;
        blendIrrPso?.Dispose(); blendIrrPso = null;
        blendDepthPso?.Dispose(); blendDepthPso = null;
        blendRootSig?.Dispose(); blendRootSig = null;
        blendHeap?.Dispose(); blendHeap = null;
        if (constCb != null) { constCb.Unmap(0); constCb.Dispose(); constCb = null; }
        built = false;
    }
}
