using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// P0b step4 — an N-BUFFERED single-value constant buffer. Wraps the repeated pattern across the renderer
// + every converted pass: a 256-aligned UPLOAD-heap CB, written once per frame with `*(T*)mapped = value`
// and bound by `GPUVirtualAddress`. Under frame overlap (Dx12Device.FramesInFlight > 1) the CPU records
// frame N+1 while the GPU still reads frame N's constants, so a SINGLE slot would be stomped mid-flight
// (the cross-frame race that TDR-crashed the PC during P0b bring-up). This sizes the buffer to
// FramesInFlight slots and offsets BOTH the CPU write and the GPU bind by the device's current FrameSlot,
// so frame N writes/binds slot N%N and frame N+1 a DIFFERENT slot — the GPU never reads a slot the CPU is
// rewriting.
//
// BYTE-IDENTICAL when overlap is off: FramesInFlight==1 → one slot, FrameSlot always 0 → Gpu()/Write target
// offset 0, exactly the old single-slot CB. The slot stride is 256-aligned per the D3D12 CBV alignment rule.
//
// Lifetime: the buffer is persistently mapped (UPLOAD heap) for the device's life; Dispose unmaps + releases.
// Not thread-safe — the per-frame render path is single-threaded (the frame-owning thread).
public sealed unsafe class Dx12FrameCb<T> : IDisposable where T : unmanaged {
    readonly Dx12Device dev;
    readonly ID3D12Resource buffer;
    readonly byte* mapped;
    readonly int slotSize;          // 256-aligned sizeof(T)
    readonly ulong gpuBase;

    public Dx12FrameCb(Dx12Device device) {
        dev = device;
        slotSize = (Marshal.SizeOf<T>() + 255) & ~255;
        int total = slotSize * dev.FramesInFlight;   // FramesInFlight==1 (overlap off) → exactly one slot
        buffer = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)total), ResourceStates.GenericRead);
        mapped = buffer.Map<byte>(0);
        gpuBase = buffer.GPUVirtualAddress;
    }

    // Byte offset of the CURRENT frame's slot (0 when overlap is off).
    int Offset => dev.FrameSlot * slotSize;

    // Write this frame's value into the current frame slot. Call AFTER Dx12Device.BeginFrame() so FrameSlot
    // is the slot this frame's GPU work will read (writing before BeginFrame lands it in the previous slot).
    public void Write(in T value) => *(T*)(mapped + Offset) = value;

    // The GPU virtual address of the current frame's slot — pass to SetGraphicsRootConstantBufferView /
    // SetComputeRootConstantBufferView. Valid for the frame whose BeginFrame set the current FrameSlot.
    public ulong Gpu => gpuBase + (ulong)Offset;

    public void Dispose() { buffer.Unmap(0); buffer.Dispose(); }
}
