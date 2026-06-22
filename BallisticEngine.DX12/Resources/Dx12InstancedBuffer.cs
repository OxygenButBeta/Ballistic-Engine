using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12InstancedBuffer : InstancedBuffer {
    protected override int UID { get; set; }
    static int nextId = 1;

    ID3D12Resource resource;
    int capacityElems;
    int elementCount;
    public override int ElementCount => elementCount;
    public ID3D12Resource Resource => resource;
    public override ulong GpuAddress => resource?.GPUVirtualAddress ?? 0;
    public override unsafe int Stride => sizeof(Matrix4x4);

    public Dx12InstancedBuffer(RenderContext renderContext) : base(renderContext) {
        UID = nextId++;
    }

    public override void Create() {
    }

    public override unsafe void SetBufferData(in Matrix4[] data, BufferUsage usage) {
        int count = data?.Length ?? 0;
        elementCount = count;
        if (count == 0)
            return;

        EnsureCapacity(count);
        Span<Matrix4x4> dst = resource.Map<Matrix4x4>(0, count);
        for (int i = 0; i < count; i++)
            dst[i] = data[i];
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
