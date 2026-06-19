using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// A thin bump-allocator over a single ID3D12DescriptorHeap. DX12 binds resources to shaders through
// descriptors living in heaps, not by name/unit like GL. Two uses here:
//   - a CPU-only (non-shader-visible) SRV heap where every texture gets ONE persistent descriptor
//     (its "home"); cheap, unbounded-ish, never bound directly.
//   - a shader-VISIBLE ring heap the renderer copies material descriptors INTO each draw, then points
//     a root descriptor table at the copied run (the classic "copy descriptors per draw" model, before
//     the Phase 4 SM6.6 bindless ResourceDescriptorHeap rewrite).
//
// Bump allocation only (Allocate advances a cursor); the persistent heap never frees in Phase 2d (texture
// lifetime ~= process), and the ring heap wraps via Reset() each frame.
public sealed class Dx12DescriptorHeap : IDisposable {
    public ID3D12DescriptorHeap Heap { get; }
    readonly uint increment;   // Vortice: GetDescriptorHandleIncrementSize returns uint
    readonly int capacity;     // PER-FRAME-SLOT logical capacity (the physical heap is capacity*framesInFlight)
    int cursor;                // SLOT-LOCAL bump cursor (0..capacity); Cpu/Gpu rebase it by the frame slot
    readonly CpuDescriptorHandle cpuStart;
    readonly GpuDescriptorHandle gpuStart;   // only valid when shader-visible
    public bool ShaderVisible { get; }

    // P0b — N-BUFFERING. A per-frame shader-visible heap the CPU re-fills every frame would, once the CPU runs
    // ahead of the GPU, overwrite the descriptors frame N's draws are still reading. Passing framesInFlight=N
    // (= dev.FramesInFlight) sizes the physical heap N× and offsets EVERY handle by `dev.FrameSlot * capacity`,
    // so frame N and frame N+1 occupy DISJOINT descriptor ranges. The offset lives in the Cpu()/Gpu() ACCESSORS
    // (not in Reset) so it applies uniformly to BOTH usage styles with zero call-site changes: cursor-based
    // heaps (Reset → AllocateRange → Cpu(returnedIndex)) AND fixed-index heaps (Cpu(0), Cpu(1), … never Reset).
    // framesInFlight=1 (default) → base is always 0 → byte-identical to the pre-P0b single-slab heap. Persistent
    // heaps (the SrvStore "home" heap, bindless, editor UI), RT/GI/HiZ/OIDN heaps, and the IBL-baker heap stay
    // at 1: they're either process-lifetime, written under a synchronous flush, or off-limits (Lumen).
    readonly Dx12Device dev;
    readonly int framesInFlight;
    // Slot offset in descriptors. MUST rebase by THIS heap's own framesInFlight, not the global FrameSlot:
    // under BALLISTIC_DX12_OVERLAP=1 the device runs FramesInFlight=2, so dev.FrameSlot toggles 0/1 for EVERY
    // heap — but a heap built with framesInFlight==1 only allocated `capacity*1` physical descriptors, so a raw
    // `FrameSlot*capacity` would rebase past the end of its single slab (OOB descriptor → access violation /
    // silent garbage; the SSGI/OIDN heaps are framesInFlight==1). `FrameSlot % framesInFlight` keeps a 1-slab
    // heap pinned to slab 0 always (correct: its descriptors are created once + used under a synchronous flush)
    // while an N-slab ring heap cycles 0..N-1 exactly as before. Byte-identical when FramesInFlight==1 (FrameSlot
    // stays 0 → Base 0 for all heaps); fixes the OOB only-under-overlap.
    int Base => (dev.FrameSlot % framesInFlight) * capacity;

    public Dx12DescriptorHeap(Dx12Device dev, DescriptorHeapType type, int capacity, bool shaderVisible,
                              int framesInFlight = 1) {
        this.dev = dev;
        this.capacity = capacity;
        this.framesInFlight = Math.Max(1, framesInFlight);
        ShaderVisible = shaderVisible;
        Heap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            type, (uint)(capacity * this.framesInFlight),
            shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None));
        increment = dev.Device.GetDescriptorHandleIncrementSize(type);
        cpuStart = Heap.GetCPUDescriptorHandleForHeapStart();
        if (shaderVisible)
            gpuStart = Heap.GetGPUDescriptorHandleForHeapStart();
    }

    // Reserve one slot; returns its index. Throws when full (a real cap, not a silent wrap) for the
    // persistent heap. The ring heap calls Reset() per frame so it never overflows under normal load.
    // Locked: the persistent SRV store is allocated from texture uploads on JobSystem worker threads.
    readonly object gate = new();
    // Freed slots for reuse (Free pushes, Allocate pops first). Lets the editor UI heap recycle thumbnail
    // descriptors across asset-browser invalidations without leaking the bump cursor toward the cap.
    readonly Stack<int> freeList = new();
    public int Allocate() {
        lock (gate) {
            if (freeList.Count > 0)
                return freeList.Pop();
            if (cursor >= capacity)
                throw new InvalidOperationException(
                    $"Descriptor heap full ({capacity}). Grow the heap or reset it per frame.");
            return cursor++;
        }
    }

    // Return a slot to the free list for reuse (the descriptor itself is overwritten on the next Allocate
    // that reuses the slot via CopyDescriptorsSimple). Only valid for heaps allocated via Allocate().
    public void Free(int index) {
        lock (gate) freeList.Push(index);
    }

    // Reserve `count` CONTIGUOUS slots (for a material's descriptor table); returns the first index.
    public int AllocateRange(int count) {
        lock (gate) {
            if (cursor + count > capacity)
                cursor = 0;   // ring wrap (shader-visible per-draw heap)
            int start = cursor;
            cursor += count;
            return start;
        }
    }

    public void Reset() => cursor = 0;   // slot-LOCAL rewind; Cpu/Gpu add the per-frame Base offset

    // Vortice handle-offset ctor: (baseHandle, int offsetInDescriptors, uint descriptorIncrementSize).
    // `index` is SLOT-LOCAL (0..capacity); Base rebases it into this frame's slot (0 when framesInFlight==1).
    // The bounds assert turns a heap-misuse (slot offset past the physical heap — the class of bug that
    // produced an intermittent 0xC0000005 in SetDescriptorHeaps/root-table binding) into a DETERMINISTIC,
    // localized throw at the call site instead of a silent dangling GPU descriptor handle. Physical size is
    // capacity*framesInFlight (ctor); the offset MUST land inside it.
    public CpuDescriptorHandle Cpu(int index) {
        CheckBounds(index);
        return new(cpuStart, Base + index, increment);
    }

    public GpuDescriptorHandle Gpu(int index) {
        CheckBounds(index);
        return new(gpuStart, Base + index, increment);
    }

    // P0b: a CPU handle at an ABSOLUTE physical descriptor index (NOT slot-local, NOT FrameSlot-rebased).
    // For one-time setup that must populate EVERY frame slab's copy of a fixed descriptor (e.g. Hi-Z's pyramid
    // SRV/UAVs, written once at init but read from whichever slab the current FrameSlot selects). Bounds-checked
    // against the physical heap size. Normal per-frame access uses Cpu()/Gpu() (FrameSlot-rebased).
    public CpuDescriptorHandle CpuPhysical(int physicalIndex) {
        if ((uint)physicalIndex >= (uint)(capacity * framesInFlight))
            throw new InvalidOperationException(
                $"CpuPhysical out of bounds: {physicalIndex} >= physical size {capacity * framesInFlight}.");
        return new(cpuStart, physicalIndex, increment);
    }

    void CheckBounds(int index) {
        int offset = Base + index;
        if ((uint)offset >= (uint)(capacity * framesInFlight))
            throw new InvalidOperationException(
                $"Descriptor heap access out of bounds: offset {offset} (FrameSlot {dev.FrameSlot}, slot-local {index}) " +
                $">= physical size {capacity * framesInFlight} (capacity {capacity} x framesInFlight {framesInFlight}).");
    }

    public void Dispose() => Heap.Dispose();
}
