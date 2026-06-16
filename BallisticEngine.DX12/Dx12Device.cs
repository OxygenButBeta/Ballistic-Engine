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
    // The 8-byte LUID of the adapter the device was created on (DXGI AdapterDesc1.AdapterLuid as raw bytes).
    // Lets OIDN create a HIP device on the SAME physical GPU (oidnNewDeviceByLUID) for zero-copy buffer sharing.
    public byte[] AdapterLuidBytes { get; }

    // Hardware ray-tracing (DXR Tier 1.0+) support, queried ONCE at device creation. The renderer reads this
    // eagerly to AUTO-DOWNGRADE RayTraced GI/reflections/shadows to their screen-space fallbacks on a no-RT GPU
    // (the audience floor — GTX-1660-class cards often lack RT). A FORCED RT path on a non-DXR device is exactly
    // the device-removal/PC-crash hazard the downgrade prevents ([[gpu-hang-launch-safety]]). The old per-effect
    // lazy CheckFeatureSupport checks now read this flag. BALLISTIC_DX12_FORCE_NORT=1 reports false even on an
    // RT-capable GPU — the dev-machine A/B door for the no-RT fallback (the dev card has RT, so it won't crash).
    public bool HasHardwareRayTracing { get; }

    readonly ID3D12CommandAllocator allocator;
    readonly ID3D12GraphicsCommandList4 commandList; // 4 = supports DXR DispatchRays later
    readonly ID3D12Fence fence;
    ulong fenceValue;
    readonly System.Threading.AutoResetEvent fenceEvent = new(false);

    // P0a — PIPELINED FRAME: a SEPARATE allocator + command list the per-frame render path records the WHOLE
    // frame into, submitted ONCE at EndFrame (vs the legacy ~40 ExecuteSync→WaitForGpu full GPU flushes, one
    // per pass + per transition). Dedicated objects (not the shared `commandList`) so a concurrent worker-thread
    // ExecuteSync (e.g. a cubemap upload) using the shared list can never corrupt the open frame list and vice
    // versa — both submit to the one Queue, which serializes execution. P0a keeps a single WaitForGpu at
    // EndFrame (no overlap yet); P0b N-buffers these for CPU↔GPU overlap. While a frame is open, ExecuteSync
    // calls ON THE FRAME-OWNING THREAD record into frameList instead of submitting; off-thread/no-frame calls
    // take the legacy synchronous path. Disabled by BALLISTIC_DX12_PIPELINED=0 (legacy per-call submit+wait).
    readonly ID3D12CommandAllocator frameAllocator;
    readonly ID3D12GraphicsCommandList4 frameList;
    bool frameOpen;                 // a frame is recording into frameList (set by BeginFrame, cleared by EndFrame)
    int frameThreadId;              // only this thread's ExecuteSync calls redirect into frameList
    readonly bool pipelinedFrames;  // BALLISTIC_DX12_PIPELINED != "0" (default ON)

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

        // DRED (Device Removed Extended Data) — diagnoses GPU HANGS/device-removals WITHOUT the debug-layer
        // SDK (which isn't installed here): on a removal, DrainDredReport() reports the GPU PAGE-FAULT VA
        // (non-zero = the GPU accessed freed/invalid memory — use-after-free or a bad descriptor). Auto-
        // breadcrumbs have a per-command GPU cost, so it's OFF unless BALLISTIC_DX12_DRED=1. Must precede
        // device creation. See [[gpu-hang-launch-safety]] (the DX12 thumbnail hang this is for).
        dredEnabled = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DRED") == "1";
        if (dredEnabled && D3D12GetDebugInterface(out ID3D12DeviceRemovedExtendedDataSettings dred).Success) {
            dred.SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
            dred.SetPageFaultEnablement(DredEnablement.ForcedOn);
            dred.Dispose();
        }

        using IDXGIFactory4 factory = CreateDXGIFactory1<IDXGIFactory4>();
        IDXGIAdapter1 adapter = PickHardwareAdapter(factory);
        Device = D3D12CreateDevice<ID3D12Device2>(adapter, FeatureLevel.Level_12_0);
        adapter.Dispose();
        // The adapter LUID as raw 8 bytes (little-endian) for OIDN HIP device matching (oidnNewDeviceByLUID).
        // ID3D12Device.AdapterLuid is the 64-bit LUID; its byte layout IS the native LUID struct.
        AdapterLuidBytes = BitConverter.GetBytes(Device.AdapterLuid);

        // Query DXR support ONCE (Options5.RaytracingTier >= Tier1_0). FORCE_NORT pins it false for the no-RT
        // fallback A/B on the (RT-capable) dev card. Wrapped — CheckFeatureSupport can throw on old runtimes.
        bool rt = false;
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_FORCE_NORT") != "1") {
            try {
                var opt5 = Device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5);
                rt = opt5.RaytracingTier >= RaytracingTier.Tier1_0;
            } catch { rt = false; }
        }
        HasHardwareRayTracing = rt;
        Console.WriteLine(rt ? "[DX12] Hardware ray tracing: AVAILABLE (DXR Tier 1.0+)"
                             : "[DX12] Hardware ray tracing: NOT available — RayTraced GI/reflections/shadows will use screen-space fallbacks.");

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

        // P0a frame list (see field comment). Created OPEN like the others → close so the first BeginFrame
        // Reset is uniform. Submitted on the shared render `fence` (one queue, one wait at EndFrame).
        frameAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        frameList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, frameAllocator, null);
        frameList.Close();
        pipelinedFrames = Environment.GetEnvironmentVariable("BALLISTIC_DX12_PIPELINED") != "0";

        // Debug info queue (if the debug layer loaded): lets us print the REAL D3D12 message text on an
        // E_FAIL instead of the opaque HRESULT. Stored-message log only; no break-on-error.
        if (enableDebugLayer)
            infoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
    }

    readonly bool dredEnabled;
    // On a device-removal, report the GPU page-fault from DRED: a non-zero PageFaultVA means a GPU command
    // dereferenced freed/invalid memory (use-after-free / bad descriptor) — the decisive clue for a hang.
    // Best-effort + fully guarded so the crash handler never throws over the real exception.
    public string DrainDredReport() {
        if (!dredEnabled) return "(DRED off — run with BALLISTIC_DX12_DRED=1)";
        try {
            using var dred = Device.QueryInterfaceOrNull<ID3D12DeviceRemovedExtendedData>();
            if (dred is null) return "(DRED unavailable on this device/OS)";
            dred.GetPageFaultAllocationOutput(out DredPageFaultOutput pf);
            return $"PageFaultVA=0x{pf.PageFaultVA:X} " +
                   "(VA!=0 => GPU touched freed/invalid memory: use-after-free or bad descriptor; " +
                   "auto-breadcrumbs are in the Watson dump / debugger)";
        }
        catch (Exception e) { return "DRED read failed: " + e.Message; }
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
        // P0a: while a pipelined frame is open ON THIS (the frame-owning) thread, just record into the open
        // frame list — no submit, no wait. The frame submits once at EndFrame. Off-thread callers (worker
        // uploads) and the no-frame case fall through to the legacy synchronous submit+wait below. No lock
        // needed for the frame-list append: frameList is touched only by the frame thread, single-threaded.
        if (frameOpen && Environment.CurrentManagedThreadId == frameThreadId) {
            record(frameList);
            return;
        }
        lock (submitGate) {
            allocator.Reset();
            commandList.Reset(allocator, null);
            record(commandList);
            commandList.Close();
            Queue.ExecuteCommandList(commandList);
            WaitForGpu();
        }
    }

    // P0a — open the per-frame command list. Subsequent ExecuteSync calls on THIS thread record into it
    // instead of submitting. No-op (legacy per-pass submit) when BALLISTIC_DX12_PIPELINED=0. The GPU is idle
    // here (EndFrame waited last frame; first frame the lists are freshly closed), so resetting the allocator
    // is safe. Must be paired with EndFrame; nesting is not supported (one frame in flight in P0a).
    public void BeginFrame() {
        if (!pipelinedFrames || frameOpen) return;
        frameAllocator.Reset();
        frameList.Reset(frameAllocator, null);
        frameThreadId = Environment.CurrentManagedThreadId;
        frameOpen = true;
    }

    // P0a — close, submit ONCE, and wait for the whole recorded frame. (P0b will drop the wait for CPU↔GPU
    // overlap via N-buffered allocators/fences.) Safe to call when no frame is open (no-op). Returns true if
    // a frame was actually submitted (so the caller knows the legacy per-pass path was bypassed).
    public bool EndFrame() {
        if (!frameOpen) return false;
        frameOpen = false;
        frameList.Close();
        Queue.ExecuteCommandList(frameList);
        WaitForGpu();
        return true;
    }

    // P0a — true while a pipelined frame is recording on the current thread (passes can branch on it if they
    // must do a real GPU round-trip mid-frame; readbacks use ExecuteSyncImmediate which handles this).
    public bool FrameOpen => frameOpen && Environment.CurrentManagedThreadId == frameThreadId;

    // P0a — a synchronous submit+wait that WORKS mid-frame: readbacks (SaveBmp/ReadColorRgb/…) and any pass
    // that must see GPU results immediately call this. If a pipelined frame is open on this thread, the
    // recorded-so-far commands are flushed FIRST (close+submit+wait) so ordering is preserved and the
    // readback observes everything drawn this frame, then `record` runs synchronously, then a FRESH frame
    // segment reopens so the rest of the frame keeps pipelining. Without an open frame it's plain ExecuteSync.
    public void ExecuteSyncImmediate(Action<ID3D12GraphicsCommandList4> record) {
        bool reopen = false;
        if (frameOpen && Environment.CurrentManagedThreadId == frameThreadId) {
            // Flush what the frame has recorded so far so `record`'s copy/readback sees it on the GPU.
            frameOpen = false;
            frameList.Close();
            Queue.ExecuteCommandList(frameList);
            WaitForGpu();
            reopen = true;
        }
        lock (submitGate) {
            allocator.Reset();
            commandList.Reset(allocator, null);
            record(commandList);
            commandList.Close();
            Queue.ExecuteCommandList(commandList);
            WaitForGpu();
        }
        if (reopen) {   // continue recording the rest of the frame into a fresh segment
            frameAllocator.Reset();
            frameList.Reset(frameAllocator, null);
            frameOpen = true;
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

    // Block until the GPU has finished ALL previously-submitted queue work. Public for the swapchain:
    // ResizeBuffers requires every backbuffer reference released AND the GPU idle, and Present in the
    // synchronous editor model waits here so the next frame's backbuffer is safe to reuse. Takes the
    // submit gate so it never races ExecuteSync's fenceValue increment.
    //
    // Waits on BOTH the render fence AND the upload fence: asset uploads run on JobSystem WORKER threads
    // via ExecuteUpload (its own fence) and submit to the SAME queue. A resize/ResizeBuffers that only
    // waited the render fence would proceed while a worker's CopyTextureRegion is still in flight on the
    // GPU — freeing backbuffers/targets under an active GPU read → device removal (the 4K->1080p hang).
    public void Flush() {
        // Hold uploadGate too so no worker ExecuteUpload starts between the two waits, then drain BOTH the
        // render queue and any in-flight worker uploads (they share this queue). The upload drain lives here
        // (not in WaitForGpu) so ExecuteSync/Dispose — which call WaitForGpu under submitGate only — never
        // touch uploadFenceValue unguarded and race a concurrent ExecuteUpload.
        lock (submitGate)
            lock (uploadGate) {
                WaitForGpu();
                ulong uTarget = ++uploadFenceValue;
                Queue.Signal(uploadFence, uTarget);
                if (uploadFence.CompletedValue < uTarget) {
                    uploadFence.SetEventOnCompletion(uTarget, uploadEvent.SafeWaitHandle.DangerousGetHandle());
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
        frameList.Dispose();
        frameAllocator.Dispose();
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
