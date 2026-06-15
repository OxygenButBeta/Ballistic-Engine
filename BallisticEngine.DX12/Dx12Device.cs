using System;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace BallisticEngine.DX12;

// Core DX12 device + a single graphics command queue/allocator/list + a fence for synchronous
// submit-and-wait. Offscreen-first (no swapchain yet): the screenshot harness renders to an RTV and
// reads it back, which needs no window — the highest-value, lowest-risk first deliverable per
// DX12Migration.md Phase 1. A windowed swapchain layers on top later.
//
// Verbose by nature (this is DX12); kept minimal and heavily commented. All COM objects are owned here
// and released in Dispose.
public sealed class Dx12Device : IDisposable {
    public ID3D12Device2 Device { get; }
    public ID3D12CommandQueue Queue { get; }

    readonly ID3D12CommandAllocator allocator;
    readonly ID3D12GraphicsCommandList4 commandList; // 4 = supports DXR DispatchRays later
    readonly ID3D12Fence fence;
    ulong fenceValue;
    readonly System.Threading.AutoResetEvent fenceEvent = new(false);

    // SEPARATE upload allocator/list/fence so asset uploads (textures, buffers) never share command-list
    // state with the per-frame render path. Sharing one list between BeginRender's ExecuteSync and the
    // interleaved asset uploads was the suspected cause of the texture CopyTextureRegion E_FAILs.
    readonly ID3D12CommandAllocator uploadAllocator;
    readonly ID3D12GraphicsCommandList4 uploadList;
    readonly ID3D12Fence uploadFence;
    ulong uploadFenceValue;
    readonly System.Threading.AutoResetEvent uploadEvent = new(false);

    public Dx12Device(bool enableDebugLayer = true) {
        // Debug layer first (must precede device creation) — catches the silent device-removals that
        // are the classic DX12 crash. On by default in Debug; harmless if the SDK layer is absent.
        if (enableDebugLayer && D3D12GetDebugInterface(out ID3D12Debug debug).Success) {
            debug.EnableDebugLayer();
            debug.Dispose();
        }
        // (D3D12GetDebugInterface<T>(out T) is the Result overload — checked above.)

        using IDXGIFactory4 factory = CreateDXGIFactory1<IDXGIFactory4>();
        IDXGIAdapter1 adapter = PickHardwareAdapter(factory);
        Device = D3D12CreateDevice<ID3D12Device2>(adapter, FeatureLevel.Level_12_0);
        adapter.Dispose();

        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        allocator = Device.CreateCommandAllocator(CommandListType.Direct);
        // Generic CreateCommandList<T>; List4 supports DXR DispatchRays in the later phases.
        commandList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, allocator, null);
        commandList.Close(); // lists are created OPEN; close so the first Reset is uniform.
        fence = Device.CreateFence(0, FenceFlags.None);

        uploadAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        uploadList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, uploadAllocator, null);
        uploadList.Close();
        uploadFence = Device.CreateFence(0, FenceFlags.None);

        // Debug info queue (if the debug layer loaded): lets us print the REAL D3D12 message text on an
        // E_FAIL instead of the opaque HRESULT. Stored-message log only; no break-on-error.
        if (enableDebugLayer)
            infoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
    }

    ID3D12InfoQueue infoQueue;
    // Drain and return the stored D3D12 debug messages (newest batch). Empty when the debug layer is off.
    public string DrainDebugMessages() {
        if (infoQueue is null) return "(no info queue — run with BALLISTIC_DX12_DEBUG=1)";
        ulong n = infoQueue.NumStoredMessages;
        if (n == 0) return "(no stored messages)";
        var sb = new System.Text.StringBuilder();
        for (ulong i = 0; i < n; i++) {
            Message m = infoQueue.GetMessage(i);
            sb.Append(m.Severity).Append(": ").Append(m.Description).Append('\n');
        }
        infoQueue.ClearStoredMessages();
        return sb.ToString();
    }

    static IDXGIAdapter1 PickHardwareAdapter(IDXGIFactory4 factory) {
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++) {
            if ((adapter.Description1.Flags & AdapterFlags.Software) == 0 &&
                D3D12CreateDevice(adapter, FeatureLevel.Level_12_0, out ID3D12Device _).Success)
                return adapter;
            adapter.Dispose();
        }
        throw new InvalidOperationException("No DX12-capable hardware adapter found.");
    }

    // Record into a fresh command list, then submit and BLOCK until the GPU finishes. Synchronous is
    // exactly right for the offscreen screenshot path (deterministic, simple); the real per-frame loop
    // will pipeline with multiple allocators + fences later.
    //
    // THREAD-SAFE via a lock: asset loading (textures, mesh buffers) runs on JobSystem WORKER THREADS,
    // so multiple threads call ExecuteSync concurrently. There is ONE shared allocator/command list/fence,
    // and command-list recording is NOT thread-safe in D3D12 — concurrent Reset/record/Close corrupts the
    // list and the GPU rejects it (E_FAIL / device removal). The lock serializes the whole submit-and-wait;
    // since each call already blocks on the GPU, this just queues concurrent uploads (correct, not the
    // fastest — the per-frame render path is single-threaded on the main thread and pays nothing extra).
    readonly object submitGate = new();
    public void ExecuteSync(Action<ID3D12GraphicsCommandList4> record) {
        lock (submitGate) {
            allocator.Reset();
            commandList.Reset(allocator, null);
            record(commandList);
            commandList.Close();
            Queue.ExecuteCommandList(commandList);
            WaitForGpu();
        }
    }

    // Run `body` holding the SAME gate ExecuteSync uses, so an entire create+map+copy upload sequence is
    // atomic w.r.t. other uploads. Asset loading creates resources (textures, buffers) on worker threads;
    // serializing the whole sequence (not just the command submit) avoids the concurrent-create E_FAILs the
    // driver throws under heavy parallel CreateCommittedResource load. C# `lock` is reentrant per-thread,
    // so `body` may freely call ExecuteUpload (it re-acquires the same gate on the same thread).
    public void RunExclusive(Action body) {
        lock (uploadGate) body();
    }

    // Record + submit + wait on the dedicated UPLOAD command list (separate from the render path's list,
    // so an asset upload interleaved with BeginRender never corrupts the render command list and vice
    // versa). Used by all texture/buffer uploads. Serialized by uploadGate.
    readonly object uploadGate = new();
    public void ExecuteUpload(Action<ID3D12GraphicsCommandList4> record) {
        lock (uploadGate) {
            uploadAllocator.Reset();
            uploadList.Reset(uploadAllocator, null);
            record(uploadList);
            uploadList.Close();
            Queue.ExecuteCommandList(uploadList);
            ulong target = ++uploadFenceValue;
            Queue.Signal(uploadFence, target);
            if (uploadFence.CompletedValue < target) {
                uploadFence.SetEventOnCompletion(target, uploadEvent.SafeWaitHandle.DangerousGetHandle());
                uploadEvent.WaitOne();
            }
        }
    }

    void WaitForGpu() {
        ulong target = ++fenceValue;
        Queue.Signal(fence, target);
        if (fence.CompletedValue < target) {
            fence.SetEventOnCompletion(target, fenceEvent.SafeWaitHandle.DangerousGetHandle());
            fenceEvent.WaitOne();
        }
    }

    // Create a DEFAULT-heap buffer of `byteSize` seeded with `data`, via a temporary upload heap +
    // CopyBufferRegion (the GPU-local path the real renderer wants — vs the cube test's upload-heap
    // shortcut). Synchronous: blocks until the copy completes, then the upload heap is freed. The
    // returned resource is left in `finalState` (e.g. VertexAndConstantBuffer / IndexBuffer).
    public unsafe ID3D12Resource CreateDefaultBuffer<T>(ReadOnlySpan<T> data, ResourceStates finalState)
        where T : unmanaged {
        // Serialize the whole create+map+copy (mesh buffers also upload from worker threads). The gate is
        // reentrant so the inner ExecuteSync re-acquires it on the same thread.
        ID3D12Resource result = null;
        int byteSize = data.Length * sizeof(T);
        lock (uploadGate) {
        ID3D12Resource dest = Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)byteSize), ResourceStates.Common);

        using ID3D12Resource upload = Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)byteSize), ResourceStates.GenericRead);

        Span<T> mapped = upload.Map<T>(0, data.Length);
        data.CopyTo(mapped);
        upload.Unmap(0);

        ExecuteUpload(cl => {
            cl.ResourceBarrierTransition(dest, ResourceStates.Common, ResourceStates.CopyDest);
            cl.CopyBufferRegion(dest, 0, upload, 0, (ulong)byteSize);
            cl.ResourceBarrierTransition(dest, ResourceStates.CopyDest, finalState);
        });
        result = dest;
        }   // submitGate
        return result;
    }

    // Create a DEFAULT-heap buffer with UNORDERED-ACCESS allowed (the GPU-driven cull writes its indirect
    // draw commands + atomic counter into these), seeded with `data`, left in `finalState`
    // (UnorderedAccess for compute writes, or IndirectArgument when fed straight to ExecuteIndirect).
    // Same upload-heap copy path as CreateDefaultBuffer; the only difference is ResourceFlags + that the
    // resource can later be transitioned to UnorderedAccess / IndirectArgument.
    public unsafe ID3D12Resource CreateUavBuffer<T>(ReadOnlySpan<T> data, ResourceStates finalState)
        where T : unmanaged {
        ID3D12Resource result = null;
        int byteSize = data.Length * sizeof(T);
        lock (uploadGate) {
            ID3D12Resource dest = Device.CreateCommittedResource(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)byteSize, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.Common);

            using ID3D12Resource upload = Device.CreateCommittedResource(
                HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)byteSize), ResourceStates.GenericRead);
            Span<T> mapped = upload.Map<T>(0, data.Length);
            data.CopyTo(mapped);
            upload.Unmap(0);

            ExecuteUpload(cl => {
                cl.ResourceBarrierTransition(dest, ResourceStates.Common, ResourceStates.CopyDest);
                cl.CopyBufferRegion(dest, 0, upload, 0, (ulong)byteSize);
                cl.ResourceBarrierTransition(dest, ResourceStates.CopyDest, finalState);
            });
            result = dest;
        }
        return result;
    }

    // A READBACK-heap buffer (CPU-mappable, always CopyDest) for reading GPU results back — used by the
    // compute self-test and any GPU->CPU buffer readback.
    public ID3D12Resource CreateReadbackBuffer(int byteSize) =>
        Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)byteSize), ResourceStates.CopyDest);

    public void Dispose() {
        WaitForGpu();
        fenceEvent.Dispose();
        fence.Dispose();
        commandList.Dispose();
        allocator.Dispose();
        uploadEvent.Dispose();
        uploadFence.Dispose();
        uploadList.Dispose();
        uploadAllocator.Dispose();
        infoQueue?.Dispose();
        Queue.Dispose();
        Device.Dispose();
    }
}
