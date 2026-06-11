using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine;

// Name-addressed writer for one std140 uniform block shared by many programs. Member offsets
// are QUERIED from the linked program (never hand-computed), so the GLSL declaration is the
// single source of truth and std140 padding rules can't be silently violated. Values are
// written into a CPU mirror and uploaded with one glBufferSubData; the GL buffer stays bound
// at a fixed binding point for every program that declares the block.
sealed class GLUniformBlock {
    readonly string blockName;
    readonly int bindingPoint;
    readonly Dictionary<string, (int Offset, int ArrayStride)> members = new();
    readonly HashSet<int> registeredPrograms = new();
    byte[] cpu = [];
    int ubo;
    bool dirty;

    public GLUniformBlock(string blockName, int bindingPoint) {
        this.blockName = blockName;
        this.bindingPoint = bindingPoint;
    }

    // True once at least one program exposed the block (size known, buffer created).
    public bool Ready => ubo != 0;

    // Inspects `program` for the block: creates/grows the buffer, merges any member offsets
    // not seen yet (a depth-only companion may report fewer ACTIVE members than the full lit
    // program — std140 keeps the layout identical, so merging is safe), and binds the
    // program's block index to our binding point. Returns false if the program has no block.
    public bool RegisterProgram(int program) {
        if (!registeredPrograms.Add(program))
            return true;

        var blockIndex = GL.GetUniformBlockIndex(program, blockName);
        if (blockIndex < 0) {
            registeredPrograms.Remove(program); // let a legacy path handle it
            return false;
        }

        GL.GetActiveUniformBlock(program, blockIndex,
            ActiveUniformBlockParameter.UniformBlockDataSize, out int size);
        if (size > cpu.Length) {
            Array.Resize(ref cpu, size);
            if (ubo != 0)
                GL.DeleteBuffer(ubo);
            ubo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.UniformBuffer, ubo);
            GL.BufferData(BufferTarget.UniformBuffer, size, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.UniformBuffer, 0);
            dirty = true; // full re-upload into the fresh buffer
        }

        GL.GetActiveUniformBlock(program, blockIndex,
            ActiveUniformBlockParameter.UniformBlockActiveUniforms, out int count);
        if (count > 0) {
            var indices = new int[count];
            GL.GetActiveUniformBlock(program, blockIndex,
                ActiveUniformBlockParameter.UniformBlockActiveUniformIndices, indices);
            var offsets = new int[count];
            var strides = new int[count];
            GL.GetActiveUniforms(program, count, indices, ActiveUniformParameter.UniformOffset, offsets);
            GL.GetActiveUniforms(program, count, indices, ActiveUniformParameter.UniformArrayStride, strides);
            for (var i = 0; i < count; i++) {
                var name = GL.GetActiveUniform(program, indices[i], out _, out _);
                var bracket = name.IndexOf('[');
                if (bracket >= 0)
                    name = name[..bracket];
                if (!members.ContainsKey(name))
                    members[name] = (offsets[i], strides[i]);
            }
        }

        GL.UniformBlockBinding(program, blockIndex, bindingPoint);
        return true;
    }

    void Write<T>(string name, int index, in T value) where T : struct {
        if (!members.TryGetValue(name, out (int Offset, int ArrayStride) m))
            return; // member absent from every registered program (optimized out everywhere)
        var offset = m.Offset + index * Math.Max(m.ArrayStride, 0);
        MemoryMarshal.Write(cpu.AsSpan(offset), in value);
        dirty = true;
    }

    public void Set(string name, ref Matrix4 v) => Write(name, 0, in v);
    public void Set(string name, int index, ref Matrix4 v) {
        // Matrix array stride is reported separately from scalar stride; for std140 mat4
        // arrays the element stride equals sizeof(mat4), so indexed writes use 64.
        if (!members.TryGetValue(name, out (int Offset, int ArrayStride) m))
            return;
        var stride = m.ArrayStride > 0 ? m.ArrayStride : 64;
        MemoryMarshal.Write(cpu.AsSpan(m.Offset + index * stride), in v);
        dirty = true;
    }

    public void Set(string name, Vector4 v) => Write(name, 0, in v);
    public void Set(string name, Vector3 v) => Write(name, 0, in v);
    public void Set(string name, Vector2 v) => Write(name, 0, in v);
    public void Set(string name, float v) => Write(name, 0, in v);
    public void Set(string name, int v) => Write(name, 0, in v);
    public void Set(string name, bool v) => Write(name, 0, v ? 1 : 0);
    public void Set(string name, int index, Vector3 v) => Write(name, index, in v);
    public void Set(string name, int index, float v) => Write(name, index, in v);
    public void Set(string name, int index, int v) => Write(name, index, in v);

    // Uploads the CPU mirror if anything changed and (re)binds the buffer to the binding
    // point. Cheap to call once per pass.
    public void UploadAndBind() {
        if (ubo == 0)
            return;
        if (dirty) {
            GL.BindBuffer(BufferTarget.UniformBuffer, ubo);
            GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, cpu.Length, cpu);
            GL.BindBuffer(BufferTarget.UniformBuffer, 0);
            dirty = false;
        }
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, bindingPoint, ubo);
    }
}
