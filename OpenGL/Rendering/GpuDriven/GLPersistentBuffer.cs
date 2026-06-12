using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.OpenGL.GpuDriven;

// A GL 4.6 persistently-mapped, N-buffered GPU buffer for per-frame streaming writes WITHOUT
// glBufferData/glBufferSubData re-specification stalls. The buffer is allocated ONCE with
// glBufferStorage (immutable storage) and mapped ONCE for the whole lifetime with
// PERSISTENT | COHERENT. Each frame the CPU writes into the NEXT region of an N-region ring;
// a fence per region makes the CPU wait only if it laps the GPU (it rarely does at N=3).
//
// This is the engine's streaming backbone for GPU-driven rendering: the per-frame submesh
// metadata, the compacted draw commands and the per-draw data all stream through these.
// COHERENT means writes are visible to the GPU without an explicit flush; we still issue a
// memory barrier before the draw that consumes the buffer (the draw, not the map, needs it).
public sealed unsafe class GLPersistentBuffer : IDisposable {
    public int Handle { get; private set; }
    public readonly int RegionBytes;
    public readonly int RegionCount;

    readonly byte* basePtr;
    readonly nint totalBytes;
    readonly IntPtr[] fences;     // GLsync per region, 0 when none pending
    int current = -1;            // region index handed out by the last BeginFrame

    public GLPersistentBuffer(int regionBytes, int regionCount = 3) {
        // std430/indirect buffers want 16-byte alignment; round each region up so region i
        // always starts aligned regardless of the requested size.
        RegionBytes = (regionBytes + 15) & ~15;
        RegionCount = Math.Max(1, regionCount);
        totalBytes = (nint)RegionBytes * RegionCount;
        fences = new IntPtr[RegionCount];

        Handle = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, Handle);
        const BufferStorageFlags flags = BufferStorageFlags.MapWriteBit |
                                         BufferStorageFlags.MapPersistentBit |
                                         BufferStorageFlags.MapCoherentBit;
        GL.BufferStorage(BufferTarget.ShaderStorageBuffer, totalBytes, IntPtr.Zero, flags);

        const BufferAccessMask access = BufferAccessMask.MapWriteBit |
                                        BufferAccessMask.MapPersistentBit |
                                        BufferAccessMask.MapCoherentBit;
        IntPtr ptr = GL.MapBufferRange(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, totalBytes, access);
        basePtr = (byte*)ptr;
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    // Advances to the next region and, if the GPU might still be reading it (a fence is
    // pending from N frames ago), blocks until that fence signals. Returns a writable pointer
    // to this frame's region. The region's previous fence is consumed here.
    public byte* BeginFrame() {
        current = (current + 1) % RegionCount;
        IntPtr fence = fences[current];
        if (fence != IntPtr.Zero) {
            // Wait until the GPU is done with whatever last used this region. With N=3 and the
            // GPU at most ~1 frame behind, this almost never actually blocks.
            WaitSyncStatus status = GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit,
                1_000_000_000); // 1s ceiling — a real stall here means something is very wrong
            if (status is WaitSyncStatus.AlreadySignaled or WaitSyncStatus.ConditionSatisfied
                       or WaitSyncStatus.WaitFailed) {
                GL.DeleteSync(fence);
                fences[current] = IntPtr.Zero;
            }
        }
        return basePtr + (nint)current * RegionBytes;
    }

    // Byte offset of the current region into the buffer (for glBindBufferRange).
    public nint CurrentOffset => (nint)current * RegionBytes;

    // Binds this frame's region as an indexed SSBO/indirect binding range.
    public void BindRange(BufferRangeTarget target, int binding, int sizeBytes) {
        GL.BindBufferRange(target, binding, Handle, CurrentOffset, Math.Min(sizeBytes, RegionBytes));
    }

    public void BindWhole(BufferTarget target) {
        GL.BindBuffer(target, Handle);
    }

    // Plants a fence after the draws that consume this frame's region, so a future BeginFrame
    // that laps back to this region waits for the GPU to finish. Call once per frame after the
    // last consuming draw is submitted.
    public void EndFrame() {
        if (current < 0)
            return;
        if (fences[current] != IntPtr.Zero)
            GL.DeleteSync(fences[current]);
        fences[current] = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, 0);
    }

    public void Dispose() {
        if (Handle == 0)
            return;
        foreach (IntPtr f in fences)
            if (f != IntPtr.Zero)
                GL.DeleteSync(f);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, Handle);
        GL.UnmapBuffer(BufferTarget.ShaderStorageBuffer);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        GL.DeleteBuffer(Handle);
        Handle = 0;
    }
}
