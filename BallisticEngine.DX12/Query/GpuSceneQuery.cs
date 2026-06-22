using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

public sealed class GpuSceneQuery : IDisposable {
    public enum SpaceClass : uint { Open = 0, Enclosed = 1, Solid = 2 }

    readonly Dx12Device dev;
    readonly Dx12SceneAS sceneAS;
    readonly bool ownsSceneAS;

    ID3D12RootSignature rootSig;
    ID3D12PipelineState psoOccupancy, psoVisibility, psoClassify, psoNudge;
    Dx12DescriptorHeap tlasHeap;
    bool dxrAvailable, checkedDxr, built;

    const float DefaultProbeRadius = 200f;
    const float DefaultRayBias = 0.02f;

    [StructLayout(LayoutKind.Sequential)]
    struct QueryConstants { public uint Count; public float ProbeRadius; public float RayBias; public uint Pad; }

    [StructLayout(LayoutKind.Sequential)]
    struct VisPair { public Vector3 A; public Vector3 B; }

    readonly bool trustSharedScene;

    public GpuSceneQuery(Dx12Device device, Dx12SceneAS shared = null, bool trustSharedScene = false) {
        dev = device;
        this.trustSharedScene = trustSharedScene && shared != null;
        if (shared != null) { sceneAS = shared; ownsSceneAS = false; }
        else { sceneAS = new Dx12SceneAS(device); ownsSceneAS = true; }
    }

    public bool Available => dxrAvailable;

    unsafe bool EnsureBuilt() {
        if (!checkedDxr) {
            checkedDxr = true;
            try {
                var opt5 = dev.Device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5);
                dxrAvailable = opt5.RaytracingTier >= RaytracingTier.Tier1_1;
            } catch { dxrAvailable = false; }
        }
        if (!dxrAvailable) return false;
        if (built) return true;

        var tlasRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var prms = new[] {
            new RootParameter1(new RootDescriptorTable1(tlasRange), ShaderVisibility.All), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, prms)));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Query.hlsl");
        psoOccupancy  = MakePso(hlsl, "Occupancy");
        psoVisibility = MakePso(hlsl, "Visibility");
        psoClassify   = MakePso(hlsl, "Classify");
        psoNudge      = MakePso(hlsl, "Nudge");

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

    bool EnsureScene() {
        if (testTlasWriter != null) return true;
        if (trustSharedScene) return sceneAS.Valid;
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        return sceneAS.Valid;
    }

    Action<CpuDescriptorHandle> testTlasWriter;
    internal void SetTestTlasWriter(Action<CpuDescriptorHandle> writer) { testTlasWriter = writer; EnsureBuilt(); }

    public bool[] OccupancyAt(IReadOnlyList<Vector3> points, float probeRadius = DefaultProbeRadius) {
        int n = points.Count;
        var result = new bool[n];
        if (n == 0 || !EnsureBuilt() || !EnsureScene()) return result;
        uint[] flags = RunPoints(psoOccupancy, points, probeRadius);
        for (int i = 0; i < n; i++) result[i] = flags[i] != 0;
        return result;
    }

    public bool[] Visibility(IReadOnlyList<(Vector3 a, Vector3 b)> pairs) {
        int n = pairs.Count;
        var result = new bool[n];
        for (int i = 0; i < n; i++) result[i] = true;
        if (n == 0 || !EnsureBuilt() || !EnsureScene()) return result;
        uint[] flags = RunPairs(psoVisibility, pairs);
        for (int i = 0; i < n; i++) result[i] = flags[i] != 0;
        return result;
    }

    public SpaceClass[] ClassifySpace(IReadOnlyList<Vector3> points, float probeRadius = DefaultProbeRadius) {
        int n = points.Count;
        var result = new SpaceClass[n];
        if (n == 0 || !EnsureBuilt() || !EnsureScene()) return result;
        uint[] flags = RunPoints(psoClassify, points, probeRadius);
        for (int i = 0; i < n; i++) result[i] = (SpaceClass)flags[i];
        return result;
    }

    public Vector3[] NudgeToFreeSpace(IReadOnlyList<Vector3> points, float probeRadius = DefaultProbeRadius) {
        int n = points.Count;
        var result = new Vector3[n];
        for (int i = 0; i < n; i++) result[i] = points[i];
        if (n == 0 || !EnsureBuilt() || !EnsureScene()) return result;
        return DispatchNudge(points, probeRadius);
    }

    public int[] VisibilityClusters(IReadOnlyList<Vector3> points) {
        int n = points.Count;
        var labels = new int[n];
        if (n == 0) return labels;
        if (!EnsureBuilt() || !EnsureScene()) return labels;

        var pairs = new List<(Vector3, Vector3)>(n * (n - 1) / 2);
        var ij = new List<(int, int)>(pairs.Capacity);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++) { pairs.Add((points[i], points[j])); ij.Add((i, j)); }

        bool[] vis = pairs.Count == 0 ? Array.Empty<bool>() : Visibility(pairs);

        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
        for (int k = 0; k < vis.Length; k++)
            if (vis[k]) { var (a, b) = ij[k]; Union(a, b); }

        var rootToLabel = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) {
            int r = Find(i);
            if (!rootToLabel.TryGetValue(r, out int lbl)) { lbl = rootToLabel.Count; rootToLabel[r] = lbl; }
            labels[i] = lbl;
        }
        return labels;
    }

    unsafe uint[] RunPoints(ID3D12PipelineState pso, IReadOnlyList<Vector3> points, float probeRadius) {
        int n = points.Count;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++) pts[i] = points[i];
        using ID3D12Resource inBuf = dev.CreateDefaultBuffer<Vector3>(pts, ResourceStates.NonPixelShaderResource);
        return Dispatch(pso, inBuf, inBuf, n, probeRadius);
    }

    unsafe uint[] RunPairs(ID3D12PipelineState pso, IReadOnlyList<(Vector3 a, Vector3 b)> pairs) {
        int n = pairs.Count;
        var data = new VisPair[n];
        for (int i = 0; i < n; i++) data[i] = new VisPair { A = pairs[i].a, B = pairs[i].b };
        using ID3D12Resource inBuf = dev.CreateDefaultBuffer<VisPair>(data, ResourceStates.NonPixelShaderResource);
        return Dispatch(pso, inBuf, inBuf, n, DefaultProbeRadius);
    }

    unsafe ID3D12Resource MakeConstants(int count, float probeRadius) {
        var consts = new QueryConstants {
            Count = (uint)count, ProbeRadius = probeRadius, RayBias = DefaultRayBias, Pad = 0,
        };
        ID3D12Resource cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(256), ResourceStates.GenericRead);
        byte* p = cb.Map<byte>(0); *(QueryConstants*)p = consts; cb.Unmap(0);
        return cb;
    }

    void BindCommon(ID3D12GraphicsCommandList4 cl, ID3D12PipelineState pso, ID3D12Resource pointsBuf,
        ID3D12Resource pairsBuf, ID3D12Resource cb) {
        if (testTlasWriter != null) testTlasWriter(tlasHeap.Cpu(0));
        else sceneAS.CreateTlasSrv(tlasHeap.Cpu(0));
        cl.SetDescriptorHeaps(tlasHeap.Heap);
        cl.SetComputeRootSignature(rootSig);
        cl.SetPipelineState(pso);
        cl.SetComputeRootDescriptorTable(0, tlasHeap.Gpu(0));
        cl.SetComputeRootShaderResourceView(1, pointsBuf.GPUVirtualAddress);
        cl.SetComputeRootShaderResourceView(2, pairsBuf.GPUVirtualAddress);
        cl.SetComputeRootConstantBufferView(5, cb.GPUVirtualAddress);
    }

    unsafe uint[] Dispatch(ID3D12PipelineState pso, ID3D12Resource pointsBuf, ID3D12Resource pairsBuf,
        int count, float probeRadius) {
        var zeros = new uint[count];
        using ID3D12Resource outBuf = dev.CreateUavBuffer<uint>(zeros, ResourceStates.UnorderedAccess);
        using ID3D12Resource dummyPts = dev.CreateUavBuffer<Vector3>(new Vector3[1], ResourceStates.UnorderedAccess);
        using ID3D12Resource outRb = dev.CreateReadbackBuffer(count * sizeof(uint));
        using ID3D12Resource cb = MakeConstants(count, probeRadius);

        dev.ExecuteSync(cl => {
            BindCommon(cl, pso, pointsBuf, pairsBuf, cb);
            cl.SetComputeRootUnorderedAccessView(3, outBuf.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(4, dummyPts.GPUVirtualAddress);
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

    unsafe Vector3[] DispatchNudge(IReadOnlyList<Vector3> points, float probeRadius) {
        int count = points.Count;
        var pts = new Vector3[count];
        for (int i = 0; i < count; i++) pts[i] = points[i];
        using ID3D12Resource inBuf = dev.CreateDefaultBuffer<Vector3>(pts, ResourceStates.NonPixelShaderResource);
        using ID3D12Resource outBuf = dev.CreateUavBuffer<Vector3>(new Vector3[count], ResourceStates.UnorderedAccess);
        using ID3D12Resource dummyFlags = dev.CreateUavBuffer<uint>(new uint[1], ResourceStates.UnorderedAccess);
        using ID3D12Resource outRb = dev.CreateReadbackBuffer(count * sizeof(float) * 3);
        using ID3D12Resource cb = MakeConstants(count, probeRadius);

        dev.ExecuteSync(cl => {
            BindCommon(cl, psoNudge, inBuf, inBuf, cb);
            cl.SetComputeRootUnorderedAccessView(3, dummyFlags.GPUVirtualAddress);
            cl.SetComputeRootUnorderedAccessView(4, outBuf.GPUVirtualAddress);
            cl.Dispatch((uint)((count + 63) / 64), 1, 1);
            cl.ResourceBarrierTransition(outBuf, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.CopyBufferRegion(outRb, 0, outBuf, 0, (ulong)(count * sizeof(float) * 3));
        });

        var result = new Vector3[count];
        Span<Vector3> mapped = outRb.Map<Vector3>(0, count);
        mapped.CopyTo(result);
        outRb.Unmap(0);
        return result;
    }

    public void Dispose() {
        rootSig?.Dispose();
        psoOccupancy?.Dispose(); psoVisibility?.Dispose(); psoClassify?.Dispose(); psoNudge?.Dispose();
        tlasHeap?.Dispose();
        if (ownsSceneAS) sceneAS?.Dispose();
    }
}
