using System;
using System.Collections.Generic;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// PIPELINED per-pass GPU timing (BALLISTIC_DX12_GPU_PROFILE=1). Unlike the legacy GpuTimerBegin/End (which
// submits + WaitForGpu around every marker → serialises the whole frame, inflating every reading), this writes
// timestamp queries INLINE into the open frame command list — zero extra submits, the frame stays fully
// pipelined. ResolveQueryData is appended at EndFrame; the resolved ticks are read back N frames later (when the
// GPU has surely passed them), so a reading is a few frames stale but otherwise the REAL GPU duration of each
// pass as it runs in the shipping pipelined frame. This is the profiler the inline passes (geometry, shadows,
// deferred, sky, IBL, aerial) lacked — the graph post-passes had TimePass, but those are recorded outside it.
//
// Usage: Begin(cl, "Name") ... End(cl) bracket a pass's commands in the frame list. Marks nest by pairing in
// order (a flat stack per frame). At EndFrame the renderer calls ResolveInto(cl); the device readback +
// Report() print the per-pass ms once the frame retires.
public sealed class Dx12GpuProfiler : IDisposable {
    readonly Dx12Device dev;
    readonly bool enabled;
    public bool Enabled => enabled;

    // Ring of N frames' worth of query data so a readback never reads a slot the GPU might still be writing.
    const int RingFrames = 3;
    const int MaxMarks = 64;             // up to 64 begin/end PAIRS per frame
    const int SlotsPerFrame = MaxMarks * 2;

    ID3D12QueryHeap heap;                // [RingFrames * SlotsPerFrame] timestamp queries
    ID3D12Resource readback;            // RingFrames * SlotsPerFrame ulong, CPU-readable
    ulong frequency;                     // ticks/sec on the queue
    bool avail;

    // Per-frame recording state (the frame currently being recorded into the open frame list).
    readonly string[] names = new string[MaxMarks];
    int markCount;                       // begin/end pairs recorded this frame
    int ringIndex;                       // which ring frame this recording uses
    readonly int[] pendingResolveRing = new int[RingFrames];   // ring index pending readback, by retire order
    readonly int[] pendingResolveCount = new int[RingFrames];  // mark count for each pending ring frame
    readonly ulong[] pendingFence = new ulong[RingFrames];     // frameFence target that retires each pending frame
    int pendingHead, pendingTail;        // queue of frames awaiting readback

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

    // Called at BeginFrame: pick this frame's ring slot and reset the per-frame mark list.
    public void BeginFrame(int frameCounter) {
        if (!avail) return;
        ringIndex = frameCounter % RingFrames;
        markCount = 0;
    }

    // Bracket a pass. Begin writes the start timestamp, End the stop — both into the OPEN frame list, no submit.
    public void Begin(ID3D12GraphicsCommandList4 cl, string name) {
        if (!avail || markCount >= MaxMarks) return;
        names[markCount] = name;
        int slot = ringIndex * SlotsPerFrame + markCount * 2;
        cl.EndQuery(heap, QueryType.Timestamp, (uint)slot);   // "begin" timestamp
    }

    public void End(ID3D12GraphicsCommandList4 cl) {
        if (!avail || markCount >= MaxMarks) return;
        int slot = ringIndex * SlotsPerFrame + markCount * 2 + 1;
        cl.EndQuery(heap, QueryType.Timestamp, (uint)slot);   // "end" timestamp
        markCount++;
    }

    // Called at EndFrame BEFORE the frame list closes: resolve THIS frame's queries into the readback buffer, and
    // enqueue it for readback once `fenceTarget` (the value EndFrame signals) retires.
    public void ResolveInto(ID3D12GraphicsCommandList4 cl, ulong fenceTarget) {
        if (!avail || markCount == 0) return;
        int baseSlot = ringIndex * SlotsPerFrame;
        cl.ResolveQueryData(heap, QueryType.Timestamp, (uint)baseSlot, (uint)(markCount * 2),
            readback, (ulong)(baseSlot * sizeof(ulong)));
        pendingResolveRing[pendingTail] = ringIndex;
        pendingResolveCount[pendingTail] = markCount;
        pendingFence[pendingTail] = fenceTarget;
        pendingTail = (pendingTail + 1) % RingFrames;
        // Snapshot the names for this ring frame so a later readback labels correctly even after newer frames record.
        Array.Copy(names, 0, ringNames[ringIndex], 0, markCount);
    }

    readonly string[][] ringNames = BuildRingNames();
    static string[][] BuildRingNames() {
        var a = new string[RingFrames][];
        for (int i = 0; i < RingFrames; i++) a[i] = new string[MaxMarks];
        return a;
    }

    // Called once per frame (e.g. after EndFrame): read back any pending frame whose fence has retired and print
    // its per-pass ms. Drains at most one per call (steady cadence). Returns the report line, or null.
    public unsafe string Drain(ulong completedFence) {
        if (!avail || pendingHead == pendingTail) return null;
        if (pendingFence[pendingHead] > completedFence) return null;   // GPU hasn't finished this frame yet
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
            total += ms;
            sb.Append($" {ringNames[ring][i]}={ms:0.000}");
        }
        UnmapReadback();
        sb.Append($" | sum={total:0.000}ms");
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
