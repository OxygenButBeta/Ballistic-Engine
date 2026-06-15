using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

// GpuSceneQuery — the engine's spatial-understanding substrate (the AI agent's "eyes"). Wraps the DXR
// scene TLAS (Dx12SceneAS) behind a tiny deterministic query API: occupancy (is a point inside solid?),
// visibility (can A see B?), and space classification (open / enclosed / solid). All queries run as inline
// RayQuery (DXR Tier 1.1) in a plain compute shader (Query.hlsl) — no shader binding table, no RT PSO, no
// hit/miss shaders. One thread per query element; the whole batch is one dispatch + one readback.
//
// DETERMINISM: every ray uses a fixed direction (the a->b vector, the 3 fixed occupancy axes, or a
// closed-form Fibonacci sphere) — no RNG, no frame index. Two runs are byte-identical (the engine's verify
// harness depends on this). Owns its OWN Dx12SceneAS so queries work even when no RT render effect is on.
//
// On-demand (NOT per-frame): a query call builds/refreshes the AS (stamp-cached, cheap), dispatches, reads
// the result back to the CPU. Headless-safe (the AS is fed from RuntimeSet<IStaticMeshRenderer>, populated
// in OnAttach in every mode). See Docs/Plans/gpu-scene-query-api-proposal.md.
public sealed class GpuSceneQuery : IDisposable {
    public enum SpaceClass : uint { Open = 0, Enclosed = 1, Solid = 2 }

    readonly Dx12Device dev;
    readonly Dx12SceneAS sceneAS;
    readonly bool ownsSceneAS;

    ID3D12RootSignature rootSig;
    ID3D12PipelineState psoOccupancy, psoVisibility, psoClassify;
    Dx12DescriptorHeap tlasHeap;        // single shader-visible slot: the TLAS AS-SRV
    bool dxrAvailable, checkedDxr, built;

    // Default ray reach for occupancy parity + classify sphere (world units). Large enough to exit any
    // reasonable interior so the parity count is complete; classify uses it as the enclosure probe radius.
    const float DefaultProbeRadius = 200f;
    const float DefaultRayBias = 0.02f;

    [StructLayout(LayoutKind.Sequential)]
    struct QueryConstants { public uint Count; public float ProbeRadius; public float RayBias; public uint Pad; }

    [StructLayout(LayoutKind.Sequential)]
    struct VisPair { public Vector3 A; public Vector3 B; }

    public GpuSceneQuery(Dx12Device device, Dx12SceneAS shared = null) {
        dev = device;
        if (shared != null) { sceneAS = shared; ownsSceneAS = false; }
        else { sceneAS = new Dx12SceneAS(device); ownsSceneAS = true; }
    }

    // True once the device is known to support DXR (lazily checked on first use).
    public bool Available => dxrAvailable;

    unsafe bool EnsureBuilt() {
        if (!checkedDxr) {
            checkedDxr = true;
            try {
                var opt5 = dev.Device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5);
                // Inline RayQuery needs Tier 1.1.
                dxrAvailable = opt5.RaytracingTier >= RaytracingTier.Tier1_1;
            } catch { dxrAvailable = false; }
        }
        if (!dxrAvailable) return false;
        if (built) return true;

        // Root sig: table0 = SRV t0 (TLAS), root SRV t1 (points), root SRV t2 (pairs), root UAV u0 (out),
        // root constants b0 (QueryConstants, 4 dwords). Unused root descriptors per pass get a safe address.
        var tlasRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var prms = new[] {
            new RootParameter1(new RootDescriptorTable1(tlasRange), ShaderVisibility.All),               // 0: t0 TLAS
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All), // 1: t1 points
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All), // 2: t2 pairs
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),// 3: u0 out
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All), // 4: b0 consts (CBV)
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, prms)));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Query.hlsl");
        psoOccupancy  = MakePso(hlsl, "Occupancy");
        psoVisibility = MakePso(hlsl, "Visibility");
        psoClassify   = MakePso(hlsl, "Classify");

        tlasHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true);
        built = true;
        return true;
    }

    ID3D12PipelineState MakePso(string hlsl, string entry) =>
        dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = rootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, entry, "Query.hlsl"),
        });

    // Refresh the AS from the live renderer set; returns false if there is no geometry to query against.
    bool EnsureScene() {
        if (testTlasWriter != null) return true;   // self-test injects its own TLAS
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        return sceneAS.Valid;
    }

    // Self-test seam: when set, the dispatch binds THIS TLAS instead of sceneAS's (the probe builds a known
    // box AS so the shader logic is verified in isolation, with no scene/renderers). Production leaves it null.
    Action<CpuDescriptorHandle> testTlasWriter;
    internal void SetTestTlasWriter(Action<CpuDescriptorHandle> writer) { testTlasWriter = writer; EnsureBuilt(); }

    // ---- public API -----------------------------------------------------------

    // True per point if it lies inside solid geometry (ray-parity, 3-axis majority vote). Empty scene -> all false.
    public bool[] OccupancyAt(IReadOnlyList<Vector3> points, float probeRadius = DefaultProbeRadius) {
        int n = points.Count;
        var result = new bool[n];
        if (n == 0 || !EnsureBuilt() || !EnsureScene()) return result;
        uint[] flags = RunPoints(psoOccupancy, points, probeRadius);
        for (int i = 0; i < n; i++) result[i] = flags[i] != 0;
        return result;
    }

    // True per pair if A has a clear line of sight to B (no opaque geometry between). Empty scene -> all true.
    public bool[] Visibility(IReadOnlyList<(Vector3 a, Vector3 b)> pairs) {
        int n = pairs.Count;
        var result = new bool[n];
        for (int i = 0; i < n; i++) result[i] = true;
        if (n == 0 || !EnsureBuilt() || !EnsureScene()) return result;
        uint[] flags = RunPairs(psoVisibility, pairs);
        for (int i = 0; i < n; i++) result[i] = flags[i] != 0;
        return result;
    }

    // Per point: Solid (inside geometry), Enclosed (walls on most sides), or Open. Empty scene -> all Open.
    public SpaceClass[] ClassifySpace(IReadOnlyList<Vector3> points, float probeRadius = DefaultProbeRadius) {
        int n = points.Count;
        var result = new SpaceClass[n];
        if (n == 0 || !EnsureBuilt() || !EnsureScene()) return result;   // default(SpaceClass) == Open
        uint[] flags = RunPoints(psoClassify, points, probeRadius);
        for (int i = 0; i < n; i++) result[i] = (SpaceClass)flags[i];
        return result;
    }

    // ---- dispatch plumbing ----------------------------------------------------

    unsafe uint[] RunPoints(ID3D12PipelineState pso, IReadOnlyList<Vector3> points, float probeRadius) {
        int n = points.Count;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++) pts[i] = points[i];
        using ID3D12Resource inBuf = dev.CreateDefaultBuffer<Vector3>(pts, ResourceStates.NonPixelShaderResource);
        return Dispatch(pso, inBuf, inBuf, n, probeRadius);   // pairs slot unused; bind inBuf as a safe address
    }

    unsafe uint[] RunPairs(ID3D12PipelineState pso, IReadOnlyList<(Vector3 a, Vector3 b)> pairs) {
        int n = pairs.Count;
        var data = new VisPair[n];
        for (int i = 0; i < n; i++) data[i] = new VisPair { A = pairs[i].a, B = pairs[i].b };
        using ID3D12Resource inBuf = dev.CreateDefaultBuffer<VisPair>(data, ResourceStates.NonPixelShaderResource);
        return Dispatch(pso, inBuf, inBuf, n, DefaultProbeRadius);   // points slot unused; bind inBuf as a safe address
    }

    // Core: bind TLAS + points (t1) + pairs (t2) + out UAV (u0) + consts, dispatch one thread per element,
    // read the uint result buffer back. pointsBuf and pairsBuf may alias the same resource (the shader only
    // reads the one it needs); binding a valid address for both keeps the debug layer quiet.
    unsafe uint[] Dispatch(ID3D12PipelineState pso, ID3D12Resource pointsBuf, ID3D12Resource pairsBuf,
        int count, float probeRadius) {
        var zeros = new uint[count];
        using ID3D12Resource outBuf = dev.CreateUavBuffer<uint>(zeros, ResourceStates.UnorderedAccess);
        using ID3D12Resource outRb = dev.CreateReadbackBuffer(count * sizeof(uint));

        // Constants in a tiny upload-heap CBV (256-byte aligned, the DX12 CBV rule). Same pattern as the
        // RT shadow/GI passes (SetComputeRootConstantBufferView).
        var consts = new QueryConstants {
            Count = (uint)count, ProbeRadius = probeRadius, RayBias = DefaultRayBias, Pad = 0,
        };
        using ID3D12Resource cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(256), ResourceStates.GenericRead);
        { byte* p = cb.Map<byte>(0); *(QueryConstants*)p = consts; cb.Unmap(0); }

        // The TLAS SRV is rebuilt into the heap slot each call (descriptors are transient; the TLAS persists).
        if (testTlasWriter != null) testTlasWriter(tlasHeap.Cpu(0));
        else sceneAS.CreateTlasSrv(tlasHeap.Cpu(0));

        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(tlasHeap.Heap);
            cl.SetComputeRootSignature(rootSig);
            cl.SetPipelineState(pso);
            cl.SetComputeRootDescriptorTable(0, tlasHeap.Gpu(0));
            cl.SetComputeRootShaderResourceView(1, pointsBuf.GPUVirtualAddress);
            cl.SetComputeRootShaderResourceView(2, pairsBuf.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(3, outBuf.GPUVirtualAddress);
            cl.SetComputeRootConstantBufferView(4, cb.GPUVirtualAddress);
            cl.Dispatch((uint)((count + 63) / 64), 1, 1);
            cl.ResourceBarrierTransition(outBuf, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.CopyBufferRegion(outRb, 0, outBuf, 0, (ulong)(count * sizeof(uint)));
        });

        var result = new uint[count];
        Span<uint> mapped = outRb.Map<uint>(0, count);
        mapped.CopyTo(result);
        outRb.Unmap(0);
        return result;
    }

    public void Dispose() {
        rootSig?.Dispose();
        psoOccupancy?.Dispose(); psoVisibility?.Dispose(); psoClassify?.Dispose();
        tlasHeap?.Dispose();
        if (ownsSceneAS) sceneAS?.Dispose();
    }
}
