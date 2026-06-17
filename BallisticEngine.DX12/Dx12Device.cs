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
    // versa — both submit to the one Queue, which serializes execution. While a frame is open, ExecuteSync
    // calls ON THE FRAME-OWNING THREAD record into frameList instead of submitting; off-thread/no-frame calls
    // take the legacy synchronous path. Disabled by BALLISTIC_DX12_PIPELINED=0 (legacy per-call submit+wait).
    //
    // P0b — FRAME-IN-FLIGHT OVERLAP: the frame ALLOCATOR is N-buffered (frameAllocators[FramesInFlight]) and
    // EndFrame SIGNALS a dedicated frameFence instead of WaitForGpu, so the CPU records frame N+1 while the GPU
    // still renders frame N (real CPU↔GPU overlap, unlocked once P0c stops the present from full-Flush'ing).
    // BeginFrame advances `frameSlot` round-robin and waits ONLY for the frame that last used that slot
    // (FramesInFlight frames ago — already done in steady state) before reusing its allocator. `frameSlot` is
    // ALSO the index every per-frame CPU-mapped upload buffer + shader-visible descriptor heap offsets into
    // (FrameSlot, read by Dx12DescriptorHeap + the renderer's CB writes) so the CPU never stomps data the GPU
    // is still reading from frame N. DEFAULT-heap GPU-only resources (cull UAVs, indirect command buffers) need
    // NO N-buffering: the single command queue executes serially, so frame N's reads of them complete before
    // frame N+1's command list begins. Only the CPU-written-ahead uploads + descriptor copies are hazards.
    // The SEPARATE frameFence (not the shared `fence`) keeps the per-slot completion targets from being
    // perturbed by interleaved ExecuteSyncImmediate/ExecuteUpload WaitForGpu increments. When pipelining is OFF
    // (BALLISTIC_DX12_PIPELINED=0) FramesInFlight==1, BeginFrame uses slot 0 always, and EndFrame waits — so
    // the whole P0b machinery collapses to the P0a single-slot single-wait path (byte-identical fallback).
    const int MaxFramesInFlight = 3;
    readonly ID3D12CommandAllocator[] frameAllocators;   // [FramesInFlight] round-robin per frame
    // P0b — one command LIST per slot too (not just the allocator): with EndFrame signalling-not-waiting, the
    // CPU loops to the next BeginFrame and Reset()s the list while the PRIOR submission may still be executing
    // on the GPU. Resetting an in-flight command list is undefined behaviour in D3D12 (corruption / removal).
    // BeginFrame's per-slot fence wait guarantees THIS slot's previous frame finished before reuse, so a
    // per-slot list is safe to reset; a single shared list would be reset every frame regardless of which
    // slot's submission is in flight. `frameList` is the active slot's list (set in BeginFrame).
    readonly ID3D12GraphicsCommandList4[] frameLists;    // [FramesInFlight]
    ID3D12GraphicsCommandList4 frameList;                // == frameLists[frameSlot] while a frame is open
    readonly ID3D12Fence frameFence;                     // SEPARATE from `fence`: per-slot frame-completion targets
    ulong frameFenceValue;
    readonly ulong[] frameFenceTargets;                  // [FramesInFlight] the frameFence value the GPU reaches when that slot's frame is done
    readonly System.Threading.AutoResetEvent frameFenceEvent = new(false);
    int frameSlot;                  // 0..FramesInFlight-1, advanced each BeginFrame; the per-frame upload/heap index
    bool frameOpen;                 // a frame is recording into frameList (set by BeginFrame, cleared by EndFrame)
    int frameThreadId;              // only this thread's ExecuteSync calls redirect into frameList
    readonly bool pipelinedFrames;  // BALLISTIC_DX12_PIPELINED != "0" (default ON)

    // The number of frames the CPU may run ahead of the GPU = the multiplier for every per-frame CPU-mapped
    // upload buffer + shader-visible descriptor heap. 1 when pipelining is off (P0a fallback). Read by the
    // renderer (CB allocation) and Dx12DescriptorHeap (capacity * FramesInFlight) so a single knob N-buffers
    // everything consistently. P0b ships N=2 (CPU at most one frame ahead through the present); MaxFramesInFlight
    // is 3 so bumping to N=3 after P0c proves the CPU outruns the GPU by >1 frame is a one-line change.
    public int FramesInFlight { get; }

    // The current frame's slot (0..FramesInFlight-1). Frozen for the WHOLE frame (advances only in BeginFrame),
    // so every per-frame CPU write between BeginFrame and EndFrame lands in the same slab the GPU will read.
    public int FrameSlot => frameSlot;

    // SEPARATE upload allocator/list/fence so asset uploads (textures, buffers) never share command-list
    // state with the per-frame render path. Sharing one list between BeginRender's ExecuteSync and the
    // interleaved asset uploads was the suspected cause of the texture CopyTextureRegion E_FAILs.
    readonly ID3D12CommandAllocator uploadAllocator;
    readonly ID3D12GraphicsCommandList4 uploadList;
    readonly ID3D12Fence uploadFence;
    ulong uploadFenceValue;
    readonly System.Threading.AutoResetEvent uploadEvent = new(false);

    public Dx12Device(bool enableDebugLayer = true) {
        // GPU-Based Validation (GBV) — the deterministic gate for the BARRIER/STATE bug class the
        // pass-graph migration relies on (W1 of the dx12-passgraph plan). GBV validates each resource's
        // state AT EACH GPU-timeline USE, catching wrong/missing barriers that leave THIS frame visually
        // clean (the silent class that ships). It REQUIRES the debug layer, is SLOW (10-100x), and can
        // trip the TDR watchdog into a false device-removal — so it's strictly opt-in (BALLISTIC_DX12_GBV=1)
        // and never default. (DirectXRenderAsset forces the debug layer on whenever GBV is requested so it
        // isn't silently a no-op.) See [[gpu-hang-launch-safety]] re: GBV-induced false removals.
        gbvEnabled = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV") == "1";

        // Debug layer first (must precede device creation) — catches the silent device-removals that
        // are the classic DX12 crash. On by default in Debug; harmless if the SDK layer is absent.
        if (enableDebugLayer && D3D12GetDebugInterface(out ID3D12Debug debug).Success) {
            debug.EnableDebugLayer();
            // GBV is configured through the ID3D12Debug1 facet of the SAME debug interface (queried before
            // it's disposed). Guarded — the facet/GBV may be absent on an old runtime; a failure must not
            // block device creation (the debug layer is already enabled either way).
            if (gbvEnabled) {
                try {
                    using var debug1 = debug.QueryInterfaceOrNull<ID3D12Debug1>();
                    if (debug1 is not null) {
                        debug1.SetEnableGPUBasedValidation(true);
                        // Synchronized command-queue validation also catches cross-queue state hazards
                        // (cheap relative to GBV itself; harmless on the single graphics queue we use today).
                        debug1.SetEnableSynchronizedCommandQueueValidation(true);
                        Console.WriteLine("[DX12] GPU-Based Validation: ENABLED (BALLISTIC_DX12_GBV=1) — slow; opt-in verification only.");
                    } else {
                        Console.WriteLine("[DX12] GPU-Based Validation requested but ID3D12Debug1 is unavailable on this runtime — skipped.");
                    }
                } catch (Exception e) {
                    Console.WriteLine("[DX12] GPU-Based Validation setup failed (continuing without it): " + e.Message);
                }
            }
            debug.Dispose();
        } else if (gbvEnabled) {
            Console.WriteLine("[DX12] BALLISTIC_DX12_GBV=1 but the debug layer is not enabled/available — GBV is a no-op. Run with the debug layer (DirectXRenderAsset forces it when GBV is set).");
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

        // P0a/P0b frame list + N-buffered allocators (see field comment). `pipelinedFrames` (the P0a single-
        // recorded-list-per-frame win) is ON by default. CPU↔GPU OVERLAP (FramesInFlight>1 → EndFrame
        // signals-not-waits so the CPU runs ahead) is GATED OFF by default and only enabled by the explicit
        // opt-in BALLISTIC_DX12_OVERLAP=1, because it requires EVERY per-frame CPU-written upload + descriptor
        // heap to be N-buffered first — until P0b's resource N-buffering is COMPLETE+verified, running ahead
        // stomps data the GPU is still reading (a cross-frame race that escalated to a TDR/PC crash during
        // bring-up). With overlap off, FramesInFlight==1: BeginFrame uses one slot, EndFrame WAITS — the proven
        // P0a single-submit single-wait frame. BALLISTIC_DX12_PIPELINED=0 disables even P0a (legacy per-pass
        // submit). One command LIST + allocator per slot → resetting an in-flight list is impossible (each
        // slot's reuse is fence-gated in BeginFrame). EndFrame submits on the dedicated `frameFence`.
        pipelinedFrames = Environment.GetEnvironmentVariable("BALLISTIC_DX12_PIPELINED") != "0";
        bool overlap = pipelinedFrames && Environment.GetEnvironmentVariable("BALLISTIC_DX12_OVERLAP") == "1";
        FramesInFlight = overlap ? 2 : 1;   // P0b overlap N=2 (opt-in); 1 = no overlap (EndFrame waits). Raise to 3 (≤ MaxFramesInFlight) post-P0c.
        frameAllocators = new ID3D12CommandAllocator[FramesInFlight];
        frameLists = new ID3D12GraphicsCommandList4[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++) {
            frameAllocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
            frameLists[i] = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
                CommandListType.Direct, frameAllocators[i], null);
            frameLists[i].Close();   // created OPEN → close so the first BeginFrame Reset is uniform
        }
        frameList = frameLists[0];
        frameFence = Device.CreateFence(0, FenceFlags.None);
        frameFenceTargets = new ulong[FramesInFlight];   // all 0 → the first N BeginFrames never wait (slots fresh)

        // Debug info queue (if the debug layer loaded): lets us print the REAL D3D12 message text on an
        // E_FAIL instead of the opaque HRESULT. Stored-message log by default.
        if (enableDebugLayer)
            infoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();

        // W4 — FAIL LOUD: break into the debugger / throw at the offending call site on a NEW state error,
        // so a barrier/state bug stops the run instead of scrolling past in the stored log. Opt-in
        // (BALLISTIC_DX12_BREAK_ON_ERROR=1) and SEPARATE from GBV: chunk 0 only wires the mechanism. The
        // baseline allowlist (W2 — "break only on messages NOT in the captured baseline") is a later chunk;
        // until then break-on-error trips on pre-existing benign noise, so it stays OFF by default and the
        // GBV verification run does NOT enable it (GBV stores+prints messages without breaking). When on,
        // we break on Corruption + Error (the state class); Warnings stay non-breaking.
        breakOnError = Environment.GetEnvironmentVariable("BALLISTIC_DX12_BREAK_ON_ERROR") == "1";
        if (breakOnError && infoQueue is not null) {
            infoQueue.SetBreakOnSeverity(MessageSeverity.Corruption, true);
            infoQueue.SetBreakOnSeverity(MessageSeverity.Error, true);
            Console.WriteLine("[DX12] Info-queue break-on-error: ENABLED (BALLISTIC_DX12_BREAK_ON_ERROR=1) — breaks on Corruption/Error. NOTE: no baseline allowlist yet, so pre-existing benign messages will also break.");
        }
    }

    readonly bool gbvEnabled;     // BALLISTIC_DX12_GBV=1 — GPU-Based Validation active (requires debug layer)
    readonly bool breakOnError;   // BALLISTIC_DX12_BREAK_ON_ERROR=1 — info queue breaks on Corruption/Error
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

    // P0a/P0b — open the per-frame command list on the next round-robin slot. Subsequent ExecuteSync calls on
    // THIS thread record into it instead of submitting. No-op (legacy per-pass submit) when
    // BALLISTIC_DX12_PIPELINED=0. P0b: advance `frameSlot`, then WAIT for the frame that last used this slot
    // (FramesInFlight frames ago — already finished in steady state; target 0 the first N frames → no wait)
    // before resetting its allocator, so the GPU isn't still reading commands out of memory we're about to
    // recycle. At FramesInFlight==1 this reduces to "wait for the single previous frame", i.e. the P0a behaviour
    // (EndFrame already signalled it). frameSlot advances ONLY here, so it's frozen for the whole frame — every
    // per-frame CPU-mapped upload + descriptor copy this frame indexes the same slab the GPU will read. Must be
    // paired with EndFrame; nesting is not supported (the slot would advance mid-frame).
    public void BeginFrame() {
        if (!pipelinedFrames || frameOpen) return;
        frameSlot = (frameSlot + 1) % FramesInFlight;
        WaitFrameFence(frameFenceTargets[frameSlot]);   // the GPU finished the frame that last used this slot's allocator+list
        frameList = frameLists[frameSlot];
        frameAllocators[frameSlot].Reset();
        frameList.Reset(frameAllocators[frameSlot], null);
        frameThreadId = Environment.CurrentManagedThreadId;
        frameOpen = true;
    }

    // P0a/P0b — close + submit the recorded frame ONCE. P0b: SIGNAL the dedicated frameFence (record the value
    // this slot's frame will reach) and RETURN — no WaitForGpu, so the CPU can immediately start recording the
    // next frame while the GPU drains this one (real overlap, once P0c stops the present from full-Flush'ing).
    // At FramesInFlight==1 we additionally WAIT (collapses to the P0a single-submit single-wait frame — the
    // byte-identical fallback). Safe to call when no frame is open (no-op). Returns true if a frame was submitted.
    public bool EndFrame() {
        if (!frameOpen) return false;
        frameOpen = false;
        frameList.Close();
        Queue.ExecuteCommandList(frameList);
        ulong target = ++frameFenceValue;
        Queue.Signal(frameFence, target);
        frameFenceTargets[frameSlot] = target;
        if (FramesInFlight == 1) WaitFrameFence(target);   // P0a fallback: no overlap, drain before returning
        return true;
    }

    // Block until the GPU has signalled frameFence to at least `target`. Used by BeginFrame (per-slot recycle
    // gate), the FramesInFlight==1 EndFrame, and Flush (so a swapchain resize/present sees the frame drained).
    void WaitFrameFence(ulong target) {
        if (target == 0 || frameFence.CompletedValue >= target) return;
        frameFence.SetEventOnCompletion(target, frameFenceEvent.SafeWaitHandle.DangerousGetHandle());
        frameFenceEvent.WaitOne();
    }

    // The latest submitted frame's completion target — Flush()/swapchain present wait on this so an overlapped
    // frame (signalled only on frameFence, not the shared `fence`) is fully drained before ResizeBuffers/Present.
    public ulong LastFrameFenceTarget => frameFenceValue;
    public void WaitForFrame(ulong target) => WaitFrameFence(target);

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
        if (reopen) {   // continue recording the rest of the frame into a fresh segment ON THE SAME SLOT
            // The pre-flush segment was submitted AND waited (WaitForGpu above), so the GPU is done reading
            // this slot's allocator → resetting it is safe. The slot does NOT advance (advancing mid-frame
            // would split one logical frame across two upload/descriptor slabs — the post-flush passes would
            // read a different slot than the pre-flush passes wrote). Per-frame mapped uploads + descriptors
            // already written this frame stay valid (only the command allocator is reset, not the upload heaps).
            frameAllocators[frameSlot].Reset();
            frameList.Reset(frameAllocators[frameSlot], null);
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
                // P0b: an overlapped frame is signalled ONLY on frameFence (EndFrame no longer advances the
                // shared `fence`), so WaitForGpu alone would let Flush return while a frame submit is still in
                // flight on the GPU — ResizeBuffers/present would then free/reuse backbuffers under an active
                // read. Drain the latest frame too. (No-op when no frame ran or it already completed.)
                WaitFrameFence(frameFenceValue);
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
        WaitFrameFence(frameFenceValue);   // P0b: drain any overlapped frame (signalled only on frameFence)
        fenceEvent.Dispose();
        fence.Dispose();
        frameFenceEvent.Dispose();
        frameFence.Dispose();
        foreach (var l in frameLists) l.Dispose();
        foreach (var a in frameAllocators) a.Dispose();
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
