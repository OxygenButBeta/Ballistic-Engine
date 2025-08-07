using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
namespace BallisticEngine.OpenGL.Experimental;

public abstract class GPUBuffer<TTarget> : IDisposable where TTarget : struct {
    public int UID { get; protected set; }

    protected abstract BufferTarget Target { get; }
    protected VertexArrayObject VertexArrayObject { get; }
    public abstract void CreateBuffer();

    public GPUBuffer(VertexArrayObject vertexArrayObject) {
        VertexArrayObject = vertexArrayObject;
        RuntimeSet<GPUBuffer<TTarget>>.Add(this);
    }

    public void SetData<T>(T[] data, BufferUsageHint usageHint) where T : struct {
        BindBuffer();
        GL.BufferData(Target, data.Length * Unsafe.SizeOf<T>(), data, usageHint);
    }

    public void BindBuffer() {
        GL.BindBuffer(Target, UID);
    }

    public void UnbindBuffer() {
        GL.BindBuffer(Target, 0);
    }

    public void Dispose() {
        if (UID == 0)
            return;

        GL.DeleteBuffer(UID);
        UID = 0;
        RuntimeSet<GPUBuffer<TTarget>>.Remove(this);
    }
}

public abstract class VertexBuffer : GPUBuffer<Vector3> {
    protected VertexBuffer(VertexArrayObject vertexArrayObject) : base(vertexArrayObject) {
    }

    protected override BufferTarget Target => BufferTarget.ArrayBuffer;
    protected virtual int AttributeLocation => 0;

    public override void CreateBuffer() {
        VertexArrayObject.Bind();
        var (Size, Stride) = GetVertexAttributes();
        UID = GL.GenBuffer();
        BindBuffer();
        GL.VertexAttribPointer(AttributeLocation, Size, VertexAttribPointerType.Float, false, stride: Stride,
            IntPtr.Zero);
        GL.EnableVertexAttribArray(AttributeLocation);
    }

    protected abstract (int Size, int Stride) GetVertexAttributes();
}

public class VertexBuffer3D : VertexBuffer {
    const int Size = 3;

    public VertexBuffer3D(VertexArrayObject vertexArrayObject) : base(vertexArrayObject) {
    }

    protected override (int Size, int Stride) GetVertexAttributes() => (Size, sizeof(float) * 3);
}

public class UVBuffer2D : VertexBuffer {
    const int Size = 2;

    public UVBuffer2D(VertexArrayObject vertexArrayObject) : base(vertexArrayObject) {
    }

    protected override int AttributeLocation => 1;

    protected override (int Size, int Stride) GetVertexAttributes() => (Size, sizeof(float) * 2);
}

public class NormalBuffer3D : VertexBuffer {
    const int Size = 3;

    public NormalBuffer3D(VertexArrayObject vertexArrayObject) : base(vertexArrayObject) {
    }

    protected override int AttributeLocation => 2;

    protected override (int Size, int Stride) GetVertexAttributes() => (Size, sizeof(float) * 3);
}

public class IndexBuffer : GPUBuffer<uint> {
    public IndexBuffer(VertexArrayObject vertexArrayObject) : base(vertexArrayObject) {
    }

    protected override BufferTarget Target => BufferTarget.ElementArrayBuffer;

    public override void CreateBuffer() {
        VertexArrayObject.Bind();
        UID = GL.GenBuffer();
    }
}

public class VertexBuffer2D : VertexBuffer {
    const int Size = 2;

    public VertexBuffer2D(VertexArrayObject vertexArrayObject) : base(vertexArrayObject) {
    }

    protected override (int Size, int Stride) GetVertexAttributes() => (Size, sizeof(float) * 2);
}

public sealed class VertexArrayObject : IDisposable {
    public int UID { get; private set; }
    static VertexArrayObject lastBound;

    public VertexArrayObject() {
        UID = GL.GenVertexArray();
        RuntimeSet<VertexArrayObject>.Add(this);
    }

    public void Bind() {
        if (UID == 0)
            throw new InvalidOperationException("VertexArrayObject has not been created.");

        if (lastBound == this)
            return;

        lastBound = this;
        GL.BindVertexArray(UID);
    }

    public void Unbind() {
        if (UID == 0)
            throw new InvalidOperationException("VertexArrayObject has not been created.");
        if (lastBound != this)
            return;
        GL.BindVertexArray(0);
    }

    public void Dispose() {
        if (UID == 0)
            return;


        GL.DeleteVertexArray(UID);
        UID = 0;
        RuntimeSet<VertexArrayObject>.Remove(this);
    }
}