using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// DX12 implementation of the engine's GPUBuffer<T>. Unlike GL (Create = glGenBuffer, then bind+upload),
// a DX12 buffer is a single committed DEFAULT-heap resource created when the data arrives (SetBufferData),
// because that's the only moment we know the size and contents. Create() is therefore a no-op marker;
// Activate/Deactivate are no-ops (DX12 binds buffers per-draw on the command list, not as context state).
//
// The renderer reads GpuAddress + ElementCount + Stride to build a VertexBufferView/IndexBufferView at
// draw time — the data is already GPU-local (CreateDefaultBuffer does the upload-heap copy once).
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

    // Vertex buffers go to VertexAndConstantBuffer state; the index subclass overrides to IndexBuffer.
    protected virtual ResourceStates FinalState => ResourceStates.VertexAndConstantBuffer;

    public Dx12Buffer(RenderContext renderContext) : base(renderContext) {
        UID = nextId++;
    }

    public override void Create() { /* allocation happens in SetBufferData when the data exists */ }

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

    public override void Activate() { /* per-draw binding on the command list, not context state */ }
    public override void Deactivate() { }

    public override void Dispose() {
        resource?.Dispose();
        resource = null;
    }
}

// Index buffer: same storage, but the resource rests in IndexBuffer state and the renderer reads it as
// R32_UInt (engine indices are uint). Mirrors GlIndexBufferBase being a typed GLBufferBase<uint>.
public sealed class Dx12IndexBuffer : Dx12Buffer<uint> {
    protected override ResourceStates FinalState => ResourceStates.IndexBuffer;
    public Dx12IndexBuffer(RenderContext renderContext) : base(renderContext) { }
}
