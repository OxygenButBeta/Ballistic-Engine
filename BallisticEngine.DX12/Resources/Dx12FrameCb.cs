using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed unsafe class Dx12FrameCb<T> : IDisposable where T : unmanaged {
    readonly Dx12Device dev;
    readonly ID3D12Resource buffer;
    readonly byte* mapped;
    readonly int slotSize;
    readonly ulong gpuBase;

    public Dx12FrameCb(Dx12Device device) {
        dev = device;
        slotSize = (Marshal.SizeOf<T>() + 255) & ~255;
        int total = slotSize * dev.FramesInFlight;
        buffer = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)total), ResourceStates.GenericRead);
        mapped = buffer.Map<byte>(0);
        gpuBase = buffer.GPUVirtualAddress;
    }

    int Offset => dev.FrameSlot * slotSize;

    public void Write(in T value) => *(T*)(mapped + Offset) = value;

    public ulong Gpu => gpuBase + (ulong)Offset;

    public void Dispose() { buffer.Unmap(0); buffer.Dispose(); }
}
