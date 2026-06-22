using Vortice.Direct3D12;
using Vortice.DXGI;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

public sealed class Dx12RtGeometry : IDisposable {
    readonly Dx12Device dev;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtInstance { public uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; public uint PositionIdx, TriCount, Pad0, Pad1; }

    sealed class MeshEntry { public int NormalIdx, UvIdx, IndexIdx, TriMatIdx, PositionIdx, TriCount; public ID3D12Resource TriMatBuf; }
    readonly Dictionary<Mesh, MeshEntry> byMesh = new();

    ID3D12Resource instanceBuf;
    public ulong InstancesGpuAddress => instanceBuf?.GPUVirtualAddress ?? 0;
    public int InstanceCount { get; private set; }
    public bool Valid => instanceBuf != null && InstanceCount > 0;

    int stamp = -1;

    public Dx12RtGeometry(Dx12Device device) { dev = device; }

    public unsafe void Ensure(IEnumerable<IStaticMeshRenderer> renderers, Dx12GpuDrivenRenderer gpu) {
        var insts = new List<(Mesh mesh, IStaticMeshRenderer r)>();
        var h = new HashCode();
        h.Add(gpu.MaterialTableStamp);
        foreach (IStaticMeshRenderer r in renderers) {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            Mesh mesh = r.SharedMesh;
            if (mesh?.VertexBuffer is not Dx12Buffer<GLVector3> || mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null)
                continue;
            if (mesh.NormalBuffer is not Dx12Buffer<GLVector3> nb || nb.Resource is null) continue;
            insts.Add((mesh, r));
            h.Add(mesh.GetHashCode());
        }
        int s = h.ToHashCode();
        if (s == stamp && instanceBuf != null) return;
        stamp = s;
        Rebuild(insts, gpu);
    }

    unsafe void Rebuild(List<(Mesh mesh, IStaticMeshRenderer r)> insts, Dx12GpuDrivenRenderer gpu) {
        foreach (MeshEntry e in byMesh.Values) e.TriMatBuf?.Dispose();
        byMesh.Clear();

        var records = new RtInstance[insts.Count];
        for (int i = 0; i < insts.Count; i++) {
            var (mesh, r) = insts[i];
            MeshEntry e = EntryFor(mesh, r, gpu);
            records[i] = new RtInstance {
                NormalIdx = (uint)e.NormalIdx, UvIdx = (uint)e.UvIdx,
                IndexIdx = (uint)e.IndexIdx, TriMatIdx = (uint)e.TriMatIdx,
                PositionIdx = (uint)e.PositionIdx, TriCount = (uint)e.TriCount,
            };
        }

        instanceBuf?.Dispose();
        InstanceCount = records.Length;
        instanceBuf = records.Length > 0
            ? dev.CreateUavBuffer<RtInstance>(records, ResourceStates.GenericRead)
            : null;
    }

    MeshEntry EntryFor(Mesh mesh, IStaticMeshRenderer r, Dx12GpuDrivenRenderer gpu) {
        if (byMesh.TryGetValue(mesh, out MeshEntry cached)) return cached;

        var ib = (Dx12IndexBuffer)mesh.IndexBuffer;
        var nb = (Dx12Buffer<GLVector3>)mesh.NormalBuffer;
        var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
        var vb = (Dx12Buffer<GLVector3>)mesh.VertexBuffer;

        var e = new MeshEntry {
            IndexIdx = RegisterTypedSrv(ib.Resource, Format.R32_UInt, ib.ElementCount),
            NormalIdx = RegisterStructuredSrv(nb.Resource, nb.ElementCount, 12),
            PositionIdx = RegisterStructuredSrv(vb.Resource, vb.ElementCount, 12),
            TriCount = ib.ElementCount / 3,
            UvIdx = ub?.Resource is not null
                ? RegisterStructuredSrv(ub.Resource, ub.ElementCount, 8)
                : RegisterStructuredSrv(nb.Resource, nb.ElementCount, 12),
        };
        BuildTriMaterials(mesh, r, gpu, out e.TriMatBuf, out e.TriMatIdx);
        byMesh[mesh] = e;
        return e;
    }

    unsafe void BuildTriMaterials(Mesh mesh, IStaticMeshRenderer r, Dx12GpuDrivenRenderer gpu,
                                  out ID3D12Resource buf, out int bindlessIdx) {
        int triCount = mesh.IndexBuffer.ElementCount / 3;
        var triMat = new uint[Math.Max(triCount, 1)];
        for (int sm = 0; sm < mesh.SubMeshes.Length; sm++) {
            SubMeshData sub = mesh.SubMeshes[sm];
            if (sub.IndexCount <= 0) continue;
            int matId = gpu.ResolveOrRegisterMaterialId(r.MaterialFor(sm));
            if (matId < 0) matId = 0;
            int triStart = sub.IndexStart / 3;
            int triEnd = Math.Min((sub.IndexStart + sub.IndexCount) / 3, triCount);
            for (int t = triStart; t < triEnd; t++) triMat[t] = (uint)matId;
        }
        buf = dev.CreateUavBuffer<uint>(triMat, ResourceStates.GenericRead);
        bindlessIdx = RegisterStructuredSrv(buf, triMat.Length, 4);
    }

    int RegisterTypedSrv(ID3D12Resource res, Format format, int elementCount) {
        int idx = Dx12Backend.BindlessHeap.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = format, ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = (uint)elementCount, StructureByteStride = 0,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, Dx12Backend.BindlessHeap.Cpu(idx));
        return idx;
    }

    int RegisterStructuredSrv(ID3D12Resource res, int elementCount, int stride) {
        int idx = Dx12Backend.BindlessHeap.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = (uint)elementCount, StructureByteStride = (uint)stride,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, Dx12Backend.BindlessHeap.Cpu(idx));
        return idx;
    }

    public void Dispose() {
        foreach (MeshEntry e in byMesh.Values) e.TriMatBuf?.Dispose();
        byMesh.Clear();
        instanceBuf?.Dispose(); instanceBuf = null;
    }
}
