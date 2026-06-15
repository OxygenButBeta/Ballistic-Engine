using System.Numerics;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// DX12 per-instance model-matrix buffer. Instancing streams model matrices into a vertex buffer the
// shader reads via SV_InstanceID (or input-assembler instance step rate). Unlike the static mesh
// buffers, this is rewritten every frame for the visible instance run, so it lives in an UPLOAD heap
// (CPU-writable, no copy) sized to a high-water mark and grown on demand.
//
// Phase 2d renders per-submesh (no instancing) first, so this is wired but only exercised once the
// instanced opaque path is enabled — same staging order the GL backend went through.
public sealed class Dx12InstancedBuffer : InstancedBuffer {
    protected override int UID { get; set; }
    static int nextId = 1;

    ID3D12Resource resource;
    int capacityElems;   // allocated capacity in matrices
    int elementCount;
    public override int ElementCount => elementCount;
    public ID3D12Resource Resource => resource;
    public override ulong GpuAddress => resource?.GPUVirtualAddress ?? 0;
    public override unsafe int Stride => sizeof(Matrix4x4);   // 64 bytes

    public Dx12InstancedBuffer(RenderContext renderContext) : base(renderContext) {
        UID = nextId++;
    }

    public override void Create() { /* lazily allocated on first SetBufferData */ }

    // OpenTK.Matrix4 in -> upload as System.Numerics row-major 4x4 (16 floats, same memory layout).
    // The renderer transposes per-draw matrices on the CBV path; instanced matrices are read in the
    // vertex shader the same way, so the transpose convention must match (handled at fill time there).
    public override unsafe void SetBufferData(in OpenTK.Mathematics.Matrix4[] data, BufferUsage usage) {
        int count = data?.Length ?? 0;
        elementCount = count;
        if (count == 0)
            return;

        EnsureCapacity(count);
        Span<Matrix4x4> dst = resource.Map<Matrix4x4>(0, count);
        for (int i = 0; i < count; i++) {
            OpenTK.Mathematics.Matrix4 m = data[i];
            // OpenTK is row-major; copy element-wise into a System.Numerics row-major matrix.
            dst[i] = new Matrix4x4(
                m.M11, m.M12, m.M13, m.M14,
                m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34,
                m.M41, m.M42, m.M43, m.M44);
        }
        resource.Unmap(0);
    }

    void EnsureCapacity(int count) {
        if (resource != null && count <= capacityElems)
            return;
        resource?.Dispose();
        capacityElems = Math.Max(count, capacityElems == 0 ? 256 : capacityElems * 2);
        resource = Dx12RenderContext.Device.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(capacityElems * Stride)),
            ResourceStates.GenericRead);
        resource.Name = $"InstanceBuffer#{UID}";
    }

    public override void Activate() { }
    public override void Deactivate() { }

    public override void Dispose() {
        resource?.Dispose();
        resource = null;
    }
}
