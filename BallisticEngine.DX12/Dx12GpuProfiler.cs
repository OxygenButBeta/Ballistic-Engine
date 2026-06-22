using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12GpuProfiler : IDisposable {
    readonly Dx12Device dev;
    readonly bool enabled;
    public bool Enabled => enabled;

    const int RingFrames = 3;
    const int MaxMarks = 64;
    const int SlotsPerFrame = MaxMarks * 2;

    ID3D12QueryHeap heap;
    ID3D12Resource readback;
    ulong frequency;
    bool avail;

    readonly string[] names = new string[MaxMarks];
    int markCount;
    int ringIndex;
    readonly int[] pendingResolveRing = new int[RingFrames];
    readonly int[] pendingResolveCount = new int[RingFrames];
    readonly ulong[] pendingFence = new ulong[RingFrames];
    int pendingHead, pendingTail;

    public Dx12GpuProfiler(Dx12Device dev) {
        this.dev = dev;
        enabled = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPU_PROFILE") == "1";
        if (!enabled) return;
        try {
            dev.Queue.GetTimestampFrequency(out frequency);
            heap = dev.Device.CreateQueryHeap<ID3D12QueryHeap>(
                new QueryHeapDescription(QueryHeapType.Timestamp, RingFrames * SlotsPerFrame));
            readback = dev.Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)(RingFrames * SlotsPerFrame * sizeof(ulong))), ResourceStates.CopyDest);
            avail = frequency > 0;
        } catch { avail = false; }
    }

    public void BeginFrame(int frameCounter) {
        if (!avail) return;
        ringIndex = frameCounter % RingFrames;
        markCount = 0;
    }

    public void Begin(ID3D12GraphicsCommandList4 cl, string name) {
        if (!avail || markCount >= MaxMarks) return;
        names[markCount] = name;
        int slot = ringIndex * SlotsPerFrame + markCount * 2;
        cl.EndQuery(heap, QueryType.Timestamp, (uint)slot);
    }

    public void End(ID3D12GraphicsCommandList4 cl) {
        if (!avail || markCount >= MaxMarks) return;
        int slot = ringIndex * SlotsPerFrame + markCount * 2 + 1;
        cl.EndQuery(heap, QueryType.Timestamp, (uint)slot);
        markCount++;
    }

    public void ResolveInto(ID3D12GraphicsCommandList4 cl, ulong fenceTarget) {
        if (!avail || markCount == 0) return;
        int baseSlot = ringIndex * SlotsPerFrame;
        cl.ResolveQueryData(heap, QueryType.Timestamp, (uint)baseSlot, (uint)(markCount * 2),
            readback, (ulong)(baseSlot * sizeof(ulong)));
        pendingResolveRing[pendingTail] = ringIndex;
        pendingResolveCount[pendingTail] = markCount;
        pendingFence[pendingTail] = fenceTarget;
        pendingTail = (pendingTail + 1) % RingFrames;
        Array.Copy(names, 0, ringNames[ringIndex], 0, markCount);
    }

    readonly string[][] ringNames = BuildRingNames();
    static string[][] BuildRingNames() {
        var a = new string[RingFrames][];
        for (int i = 0; i < RingFrames; i++) a[i] = new string[MaxMarks];
        return a;
    }

    public unsafe string Drain(ulong completedFence) {
        if (!avail || pendingHead == pendingTail) return null;
        if (pendingFence[pendingHead] > completedFence) return null;
        int ring = pendingResolveRing[pendingHead];
        int count = pendingResolveCount[pendingHead];
        pendingHead = (pendingHead + 1) % RingFrames;

        ulong* data = (ulong*)0;
        var sb = new System.Text.StringBuilder("[GpuProf]");
        ulong* p = MapReadback();
        if (p == null) return null;
        int baseSlot = ring * SlotsPerFrame;
        double total = 0;
        for (int i = 0; i < count; i++) {
            ulong begin = p[baseSlot + i * 2], end = p[baseSlot + i * 2 + 1];
            double ms = end > begin ? (end - begin) * 1000.0 / frequency : 0.0;
            const double FlushGuardMs = 6.0;
            if (ms > FlushGuardMs) {
                sb.Append($" {ringNames[ring][i]}=~flush?");
            } else {
                total += ms;
                sb.Append($" {ringNames[ring][i]}={ms:0.000}");
            }
        }
        UnmapReadback();
        sb.Append($" | sum(valid)={total:0.000}ms");
        _ = data;
        return sb.ToString();
    }

    unsafe ulong* MapReadback() {
        try { return readback.Map<ulong>(0); } catch { return null; }
    }
    void UnmapReadback() { try { readback.Unmap(0); } catch { } }

    public void Dispose() {
        heap?.Dispose();
        readback?.Dispose();
    }
}
