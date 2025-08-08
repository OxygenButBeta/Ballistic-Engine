using System.Diagnostics.CodeAnalysis;
using OpenTK.Graphics.OpenGL4;

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
    protected abstract BufferTarget Target { get; }

    public GPUBuffer([NotNull] RenderContext renderContext) {
        RenderContext = renderContext;
        RuntimeSet<GPUBuffer<TDataType>>.Add(this);
    }

    public abstract void SetBufferData(in TDataType[] data, BufferUsageHint usageHint);
    public abstract void Create();
    public abstract void Dispose();
    public abstract void Activate();
    public abstract void Deactivate();
}