using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12Device : IDisposable {
    public ID3D12Device2 Device { get; }
    public ID3D12CommandQueue Queue { get; }

    public byte[] AdapterLuidBytes { get; }

    public string AdapterDescription { get; }
    public string AdapterDriverVersion { get; }

    public ulong DedicatedVideoMemoryBytes { get; }

    public bool HasHardwareRayTracing { get; }
    public bool HasMeshShaders { get; }

    Dx12PsoCache psoCache;
    public static string PsoCacheDirectory { get; set; }
    Dx12PsoCache PsoCache => psoCache ??= new Dx12PsoCache(Device, PsoCacheDirectory);

    public ID3D12PipelineState CreateGraphicsPso(in GraphicsPipelineStateDescription desc, string name)
        => PsoCache.CreateGraphics(desc, name);
    public ID3D12PipelineState CreateComputePso(in ComputePipelineStateDescription desc, string name)
        => PsoCache.CreateCompute(desc, name);

    public void SavePsoCache() => psoCache?.SaveToDisk();

    readonly ID3D12CommandAllocator allocator;
    readonly ID3D12GraphicsCommandList4 commandList;
    readonly ID3D12Fence fence;
    ulong fenceValue;
    readonly System.Threading.AutoResetEvent fenceEvent = new(false);

    const int MaxFramesInFlight = 3;
    readonly ID3D12CommandAllocator[] frameAllocators;

    readonly ID3D12GraphicsCommandList4[] frameLists;
    ID3D12GraphicsCommandList4 frameList;
    readonly ID3D12Fence frameFence;
    ulong frameFenceValue;
    readonly ulong[] frameFenceTargets;
    readonly System.Threading.AutoResetEvent frameFenceEvent = new(false);
    int frameSlot;
    bool frameOpen;
    int frameThreadId;
    readonly bool pipelinedFrames;

    public int FramesInFlight { get; }

    public int FrameSlot => frameSlot;

    readonly ID3D12CommandAllocator uploadAllocator;
    readonly ID3D12GraphicsCommandList4 uploadList;
    readonly ID3D12Fence uploadFence;
    ulong uploadFenceValue;
    readonly System.Threading.AutoResetEvent uploadEvent = new(false);

    public bool AsyncComputeEnabled { get; }
    ID3D12CommandQueue computeQueue;

    const int MaxAsyncHandoffs = 4;
    ID3D12CommandAllocator[,] computeAllocators;
    ID3D12GraphicsCommandList4[,] computeLists;

    ulong[] computeFenceTargets;

    ID3D12CommandAllocator[,] framePostAllocators;

    int frameHandoffCount;

    ID3D12Fence asyncFence;
    ulong asyncFenceValue;

    public Dx12Device(bool enableDebugLayer = true) {
        gbvEnabled = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV") == "1";

        if (enableDebugLayer && D3D12GetDebugInterface(out ID3D12Debug debug).Success) {
            debug.EnableDebugLayer();
            if (gbvEnabled) {
                try {
                    using var debug1 = debug.QueryInterfaceOrNull<ID3D12Debug1>();
                    if (debug1 is not null) {
                        debug1.SetEnableGPUBasedValidation(true);
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

        bool breadcrumbs = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DRED") == "1";
        if (D3D12GetDebugInterface(out ID3D12DeviceRemovedExtendedDataSettings dred).Success) {
            dred.SetPageFaultEnablement(DredEnablement.ForcedOn);
            if (breadcrumbs) dred.SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
            dred.Dispose();
            dredEnabled = true;
        }

        using IDXGIFactory4 factory = CreateDXGIFactory1<IDXGIFactory4>();
        IDXGIAdapter1 adapter = PickHardwareAdapter(factory);
        Device = D3D12CreateDevice<ID3D12Device2>(adapter, FeatureLevel.Level_12_0);
        try { AdapterDescription = adapter.Description1.Description?.Trim() ?? ""; } catch { AdapterDescription = ""; }
        try { DedicatedVideoMemoryBytes = (ulong)adapter.Description1.DedicatedVideoMemory; } catch { DedicatedVideoMemoryBytes = 0; }
        try {
            if (adapter.CheckInterfaceSupport<IDXGIDevice>(out long umd)) {
                AdapterDriverVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}");
            } else AdapterDriverVersion = "";
        } catch { AdapterDriverVersion = ""; }
        adapter.Dispose();
        AdapterLuidBytes = BitConverter.GetBytes(Device.AdapterLuid);

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

        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_NRD_SELFTEST") == "1")
            NrdApi.SelfTest();

        bool ms = false;
        try {
            var opt7 = Device.CheckFeatureSupport<FeatureDataD3D12Options7>(Vortice.Direct3D12.Feature.Options7);
            ms = opt7.MeshShaderTier >= MeshShaderTier.Tier1;
        } catch { ms = false; }
        HasMeshShaders = ms;
        Console.WriteLine(ms ? "[DX12] Mesh shaders: AVAILABLE (Tier 1+)"
                             : "[DX12] Mesh shaders: NOT available — the meshlet pipeline (R4) is disabled.");

        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        allocator = Device.CreateCommandAllocator(CommandListType.Direct);
        commandList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, allocator, null);
        commandList.Close();
        fence = Device.CreateFence(0, FenceFlags.None);

        uploadAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        uploadList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, uploadAllocator, null);
        uploadList.Close();
        uploadFence = Device.CreateFence(0, FenceFlags.None);

        pipelinedFrames = Environment.GetEnvironmentVariable("BALLISTIC_DX12_PIPELINED") != "0";
        string overlapEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_OVERLAP");
        bool overlap = pipelinedFrames && overlapEnv != "0";
        FramesInFlight = !overlap ? 1 : (overlapEnv == "3" ? Math.Min(3, MaxFramesInFlight) : 2);
        frameAllocators = new ID3D12CommandAllocator[FramesInFlight];
        frameLists = new ID3D12GraphicsCommandList4[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++) {
            frameAllocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
            frameLists[i] = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
                CommandListType.Direct, frameAllocators[i], null);
            frameLists[i].Close();
        }
        frameList = frameLists[0];
        frameFence = Device.CreateFence(0, FenceFlags.None);
        frameFenceTargets = new ulong[FramesInFlight];

        AsyncComputeEnabled = Environment.GetEnvironmentVariable("BALLISTIC_DX12_ASYNC_COMPUTE") == "1";
        if (AsyncComputeEnabled) {
            computeQueue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
            computeAllocators = new ID3D12CommandAllocator[FramesInFlight, MaxAsyncHandoffs];
            computeLists = new ID3D12GraphicsCommandList4[FramesInFlight, MaxAsyncHandoffs];
            framePostAllocators = new ID3D12CommandAllocator[FramesInFlight, MaxAsyncHandoffs];
            for (int i = 0; i < FramesInFlight; i++) {
                for (int h = 0; h < MaxAsyncHandoffs; h++) {
                    computeAllocators[i, h] = Device.CreateCommandAllocator(CommandListType.Compute);
                    computeLists[i, h] = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
                        CommandListType.Compute, computeAllocators[i, h], null);
                    computeLists[i, h].Close();
                    framePostAllocators[i, h] = Device.CreateCommandAllocator(CommandListType.Direct);
                }
            }
            asyncFence = Device.CreateFence(0, FenceFlags.None);
            computeFenceTargets = new ulong[FramesInFlight];
        }

        if (enableDebugLayer)
            infoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();

        breakOnError = Environment.GetEnvironmentVariable("BALLISTIC_DX12_BREAK_ON_ERROR") == "1";
        if (breakOnError && infoQueue is not null)
            Console.WriteLine("[DX12] Info-queue break-on-error: ENABLED (BALLISTIC_DX12_BREAK_ON_ERROR=1) — baseline-aware: the end-of-frame drain fails loud on NEW (non-baseline) Corruption/Error messages only.");
    }

    readonly bool gbvEnabled;
    readonly bool breakOnError;

    readonly bool dredEnabled;

    public string DrainDredReport() {
        if (!dredEnabled) return "(DRED off — run with BALLISTIC_DX12_DRED=1)";
        try {
            using var dred = Device.QueryInterfaceOrNull<ID3D12DeviceRemovedExtendedData>();
            if (dred is null) return "(DRED unavailable on this device/OS)";
            dred.GetPageFaultAllocationOutput(out DredPageFaultOutput pf);
            return $"PageFaultVA=0x{pf.PageFaultVA:X} " +
                   "(VA!=0 => GPU touched freed/invalid memory: use-after-free or bad descriptor; " +
                   "VA==0 => bad bind / GPU HANG, not a memory fault)";
        }
        catch (Exception e) { return "DRED read failed: " + e.Message; }
    }

    ID3D12InfoQueue infoQueue;

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

    public bool HasInfoQueue => infoQueue is not null;
    public bool GbvEnabled => gbvEnabled;

    public System.Collections.Generic.IReadOnlyList<DebugMessage> DrainDebugMessagesStructured() {
        if (infoQueue is null) return System.Array.Empty<DebugMessage>();
        ulong n = infoQueue.NumStoredMessages;
        if (n == 0) return System.Array.Empty<DebugMessage>();
        var list = new System.Collections.Generic.List<DebugMessage>((int)n);
        for (ulong i = 0; i < n; i++) {
            Message m = infoQueue.GetMessage(i);
            list.Add(new DebugMessage(m.Category, m.Severity, m.Id, m.Description));
        }
        infoQueue.ClearStoredMessages();
        return list;
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

    readonly object submitGate = new();
    public void ExecuteSync(Action<ID3D12GraphicsCommandList4> record) {
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

    ID3D12QueryHeap tsHeap;
    ID3D12Resource tsReadback;
    ulong tsFrequency;
    bool tsInit, tsAvail;
    const int TsSlots = 2;

    void EnsureTimestampHeap() {
        if (tsInit) return;
        tsInit = true;
        try {
            Queue.GetTimestampFrequency(out tsFrequency);
            tsHeap = Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, TsSlots));
            tsReadback = Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)(TsSlots * sizeof(ulong))), ResourceStates.CopyDest);
            tsAvail = tsFrequency > 0;
        } catch { tsAvail = false; }
    }
    void WriteTimestampMarker(int slot) {
        lock (submitGate) {
            allocator.Reset();
            commandList.Reset(allocator, null);
            commandList.EndQuery(tsHeap, QueryType.Timestamp, (uint)slot);
            commandList.Close();
            Queue.ExecuteCommandList(commandList);
            WaitForGpu();
        }
    }

    public bool GpuTimerAvailable { get { EnsureTimestampHeap(); return tsAvail; } }
    public void GpuTimerBegin() { if (GpuTimerAvailable) WriteTimestampMarker(0); }

    public unsafe double GpuTimerEnd() {
        if (!tsAvail) return -1.0;
        WriteTimestampMarker(1);
        lock (submitGate) {
            allocator.Reset();
            commandList.Reset(allocator, null);
            commandList.ResolveQueryData(tsHeap, QueryType.Timestamp, 0, (uint)TsSlots, tsReadback, 0);
            commandList.Close();
            Queue.ExecuteCommandList(commandList);
            WaitForGpu();
            ulong* t = tsReadback.Map<ulong>(0);
            ulong begin = t[0], end = t[1];
            tsReadback.Unmap(0);
            return end > begin ? (end - begin) * 1000.0 / tsFrequency : 0.0;
        }
    }

    Dx12GpuProfiler gpuProfiler;
    public Dx12GpuProfiler GpuProfiler => gpuProfiler ??= new Dx12GpuProfiler(this);
    int profileFrameCounter;

    public ID3D12GraphicsCommandList4 FrameList => frameList;

    public void BeginFrame() {
        if (!pipelinedFrames || frameOpen) return;
        frameSlot = (frameSlot + 1) % FramesInFlight;
        WaitFrameFence(frameFenceTargets[frameSlot]);
        DrainDeferredReleases();
        frameList = frameLists[frameSlot];
        frameAllocators[frameSlot].Reset();
        frameList.Reset(frameAllocators[frameSlot], null);
        if (AsyncComputeEnabled)
            for (int h = 0; h < MaxAsyncHandoffs; h++) framePostAllocators[frameSlot, h].Reset();
        frameHandoffCount = 0;
        frameThreadId = Environment.CurrentManagedThreadId;
        frameOpen = true;
        if (gpuProfiler is { Enabled: true }) gpuProfiler.BeginFrame(profileFrameCounter++);
    }

    readonly System.Collections.Generic.Queue<(ulong target, IDisposable res)> deferredReleases = new();
    public void DeferredRelease(IDisposable resource) {
        if (resource is null) return;
        if (FramesInFlight == 1) { resource.Dispose(); return; }

        deferredReleases.Enqueue((frameFenceValue + 1, resource));
    }
    void DrainDeferredReleases() {
        ulong done = frameFence.CompletedValue;
        while (deferredReleases.Count > 0 && deferredReleases.Peek().target <= done) {
            deferredReleases.Dequeue().res.Dispose();
        }
    }

    public bool EndFrame() {
        if (!frameOpen) return false;
        frameOpen = false;
        ulong profTarget = frameFenceValue + 1;
        if (gpuProfiler is { Enabled: true }) gpuProfiler.ResolveInto(frameList, profTarget);
        frameList.Close();
        Queue.ExecuteCommandList(frameList);
        ulong target = ++frameFenceValue;
        Queue.Signal(frameFence, target);
        frameFenceTargets[frameSlot] = target;
        if (gpuProfiler is { Enabled: true }) {
            string line = gpuProfiler.Drain(frameFence.CompletedValue);
            if (line is not null) Console.WriteLine(line);
        }

        if (FramesInFlight == 1 || syncThisFrame) WaitFrameFence(target);
        syncThisFrame = false;
        return true;
    }

    bool syncThisFrame;
    public void RequestFrameSync() => syncThisFrame = true;

    void WaitFrameFence(ulong target) {
        if (target == 0 || frameFence.CompletedValue >= target) return;
        frameFence.SetEventOnCompletion(target, frameFenceEvent.SafeWaitHandle.DangerousGetHandle());
        frameFenceEvent.WaitOne();
        if (dredTrap) {
            var rr = Device.DeviceRemovedReason;
            if (!rr.Success) {
                Console.Error.WriteLine($"[DRED-TRAP] DEVICE REMOVED after frame submit (WaitFrameFence): reason={rr} DRED={DrainDredReport()}");
                if (HasInfoQueue) Console.Error.WriteLine($"[DRED-TRAP] debug-msgs:\n{DrainDebugMessages()}");
                Environment.Exit(7);
            }
        }
    }

    public ulong LastFrameFenceTarget => frameFenceValue;
    public void WaitForFrame(ulong target) => WaitFrameFence(target);

    public void RecordAsyncCompute(Action<ID3D12GraphicsCommandList4> record) {
        if (!AsyncComputeEnabled || !(frameOpen && Environment.CurrentManagedThreadId == frameThreadId)) {
            ExecuteSync(record);
            return;
        }
        if (frameHandoffCount >= MaxAsyncHandoffs) {
            ExecuteSync(record);
            return;
        }
        int handoff = frameHandoffCount++;
        frameList.Close();
        Queue.ExecuteCommandList(frameList);
        ulong a = ++asyncFenceValue;
        Queue.Signal(asyncFence, a);
        computeQueue.Wait(asyncFence, a);
        var cAlloc = computeAllocators[frameSlot, handoff];
        var cList = computeLists[frameSlot, handoff];
        cAlloc.Reset();
        cList.Reset(cAlloc, null);
        record(cList);
        try { cList.Close(); }
        catch (Exception ex) {
            if (HasInfoQueue) Console.Error.WriteLine($"[ASYNC-CLOSE-FAIL] {ex.Message}\n{DrainDebugMessages()}");
            throw;
        }
        computeQueue.ExecuteCommandList(cList);
        ulong b = ++asyncFenceValue;
        computeQueue.Signal(asyncFence, b);
        computeFenceTargets[frameSlot] = b;
        frameList.Reset(framePostAllocators[frameSlot, handoff], null);
        Queue.Wait(asyncFence, b);
    }

    public bool FrameOpen => frameOpen && Environment.CurrentManagedThreadId == frameThreadId;

    public void ExecuteSyncImmediate(Action<ID3D12GraphicsCommandList4> record) {
        bool reopen = false;
        if (frameOpen && Environment.CurrentManagedThreadId == frameThreadId) {
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
        if (reopen) {
            frameAllocators[frameSlot].Reset();
            frameList.Reset(frameAllocators[frameSlot], null);
            frameOpen = true;
        }
    }

    public void RunExclusive(Action body) {
        lock (uploadGate) body();
    }

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

    public void Flush() {
        lock (submitGate)
            lock (uploadGate) {
                WaitForGpu();
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

        if (dredTrap) {
            var rr = Device.DeviceRemovedReason;
            if (!rr.Success) {
                Console.Error.WriteLine($"[DRED-TRAP] DEVICE REMOVED right after a GPU submit: reason={rr} DRED={DrainDredReport()}");
                if (HasInfoQueue) Console.Error.WriteLine($"[DRED-TRAP] debug-msgs:\n{DrainDebugMessages()}");
                Environment.Exit(7);
            }
        }
    }
    readonly bool dredTrap = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DRED_TRAP") == "1";

    public unsafe ID3D12Resource CreateDefaultBuffer<T>(ReadOnlySpan<T> data, ResourceStates finalState)
        where T : unmanaged {
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
        }

        return result;
    }

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

    public ID3D12Resource CreateReadbackBuffer(int byteSize) =>
        Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)byteSize), ResourceStates.CopyDest);

    public void Dispose() {
        WaitForGpu();
        WaitFrameFence(frameFenceValue);
        if (asyncFence is not null && asyncFenceValue > 0 && asyncFence.CompletedValue < asyncFenceValue) {
            asyncFence.SetEventOnCompletion(asyncFenceValue, frameFenceEvent.SafeWaitHandle.DangerousGetHandle());
            frameFenceEvent.WaitOne();
        }
        while (deferredReleases.Count > 0) deferredReleases.Dequeue().res.Dispose();
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
        if (computeLists is not null) foreach (var l in computeLists) l.Dispose();
        if (computeAllocators is not null) foreach (var a in computeAllocators) a.Dispose();
        if (framePostAllocators is not null) foreach (var a in framePostAllocators) a.Dispose();
        asyncFence?.Dispose();
        computeQueue?.Dispose();
        psoCache?.SaveToDisk();
        psoCache?.Dispose();
        infoQueue?.Dispose();
        Queue.Dispose();
        Device.Dispose();
    }
}
