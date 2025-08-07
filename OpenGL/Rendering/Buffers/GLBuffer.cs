using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

public abstract class GLBuffer<TData>(RenderContext renderContext) : GPUBuffer<TData>(renderContext)
    where TData : struct
{
    protected override int UID { get; set; }
    protected override BufferTarget Target => BufferTarget.ArrayBuffer;

    public override void SetBufferData(in TData[] data, BufferUsageHint usageHint)
    {
        Select();
        GL.BufferData(Target, data.Length * Unsafe.SizeOf<TData>(), data, usageHint);
    }

    public override void Dispose()
    {
        if (UID == 0)
            return;

        GL.DeleteBuffer(UID);
        RuntimeSet<GPUBuffer<TData>>.Remove(this);
    }

    public override void Select()
    {
        RenderContext.Activate();
        GL.BindBuffer(Target, UID);
    }

    public override void Deselect()
    {
        GL.BindBuffer(Target, 0);
    }
}