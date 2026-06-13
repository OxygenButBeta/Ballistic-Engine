using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GpuDriven;

// Owns the GPU-driven culling + MultiDrawIndirect path for ONE whole-mesh renderer (Bistro's
// ~1600-submesh mesh). The CPU builds per-submesh metadata once per move; the GPU culls every
// frame in a compute pass and the result draws in a single glMultiDrawElementsIndirectCount.
//
// Layout decisions verified empirically (see DESIGN.md): OpenTK Matrix4 raw-blits into std430 and
// reads back as `model * vec4(pos)` identically to the uniform path — so the GPU-driven vertex
// position is bit-identical to the legacy path (z-prepass invariance holds).
//
// Buffers:
//   binding 2: SubmeshMeta[]  — persistent triple-buffered, rebuilt only when the renderer moves
//   binding 3: DrawCommand[]  — per-frame output of the cull compute
//   binding 4: DrawCount      — single uint, zeroed each frame before cull
//   binding 5: PerDrawData[]  — per-emitted-draw model+materialId, indexed by gl_DrawID
//   binding 6: GpuMaterial[]  — bindless material table (built once, rebuilt on material change)
//   binding 7: CullParams UBO — frustum planes + counts
public sealed class GpuDrivenRenderer : IDisposable {
    public const int MetaBinding = 2;
    public const int CmdBinding = 3;
    public const int CountBinding = 4;
    public const int PerDrawBinding = 5;
    public const int MaterialBinding = 6;
    public const int CullParamsBinding = 7;

    const int RegionCount = 3;

    // Two batches: 0 = SOLID (backface cull on), 1 = CUTOUT (cull off — single-sided foliage).
    // Separate command/count/perdraw buffers per batch so culling batch 1 never races the draw of
    // batch 0 (write-after-read hazard avoided without barriers between draw and the next compute).
    const int BatchCount = 2;
    public const int BatchSolid = 0;
    public const int BatchCutout = 1;

    int cullProgram;
    // Plain SSBOs (NOT persistent-mapped). Metadata is re-uploaded only on a rebuild (rare), cull
    // params per cull — GL orders BufferData/BufferSubData before the dispatch that reads them
    // automatically. The persistent triple-buffered variant raced (a missing client-mapped barrier
    // let the compute read stale metadata → whole solid batch culled → flicker).
    int metaBuffer;                   // SubmeshMeta[]
    int metaCapacity;                 // submeshes the metaBuffer is sized for
    int cullParamsBuffer;             // CullParams UBO (std140, 112 bytes)
    readonly byte[] cullParamsCpu = new byte[112];
    readonly int[] cmdBuffer = new int[BatchCount];     // DrawCommand[] per batch
    readonly int[] countBuffer = new int[BatchCount];   // single uint per batch
    readonly int[] perDrawBuffer = new int[BatchCount]; // PerDrawData[] per batch
    int materialBuffer;               // GpuMaterial[]

    int submeshCapacity;
    int currentSubmeshCount;
    bool metaDirty = true;
    Matrix4 lastWorldMatrix = Matrix4.Identity * 0f; // force first build

    readonly GpuMaterialTable materialTable = new();
    public bool Available { get; private set; }

    // Compiles the cull compute program from the embedded source. Call once with a live GL context.
    public void Initialize(string cullComputeSource) {
        cullProgram = CompileCompute(cullComputeSource);
        if (cullProgram == 0)
            return;

        for (var b = 0; b < BatchCount; b++) {
            cmdBuffer[b] = GL.GenBuffer();
            countBuffer[b] = GL.GenBuffer();
            perDrawBuffer[b] = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, countBuffer[b]);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, sizeof(uint), IntPtr.Zero,
                BufferUsageHint.DynamicDraw);
        }
        materialBuffer = GL.GenBuffer();
        metaBuffer = GL.GenBuffer();

        // CullParams UBO: 6*vec4 planes + 4 uints = 96 + 16 = 112 bytes, std140. Plain DynamicDraw.
        cullParamsBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, cullParamsBuffer);
        GL.BufferData(BufferTarget.UniformBuffer, 112, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        Available = true;
    }

    // Ensures GPU buffers are sized for `count` submeshes. Grows by reallocating (rare — only when
    // a new/larger mesh arrives).
    void EnsureCapacity(int count) {
        if (count <= submeshCapacity && metaCapacity >= count)
            return;

        int cap = Math.Max(count, 256);
        submeshCapacity = cap;
        metaCapacity = cap;

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, metaBuffer);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, cap * SubmeshMeta.SizeBytes,
            IntPtr.Zero, BufferUsageHint.DynamicDraw);

        for (var b = 0; b < BatchCount; b++) {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, cmdBuffer[b]);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, cap * DrawElementsIndirectCommand.SizeBytes,
                IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, perDrawBuffer[b]);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, cap * PerDrawData.SizeBytes,
                IntPtr.Zero, BufferUsageHint.DynamicDraw);
        }
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        metaDirty = true;
    }

    // Marks the metadata as needing a rebuild (the renderer moved, or material set changed).
    public void Invalidate() => metaDirty = true;

    SubmeshMeta[] metaScratch = [];

    // Builds the per-submesh metadata array on the CPU and uploads it to the GPU IF dirty.
    // `models[i]` is the per-submesh model matrix; bounds are local AABBs; matIds index the table.
    public void UpdateMetadata(int count, Matrix4 worldMatrix,
        Matrix4[] models, Vector3[] localMin, Vector3[] localMax,
        uint[] firstIndex, uint[] indexCount, uint[] materialId, uint[] flags) {
        EnsureCapacity(count);
        currentSubmeshCount = count;

        // Skip the rebuild when nothing moved and the metadata is already resident.
        if (!metaDirty && worldMatrix == lastWorldMatrix)
            return;
        lastWorldMatrix = worldMatrix;
        metaDirty = false;

        if (metaScratch.Length < count)
            metaScratch = new SubmeshMeta[count];
        for (var i = 0; i < count; i++) {
            metaScratch[i] = new SubmeshMeta {
                Model = models[i],
                LocalAabbMin = new Vector4(localMin[i], 0f),
                LocalAabbMax = new Vector4(localMax[i], 0f),
                FirstIndex = firstIndex[i],
                IndexCount = indexCount[i],
                MaterialId = materialId[i],
                Flags = flags[i],
            };
        }
        // Plain BufferSubData — GL orders it before the cull dispatch that reads metaBuffer.
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, metaBuffer);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
            count * SubmeshMeta.SizeBytes, metaScratch);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    // Runs the cull compute: zero the count, upload frustum planes, dispatch one invocation per
    // submesh. After this the command buffer + count buffer hold the visible draw set.
    // cutoutFilter: 0 = SOLID submeshes only, 1 = CUTOUT only (drawn separately with backface
    // culling off, like the CPU path's single-sided foliage cards).
    public unsafe void Cull(int batch, Vector4[] frustumPlanes, uint pass, uint cutoutFilter) {
        if (!Available || currentSubmeshCount == 0)
            return;

        // Zero this batch's draw count.
        uint zero = 0;
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, countBuffer[batch]);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, sizeof(uint), ref zero);

        // Upload CullParams (std140): 6 vec4 planes, then submeshCount, pass, cutoutFilter, pad.
        fixed (byte* cp = cullParamsCpu) {
            var planeSpan = new Span<Vector4>(cp, 6);
            for (var i = 0; i < 6; i++)
                planeSpan[i] = frustumPlanes[i];
            var tail = (uint*)(cp + 96);
            tail[0] = (uint)currentSubmeshCount;
            tail[1] = pass;
            tail[2] = cutoutFilter;
            tail[3] = 0;
        }
        GL.BindBuffer(BufferTarget.UniformBuffer, cullParamsBuffer);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, 112, cullParamsCpu);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);

        // Bind this batch's buffers and dispatch. (BufferSubData above is ordered before the
        // dispatch by GL, so the compute reads the freshly-written metadata/params/count.)
        GL.BindBufferRange(BufferRangeTarget.ShaderStorageBuffer, MetaBinding, metaBuffer,
            IntPtr.Zero, currentSubmeshCount * SubmeshMeta.SizeBytes);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, CmdBinding, cmdBuffer[batch]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, CountBinding, countBuffer[batch]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, PerDrawBinding, perDrawBuffer[batch]);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, CullParamsBinding, cullParamsBuffer);

        GL.UseProgram(cullProgram);
        int groups = (currentSubmeshCount + 63) / 64;
        GL.DispatchCompute(groups, 1, 1);

        // The MDI draw reads the command buffer (indirect) and per-draw SSBO; the count buffer is
        // read as the parameter buffer. Barrier on all three before the draw consumes them.
        GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit |
                         MemoryBarrierFlags.ShaderStorageBarrierBit |
                         MemoryBarrierFlags.BufferUpdateBarrierBit);
    }

    // Debug: reads back how many draws a batch's cull emitted (blocks — debug only).
    public int DebugReadDrawCount(int batch) {
        if (!Available)
            return -1;
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
        uint count = 0;
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, countBuffer[batch]);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, sizeof(uint), ref count);
        return (int)count;
    }

    // Binds the per-draw SSBO + material table so the vertex/fragment shaders can index them,
    // then issues the single multi-draw for this batch. Caller has the mesh VAO + program bound.
    public void DrawIndirectCount(int batch) {
        if (!Available || currentSubmeshCount == 0)
            return;

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, PerDrawBinding, perDrawBuffer[batch]);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, MaterialBinding, materialBuffer);

        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, cmdBuffer[batch]);
        GL.BindBuffer(BufferTarget.ParameterBuffer, countBuffer[batch]);

        // maxDrawCount = capacity; the GPU reads the real count from the parameter buffer at offset 0.
        GL.Arb.MultiDrawElementsIndirectCount(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            IntPtr.Zero,   // indirect offset into the bound DrawIndirectBuffer
            IntPtr.Zero,   // parameter (count) buffer offset
            submeshCapacity,
            0);            // stride 0 = tightly packed (20 bytes)

        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
        GL.BindBuffer(BufferTarget.ParameterBuffer, 0);
    }

    // Builds/refreshes the bindless material table from the resolved materials. Idempotent: the
    // table caches by material reference and only rebuilds the SSBO when the set changes.
    public void UpdateMaterialTable(IReadOnlyList<Material> materials,
        float globalMetallic, float globalRoughness, float globalNormalStrength) {
        if (!Available)
            return;
        if (materialTable.Build(materials, globalMetallic, globalRoughness, globalNormalStrength,
                out GpuMaterial[] table)) {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, materialBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, table.Length * GpuMaterial.SizeBytes,
                table, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        }
    }

    public int MaterialIndexOf(Material m) => materialTable.IndexOf(m);

    // No-op now that the buffers are plain (GL handles ordering). Kept for the call site.
    public void EndFrame() { }

    static int CompileCompute(string src) {
        int shader = GL.CreateShader(ShaderType.ComputeShader);
        // Sanitize multibyte chars (em-dash in a comment etc.) — GL.ShaderSource truncates UTF-8.
        GL.ShaderSource(shader, GLSLShaderUtilities.ToAscii(src));
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) {
            Console.WriteLine("[GpuDriven] cull compute compile failed:\n" + GL.GetShaderInfoLog(shader));
            GL.DeleteShader(shader);
            return 0;
        }
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, shader);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int lok);
        GL.DeleteShader(shader);
        if (lok == 0) {
            Console.WriteLine("[GpuDriven] cull compute link failed:\n" + GL.GetProgramInfoLog(prog));
            GL.DeleteProgram(prog);
            return 0;
        }
        return prog;
    }

    public void Dispose() {
        if (metaBuffer != 0) GL.DeleteBuffer(metaBuffer);
        if (cullParamsBuffer != 0) GL.DeleteBuffer(cullParamsBuffer);
        for (var b = 0; b < BatchCount; b++) {
            if (cmdBuffer[b] != 0) GL.DeleteBuffer(cmdBuffer[b]);
            if (countBuffer[b] != 0) GL.DeleteBuffer(countBuffer[b]);
            if (perDrawBuffer[b] != 0) GL.DeleteBuffer(perDrawBuffer[b]);
        }
        if (materialBuffer != 0) GL.DeleteBuffer(materialBuffer);
        if (cullProgram != 0) GL.DeleteProgram(cullProgram);
        materialTable.Dispose();
        Available = false;
    }
}
