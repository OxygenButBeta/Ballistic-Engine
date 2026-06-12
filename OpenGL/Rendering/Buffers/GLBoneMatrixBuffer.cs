using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL;

// The engine's FIRST shader-storage buffer (GL 4.6). Holds one skinned mesh's bone matrices for a
// single draw, re-uploaded per skinned draw and bound at a fixed binding point (1 — PassData owns
// the UBO binding 0). std430 packs a mat4[] tightly (stride 64), so a straight Matrix4[] blit is the
// correct layout — no per-element padding like a std140 UBO.
//
// One shared instance lives on the renderer; each skinned draw calls Upload(matrices) then the draw
// reads bones[gl_...]. Critically, the prepass and main pass bind the SAME buffer with the SAME
// contents for a given mesh in a frame, so depth stays bit-identical (z-prepass invariance).
public sealed class GLBoneMatrixBuffer {
    public const int BindingPoint = 1;

    int ssbo;
    int capacityBytes;

    // Uploads `count` matrices and binds the buffer at BindingPoint. Grows on demand. A no-op-safe
    // count of 0 still binds (an empty/identity skeleton just skins to identity).
    public void Upload(Matrix4[] matrices, int count) {
        if (ssbo == 0)
            ssbo = GL.GenBuffer();

        int bytes = Math.Max(count, 1) * 64;   // mat4 = 64 bytes, std430 stride
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        if (bytes > capacityBytes) {
            GL.BufferData(BufferTarget.ShaderStorageBuffer, bytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            capacityBytes = bytes;
        }
        if (count > 0)
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * 64,
                MemoryMarshal.AsBytes(matrices.AsSpan(0, count)).ToArray());
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BindingPoint, ssbo);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    public void Dispose() {
        if (ssbo != 0) {
            GL.DeleteBuffer(ssbo);
            ssbo = 0;
            capacityBytes = 0;
        }
    }
}
