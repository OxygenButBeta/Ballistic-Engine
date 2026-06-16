using System.Diagnostics.CodeAnalysis;

namespace BallisticEngine;

/// <summary>
/// Base structure for GPU buffers.
/// This class is used to create and manage GPU buffers for various data types.
/// Every GPU buffer must inherit from this class and implement the required methods.
/// An instance of this class is represented a chuck of memory on the GPU that can be used to store data.
/// </summary>
/// <typeparam name="TDataType"></typeparam>
public abstract class GPUBuffer<TDataType> : IDisposable where TDataType : struct
{
    protected RenderContext RenderContext { get; private set; }
    protected abstract int UID { get; set; }

    public GPUBuffer([NotNull] RenderContext renderContext) {
        RenderContext = renderContext;
    }

    public abstract void SetBufferData(in TDataType[] data, BufferUsage usage);
    public abstract void Create();
    public abstract void Dispose();
    public abstract void Activate();
    public abstract void Deactivate();

    // GPU-address accessors for backends that bind buffers per-draw (DX12) rather than via a bound VAO
    // (GL). The renderer reads these to build vertex/index buffer views without knowing the concrete
    // backend type. The GL backend draws off VAO state and never reads them, so the base defaults are
    // fine there; the DX12 buffer overrides with its committed resource's address/size. (Part of the
    // DX-native abstraction redesign — Docs/Plans/dx-native-abstraction-redesign.md.)
    public virtual ulong GpuAddress => 0;
    public virtual int ElementCount => 0;
    public virtual int Stride => 0;
    public virtual int ByteSize => ElementCount * Stride;
}