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
    readonly int capacity;
    int cursor;
    readonly CpuDescriptorHandle cpuStart;
    readonly GpuDescriptorHandle gpuStart;   // only valid when shader-visible
    public bool ShaderVisible { get; }

    public Dx12DescriptorHeap(Dx12Device dev, DescriptorHeapType type, int capacity, bool shaderVisible) {
        this.capacity = capacity;
        ShaderVisible = shaderVisible;
        Heap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            type, (uint)capacity,
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

    public void Reset() => cursor = 0;

    // Vortice handle-offset ctor: (baseHandle, int offsetInDescriptors, uint descriptorIncrementSize).
    public CpuDescriptorHandle Cpu(int index) =>
        new(cpuStart, index, increment);

    public GpuDescriptorHandle Gpu(int index) =>
        new(gpuStart, index, increment);

    public void Dispose() => Heap.Dispose();
}
