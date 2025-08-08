using System.Runtime.CompilerServices;
using BallisticEngine;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

public class GLInstancedBuffer(RenderContext renderContext)
    : InstancedBuffer(renderContext) {
    protected override int UID { get; set; }
    protected override BufferTarget Target => BufferTarget.ArrayBuffer;

    public override void SetBufferData(in Matrix4[] data, BufferUsageHint usageHint) {
        
        Activate();
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * Unsafe.SizeOf<Matrix4>(), data,
            BufferUsageHint.StreamDraw);
       // Deactivate();
    }

    public override void Create() {
        RenderContext.Activate();
        UID = GL.GenBuffer();
        GL.BindBuffer(Target, UID);
        GL.BufferData(Target, (IntPtr)(sizeof(float) * 16 * 5), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        const int attribLocation = 3;
        for (var i = 0; i < 4; i++) {
            GL.EnableVertexAttribArray(attribLocation + i);
            GL.VertexAttribPointer(
                attribLocation + i,
                4,
                VertexAttribPointerType.Float,
                false,
                sizeof(float) * 16,
                i * sizeof(float) * 4
            );
            GL.VertexAttribDivisor(attribLocation + i, 1);
        }
        GL.BindBuffer(Target, 0);
    }

    public override void Dispose() {
        GL.DeleteBuffer(UID);
        RuntimeSet<InstancedBuffer>.Remove(this);
    }

    public override void Activate() {
        RenderContext.Activate();
        GL.BindBuffer(Target, UID);
    }

    public override void Deactivate() {
        GL.BindBuffer(Target, 0);
    }
}