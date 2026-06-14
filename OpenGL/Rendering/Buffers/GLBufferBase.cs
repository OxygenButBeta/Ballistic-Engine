using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

public abstract class GLBufferBase<TData>(RenderContext renderContext) : GPUBuffer<TData>(renderContext)
    where TData : struct
{
    protected override int UID { get; set; }
    // GL-internal target (no longer on the abstraction — it's a GL implementation detail).
    protected virtual BufferTarget Target => BufferTarget.ArrayBuffer;

    public override void SetBufferData(in TData[] data, BufferUsage usage)
    {
        Activate();
        GL.BufferData(Target, data.Length * Unsafe.SizeOf<TData>(), data, GLBuffers.Hint(usage));
    }

    public override void Dispose()
    {
        if (UID == 0)
            return;

        GL.DeleteBuffer(UID);
    }

    public override void Activate()
    {
        RenderContext.Activate();
        GL.BindBuffer(Target, UID);
    }

    public override void Deactivate()
    {
        GL.BindBuffer(Target, 0);
    }
}