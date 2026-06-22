using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12DescriptorHeap : IDisposable {
    public ID3D12DescriptorHeap Heap { get; }
    readonly uint increment;
    readonly int capacity;
    int cursor;
    readonly CpuDescriptorHandle cpuStart;
    readonly GpuDescriptorHandle gpuStart;
    public bool ShaderVisible { get; }

    readonly Dx12Device dev;
    readonly int framesInFlight;

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

    readonly object gate = new();

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

    public void Free(int index) {
        lock (gate) freeList.Push(index);
    }

    public int AllocateRange(int count) {
        lock (gate) {
            if (cursor + count > capacity)
                cursor = 0;
            int start = cursor;
            cursor += count;
            return start;
        }
    }

    public void Reset() => cursor = 0;

    public CpuDescriptorHandle Cpu(int index) {
        CheckBounds(index);
        return new(cpuStart, Base + index, increment);
    }

    public GpuDescriptorHandle Gpu(int index) {
        CheckBounds(index);
        return new(gpuStart, Base + index, increment);
    }

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
