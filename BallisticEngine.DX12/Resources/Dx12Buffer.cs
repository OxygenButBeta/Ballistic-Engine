using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public class Dx12Buffer<T> : GPUBuffer<T> where T : unmanaged {
    protected override int UID { get; set; }
    static int nextId = 1;

    ID3D12Resource resource;
    public ID3D12Resource Resource => resource;
    int elementCount;
    public override int ElementCount => elementCount;
    public override unsafe int Stride => sizeof(T);
    public override int ByteSize => elementCount * Stride;
    public override ulong GpuAddress => resource?.GPUVirtualAddress ?? 0;

    protected virtual ResourceStates FinalState => ResourceStates.VertexAndConstantBuffer;

    public Dx12Buffer(RenderContext renderContext) : base(renderContext) {
        UID = nextId++;
    }

    public override void Create() {
    }

    public override void SetBufferData(in T[] data, BufferUsage usage) {
        resource?.Dispose();
        if (data is null || data.Length == 0) {
            resource = null;
            elementCount = 0;
            return;
        }
        elementCount = data.Length;
        resource = Dx12RenderContext.Device.CreateDefaultBuffer<T>(data, FinalState);
        resource.Name = $"{typeof(T).Name}Buffer#{UID}";
    }

    public override void Activate() {
    }
    public override void Deactivate() { }

    public override void Dispose() {
        resource?.Dispose();
        resource = null;
    }
}
